using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;

namespace BatchConvertToCHD.Tests;

/// <summary>
/// Builds synthetic ISZ files for the decoder tests.
///
/// The layout here is written straight from EZB Systems' ISZ File Format Specification 1.00 rather
/// than from the production reader, with every offset spelled out, so the two agreeing means the
/// reader matches the spec and not merely itself. There is no sample from UltraISO to test against,
/// which is what this stands in for.
/// </summary>
internal static class IszImageBuilder
{
    internal const int HeaderLength = 48;
    internal const int SegmentEntryLength = 24;

    // Chunk flag values, in the position the spec gives them: the top two bits of the entry.
    internal const int AdiZero = 0x00;
    internal const int AdiData = 0x40;
    internal const int AdiZlib = 0x80;
    internal const int AdiBz2 = 0xC0;

    internal const uint DefaultVolumeSerial = 0x11223344;

    /// <summary>
    /// Writes a whole-file ISZ. Returns the image bytes it describes so a caller can compare them
    /// with what the decoder produces.
    /// </summary>
    /// <param name="path">File to write.</param>
    /// <param name="image">The image being stored.</param>
    /// <param name="sectorSize">Sector size to declare.</param>
    /// <param name="chunkSize">Uncompressed bytes per chunk.</param>
    /// <param name="pointerLength">Bytes per chunk table entry.</param>
    /// <param name="flagForChunk">Chosen storage method per chunk index.</param>
    /// <param name="passwordMode">Encryption field value; 0 means none.</param>
    /// <param name="declaredSectors">Sector count to declare, when it should not match the image.</param>
    internal static void WriteSingle(
        string path,
        byte[] image,
        int sectorSize,
        int chunkSize,
        int pointerLength,
        Func<int, int> flagForChunk,
        int passwordMode = 0,
        uint? declaredSectors = null
    )
    {
        var chunks = BuildChunks(image, chunkSize, pointerLength, flagForChunk);
        var chunkTable = BuildChunkTable(chunks, pointerLength);
        var data = Concat(chunks);

        var dataOffset = HeaderLength + chunkTable.Length;

        var header = BuildHeader(
            sectorSize: sectorSize,
            totalSectors: declaredSectors ?? (uint)(image.Length / sectorSize),
            passwordMode: passwordMode,
            segmentSize: 0,
            chunkCount: (uint)chunks.Count,
            chunkSize: (uint)chunkSize,
            pointerLength: pointerLength,
            segmentNumber: 1,
            chunkTableOffset: HeaderLength,
            segmentTableOffset: 0,
            dataOffset: (uint)dataOffset,
            volumeSerial: DefaultVolumeSerial
        );

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(header);
        file.Write(chunkTable);
        file.Write(data);
    }

    /// <summary>
    /// Writes a two-segment ISZ, cutting the chunk data at <paramref name="splitAfterBytes"/> so a
    /// chunk straddles the boundary when that offset falls inside one.
    /// </summary>
    /// <param name="firstPath">Path of the .isz first segment; the second becomes ".i01".</param>
    /// <param name="image">The image being stored.</param>
    /// <param name="sectorSize">Sector size to declare.</param>
    /// <param name="chunkSize">Uncompressed bytes per chunk.</param>
    /// <param name="pointerLength">Bytes per chunk table entry.</param>
    /// <param name="flagForChunk">Chosen storage method per chunk index.</param>
    /// <param name="splitAfterBytes">Bytes of chunk data to keep in the first segment.</param>
    /// <param name="secondSegmentVolumeSerial">Serial for the second segment, to test a mismatch.</param>
    /// <param name="writeSecondSegment">False to leave the second segment missing.</param>
    internal static void WriteSplit(
        string firstPath,
        byte[] image,
        int sectorSize,
        int chunkSize,
        int pointerLength,
        Func<int, int> flagForChunk,
        int splitAfterBytes,
        uint? secondSegmentVolumeSerial = null,
        bool writeSecondSegment = true
    )
    {
        var chunks = BuildChunks(image, chunkSize, pointerLength, flagForChunk);
        var chunkTable = BuildChunkTable(chunks, pointerLength);
        var data = Concat(chunks);

        const int chunkTableOffset = HeaderLength + 3 * SegmentEntryLength;
        var dataOffset = chunkTableOffset + chunkTable.Length;

        var firstData = data.AsSpan(0, splitAfterBytes).ToArray();
        var secondData = data.AsSpan(splitAfterBytes).ToArray();

        // Which chunk the cut lands in, and how much of it ends up in the second file.
        var chunksStartingInFirst = 0;
        var leftSize = 0;
        var cursor = 0;
        foreach (var chunk in chunks)
        {
            if (cursor >= splitAfterBytes)
            {
                break;
            }

            chunksStartingInFirst++;
            var end = cursor + chunk.Stored.Length;
            if (end > splitAfterBytes)
            {
                leftSize = end - splitAfterBytes;
            }

            cursor = end;
        }

        var firstLength = dataOffset + firstData.Length;
        var secondLength = HeaderLength + secondData.Length;

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
            HeaderLength,
            0
        );
        // Third entry stays zeroed: the spec terminates the table with a zero-size entry.

        var firstHeader = BuildHeader(
            sectorSize: sectorSize,
            totalSectors: (uint)(image.Length / sectorSize),
            passwordMode: 0,
            segmentSize: firstLength,
            chunkCount: (uint)chunks.Count,
            chunkSize: (uint)chunkSize,
            pointerLength: pointerLength,
            segmentNumber: 1,
            chunkTableOffset: chunkTableOffset,
            segmentTableOffset: HeaderLength,
            dataOffset: (uint)dataOffset,
            volumeSerial: DefaultVolumeSerial
        );

        using (var file = new FileStream(firstPath, FileMode.Create, FileAccess.Write))
        {
            file.Write(firstHeader);
            file.Write(segmentTable);
            file.Write(chunkTable);
            file.Write(firstData);
        }

        if (!writeSecondSegment)
        {
            return;
        }

        var secondHeader = BuildHeader(
            sectorSize: sectorSize,
            totalSectors: (uint)(image.Length / sectorSize),
            passwordMode: 0,
            segmentSize: secondLength,
            chunkCount: (uint)chunks.Count,
            chunkSize: (uint)chunkSize,
            pointerLength: pointerLength,
            segmentNumber: 2,
            chunkTableOffset: 0,
            segmentTableOffset: 0,
            dataOffset: HeaderLength,
            volumeSerial: secondSegmentVolumeSerial ?? DefaultVolumeSerial
        );

        using var second = new FileStream(
            GetSecondSegmentPath(firstPath),
            FileMode.Create,
            FileAccess.Write
        );
        second.Write(secondHeader);
        second.Write(secondData);
    }

    /// <summary>The ".i01" path beside a ".isz", which is what the spec names segment 2.</summary>
    /// <param name="firstPath">Path of the first segment.</param>
    internal static string GetSecondSegmentPath(string firstPath)
    {
        return Path.ChangeExtension(firstPath, ".i01");
    }

    /// <summary>
    /// Builds a 48-byte header with a distinct value in every field, for checking that each one is
    /// read at the offset the spec gives it.
    /// </summary>
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
        int version = 1
    )
    {
        var header = new byte[HeaderLength];

        header[0] = (byte)'I';
        header[1] = (byte)'s';
        header[2] = (byte)'Z';
        header[3] = (byte)'!';
        header[4] = (byte)headerSize;
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
                        leaveOpen: true
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
                        SharpCompress.Compressors.CompressionMode.Compress,
                        decompressConcatenated: false,
                        leaveOpen: true
                    )
                )
                {
                    bzip2.Write(plain);
                }

                return output.ToArray();
            }
        }
    }

    private static List<Chunk> BuildChunks(
        byte[] image,
        int chunkSize,
        int pointerLength,
        Func<int, int> flagForChunk
    )
    {
        var chunks = new List<Chunk>();
        var maxStored = (1 << (8 * pointerLength - 2)) - 1;

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

            if (stored.Length > maxStored)
            {
                throw new InvalidOperationException(
                    $"chunk {chunks.Count} stores {stored.Length} bytes, more than a {pointerLength}-byte pointer can express"
                );
            }

            chunks.Add(new Chunk(flag, stored));
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
                (uint)chunk.Stored.Length | ((ulong)(chunk.Flag >> 6) << (8 * pointerLength - 2));

            for (var b = 0; b < pointerLength; b++)
            {
                table[index * pointerLength + b] = (byte)(entry >> (8 * b));
            }
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
        foreach (var chunk in chunks)
        {
            buffer.Write(chunk.Stored);
        }

        return buffer.ToArray();
    }

    private sealed record Chunk(int Flag, byte[] Stored);
}
