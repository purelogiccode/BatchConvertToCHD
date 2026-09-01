using System.Buffers.Binary;
using System.Text;

namespace Alcohol120Sharp;

/// <summary>
///     Reads the track table out of an Alcohol 120% .mds descriptor.
///     The layout below was recovered by inspecting real descriptors; Alcohol's format is not published.
///     Only four fields per track are needed to build a correct cue: the track number, the mode, the
///     sector size and the start LBA.
///     header    0x00  16 bytes  "MEDIA DESCRIPTOR" signature
///     0x14  u16       session count
///     0x50  u32       offset of the first session block
///     session   24 bytes each
///     0x0A  u8        number of track blocks in this session
///     0x14  u32       offset of the first track block
///     track     80 bytes each
///     0x00  u8        mode
///     0x04  u8        POINT - the track number, or a lead-in/lead-out marker
///     0x10  u16       sector size
///     0x24  u32       start LBA
/// </summary>
public static class MdsParser
{
    private const string Signature = "MEDIA DESCRIPTOR";
    private const int SignatureLength = 16;
    private const int SessionCountOffset = 0x14;
    private const int SessionBlockOffsetOffset = 0x50;
    private const int SessionBlockSize = 24;
    private const int SessionTrackCountOffset = 0x0A;
    private const int SessionTrackOffsetOffset = 0x14;
    private const int TrackBlockSize = 80;
    private const int TrackModeOffset = 0x00;
    private const int TrackPointOffset = 0x04;
    private const int TrackSectorSizeOffset = 0x10;
    private const int TrackStartLbaOffset = 0x24;

    /// <summary>An .mds is small; anything larger is not a descriptor.</summary>
    private const long MaxDescriptorBytes = 1024 * 1024;

    /// <summary>Sessions beyond this mean the bytes are not a real descriptor.</summary>
    private const int MaxPlausibleSessions = 99;

    private const string MdfExtension = ".mdf";
    private const string SplitFirstAlcoholExtension = ".i00";

    /// <summary>True when <paramref name="path" /> starts with the Alcohol descriptor signature.</summary>
    /// <param name="path">File to test.</param>
    public static bool IsMdsFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            var header = new byte[SignatureLength];
            if (
                stream.ReadAtLeast(header, header.Length, false) < header.Length
            )
                return false;

            return Encoding.ASCII.GetString(header).Equals(Signature, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Parses <paramref name="mdsPath" /> and locates its .mdf data file.
    /// </summary>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    /// <exception cref="InvalidDataException">The file is not a usable descriptor.</exception>
    public static MdsDisc Parse(string mdsPath)
    {
        var info = new FileInfo(mdsPath);
        if (!info.Exists) throw new FileNotFoundException("MDS descriptor not found.", mdsPath);

        if (info.Length > MaxDescriptorBytes)
            throw new InvalidDataException(
                $"{info.Length:N0} bytes is too large to be an MDS descriptor."
            );

        var bytes = File.ReadAllBytes(mdsPath);
        if (
            bytes.Length < SessionBlockOffsetOffset + sizeof(uint)
            || !Encoding
                .ASCII.GetString(bytes, 0, SignatureLength)
                .Equals(Signature, StringComparison.Ordinal)
        )
            throw new InvalidDataException(
                "Not an Alcohol MDS descriptor (missing \"MEDIA DESCRIPTOR\" signature)."
            );

        var sessionCount = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(SessionCountOffset)
        );

        // A corrupt or truncated descriptor produces nonsense here - one real example reported 8233
        // sessions - and walking that many offsets would just read garbage.
        if (sessionCount is 0 or > MaxPlausibleSessions)
            throw new InvalidDataException(
                $"Descriptor reports {sessionCount} sessions, so it is corrupt or truncated."
            );

        var sessionBlockOffset = (long)
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SessionBlockOffsetOffset));
        var tracks = new List<MdsTrack>();

        for (var session = 0; session < sessionCount; session++)
        {
            var sessionBase = sessionBlockOffset + (long)session * SessionBlockSize;
            if (sessionBase < 0 || sessionBase + SessionBlockSize > bytes.Length) break;

            var trackCount = bytes[sessionBase + SessionTrackCountOffset];
            var trackBlockOffset = (long)
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(sessionBase + SessionTrackOffsetOffset))
                );

            for (var track = 0; track < trackCount; track++)
            {
                var trackBase = trackBlockOffset + (long)track * TrackBlockSize;
                if (trackBase < 0 || trackBase + TrackBlockSize > bytes.Length) break;

                var point = bytes[trackBase + TrackPointOffset];

                // POINT outside 1-99 is a lead-in or lead-out descriptor, not a playable track.
                if (point is < 1 or > 99) continue;

                tracks.Add(
                    new MdsTrack(
                        point,
                        bytes[trackBase + TrackModeOffset],
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan((int)(trackBase + TrackSectorSizeOffset))
                        ),
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan((int)(trackBase + TrackStartLbaOffset))
                        )
                    )
                );
            }
        }

        if (tracks.Count == 0) throw new InvalidDataException("Descriptor contains no readable tracks.");

        return new MdsDisc(sessionCount, tracks, mdsPath, FindDataFile(mdsPath));
    }

    /// <summary>
    ///     Finds the .mdf beside <paramref name="mdsPath" />: the matching base name first, then the only
    ///     .mdf in the folder when there is exactly one.
    /// </summary>
    private static string? FindDataFile(string mdsPath)
    {
        var directory = Path.GetDirectoryName(mdsPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

        string[] candidates;
        try
        {
            candidates =
            [
                .. Directory
                    .GetFiles(directory)
                    .Where(static f =>
                        Path.GetExtension(f)
                            .Equals(MdfExtension, StringComparison.OrdinalIgnoreCase)
                    )
            ];
        }
        catch (Exception)
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(mdsPath);
        var byName = candidates.FirstOrDefault(f =>
            string.Equals(
                Path.GetFileNameWithoutExtension(f),
                baseName,
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (byName is not null) return byName;

        if (candidates.Length == 1) return candidates[0];

        // Alcohol also splits the data across ".i00", ".i01" and so on with no .mdf at all. The
        // first volume stands in for the data file; the preparer joins the set before reading it.
        try
        {
            return Directory
                .GetFiles(directory)
                .FirstOrDefault(f =>
                    Path.GetExtension(f)
                        .Equals(
                            SplitFirstAlcoholExtension,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
        }
        catch (Exception)
        {
            return null;
        }
    }
}