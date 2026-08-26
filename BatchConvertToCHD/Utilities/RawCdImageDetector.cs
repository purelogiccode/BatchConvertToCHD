using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Identifies raw CD images by their sector layout rather than their file extension.
///
/// Raw 2352-byte-per-sector CD dumps are routinely distributed with an .iso, .img or .bin
/// extension. chdman chooses its verb from the extension, so such a file is handed to createdvd
/// (which requires a multiple of 2048) or createhd (a multiple of 512) and fails on the sector
/// arithmetic - "Data size 546,943,488 is not divisible by sector size 2048" - even though the
/// image is perfectly good. Reading the sector header settles what the file actually is, and a
/// generated cue lets it convert as the CD it is.
/// </summary>
internal static class RawCdImageDetector
{
    /// <summary>Bytes per sector in a raw CD image (2048 user bytes plus sync, header and EDC/ECC).</summary>
    internal const int RawSectorSize = 2352;

    /// <summary>
    /// The 12-byte sync pattern every raw CD sector opens with: 00 followed by ten FF and a 00.
    /// </summary>
    private static readonly byte[] SyncMark =
    [
        0x00,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0x00,
    ];

    /// <summary>Offset of the mode byte: 12 bytes of sync plus a 3-byte MSF address.</summary>
    private const int ModeOffset = 15;

    /// <summary>Extensions worth sniffing, being the ones raw CD dumps get mislabelled with.</summary>
    private static readonly HashSet<string> CandidateExtensions = new(
        [FileExtensions.Iso, FileExtensions.Img, FileExtensions.Bin],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>True when <paramref name="extension"/> is one that raw CD dumps are found under.</summary>
    /// <param name="extension">File extension including the leading dot.</param>
    internal static bool IsCandidateExtension(string extension)
    {
        return CandidateExtensions.Contains(extension);
    }

    /// <summary>
    /// Returns the cue track mode for <paramref name="imagePath"/> when it holds raw CD sectors,
    /// or null when it does not (a cooked 2048-byte image, a DVD image, or an unknown layout).
    /// </summary>
    /// <param name="imagePath">Path of the disc image to inspect.</param>
    internal static string? DetectTrackMode(string imagePath)
    {
        try
        {
            var length = new FileInfo(imagePath).Length;

            // Every raw CD image is a whole number of 2352-byte sectors. A file that is not cannot
            // be one, whatever its first bytes look like.
            if (length == 0 || length % RawSectorSize != 0)
            {
                return null;
            }

            var header = new byte[ModeOffset + 1];
            using var stream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            if (
                stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            )
            {
                return null;
            }

            if (!header.AsSpan(0, SyncMark.Length).SequenceEqual(SyncMark))
            {
                return null;
            }

            // Mode 1 is the plain data layout; Mode 2 (Form 1 or 2) carries an 8-byte subheader and
            // is what PlayStation discs use. Anything else is not a layout a cue can describe.
            return header[ModeOffset] switch
            {
                1 => BinCueGenerator.Mode1,
                2 => BinCueGenerator.Mode2,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a single-track cue for <paramref name="imagePath"/> into <paramref name="workDir"/>
    /// and returns its path, or null when the image cannot be referenced from there.
    ///
    /// The image is not copied. chdman resolves a cue's FILE entry against the cue's own directory,
    /// so the FILE line holds a path relative to <paramref name="workDir"/>. When the two are on
    /// different volumes that relative path comes back rooted, which chdman would mangle, so the
    /// caller is told to leave the image alone instead.
    /// </summary>
    /// <param name="imagePath">Path of the raw CD image.</param>
    /// <param name="trackMode">Cue track mode to declare, e.g. "MODE2/2352".</param>
    /// <param name="workDir">Existing directory the cue is written into.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<string?> TryWriteCueAsync(
        string imagePath,
        string trackMode,
        string workDir,
        CancellationToken token
    )
    {
        string relativeImagePath;
        try
        {
            relativeImagePath = Path.GetRelativePath(workDir, imagePath);
        }
        catch (ArgumentException)
        {
            // Drive-versus-UNC or partially qualified roots make GetRelativePath throw.
            return null;
        }

        if (Path.IsPathRooted(relativeImagePath))
        {
            return null;
        }

        var cuePath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(imagePath) + FileExtensions.Cue
        );
        await File.WriteAllTextAsync(
                cuePath,
                BinCueGenerator.BuildCueContent(relativeImagePath, trackMode),
                token
            )
            .ConfigureAwait(false);

        return cuePath;
    }
}