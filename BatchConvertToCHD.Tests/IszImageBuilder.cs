using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using CompressionMode = SharpCompress.Compressors.CompressionMode;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     Builds synthetic ISZ files for the decoder tests.
///     The layout here is written from EZB Systems' ISZ File Format Specification 1.00 plus the
///     behaviours libMirage's ISZ filter and isz-tool agree UltraISO adds on top of it (the obfuscated
///     tables, the stripped bzip2 header), with every offset spelled out, so the two agreeing means
///     the reader matches real files and not merely itself. There is no sample from UltraISO to test
///     against, which is what this stands in for.
/// </summary>
internal static class IszImageBuilder
{
    internal const int HeaderLength = 48;
    internal const int ExtendedHeaderLength = 64;
    internal const int SegmentEntryLength = 24;

    // Chunk flag values, in the position the spec gives them: the top two bits of the entry.
    internal const int AdiZero = 0x00;
    internal const int AdiData = 0x40;
    internal const int AdiZlib = 0x80;
    internal const int AdiBz2 = 0xC0;

    internal const uint DefaultVolumeSerial = 0x11223344;

    /// <summary>The XOR mask UltraISO stores both tables under: the complement of "IsZ!".</summary>
    private static ReadOnlySpan<byte> ObfuscationMask => [0xB6, 0x8C, 0xA5, 0xDE];

    /// <summary>
    ///     Writes a whole-file ISZ. Returns the image bytes it describes so a caller can compare them
    ///     with what the decoder produces.
    /// </summary>
    /// <param name="path">File to write.</param>
    /// <param name="image">The image being stored.</param>
    /// <param name="sectorSize">Sector size to declare.</param>
    /// <param name="chunkSize">Uncompressed bytes per chunk.</param>
    /// <param name="pointerLength">Bytes per chunk table entry.</param>
    /// <param name="flagForChunk">Chosen storage method per chunk index.</param>
    /// <param name="passwordMode">Encryption field value; 0 means none.</param>
    /// <param name="declaredSectors">Sector count to declare, when it should not match the image.</param>
    /// <param name="writeChecksums">True to write UltraISO's 64-byte header with valid checksums.</param>
    /// <param name="zeroChunksCarryLength">True to record a zero chunk's uncompressed length in its table entry, as UltraISO does; false records zero.</param>
    internal static void WriteSingle(
        string path,
        byte[] image,
        int sectorSize,
        int chunkSize,
        int pointerLength,
        Func<int, int> flagForChunk,
        int passwordMode = 0,
        uint? declaredSectors = null,
        bool writeChecksums = false,
        bool zeroChunksCarryLength = true
    )
    {
        var chunks = BuildChunks(
            image,
            chunkSize,
            pointerLength,
            flagForChunk,
            zeroChunksCarryLength
        );
        var chunkTable = BuildChunkTable(chunks, pointerLength);
        Obfuscate(chunkTable);
        var data = Concat(chunks);

        var headerLength = writeChecksums ? ExtendedHeaderLength : HeaderLength;
        var dataOffset = headerLength + chunkTable.Length;
        var checksums = Checksums(writeChecksums, image, data);

        var header = BuildHeader(
            sectorSize,
            declaredSectors ?? (uint)(image.Length / sectorSize),
            passwordMode,
            0,
            (uint)chunks.Count,
            (uint)chunkSize,
            pointerLength,
            0,
            (uint)headerLength,
            0,
            (uint)dataOffset,
            DefaultVolumeSerial,
            headerLength,
            1,
            checksums.UncompressedCrc,
            checksums.DataSize,
            checksums.StoredCrc
        );

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(header);
        file.Write(chunkTable);
        file.Write(data);
    }

    /// <summary>
    ///     Writes a whole-file ISZ whose header declares no chunk table, which the specification
    ///     allows: the data is then one uncompressed run starting straight after the header.
    /// </summary>
    /// <param name="path">File to write.</param>
    /// <param name="image">The image being stored.</param>
    /// <param name="sectorSize">Sector size to declare.</param>
    /// <param name="chunkSize">Chunk size to declare; the decoder uses it to size its buffers.</param>
    /// <param name="writeChecksums">True to write UltraISO's 64-byte header with valid checksums.</param>
    internal static void WriteWithoutChunkTable(
        string path,
        byte[] image,
        int sectorSize,
        int chunkSize,
        bool writeChecksums = false
    )
    {
        var headerLength = writeChecksums ? ExtendedHeaderLength : HeaderLength;
        var checksums = Checksums(writeChecksums, image, image);

        var header = BuildHeader(
            sectorSize,
            (uint)(image.Length / sectorSize),
            0,
            0,
            0,
            (uint)chunkSize,
            3,
            0,
            0,
            0,
            (uint)headerLength,
            DefaultVolumeSerial,
            headerLength,
            1,
            checksums.UncompressedCrc,
            checksums.DataSize,
            checksums.StoredCrc
        );

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(header);
        file.Write(image);
    }

    /// <summary>
    ///     Writes a two-segment ISZ, cutting the chunk data at <paramref name="splitAfterBytes" /> so a
    ///     chunk straddles the boundary when that offset falls inside one.
    /// </summary>
    /// <param name="firstPath">Path of the .isz first segment; the second becomes ".i01" or the next part number.</param>
    /// <param name="image">The image being stored.</param>
    /// <param name="sectorSize">Sector size to declare.</param>
    /// <param name="chunkSize">Uncompressed bytes per chunk.</param>
    /// <param name="pointerLength">Bytes per chunk table entry.</param>
    /// <param name="flagForChunk">Chosen storage method per chunk index.</param>
    /// <param name="splitAfterBytes">Bytes of chunk data to keep in the first segment.</param>
    /// <param name="secondSegmentVolumeSerial">Serial for the second segment, to test a mismatch.</param>
    /// <param name="writeSecondSegment">False to leave the second segment missing.</param>
    /// <param name="writeChecksums">True to write UltraISO's 64-byte header with valid checksums.</param>
    /// <param name="zeroChunksCarryLength">True to record a zero chunk's uncompressed length in its table entry, as UltraISO does; false records zero.</param>
    internal static void WriteSplit(
        string firstPath,
        byte[] image,
        int sectorSize,
        int chunkSize,
        int pointerLength,
        Func<int, int> flagForChunk,
        int splitAfterBytes,
        uint? secondSegmentVolumeSerial = null,
        bool writeSecondSegment = true,
        bool writeChecksums = false,
        bool zeroChunksCarryLength = true
    )
    {
        var chunks = BuildChunks(
            image,
            chunkSize,
            pointerLength,
            flagForChunk,
            zeroChunksCarryLength
        );
        var chunkTable = BuildChunkTable(chunks, pointerLength);
        Obfuscate(chunkTable);
        var data = Concat(chunks);

        var headerLength = writeChecksums ? ExtendedHeaderLength : HeaderLength;
        var chunkTableOffset = headerLength + (3 * SegmentEntryLength);
        var dataOffset = chunkTableOffset + chunkTable.Length;

        var firstData = data.AsSpan(0, splitAfterBytes).ToArray();
        var secondData = data.AsSpan(splitAfterBytes).ToArray();

        // Which chunk the cut lands in, and how much of it ends up in the second file.
        var chunksStartingInFirst = 0;
        var leftSize = 0;
        var cursor = 0;
        foreach (var chunk in chunks)
        {
            if (cursor >= splitAfterBytes) break;

            chunksStartingInFirst++;
            var end = cursor + chunk.Stored.Length;
            if (end > splitAfterBytes) leftSize = end - splitAfterBytes;

            cursor = end;
        }

        var firstLength = dataOffset + firstData.Length;
        var secondLength = headerLength + secondData.Length;

        var segmentTable = new byte[3 * SegmentEntryLength];
        WriteSegmentEntry(
            segmentTable.AsSpan(0),
            firstLength,
            chunksStartingInFirst,
            0,
            dataOffset,
            leftSize
        );
        WriteSegmentEntry(
            segmentTable.AsSpan(SegmentEntryLength),
            secondLength,
            chunks.Count - chunksStartingInFirst,
            chunksStartingInFirst,
            headerLength,
            0
        );
        // Third entry stays zeroed before obfuscation: the spec terminates the table with a zero-size
        // entry, which UltraISO stores obfuscated like every other entry.
        Obfuscate(segmentTable);

        var checksums = Checksums(writeChecksums, image, data);

        var firstHeader = BuildHeader(
            sectorSize,
            (uint)(image.Length / sectorSize),
            0,
            firstLength,
            (uint)chunks.Count,
            (uint)chunkSize,
            pointerLength,
            0,
            (uint)chunkTableOffset,
            (uint)headerLength,
            (uint)dataOffset,
            DefaultVolumeSerial,
            headerLength,
            1,
            checksums.UncompressedCrc,
            checksums.DataSize,
            checksums.StoredCrc
        );

        using (var file = new FileStream(firstPath, FileMode.Create, FileAccess.Write))
        {
            file.Write(firstHeader);
            file.Write(segmentTable);
            file.Write(chunkTable);
            file.Write(firstData);
        }

        if (!writeSecondSegment) return;

        var secondHeader = BuildHeader(
            sectorSize,
            (uint)(image.Length / sectorSize),
            0,
            secondLength,
            (uint)chunks.Count,
            (uint)chunkSize,
            pointerLength,
            1,
            0,
            0,
            (uint)headerLength,
            secondSegmentVolumeSerial ?? DefaultVolumeSerial,
            headerLength,
            1,
            checksums.UncompressedCrc,
            checksums.DataSize,
            checksums.StoredCrc
        );

        using var second = new FileStream(
            GetSecondSegmentPath(firstPath),
            FileMode.Create,
            FileAccess.Write
        );
        second.Write(secondHeader);
        second.Write(secondData);
    }

    /// <summary>
    ///     The second-segment path beside a first segment: ".i01" for the spec scheme, or the next
    ///     ".partNN.isz"/".partNNN.isz" when the first segment uses that naming.
    /// </summary>
    /// <param name="firstPath">Path of the first segment.</param>
    internal static string GetSecondSegmentPath(string firstPath)
    {
        var name = Path.GetFileName(firstPath);
        if (!name.EndsWith(".isz", StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(firstPath, ".i01");

        var beforeExtension = name[..^".isz".Length];
        var dot = beforeExtension.LastIndexOf('.');
        if (dot > 0)
        {
            var last = beforeExtension[(dot + 1)..];
            if (
                last.StartsWith("part", StringComparison.OrdinalIgnoreCase)
                && last.Length is >= 6 and <= 7
                && last[4..].All(char.IsAsciiDigit)
            )
            {
                var digits = last.Length - 4;
                var next = 2.ToString("D" + digits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture);

                return firstPath[..^name.Length] + beforeExtension[..(dot + 1)] + "part" + next + ".isz";
            }
        }

        return Path.ChangeExtension(firstPath, ".i01");
    }

    /// <summary>
    ///     Builds a header, 48 bytes by default and 64 when checksums are given. Every field is
    ///     written at its documented offset so a test can tell which reader offset is wrong.
    /// </summary>
    /// <param name="sectorSize">Sector size field.</param>
    /// <param name="totalSectors">Total sectors field.</param>
    /// <param name="passwordMode">Encryption field.</param>
    /// <param name="segmentSize">Segment size field.</param>
    /// <param name="chunkCount">Chunk count field.</param>
    /// <param name="chunkSize">Chunk size field.</param>
    /// <param name="pointerLength">Chunk table entry width field.</param>
    /// <param name="segmentNumber">Segment number field.</param>
    /// <param name="chunkTableOffset">Chunk table offset field.</param>
    /// <param name="segmentTableOffset">Segment table offset field.</param>
    /// <param name="dataOffset">Data offset field.</param>
    /// <param name="volumeSerial">Volume serial number field.</param>
    /// <param name="headerSize">Header size field when no checksums are given.</param>
    /// <param name="version">Format version field.</param>
    /// <param name="uncompressedCrc">Checksum of the restored image, or null for a 48-byte header.</param>
    /// <param name="dataSize">Data size field, or null.</param>
    /// <param name="storedCrc">Checksum of the stored data, or null.</param>
    internal static byte[] BuildHeader(
        int sectorSize,
        uint totalSectors,
        int passwordMode,
        long segmentSize,
        uint chunkCount,
        uint chunkSize,
        int pointerLength,
        int segmentNumber,
        uint chunkTableOffset,
        uint segmentTableOffset,
        uint dataOffset,
        uint volumeSerial,
        int headerSize = HeaderLength,
        int version = 1,
        uint? uncompressedCrc = null,
        uint? dataSize = null,
        uint? storedCrc = null
    )
    {
        var extended =
            headerSize >= ExtendedHeaderLength
            || uncompressedCrc is not null
            || dataSize is not null
            || storedCrc is not null;

        var header = new byte[extended ? ExtendedHeaderLength : HeaderLength];

        header[0] = (byte)'I';
        header[1] = (byte)'s';
        header[2] = (byte)'Z';
        header[3] = (byte)'!';
        header[4] = (byte)(extended ? ExtendedHeaderLength : HeaderLength);
        header[5] = (byte)version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6), volumeSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)sectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), totalSectors);
        header[16] = (byte)passwordMode;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(17), segmentSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(25), chunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(29), chunkSize);
        header[33] = (byte)pointerLength;
        header[34] = (byte)segmentNumber;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(35), chunkTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(39), segmentTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(43), dataOffset);
        header[47] = 0;

        if (extended)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), uncompressedCrc ?? 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), dataSize ?? 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 0);

            // The stored-data checksum is the checksum of the restored image unless the caller says
            // otherwise, which keeps a hand-built header self-consistent.
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(60),
                storedCrc ?? uncompressedCrc ?? 0
            );
        }

        return header;
    }

    /// <summary>Compresses one chunk the way the given flag says it is stored.</summary>
    /// <param name="adiFlag">One of the ADI_* values.</param>
    /// <param name="plain">The chunk's uncompressed bytes.</param>
    internal static byte[] Store(int adiFlag, byte[] plain)
    {
        switch (adiFlag)
        {
            case AdiZero:
                return [];
            case AdiData:
                return plain;
            case AdiZlib:
            {
                using var output = new MemoryStream();
                using (
                    var deflate = new ZLibStream(
                        output,
                        CompressionLevel.SmallestSize,
                        true
                    )
                )
                {
                    deflate.Write(plain);
                }

                return output.ToArray();
            }
            default:
            {
                using var output = new MemoryStream();
                using (
                    var bzip2 = BZip2Stream.Create(
                        output,
                        CompressionMode.Compress,
                        false,
                        true
                    )
                )
                {
                    bzip2.Write(plain);
                }

                var compressed = output.ToArray();

                // UltraISO stores bzip2 chunks without the "BZh" stream header; readers put it back.
                Array.Clear(compressed, 0, Math.Min(3, compressed.Length));

                return compressed;
            }
        }
    }

    /// <summary>Applies the table XOR mask in place, matching what UltraISO writes.</summary>
    /// <param name="table">Segment or chunk table bytes.</param>
    private static void Obfuscate(Span<byte> table)
    {
        for (var index = 0; index < table.Length; index++)
            table[index] ^= ObfuscationMask[index % ObfuscationMask.Length];
    }

    /// <summary>The checksum values for a header, or all-null when the fixture should not carry any.</summary>
    /// <param name="write">True to compute them.</param>
    /// <param name="image">The image being stored.</param>
    /// <param name="storedData">The concatenated stored chunk bytes.</param>
    private static ChecksumValues Checksums(bool write, byte[] image, byte[] storedData)
    {
        return write
            ? new ChecksumValues(
                Complement(Crc32(image)),
                (uint)image.Length,
                Complement(Crc32(storedData))
            )
            : new ChecksumValues(null, null, null);
    }

    private static List<Chunk> BuildChunks(
        byte[] image,
        int chunkSize,
        int pointerLength,
        Func<int, int> flagForChunk,
        bool zeroChunksCarryLength
    )
    {
        var chunks = new List<Chunk>();
        var maxStored = (1 << ((8 * pointerLength) - 2)) - 1;

        for (var offset = 0; offset < image.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, image.Length - offset);
            var plain = image.AsSpan(offset, length).ToArray();
            var flag = flagForChunk(chunks.Count);
            var stored = Store(flag, plain);

            // The spec requires a stored chunk to be no larger than the chunk size, so a real writer
            // keeps the chunk verbatim whenever compressing it would not shrink it. Incompressible
            // content otherwise produces a file the format does not allow.
            if (flag is not AdiZero && stored.Length >= plain.Length)
            {
                flag = AdiData;
                stored = plain;
            }

            var tableLength = stored.Length;
            if (flag == AdiZero) tableLength = zeroChunksCarryLength ? plain.Length : 0;

            if (tableLength > maxStored)
            {
                throw new InvalidOperationException(
                    $"chunk {chunks.Count} stores {tableLength} bytes, more than a {pointerLength}-byte pointer can express"
                );
            }

            chunks.Add(new Chunk(flag, stored, tableLength));
        }

        return chunks;
    }

    private static byte[] BuildChunkTable(List<Chunk> chunks, int pointerLength)
    {
        var table = new byte[chunks.Count * pointerLength];

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];

            // The flag occupies the top two bits of the whole entry, so it lands in the top two bits
            // of the last byte of a little-endian value.
            var entry =
                (uint)chunk.TableLength | ((ulong)(chunk.Flag >> 6) << ((8 * pointerLength) - 2));

            for (var b = 0; b < pointerLength; b++) table[(index * pointerLength) + b] = (byte)(entry >> (8 * b));
        }

        return table;
    }

    private static void WriteSegmentEntry(
        Span<byte> target,
        long size,
        int chunkCount,
        int firstChunkNumber,
        int chunkOffset,
        int leftSize
    )
    {
        BinaryPrimitives.WriteInt64LittleEndian(target, size);
        BinaryPrimitives.WriteInt32LittleEndian(target[8..], chunkCount);
        BinaryPrimitives.WriteInt32LittleEndian(target[12..], firstChunkNumber);
        BinaryPrimitives.WriteInt32LittleEndian(target[16..], chunkOffset);
        BinaryPrimitives.WriteInt32LittleEndian(target[20..], leftSize);
    }

    private static byte[] Concat(List<Chunk> chunks)
    {
        using var buffer = new MemoryStream();
        foreach (var chunk in chunks) buffer.Write(chunk.Stored);

        return buffer.ToArray();
    }

    /// <summary>Standard CRC-32 (IEEE 802.3), implemented bitwise so it does not share code with the reader.</summary>
    /// <param name="data">Bytes to checksum.</param>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Turns a standard CRC-32 into the value the ISZ header stores.</summary>
    /// <param name="crc">Standard CRC-32 value.</param>
    private static uint Complement(uint crc)
    {
        return ~crc;
    }

    /// <summary>The checksum fields of a header.</summary>
    /// <param name="UncompressedCrc">Checksum of the restored image.</param>
    /// <param name="DataSize">Total data size.</param>
    /// <param name="StoredCrc">Checksum of the stored chunk bytes.</param>
    private sealed record ChecksumValues(uint? UncompressedCrc, uint? DataSize, uint? StoredCrc);

    private sealed record Chunk(int Flag, byte[] Stored, int TableLength);
}