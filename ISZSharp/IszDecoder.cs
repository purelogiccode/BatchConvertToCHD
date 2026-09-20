using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;

namespace ISZSharp;

/// <summary>
///     Decompresses UltraISO ISZ images back to the plain image they were made from.
///     An ISZ is an image cut into fixed-size chunks, each stored raw, zlib-deflated, bzip2-compressed
///     or elided when it is all zeros, with a table at the front saying which is which. chdman cannot
///     read one, so the image has to be restored first. Both compressors are already available - zlib
///     through <see cref="ZLibStream" /> and bzip2 through SharpCompress - so this needs no external
///     tool, unlike ECM.
///     Written against EZB Systems' ISZ File Format Specification 1.00 and checked against the two
///     independent readers, libMirage's ISZ filter and isz-tool, which is where the behaviours the
///     specification omits come from: the segment and chunk tables are stored obfuscated with a
///     repeating XOR (<see cref="IszObfuscation" />), bzip2 chunks are stored without their "BZh"
///     stream header, a zero pointer offset means no chunk table at all, and split images may use the
///     ".part01.isz"/".part001.isz" naming as well as the specification's ".i01". When the 64-byte
///     UltraISO header carries a checksum it is validated, and a mismatch is a failure rather than a
///     warning. Anything the format leaves undefined, or that does not add up on the way through, is
///     reported rather than guessed: a half-decompressed image that chdman happily accepts is the one
///     outcome worth avoiding, so a failed decode deletes its partial output.
/// </summary>
public static class IszDecoder
{
    /// <summary>Read and write buffer for the output image.</summary>
    private const int FileBufferBytes = 1024 * 1024;

    /// <summary>The spec caps a split image at 99 segments.</summary>
    private const int MaxSegments = 99;

    /// <summary>How often decoding progress is logged, as a fraction of the image.</summary>
    private const int ProgressStepPercent = 10;

    private const string IszExtension = ".isz";
    private const string IsoExtension = ".iso";

    /// <summary>
    ///     Reads the header of an ISZ file, or returns null when the file does not start with one.
    /// </summary>
    /// <param name="path">Path of the .isz file.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<IszHeader?> TryReadHeaderAsync(string path, CancellationToken token)
    {
        try
        {
            var buffer = new byte[IszHeader.ExtendedLength];
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            var read = await stream
                .ReadAtLeastAsync(buffer, IszHeader.Length, false, token)
                .ConfigureAwait(false);

            return IszHeader.TryRead(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The name the decompressed image should be given.</summary>
    /// <param name="iszPath">Path of the .isz file.</param>
    public static string GetDecodedFileName(string iszPath)
    {
        return Path.GetFileNameWithoutExtension(iszPath) + IsoExtension;
    }

    /// <summary>
    ///     Returns the path of segment <paramref name="segmentIndex" /> of a split image, counting the
    ///     .isz itself as segment 0. The later segments follow the naming scheme the first segment
    ///     already uses: the specification's "game.isz"/"game.i01", or the ".part01.isz" and
    ///     ".part001.isz" forms UltraISO can also write and both reference readers accept.
    /// </summary>
    /// <param name="firstSegmentPath">Path of the .isz file.</param>
    /// <param name="segmentIndex">Zero-based segment index.</param>
    public static string GetSegmentPath(string firstSegmentPath, int segmentIndex)
    {
        if (segmentIndex <= 0) return firstSegmentPath;

        if (TryGetPartScheme(firstSegmentPath, out var prefix, out var digits))
        {
            var number = (segmentIndex + 1).ToString(
                "D" + digits.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture
            );

            return prefix + ".part" + number + IszExtension;
        }

        var extension = Path.GetExtension(firstSegmentPath);
        var stem = firstSegmentPath[..^extension.Length];

        return stem + ".i" + segmentIndex.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Decompresses <paramref name="iszPath" /> to <paramref name="destinationPath" />.
    /// </summary>
    /// <param name="iszPath">Path of the .isz file, the first segment when the image is split.</param>
    /// <param name="destinationPath">File to write the restored image to.</param>
    /// <param name="onLog">Log callback.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<IszDecodeResult> DecodeAsync(
        string iszPath,
        string destinationPath,
        Action<string> onLog,
        CancellationToken token
    )
    {
        IszHeader header;
        byte[]? chunkTable;
        List<DataRegion> regions;

        try
        {
            var readHeader = await TryReadHeaderAsync(iszPath, token).ConfigureAwait(false);
            if (readHeader is null)
            {
                return IszDecodeResult.Failed(
                    "the file does not start with an ISZ header, so it is not an ISZ image."
                );
            }

            header = readHeader;

            var unusable = header.GetUnusableReason();
            if (unusable is not null) return IszDecodeResult.Failed(unusable);

            onLog($" ISZ header: {header.Summary}.");

            await using var first = new FileStream(
                iszPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );

            var segments = header.IsSegmented
                ? await ReadSegmentTableAsync(first, header, token).ConfigureAwait(false)
                : [];

            chunkTable = header.ChunkTableOffset == 0
                ? null
                : await ReadChunkTableAsync(first, header, token).ConfigureAwait(false);

            var (built, regionFailure) = BuildRegions(iszPath, header, segments, first.Length);
            if (regionFailure is not null) return IszDecodeResult.Failed(regionFailure);

            regions = built;

            if (segments.Count > 1)
            {
                onLog(
                    $" The image is split across {segments.Count.ToString(CultureInfo.InvariantCulture)} segments; reading them in order."
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return IszDecodeResult.Failed(
                $"the ISZ header or chunk table could not be read: {ex.Message}"
            );
        }

        try
        {
            var crc = header.UncompressedCrc is null ? null : new IszCrc32();

            var written = await WriteImageAsync(
                    header,
                    chunkTable,
                    regions,
                    destinationPath,
                    crc,
                    onLog,
                    token
                )
                .ConfigureAwait(false);
            var expected = header.ImageSizeBytes;

            if (written != expected)
            {
                TryDeleteFile(destinationPath);

                return IszDecodeResult.Failed(
                    $"the ISZ decompressed to {written.ToString("N0", CultureInfo.InvariantCulture)} bytes but its header declares {expected.ToString("N0", CultureInfo.InvariantCulture)}. The file is truncated or a segment is missing, so the partial image has been deleted."
                );
            }

            if (header.UncompressedCrc is { } declaredCrc)
            {
                var actualCrc = crc!.Finish;
                if (actualCrc != declaredCrc)
                {
                    TryDeleteFile(destinationPath);

                    return IszDecodeResult.Failed(
                        $"the restored image does not match the checksum the ISZ header declares (expected {declaredCrc.ToString("X8", CultureInfo.InvariantCulture)}, computed {actualCrc.ToString("X8", CultureInfo.InvariantCulture)}), so the image is damaged and has been deleted."
                    );
                }
            }

            return IszDecodeResult.Succeeded(destinationPath, header.SectorSize);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(destinationPath);
            throw;
        }
        catch (InvalidDataException ex)
        {
            TryDeleteFile(destinationPath);

            return IszDecodeResult.Failed(
                $"the compressed data inside the ISZ is damaged: {ex.Message}"
            );
        }
        catch (Exception ex)
        {
            TryDeleteFile(destinationPath);

            return IszDecodeResult.Failed($"the ISZ could not be decompressed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Deletes a partial output, ignoring a failure to do so. A half-written image is worse than
    ///     none, so every failure path after writing has started removes the file; a file that cannot
    ///     be deleted is left to the caller's temp cleanup.
    /// </summary>
    /// <param name="path">File to delete.</param>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            /* left behind; the caller deletes its temp folder */
        }
        catch (UnauthorizedAccessException)
        {
            /* left behind; the caller deletes its temp folder */
        }
    }

    /// <summary>
    ///     Matches the ".partNN.isz" / ".partNNN.isz" naming schemes. Returns false when the first
    ///     segment uses another name, which is then treated as the specification's ".isz" scheme.
    /// </summary>
    /// <param name="firstSegmentPath">Path of the first segment.</param>
    /// <param name="prefix">The name before ".part", including its folder.</param>
    /// <param name="digits">Digits the writer used for the part number, 2 or 3.</param>
    private static bool TryGetPartScheme(string firstSegmentPath, out string prefix, out int digits)
    {
        prefix = string.Empty;
        digits = 0;

        var name = Path.GetFileName(firstSegmentPath);
        if (!name.EndsWith(IszExtension, StringComparison.OrdinalIgnoreCase)) return false;

        var beforeExtension = name[..^IszExtension.Length];
        var dot = beforeExtension.LastIndexOf('.');
        if (dot <= 0) return false;

        var part = beforeExtension[(dot + 1)..];
        if (!part.StartsWith("part", StringComparison.OrdinalIgnoreCase)) return false;

        var number = part[4..];
        if (number.Length is < 2 or > 3 || !number.All(char.IsAsciiDigit)) return false;

        digits = number.Length;

        var folder = firstSegmentPath[..^name.Length];
        prefix = folder + beforeExtension[..dot];

        return true;
    }

    /// <summary>
    ///     Reads the segment definition table, which the spec places immediately after the header and
    ///     terminates with a zero-size entry. Each entry is stored obfuscated and is de-obfuscated as
    ///     it is read.
    /// </summary>
    private static async Task<List<IszSegment>> ReadSegmentTableAsync(
        FileStream stream,
        IszHeader header,
        CancellationToken token
    )
    {
        var segments = new List<IszSegment>();
        var entry = new byte[IszSegment.EntryLength];

        stream.Position = header.SegmentTableOffset;

        for (var index = 0; index <= MaxSegments; index++)
        {
            var read = await stream
                .ReadAtLeastAsync(entry, entry.Length, false, token)
                .ConfigureAwait(false);
            if (read < entry.Length) break;

            IszObfuscation.Apply(entry);

            var segment = new IszSegment(
                BinaryPrimitives.ReadInt64LittleEndian(entry),
                BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(8)),
                BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(12)),
                BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(16)),
                BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(20))
            );

            if (segment.IsTerminator) break;

            segments.Add(segment);
        }

        return segments;
    }

    /// <summary>
    ///     Reads the chunk definition table whole and de-obfuscates it. It is one entry of
    ///     <c>PointerLength</c> bytes per chunk, so even a large image's table is a few hundred
    ///     kilobytes.
    /// </summary>
    private static async Task<byte[]> ReadChunkTableAsync(
        FileStream stream,
        IszHeader header,
        CancellationToken token
    )
    {
        var tableBytes = checked((int)(header.ChunkCount * (uint)header.PointerLength));
        var table = new byte[tableBytes];

        stream.Position = header.ChunkTableOffset;
        var read = await stream
            .ReadAtLeastAsync(table, tableBytes, false, token)
            .ConfigureAwait(false);
        if (read < tableBytes)
        {
            throw new InvalidDataException(
                $"the chunk table is {read.ToString("N0", CultureInfo.InvariantCulture)} of an expected {tableBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, so the file is truncated"
            );
        }

        IszObfuscation.Apply(table);

        return table;
    }

    /// <summary>
    ///     Works out where chunk data lives. Chunks are stored back to back and a single chunk may
    ///     straddle a segment boundary, so the data is treated as one logical stream made of a region
    ///     per file rather than as separate per-segment sequences.
    /// </summary>
    private static (List<DataRegion> Regions, string? Failure) BuildRegions(
        string iszPath,
        IszHeader header,
        List<IszSegment> segments,
        long firstSegmentLength
    )
    {
        if (segments.Count == 0)
        {
            if (header.DataOffset >= firstSegmentLength)
                return ([], "the ISZ header points past the end of the file, so it is truncated.");

            return (
                [
                    new DataRegion(
                        iszPath,
                        header.DataOffset,
                        firstSegmentLength - header.DataOffset
                    )
                ],
                null
            );
        }

        var regions = new List<DataRegion>(segments.Count);

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var path = GetSegmentPath(iszPath, index);

            long actualLength;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return (
                        [],
                        $"the image is split across {segments.Count.ToString(CultureInfo.InvariantCulture)} segments and {Path.GetFileName(path)} is not in the same folder. Put every segment together and try again."
                    );
                }

                actualLength = info.Length;
            }
            catch (Exception ex)
            {
                return ([], $"segment {Path.GetFileName(path)} could not be read: {ex.Message}");
            }

            if (index > 0)
            {
                var mismatch = VerifySegmentBelongs(path, header);
                if (mismatch is not null) return ([], mismatch);
            }

            // The declared size is what the writer intended; the file on disk is what there is.
            // Taking the smaller means a short segment shows up as a shortfall in the final size
            // check, with a message about a truncated download, instead of as a read past the end.
            var usableLength =
                segment.Size > 0 ? Math.Min(actualLength, segment.Size) : actualLength;
            var start =
                index == 0 && segment.ChunkOffset == 0
                    ? header.DataOffset
                    : (uint)segment.ChunkOffset;

            if (start < usableLength) regions.Add(new DataRegion(path, start, usableLength - start));
        }

        return regions.Count > 0
            ? (regions, null)
            : ([], "the ISZ segment table describes no data, so the file header is damaged.");
    }

    /// <summary>
    ///     Confirms a later segment carries the same volume serial number as the first, which is the
    ///     only thing distinguishing the right ".i01" from one belonging to another image.
    /// </summary>
    private static string? VerifySegmentBelongs(string segmentPath, IszHeader header)
    {
        try
        {
            var buffer = new byte[IszHeader.ExtendedLength];
            using var stream = new FileStream(
                segmentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            var read = stream.ReadAtLeast(buffer, IszHeader.Length, false);
            var segmentHeader = IszHeader.TryRead(buffer.AsSpan(0, read));

            if (segmentHeader is null)
            {
                return
                    $"segment {Path.GetFileName(segmentPath)} does not start with an ISZ header, so it is not part of this image.";
            }

            if (segmentHeader.VolumeSerialNumber != header.VolumeSerialNumber)
            {
                return
                    $"segment {Path.GetFileName(segmentPath)} belongs to a different ISZ image (volume serial number does not match). Collect the segments of one image together and try again.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"segment {Path.GetFileName(segmentPath)} could not be read: {ex.Message}";
        }
    }

    /// <summary>
    ///     Walks the chunk table, decompresses each chunk and writes the image out. Returns the bytes
    ///     written, which the caller checks against the size the header declared. A null chunk table
    ///     means the header declared none: the data is then one uncompressed run, which is split into
    ///     whole chunks for the reading loop.
    /// </summary>
    private static async Task<long> WriteImageAsync(
        IszHeader header,
        byte[]? chunkTable,
        List<DataRegion> regions,
        string destinationPath,
        IszCrc32? crc,
        Action<string> onLog,
        CancellationToken token
    )
    {
        var chunkSize = (int)header.ChunkSize;
        var expected = header.ImageSizeBytes;
        var chunkCount = chunkTable is null ? ImplicitChunkCount(header) : header.ChunkCount;

        var compressed = new byte[chunkSize];
        var plain = new byte[chunkSize];
        byte[]? zeros = null;

        await using var reader = new SequentialReader(regions);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileBufferBytes,
            true
        );

        long written = 0;
        var nextProgressPercent = ProgressStepPercent;

        for (uint index = 0; index < chunkCount && written < expected; index++)
        {
            token.ThrowIfCancellationRequested();

            var (type, storedLength) = chunkTable is null
                ? (IszChunkType.Stored, (int)Math.Min(chunkSize, expected - written))
                : ReadChunkEntry(chunkTable, (int)index, header.PointerLength);
            if (storedLength > chunkSize)
            {
                throw new InvalidDataException(
                    $"chunk {index.ToString("N0", CultureInfo.InvariantCulture)} declares {storedLength.ToString("N0", CultureInfo.InvariantCulture)} stored bytes, more than the {chunkSize.ToString("N0", CultureInfo.InvariantCulture)}-byte chunk size allows"
                );
            }

            int produced;
            if (type == IszChunkType.Zero)
            {
                // A zero chunk stores no bytes; UltraISO records the uncompressed length in the entry
                // so that a short final chunk comes back the right length. Zero means a whole chunk.
                zeros ??= new byte[chunkSize];
                produced = storedLength > 0 ? storedLength : chunkSize;
                await WriteCappedAsync(output, zeros, produced, expected, written, crc, token)
                    .ConfigureAwait(false);
            }
            else
            {
                if (
                    storedLength > 0
                    && !await reader
                        .ReadExactlyAsync(compressed, storedLength, token)
                        .ConfigureAwait(false)
                )
                {
                    break;
                }

                produced = type switch
                {
                    IszChunkType.Stored => CopyStored(compressed, storedLength, plain),
                    IszChunkType.ZLib => await InflateAsync(
                            compressed,
                            storedLength,
                            plain,
                            index,
                            token
                        )
                        .ConfigureAwait(false),
                    _ => await UnBzip2Async(compressed, storedLength, plain, index, token)
                        .ConfigureAwait(false)
                };

                await WriteCappedAsync(output, plain, produced, expected, written, crc, token)
                    .ConfigureAwait(false);
            }

            written += Math.Min(produced, expected - written);

            var percent = (int)(written * 100 / expected);
            if (percent >= nextProgressPercent)
            {
                onLog(
                    $" Decompressed {percent.ToString(CultureInfo.InvariantCulture)}% of the ISZ image."
                );
                nextProgressPercent = percent - (percent % ProgressStepPercent) + ProgressStepPercent;
            }
        }

        await output.FlushAsync(token).ConfigureAwait(false);

        return written;
    }

    /// <summary>
    ///     How many chunks an image with no chunk table holds: the count the header declares, or one
    ///     whole-image chunk when it declares none.
    /// </summary>
    /// <param name="header">Header that declared no chunk table.</param>
    private static uint ImplicitChunkCount(IszHeader header)
    {
        if (header.ChunkCount != 0) return header.ChunkCount;

        return (uint)((header.ImageSizeBytes + header.ChunkSize - 1) / header.ChunkSize);
    }

    /// <summary>
    ///     Writes a decompressed chunk, stopping at the image size the header declared so a writer
    ///     that padded its last chunk does not lengthen the image, and adds the bytes actually written
    ///     to the checksum when one is being computed.
    /// </summary>
    private static async Task WriteCappedAsync(
        FileStream output,
        byte[] buffer,
        int produced,
        long expected,
        long written,
        IszCrc32? crc,
        CancellationToken token
    )
    {
        var room = expected - written;
        var count = (int)Math.Min(produced, room);
        if (count <= 0) return;

        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        crc?.Append(buffer.AsSpan(0, count));
    }

    /// <summary>
    ///     Splits one chunk table entry into its type and stored length. The entry is a little-endian
    ///     integer <paramref name="pointerLength" /> bytes wide whose top two bits are the type.
    /// </summary>
    /// <param name="chunkTable">The whole chunk table, already de-obfuscated.</param>
    /// <param name="index">Zero-based chunk index.</param>
    /// <param name="pointerLength">Bytes per entry.</param>
    public static (IszChunkType Type, int StoredLength) ReadChunkEntry(
        byte[] chunkTable,
        int index,
        int pointerLength
    )
    {
        var offset = index * pointerLength;

        ulong raw = 0;
        for (var i = 0; i < pointerLength; i++) raw |= (ulong)chunkTable[offset + i] << (8 * i);

        var typeShift = (8 * pointerLength) - 2;
        var type = (IszChunkType)(int)((raw >> typeShift) & 0x03);
        var length = (int)(raw & ((1UL << typeShift) - 1));

        return (type, length);
    }

    /// <summary>Copies a stored chunk, which is already the image's bytes.</summary>
    private static int CopyStored(byte[] compressed, int storedLength, byte[] plain)
    {
        compressed.AsSpan(0, storedLength).CopyTo(plain);

        return storedLength;
    }

    /// <summary>Inflates a zlib-wrapped deflate chunk.</summary>
    private static async Task<int> InflateAsync(
        byte[] compressed,
        int storedLength,
        byte[] plain,
        uint index,
        CancellationToken token
    )
    {
        await using var input = new MemoryStream(compressed, 0, storedLength, false);
        await using var inflate = new ZLibStream(input, CompressionMode.Decompress);

        return await FillAsync(inflate, plain, index, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Decompresses a bzip2 chunk. UltraISO stores these without the three-byte "BZh" stream
    ///     header, so it is put back first, exactly as both reference readers do.
    /// </summary>
    private static async Task<int> UnBzip2Async(
        byte[] compressed,
        int storedLength,
        byte[] plain,
        uint index,
        CancellationToken token
    )
    {
        if (storedLength >= 3)
        {
            compressed[0] = (byte)'B';
            compressed[1] = (byte)'Z';
            compressed[2] = (byte)'h';
        }

        await using var input = new MemoryStream(compressed, 0, storedLength, false);
        await using var bzip2 = await BZip2Stream.CreateAsync(
            input,
            SharpCompress.Compressors.CompressionMode.Decompress,
            false,
            cancellationToken: token
        );

        return await FillAsync(bzip2, plain, index, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads a decompressed chunk into <paramref name="plain" />. A chunk that does not fit is a
    ///     corrupt table rather than a large chunk - the spec caps the decompressed size at the chunk
    ///     size - and it has to be an error, because silently keeping the first part of it would put a
    ///     gap in the middle of the image that still converts.
    /// </summary>
    private static async Task<int> FillAsync(
        Stream source,
        byte[] plain,
        uint index,
        CancellationToken token
    )
    {
        var produced = await source
            .ReadAtLeastAsync(plain, plain.Length, false, token)
            .ConfigureAwait(false);
        if (produced < plain.Length) return produced;

        var overflow = new byte[1];
        if (await source.ReadAsync(overflow, token).ConfigureAwait(false) > 0)
        {
            throw new InvalidDataException(
                $"chunk {index.ToString("N0", CultureInfo.InvariantCulture)} decompresses to more than the chunk size the header declares"
            );
        }

        return produced;
    }

    /// <summary>One file's worth of chunk data.</summary>
    /// <param name="Path">File holding it.</param>
    /// <param name="Start">Offset the data starts at.</param>
    /// <param name="Length">Bytes of data in this file.</param>
    private sealed record DataRegion(string Path, long Start, long Length);

    /// <summary>
    ///     Reads the chunk data as one continuous stream over a list of regions, so a chunk split
    ///     across two segment files is read without the caller knowing about the boundary.
    /// </summary>
    private sealed class SequentialReader(IReadOnlyList<DataRegion> regions) : IAsyncDisposable
    {
        private readonly IReadOnlyList<DataRegion> _regions = regions;
        private int _index = -1;
        private long _remainingInRegion;
        private FileStream? _stream;

        public async ValueTask DisposeAsync()
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
        }

        /// <summary>
        ///     Fills the first <paramref name="count" /> bytes of <paramref name="buffer" />, crossing
        ///     into later regions as needed. False means the data ran out first.
        /// </summary>
        public async Task<bool> ReadExactlyAsync(
            byte[] buffer,
            int count,
            CancellationToken token
        )
        {
            var filled = 0;
            while (filled < count)
            {
                if (_stream is null || _remainingInRegion <= 0)
                {
                    if (!await MoveNextAsync().ConfigureAwait(false)) return false;

                    continue;
                }

                var want = (int)Math.Min(count - filled, _remainingInRegion);
                var read = await _stream
                    .ReadAsync(buffer.AsMemory(filled, want), token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    // The region was shorter than the table claimed; carry on with the next file.
                    _remainingInRegion = 0;
                    continue;
                }

                filled += read;
                _remainingInRegion -= read;
            }

            return true;
        }

        private async Task<bool> MoveNextAsync()
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }

            while (++_index < _regions.Count)
            {
                var region = _regions[_index];
                if (region.Length <= 0) continue;

                _stream = new FileStream(
                    region.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    FileBufferBytes,
                    true
                )
                {
                    Position = region.Start
                };
                _remainingInRegion = region.Length;

                return true;
            }

            return false;
        }
    }
}