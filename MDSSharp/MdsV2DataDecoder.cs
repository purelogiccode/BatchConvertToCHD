using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace MDSSharp;

/// <summary>
///     Turns the track data of an MDS v2 / MDX image into a plain image file that the normal
///     preparation pipeline can consume. MDS v2 images may store their track data compressed
///     (deflate with a per-track compression table) and/or encrypted (AES-256 in LRW mode), and
///     MDX images keep the data inside the container; all three are decoded here.
///     The block layout and algorithms follow the MIT-licensed mdsx project
///     (https://github.com/Marisa-Chan/mdsx) and libMirage's image-mdx parser, and are validated
///     byte-for-byte against the mdsx test images.
/// </summary>
internal static class MdsV2DataDecoder
{
    /// <summary>Size of one v2 session block.</summary>
    private const int SessionBlockSize = 32;

    /// <summary>Size of one v2 track block.</summary>
    private const int TrackBlockSize = 80;

    /// <summary>Size of one v2 footer block.</summary>
    private const int FooterBlockSize = 32;

    /// <summary>Extra bytes read past the compression table so zlib can finish its stream.</summary>
    private const int CompressionTableSlack = 0x800;

    /// <summary>Footers considered per track; a track is never split into more files than this.</summary>
    private const int MaxFooterFiles = 64;

    /// <summary>Bytes read per chunk when copying an uncompressed fragment.</summary>
    private const int DecodeBufferBytes = 1024 * 1024;

    /// <summary>Largest compression group that is decoded at once, to bound a corrupt group size.</summary>
    private const long MaxCompressionGroupBytes = 256L * 1024 * 1024;

    /// <summary>Largest compression table that is inflated, to bound a corrupt entry count.</summary>
    private const long MaxCompressionTableBytes = 64L * 1024 * 1024;

    /// <summary>
    ///     Decodes every track of <paramref name="disc" /> into one plain image in
    ///     <paramref name="workDir" /> and returns its path.
    /// </summary>
    /// <param name="disc">The parsed image.</param>
    /// <param name="workDir">Existing directory for the decoded file.</param>
    /// <param name="password">Password for encrypted track data, or null.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The decoded file path, or a failure reason.</returns>
    internal static async Task<(string? Path, string? FailureReason)> DecodeAsync(
        MdsDisc disc,
        string workDir,
        string? password,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        try
        {
            return await Task.Run(
                    () => Decode(disc, workDir, password, onLog, token),
                    token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"the image's track data could not be decoded: {ex.Message}");
        }
    }

    /// <summary>Decodes the image synchronously; see <see cref="DecodeAsync" />.</summary>
    /// <param name="disc">The parsed image.</param>
    /// <param name="workDir">Directory for the decoded file.</param>
    /// <param name="password">Password for encrypted track data, or null.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    private static (string? Path, string? FailureReason) Decode(
        MdsDisc disc,
        string workDir,
        string? password,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        byte[] descriptor;
        bool isMdx;
        using (
            var descriptorStream = new FileStream(
                disc.MdsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                false
            )
        )
        {
            (descriptor, isMdx) = MdxCrypto.DecryptDescriptor(descriptorStream);
        }

        var keyData = MdxCrypto.DecipherDataHeader(descriptor, password);

        var outputPath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(disc.MdsPath) + ".decoded.bin"
        );

        var tracks = ReadTracks(descriptor, disc);
        if (tracks.Count == 0)
            return (null, "the descriptor contains no readable tracks.");

        if (keyData is not null)
            onLog?.Invoke(" The image's track data is encrypted; decrypting it with the supplied key.");

        try
        {
            using (var output = new FileStream(
                       outputPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024
                   ))
            {
                var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var track in tracks)
                    {
                        token.ThrowIfCancellationRequested();

                        var totalSectors = track.Fragments.Sum(static f => f.DataLengthSectors);
                        onLog?.Invoke(
                            $" Decoding track {track.Point} ({totalSectors.ToString("N0", CultureInfo.InvariantCulture)} sectors at {track.SectorSize} bytes)"
                        );

                        foreach (var fragment in track.Fragments)
                        {
                            token.ThrowIfCancellationRequested();

                            var dataPath = ResolveTrackDataFile(fragment, disc, isMdx);
                            if (dataPath is null)
                            {
                                throw new InvalidDataException(
                                    $"the data file named by track {track.Point} was not found next to the descriptor."
                                );
                            }

                            if (!streams.TryGetValue(dataPath, out var stream))
                            {
                                stream = new FileStream(
                                    dataPath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite,
                                    1024 * 1024
                                );
                                streams[dataPath] = stream;
                            }

                            DecodeFragment(
                                stream,
                                output,
                                track.Point,
                                fragment,
                                track.SectorSize,
                                keyData,
                                token
                            );
                        }
                    }
                }
                finally
                {
                    foreach (var stream in streams.Values) stream.Dispose();
                }

                output.Flush();
            }

            return (outputPath, null);
        }
        catch (InvalidDataException ex)
        {
            TryDelete(outputPath);
            return (null, ex.Message);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    /// <summary>Deletes a partially decoded output file, ignoring any failure.</summary>
    /// <param name="path">Path to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    ///     Decodes one track fragment to <paramref name="output" />, decrypting and/or decompressing
    ///     the stored blocks as the footer dictates.
    /// </summary>
    /// <param name="data">The fragment's data file.</param>
    /// <param name="output">The decoded image being written.</param>
    /// <param name="trackPoint">Track number, for messages.</param>
    /// <param name="fragment">Fragment metadata from the footer.</param>
    /// <param name="sectorSize">Stored bytes per sector.</param>
    /// <param name="keyData">Decrypted data-encryption key data, or null.</param>
    /// <param name="token">Cancellation token.</param>
    private static void DecodeFragment(
        FileStream data,
        FileStream output,
        int trackPoint,
        TrackFragment fragment,
        int sectorSize,
        byte[]? keyData,
        CancellationToken token
    )
    {
        if (fragment.DataLengthSectors <= 0) return;

        if (sectorSize <= 0)
        {
            throw new InvalidDataException(
                $"track {trackPoint} declares a sector size of zero bytes."
            );
        }

        if ((fragment.FooterFlags & 0x01) == 0)
        {
            DecodePlainFragment(data, output, fragment, sectorSize, keyData, token);
            return;
        }

        DecodeCompressedFragment(data, output, trackPoint, fragment, sectorSize, keyData, token);
    }

    /// <summary>
    ///     Copies an uncompressed fragment, decrypting every sector with LRW when the image is
    ///     encrypted. The fragment is streamed in bounded chunks so a large track never has to be
    ///     held in memory whole.
    /// </summary>
    /// <param name="data">The fragment's data file.</param>
    /// <param name="output">The decoded image being written.</param>
    /// <param name="fragment">Fragment metadata from the footer.</param>
    /// <param name="sectorSize">Stored bytes per sector.</param>
    /// <param name="keyData">Decrypted data-encryption key data, or null.</param>
    /// <param name="token">Cancellation token.</param>
    private static void DecodePlainFragment(
        FileStream data,
        FileStream output,
        TrackFragment fragment,
        int sectorSize,
        byte[]? keyData,
        CancellationToken token
    )
    {
        var sectorsPerBuffer = Math.Max(1, DecodeBufferBytes / sectorSize);
        var buffer = new byte[sectorsPerBuffer * sectorSize];

        long remaining = fragment.DataLengthSectors;
        long position = (long)fragment.StartOffset;
        long sectorIndex = 0;

        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();

            var sectors = (int)Math.Min(remaining, sectorsPerBuffer);
            var bytes = sectors * sectorSize;
            ReadExactlyAt(data, position, buffer, bytes);

            if (keyData is not null)
                DecipherSectors(buffer, bytes, sectorSize, sectorIndex, keyData);

            output.Write(buffer, 0, bytes);

            position += bytes;
            sectorIndex += sectors;
            remaining -= sectors;
        }
    }

    /// <summary>
    ///     Decodes a compressed fragment: reads the zlib-compressed compression table, then walks
    ///     the sector groups, applying the entry's stored/RLE/deflate mode and LRW decryption. Each
    ///     group is written as it is decoded, so the whole fragment is never held in memory.
    /// </summary>
    /// <param name="data">The fragment's data file.</param>
    /// <param name="output">The decoded image being written.</param>
    /// <param name="trackPoint">Track number, for messages.</param>
    /// <param name="fragment">Fragment metadata from the footer.</param>
    /// <param name="sectorSize">Stored bytes per sector.</param>
    /// <param name="keyData">Decrypted data-encryption key data, or null.</param>
    /// <param name="token">Cancellation token.</param>
    private static void DecodeCompressedFragment(
        FileStream data,
        FileStream output,
        int trackPoint,
        TrackFragment fragment,
        int sectorSize,
        byte[]? keyData,
        CancellationToken token
    )
    {
        var group = fragment.BlocksInCompressionGroup;
        if (group == 0)
        {
            throw new InvalidDataException(
                $"track {trackPoint} declares a compression group of zero sectors."
            );
        }

        var maxGroupBytes = (long)group * sectorSize;
        if (maxGroupBytes > MaxCompressionGroupBytes)
        {
            throw new InvalidDataException(
                $"track {trackPoint} declares a compression group of {group.ToString("N0", CultureInfo.InvariantCulture)} sectors, which is too large to decode."
            );
        }

        var entryCount = (fragment.DataLengthSectors + group - 1) / group;
        if (entryCount > int.MaxValue || entryCount * 2 > MaxCompressionTableBytes)
        {
            throw new InvalidDataException(
                $"track {trackPoint} declares too many compression groups to decode."
            );
        }

        var entries = (int)entryCount;
        var tableValues = ReadCompressionTable(data, fragment, entries);

        var buffer = new byte[maxGroupBytes];
        var position = (long)fragment.StartOffset;
        long cumulative = 0;

        for (var index = 0; index < entries; index++)
        {
            token.ThrowIfCancellationRequested();

            var sectors = (long)group;
            if (index + 1 == entries && fragment.DataLengthSectors % group != 0)
                sectors = fragment.DataLengthSectors % group;

            var bytes = (int)(sectors * sectorSize);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(tableValues.AsSpan(index * 2));

            if (value == 0)
            {
                ReadExactlyAt(data, position + cumulative, buffer, bytes);
                if (keyData is not null)
                    DecipherGroup(buffer, bytes, sectorSize, index, group, keyData);

                output.Write(buffer, 0, bytes);
                cumulative += bytes;
            }
            else if ((value & 0x8000) != 0)
            {
                Array.Fill(buffer, (byte)(value & 0xFF), 0, bytes);
                output.Write(buffer, 0, bytes);
            }
            else
            {
                var compressed = new byte[value];
                ReadExactlyAt(data, position + cumulative, compressed, compressed.Length);
                if (keyData is not null)
                    DecipherGroup(compressed, compressed.Length, sectorSize, index, group, keyData);

                using var input = new MemoryStream(compressed);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                var read = 0;
                while (read < bytes)
                {
                    var count = deflate.Read(buffer, read, bytes - read);
                    if (count == 0) break;

                    read += count;
                }

                if (read != bytes)
                {
                    throw new InvalidDataException(
                        $"track {trackPoint}, compression group {index}: expected {bytes} bytes, got {read}."
                    );
                }

                output.Write(buffer, 0, bytes);
                cumulative += value;
            }
        }
    }

    /// <summary>
    ///     Reads and inflates the per-fragment compression table. The table's compressed size is
    ///     not stored, so a generous amount is read and zlib is allowed to finish early.
    /// </summary>
    /// <param name="data">The fragment's data file.</param>
    /// <param name="fragment">Fragment metadata from the footer.</param>
    /// <param name="entries">Number of table entries.</param>
    private static byte[] ReadCompressionTable(
        FileStream data,
        TrackFragment fragment,
        int entries
    )
    {
        var expected = entries * 2;
        var toRead = (long)expected + CompressionTableSlack * 2L;
        var position = (long)fragment.StartOffset + (long)fragment.CompressionTableOffset;

        var available = (int)Math.Min(toRead, Math.Max(0, data.Length - position));
        var compressed = new byte[available];
        ReadExactlyAt(data, position, compressed, available);

        var table = new byte[expected];
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var read = 0;
        while (read < expected)
        {
            var count = zlib.Read(table, read, expected - read);
            if (count == 0) break;

            read += count;
        }

        if (read != expected)
        {
            throw new InvalidDataException(
                $"the compression table is truncated ({read} of {expected} bytes)."
            );
        }

        return table;
    }

    /// <summary>Decrypts every sector of an uncompressed fragment with LRW.</summary>
    /// <param name="buffer">Fragment data, decrypted in place.</param>
    /// <param name="count">Number of valid bytes in the buffer.</param>
    /// <param name="sectorSize">Stored bytes per sector.</param>
    /// <param name="startSectorIndex">Fragment-relative index of the first sector in the buffer.</param>
    /// <param name="keyData">Decrypted data-encryption key data.</param>
    private static void DecipherSectors(
        byte[] buffer,
        int count,
        int sectorSize,
        long startSectorIndex,
        byte[] keyData
    )
    {
        var alignedSector = sectorSize & ~15;
        if (alignedSector == 0) return;

        var aesKey = keyData.AsSpan(32, 32).ToArray();
        var tweakKey = keyData.AsSpan(0, 16).ToArray();

        var sectors = count / sectorSize;
        for (var sector = 0; sector < sectors; sector++)
        {
            var offset = (int)((long)sector * sectorSize);
            var length = Math.Min(alignedSector, count - offset);
            if (length <= 0) break;

            var tweakCounter =
                1UL + (ulong)(startSectorIndex + sector) * (ulong)(alignedSector / 16);
            var slice = new byte[length];
            Array.Copy(buffer, offset, slice, 0, length);
            MdxCrypto.DecipherLrw(aesKey, tweakKey, slice, tweakCounter);
            Array.Copy(slice, 0, buffer, offset, length);
        }
    }

    /// <summary>Decrypts one compression group with LRW, using its first sector for the counter.</summary>
    /// <param name="buffer">Group data, decrypted in place.</param>
    /// <param name="count">Number of valid bytes in the buffer.</param>
    /// <param name="sectorSize">Stored bytes per sector.</param>
    /// <param name="groupIndex">Zero-based compression-group index within the fragment.</param>
    /// <param name="group">Sectors per compression group.</param>
    /// <param name="keyData">Decrypted data-encryption key data.</param>
    private static void DecipherGroup(
        byte[] buffer,
        int count,
        int sectorSize,
        int groupIndex,
        uint group,
        byte[] keyData
    )
    {
        var aligned = count & ~15;
        if (aligned == 0) return;

        var alignedSector = sectorSize & ~15;
        var startSector = (ulong)groupIndex * group;
        var tweakCounter = 1UL + startSector * (ulong)(alignedSector / 16);

        var aesKey = keyData.AsSpan(32, 32).ToArray();
        var tweakKey = keyData.AsSpan(0, 16).ToArray();
        var slice = new byte[aligned];
        Array.Copy(buffer, slice, aligned);
        MdxCrypto.DecipherLrw(aesKey, tweakKey, slice, tweakCounter);
        Array.Copy(slice, 0, buffer, 0, aligned);
    }

    /// <summary>Resolves the data file a fragment's footer names, or null when it cannot be found.</summary>
    /// <param name="fragment">Fragment metadata from the footer.</param>
    /// <param name="disc">The parsed image.</param>
    /// <param name="isMdx">Whether the image is a single-file MDX container.</param>
    private static string? ResolveTrackDataFile(TrackFragment fragment, MdsDisc disc, bool isMdx)
    {
        if (isMdx) return disc.MdsPath;

        if (!string.IsNullOrWhiteSpace(fragment.DataFileName))
        {
            var resolved = MdsParser.ResolveDeclaredFile(disc.MdsPath, fragment.DataFileName);
            if (resolved is not null) return resolved;
        }

        // Only the first fragment may fall back to the descriptor's data file. A later fragment
        // stored elsewhere (its offset is file-relative and starts at 0) would otherwise read the
        // wrong sectors from the first file; report it so the caller fails with a clear message.
        if (!fragment.IsFirstFragment) return null;

        return disc.DataFilePaths.Count > 0 ? disc.DataFilePaths[0] : disc.MdfPath;
    }

    /// <summary>
    ///     Reads every track's data layout out of the decrypted descriptor. A track may be split
    ///     across several data files, one footer block each, and the footer's
    ///     <c>track_data_length</c> is the number of sectors actually stored (the extra block's
    ///     length is only the logical length and may exclude a pregap kept in the file).
    /// </summary>
    /// <param name="descriptor">Decrypted descriptor buffer.</param>
    /// <param name="disc">The parsed image, for the medium type.</param>
    private static List<TrackData> ReadTracks(byte[] descriptor, MdsDisc disc)
    {
        var tracks = new List<TrackData>();
        if (descriptor.Length < 0x54) return tracks;

        var sessionCount = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(0x14));
        var sessionOffset = (long)
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0x50));

        for (var session = 0; session < sessionCount; session++)
        {
            var sessionBase = sessionOffset + session * SessionBlockSize;
            if (sessionBase < 0 || sessionBase + SessionBlockSize > descriptor.Length) break;

            var trackCount = descriptor[sessionBase + 0x0A];
            var trackOffset = (long)
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan((int)sessionBase + 0x14));

            for (var index = 0; index < trackCount; index++)
            {
                var trackBase = trackOffset + index * TrackBlockSize;
                if (trackBase < 0 || trackBase + TrackBlockSize > descriptor.Length) break;

                var sectorType = (byte)(descriptor[trackBase] & 0x07);
                if (sectorType == 0) continue;

                var point = descriptor[trackBase + 4];
                if (point is < 1 or > 99) continue;

                var extraOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x0C)
                );
                var sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x10)
                );
                var startOffset = BinaryPrimitives.ReadUInt64LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x28)
                );
                var footerOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x34)
                );
                var footerCount = BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x30)
                );
                var trackLength64 = BinaryPrimitives.ReadUInt64LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x40)
                );

                long logicalLength;
                if (disc.IsCdMedia || disc.MediumType == MdsMedium.Unknown)
                {
                    logicalLength = 0;
                    if (extraOffset != 0 && extraOffset + 8 <= descriptor.Length)
                    {
                        logicalLength = BinaryPrimitives.ReadUInt32LittleEndian(
                            descriptor.AsSpan((int)extraOffset + 4)
                        );
                    }
                }
                else
                {
                    logicalLength = (long)trackLength64;
                }

                var fragments = new List<TrackFragment>();
                if (footerOffset != 0)
                {
                    // mdsx, the format reference, ignores footer_count and always reads footer 0;
                    // some producers leave the count at zero even though a footer exists. Never
                    // synthesize an uncompressed fragment for a track that actually has a footer.
                    var count = (int)Math.Min(Math.Max(footerCount, 1u), MaxFooterFiles);
                    for (var footerIndex = 0; footerIndex < count; footerIndex++)
                    {
                        var footerBase = (long)footerOffset + (long)footerIndex * FooterBlockSize;
                        if (footerBase < 0 || footerBase + FooterBlockSize > descriptor.Length) break;

                        var flags = descriptor[footerBase + 4];
                        var group = BinaryPrimitives.ReadUInt32LittleEndian(
                            descriptor.AsSpan((int)footerBase + 0x0C)
                        );
                        var dataLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(
                            descriptor.AsSpan((int)footerBase + 0x10)
                        );
                        var compressionTable = BinaryPrimitives.ReadUInt64LittleEndian(
                            descriptor.AsSpan((int)footerBase + 0x18)
                        );

                        var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                            descriptor.AsSpan((int)footerBase)
                        );
                        var dataName =
                            nameOffset != 0 && nameOffset < descriptor.Length
                                ? MdsParser.ReadWideName(descriptor, nameOffset)
                                : null;

                        // The footer is authoritative; fall back to the logical length when a
                        // producer left it at zero.
                        if (dataLength <= 0)
                            dataLength = footerIndex == 0 ? logicalLength : 0;

                        if (dataLength <= 0) continue;

                        fragments.Add(
                            new TrackFragment(
                                footerIndex == 0 ? startOffset : 0,
                                dataLength,
                                flags,
                                group,
                                compressionTable,
                                dataName,
                                footerIndex == 0
                            )
                        );
                    }
                }

                if (fragments.Count == 0 && logicalLength > 0)
                {
                    fragments.Add(
                        new TrackFragment(startOffset, logicalLength, 0, 0, 0, null, true)
                    );
                }

                if (fragments.Count == 0) continue;

                tracks.Add(new TrackData(point, sectorSize, fragments));
            }
        }

        return tracks;
    }

    /// <summary>Reads exactly <paramref name="count" /> bytes at an absolute file offset.</summary>
    /// <param name="data">File to read from.</param>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="count">Number of bytes to read.</param>
    private static void ReadExactlyAt(FileStream data, long offset, byte[] buffer, int count)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
        {
            throw new InvalidDataException(
                $"the data file is shorter than the descriptor's track layout says (needed {count} bytes at offset {offset}, file is {data.Length} bytes)."
            );
        }

        data.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var bytes = data.Read(buffer, read, count - read);
            if (bytes == 0)
            {
                throw new InvalidDataException(
                    "the data file ended while reading the track data."
                );
            }

            read += bytes;
        }
    }

    /// <summary>One track's data layout, as read from the descriptor.</summary>
    /// <param name="Point">Track number.</param>
    /// <param name="SectorSize">Stored bytes per sector.</param>
    /// <param name="Fragments">Data fragments, in file order.</param>
    private sealed record TrackData(int Point, int SectorSize, List<TrackFragment> Fragments);

    /// <summary>
    ///     One data fragment of a track: one footer block, or a synthetic fragment when the track
    ///     has no footer.
    /// </summary>
    /// <param name="StartOffset">Offset of the fragment data in its file (0 for later fragments).</param>
    /// <param name="DataLengthSectors">Sectors stored by this fragment, from the footer.</param>
    /// <param name="FooterFlags">Footer flags (bit 0 = compressed).</param>
    /// <param name="BlocksInCompressionGroup">Sectors per compression group.</param>
    /// <param name="CompressionTableOffset">Compression table offset, relative to the fragment data.</param>
    /// <param name="DataFileName">Data file name recorded in the footer, or null.</param>
    /// <param name="IsFirstFragment">Whether this is the track's first fragment.</param>
    private sealed record TrackFragment(
        ulong StartOffset,
        long DataLengthSectors,
        uint FooterFlags,
        uint BlocksInCompressionGroup,
        ulong CompressionTableOffset,
        string? DataFileName,
        bool IsFirstFragment
    );
}
