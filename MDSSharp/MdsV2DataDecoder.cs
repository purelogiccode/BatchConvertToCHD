using System.Buffers.Binary;
using System.IO.Compression;

namespace MDSSharp;

/// <summary>
///     Turns the track data of an MDS v2 / MDX image into a plain image file that the normal
///     preparation pipeline can consume. MDS v2 images may store their track data compressed
///     (deflate with a per-track compression table) and/or encrypted (AES-256 in LRW mode), and
///     MDX images keep the data inside the container; all three are decoded here.
///     The block layout and algorithms follow the MIT-licensed mdsx project
///     (https://github.com/Marisa-Chan/mdsx) and are validated byte-for-byte against its test images.
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
        var fileBytes = File.ReadAllBytes(disc.MdsPath);
        var (descriptor, isMdx) = MdxCrypto.DecryptDescriptor(fileBytes);
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

        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024
        );

        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var track in tracks)
            {
                token.ThrowIfCancellationRequested();

                var dataPath = ResolveTrackDataFile(track, disc, isMdx);
                if (dataPath is null)
                {
                    return (
                        null,
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

                onLog?.Invoke(
                    $" Decoding track {track.Point} ({track.LengthSectors:N0} sectors at {track.SectorSize} bytes)"
                );

                DecodeTrack(stream, output, track, keyData);
            }
        }
        finally
        {
            foreach (var stream in streams.Values) stream.Dispose();
        }

        output.Flush();
        return (outputPath, null);
    }

    /// <summary>
    ///     Decodes one track's data to <paramref name="output" />, decrypting and/or decompressing
    ///     the stored blocks as the footer dictates.
    /// </summary>
    /// <param name="data">The track's data file.</param>
    /// <param name="output">The decoded image being written.</param>
    /// <param name="track">Track metadata.</param>
    /// <param name="keyData">Decrypted data-encryption key data, or null.</param>
    private static void DecodeTrack(
        FileStream data,
        FileStream output,
        TrackData track,
        byte[]? keyData
    )
    {
        var totalBytes = track.LengthSectors * track.SectorSize;
        if (totalBytes <= 0) return;

        if ((track.FooterFlags & 0x01) == 0)
        {
            var buffer = new byte[totalBytes];
            ReadExactlyAt(data, (long)track.StartOffset, buffer);
            if (keyData is not null) DecipherSectors(buffer, track, keyData);
            output.Write(buffer, 0, buffer.Length);
            return;
        }

        DecodeCompressedTrack(data, output, track, keyData);
    }

    /// <summary>
    ///     Decodes a compressed track: reads the zlib-compressed compression table, then walks the
    ///     sector groups, applying the entry's stored/RLE/deflate mode and LRW decryption.
    /// </summary>
    /// <param name="data">The track's data file.</param>
    /// <param name="output">The decoded image being written.</param>
    /// <param name="track">Track metadata.</param>
    /// <param name="keyData">Decrypted data-encryption key data, or null.</param>
    private static void DecodeCompressedTrack(
        FileStream data,
        FileStream output,
        TrackData track,
        byte[]? keyData
    )
    {
        var group = track.BlocksInCompressionGroup;
        if (group == 0)
            throw new InvalidDataException(
                $"track {track.Point} declares a compression group of zero sectors."
            );

        var entries = (int)((track.LengthSectors + group - 1) / group);
        var tableValues = ReadCompressionTable(data, track, entries);

        var outputBuffer = new byte[track.LengthSectors * track.SectorSize];
        long outputPosition = 0;
        long cumulative = 0;

        for (var index = 0; index < entries; index++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(tableValues.AsSpan(index * 2));

            var sectors = (long)group;
            if (index + 1 == entries && track.LengthSectors % group != 0)
                sectors = track.LengthSectors % group;

            var groupBytes = sectors * track.SectorSize;

            if (value == 0)
            {
                var buffer = new byte[groupBytes];
                ReadExactlyAt(data, (long)track.StartOffset + cumulative, buffer);
                if (keyData is not null) DecipherGroup(buffer, track, index, keyData);
                Array.Copy(buffer, 0, outputBuffer, outputPosition, buffer.Length);
                cumulative += groupBytes;
            }
            else if ((value & 0x8000) != 0)
            {
                Array.Fill(
                    outputBuffer,
                    (byte)(value & 0xFF),
                    (int)outputPosition,
                    (int)groupBytes
                );
            }
            else
            {
                var compressed = new byte[value];
                ReadExactlyAt(data, (long)track.StartOffset + cumulative, compressed);
                if (keyData is not null) DecipherGroup(compressed, track, index, keyData);

                using var input = new MemoryStream(compressed);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                var read = 0;
                while (read < groupBytes)
                {
                    var count = deflate.Read(
                        outputBuffer,
                        (int)(outputPosition + read),
                        (int)(groupBytes - read)
                    );
                    if (count == 0) break;

                    read += count;
                }

                if (read != groupBytes)
                {
                    throw new InvalidDataException(
                        $"track {track.Point}, compression group {index}: expected {groupBytes} bytes, got {read}."
                    );
                }

                cumulative += value;
            }

            outputPosition += groupBytes;
        }

        output.Write(outputBuffer, 0, outputBuffer.Length);
    }

    /// <summary>
    ///     Reads and inflates the per-track compression table. The table's compressed size is not
    ///     stored, so a generous amount is read and zlib is allowed to finish early.
    /// </summary>
    /// <param name="data">The track's data file.</param>
    /// <param name="track">Track metadata.</param>
    /// <param name="entries">Number of table entries.</param>
    private static byte[] ReadCompressionTable(FileStream data, TrackData track, int entries)
    {
        var expected = entries * 2;
        var toRead = expected + (CompressionTableSlack * 2);
        var position = (long)track.StartOffset + (long)track.CompressionTableOffset;

        var available = (int)Math.Min(toRead, Math.Max(0, data.Length - position));
        var compressed = new byte[available];
        ReadExactlyAt(data, position, compressed);

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
                $"track {track.Point}: the compression table is truncated ({read} of {expected} bytes)."
            );
        }

        return table;
    }

    /// <summary>Decrypts every sector of an uncompressed track with LRW.</summary>
    /// <param name="buffer">Track data, decrypted in place.</param>
    /// <param name="track">Track metadata.</param>
    /// <param name="keyData">Decrypted data-encryption key data.</param>
    private static void DecipherSectors(byte[] buffer, TrackData track, byte[] keyData)
    {
        var alignedSector = track.SectorSize & ~15;
        if (alignedSector == 0) return;

        var aesKey = keyData.AsSpan(32, 32).ToArray();
        var tweakKey = keyData.AsSpan(0, 16).ToArray();

        for (long sector = 0; sector < track.LengthSectors; sector++)
        {
            var offset = (int)(sector * track.SectorSize);
            var length = Math.Min(alignedSector, buffer.Length - offset);
            if (length <= 0) break;

            var tweakCounter = 1UL + ((ulong)sector * (ulong)(alignedSector / 16));
            var slice = new byte[length];
            Array.Copy(buffer, offset, slice, 0, length);
            MdxCrypto.DecipherLrw(aesKey, tweakKey, slice, tweakCounter);
            Array.Copy(slice, 0, buffer, offset, length);
        }
    }

    /// <summary>Decrypts one compression group with LRW, using its first sector for the counter.</summary>
    /// <param name="buffer">Group data, decrypted in place.</param>
    /// <param name="track">Track metadata.</param>
    /// <param name="groupIndex">Zero-based compression-group index.</param>
    /// <param name="keyData">Decrypted data-encryption key data.</param>
    private static void DecipherGroup(
        byte[] buffer,
        TrackData track,
        int groupIndex,
        byte[] keyData
    )
    {
        var aligned = buffer.Length & ~15;
        if (aligned == 0) return;

        var alignedSector = track.SectorSize & ~15;
        var startSector = (ulong)groupIndex * track.BlocksInCompressionGroup;
        var tweakCounter = 1UL + (startSector * (ulong)(alignedSector / 16));

        var aesKey = keyData.AsSpan(32, 32).ToArray();
        var tweakKey = keyData.AsSpan(0, 16).ToArray();
        var slice = new byte[aligned];
        Array.Copy(buffer, slice, aligned);
        MdxCrypto.DecipherLrw(aesKey, tweakKey, slice, tweakCounter);
        Array.Copy(slice, 0, buffer, 0, aligned);
    }

    /// <summary>Resolves the data file a track's footer names, or null when it cannot be found.</summary>
    /// <param name="track">Track metadata.</param>
    /// <param name="disc">The parsed image.</param>
    /// <param name="isMdx">Whether the image is a single-file MDX container.</param>
    private static string? ResolveTrackDataFile(TrackData track, MdsDisc disc, bool isMdx)
    {
        if (isMdx) return disc.MdsPath;

        if (!string.IsNullOrWhiteSpace(track.DataFileName))
        {
            var resolved = MdsParser.ResolveDeclaredFile(disc.MdsPath, track.DataFileName);
            if (resolved is not null) return resolved;
        }

        return disc.DataFilePaths.Count > 0 ? disc.DataFilePaths[0] : disc.MdfPath;
    }

    /// <summary>Reads every track's data layout out of the decrypted descriptor.</summary>
    /// <param name="descriptor">Decrypted descriptor buffer.</param>
    /// <param name="disc">The parsed image, for the medium type.</param>
    private static List<TrackData> ReadTracks(byte[] descriptor, MdsDisc disc)
    {
        var tracks = new List<TrackData>();
        var sessionCount = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(0x14));
        var sessionOffset = (long)
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0x50));

        for (var session = 0; session < sessionCount; session++)
        {
            var sessionBase = sessionOffset + (session * SessionBlockSize);
            if (sessionBase < 0 || sessionBase + SessionBlockSize > descriptor.Length) break;

            var trackCount = descriptor[sessionBase + 0x0A];
            var trackOffset = (long)
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan((int)sessionBase + 0x14));

            for (var index = 0; index < trackCount; index++)
            {
                var trackBase = trackOffset + (index * TrackBlockSize);
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
                var trackLength64 = BinaryPrimitives.ReadUInt64LittleEndian(
                    descriptor.AsSpan((int)trackBase + 0x40)
                );

                long length;
                if (disc.IsCdMedia || disc.MediumType == MdsMedium.Unknown)
                {
                    length = 0;
                    if (extraOffset != 0 && extraOffset + 8 <= descriptor.Length)
                    {
                        length = BinaryPrimitives.ReadUInt32LittleEndian(
                            descriptor.AsSpan((int)extraOffset + 4)
                        );
                    }
                }
                else
                {
                    length = (long)trackLength64;
                }

                var footerFlags = 0u;
                var group = 0u;
                ulong compressionTable = 0;
                string? dataName = null;

                if (footerOffset != 0 && footerOffset + FooterBlockSize <= descriptor.Length)
                {
                    footerFlags = descriptor[footerOffset + 4];
                    group = BinaryPrimitives.ReadUInt32LittleEndian(
                        descriptor.AsSpan((int)footerOffset + 0x0C)
                    );
                    compressionTable = BinaryPrimitives.ReadUInt64LittleEndian(
                        descriptor.AsSpan((int)footerOffset + 0x18)
                    );

                    var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                        descriptor.AsSpan((int)footerOffset)
                    );
                    if (nameOffset != 0 && nameOffset < descriptor.Length)
                        dataName = MdsParser.ReadWideName(descriptor, nameOffset);
                }

                tracks.Add(
                    new TrackData(
                        point,
                        sectorSize,
                        startOffset,
                        length,
                        footerFlags,
                        group,
                        compressionTable,
                        dataName
                    )
                );
            }
        }

        return tracks;
    }

    /// <summary>Reads exactly <paramref name="buffer" />.Length bytes at an absolute file offset.</summary>
    /// <param name="data">File to read from.</param>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="buffer">Destination buffer.</param>
    private static void ReadExactlyAt(FileStream data, long offset, byte[] buffer)
    {
        if (offset < 0 || offset + buffer.Length > data.Length)
        {
            throw new InvalidDataException(
                $"the data file is shorter than the descriptor's track layout says (needed {buffer.Length} bytes at offset {offset}, file is {data.Length} bytes)."
            );
        }

        data.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var count = data.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                throw new InvalidDataException(
                    "the data file ended while reading the track data."
                );
            }

            read += count;
        }
    }

    /// <summary>One track's data layout, as read from the descriptor.</summary>
    /// <param name="Point">Track number.</param>
    /// <param name="SectorSize">Stored bytes per sector.</param>
    /// <param name="StartOffset">Track data offset in its data file.</param>
    /// <param name="LengthSectors">Track length in sectors.</param>
    /// <param name="FooterFlags">Footer flags (bit 0 = compressed).</param>
    /// <param name="BlocksInCompressionGroup">Sectors per compression group.</param>
    /// <param name="CompressionTableOffset">Compression table offset, relative to the track data.</param>
    /// <param name="DataFileName">Data file name recorded in the footer, or null.</param>
    private sealed record TrackData(
        int Point,
        int SectorSize,
        ulong StartOffset,
        long LengthSectors,
        uint FooterFlags,
        uint BlocksInCompressionGroup,
        ulong CompressionTableOffset,
        string? DataFileName
    );
}
