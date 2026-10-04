using System.Globalization;
using System.Text;
using CHDSharp;
using CHDSharp.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Writes the checksum report produced after a successful verification: the whole-image
///     SHA-1, CRC-32 and XXH3 plus per-track SHA-1, CRC-32 and XXH3 for CD/GD-ROM images.
/// </summary>
internal static class ChdChecksumReport
{
    /// <summary>Hash algorithms written into the report (computed in one decompression pass).</summary>
    private const ChdHashType ReportHashTypes =
        ChdHashType.Sha1 | ChdHashType.Crc32 | ChdHashType.Xxh3;

    /// <summary>Extension appended to the CHD name for the report file.</summary>
    internal const string ReportExtension = ".checksums.txt";

    /// <summary>
    ///     Computes the report hashes and writes them next to <paramref name="chdPath" />.
    /// </summary>
    /// <param name="chdPath">Path of the verified CHD file.</param>
    /// <param name="progress">Optional progress receiver for the hashing passes.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Path of the written report.</returns>
    internal static string Write(
        string chdPath,
        IProgress<ChdProgress>? progress,
        CancellationToken token
    )
    {
        var hashes = Chd.ComputeHashes(chdPath, ReportHashTypes, null, true, progress, token);
        var tracks = hashes.Where(static h => h.TrackNumber is not null).ToList();
        var wholeImage = hashes.FirstOrDefault(static h => h.TrackNumber is null);

        // CD/GD-ROM per-track hashing returns no whole-image entry, so compute it in a second
        // pass. The report's top hashes then describe the decompressed image itself, exactly like
        // the hashes of a non-CD image (the CHD header SHA-1 is the combined metadata hash and
        // would not match a hash of the extracted data).
        if (wholeImage is null)
        {
            var whole = Chd.ComputeHashes(chdPath, ReportHashTypes, null, false, progress, token);
            wholeImage = whole.FirstOrDefault();
        }

        var builder = new StringBuilder();
        builder.AppendLine("# CHD Studio checksum report");
        builder.Append("File:       ").AppendLine(Path.GetFileName(chdPath));
        builder.Append("Size:       ")
            .Append(new FileInfo(chdPath).Length.ToString("N0", CultureInfo.InvariantCulture))
            .AppendLine(" bytes");

        builder
            .Append("SHA-1:      ")
            .AppendLine(NonEmptyOrUnavailable(wholeImage?.ToHex(ChdHashType.Sha1)));
        if (wholeImage is not null)
        {
            builder
                .Append("CRC-32:     ")
                .AppendLine(NonEmptyOrUnavailable(wholeImage.ToHex(ChdHashType.Crc32)));
            builder
                .Append("XXH3-64:    ")
                .AppendLine(NonEmptyOrUnavailable(wholeImage.ToHex(ChdHashType.Xxh3)));
        }

        if (tracks.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Tracks:");
            foreach (var track in tracks)
            {
                builder
                    .Append("  Track ")
                    .Append(track.TrackNumber!.Value.ToString("D2", CultureInfo.InvariantCulture))
                    .Append(" (offset ")
                    .Append(track.StartOffset.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(", ")
                    .Append(track.Length.ToString("N0", CultureInfo.InvariantCulture))
                    .AppendLine(" bytes)");
                builder
                    .Append("    SHA-1:   ")
                    .AppendLine(NonEmptyOrUnavailable(track.ToHex(ChdHashType.Sha1)));
                builder
                    .Append("    CRC-32:  ")
                    .AppendLine(NonEmptyOrUnavailable(track.ToHex(ChdHashType.Crc32)));
                builder
                    .Append("    XXH3-64: ")
                    .AppendLine(NonEmptyOrUnavailable(track.ToHex(ChdHashType.Xxh3)));
            }
        }

        var reportPath = Path.ChangeExtension(chdPath, ReportExtension);
        File.WriteAllText(reportPath, builder.ToString(), new UTF8Encoding(false));
        return reportPath;
    }

    /// <summary>Returns the value, or a placeholder when the hash is unavailable.</summary>
    /// <param name="value">The formatted hash value, or null/empty.</param>
    /// <returns>The value or <c>"(not available)"</c>.</returns>
    private static string NonEmptyOrUnavailable(string? value)
    {
        return string.IsNullOrEmpty(value) ? "(not available)" : value;
    }
}
