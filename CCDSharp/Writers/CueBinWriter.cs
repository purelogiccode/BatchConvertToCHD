using System.Globalization;
using System.Text;
using CCDSharp.Models;
using CCDSharp.Parsers;

namespace CCDSharp.Writers;

/// <summary>
/// Converts a CloneCD disc image to CUE/BIN format.
/// The .img file serves as the raw .bin data, and a .cue sheet is generated to describe it.
/// </summary>
internal static class CueBinWriter
{
    private const int MaxCopyRetries = 4;

    /// <summary>
    /// Generates a CUE sheet string from a parsed DiscImage.
    /// </summary>
    /// <param name="disc">The parsed disc image.</param>
    /// <param name="binFileName">The BIN file name to reference in the CUE sheet (without path).</param>
    /// <returns>The complete CUE sheet content as a string.</returns>
    internal static string GenerateCueSheet(DiscImage disc, string binFileName)
    {
        var sb = new StringBuilder();

        // CATALOG if present
        if (!string.IsNullOrEmpty(disc.Catalog))
            sb.AppendLine($"CATALOG {disc.Catalog}");

        // FILE directive
        sb.AppendLine($"FILE \"{binFileName}\" BINARY");

        // TRACK entries
        foreach (var track in disc.Tracks)
        {
            sb.Append("  TRACK ")
                .Append(track.Number.ToString("00", CultureInfo.InvariantCulture))
                .Append(' ')
                .AppendLine(track.CueTrackType);

            // FLAGS if present
            if (!string.IsNullOrEmpty(track.Flags))
                sb.AppendLine($"    FLAGS {track.Flags}");

            // ISRC if present
            if (!string.IsNullOrEmpty(track.Isrc))
                sb.AppendLine($"    ISRC {track.Isrc}");

            // INDEX 00 (pregap) for audio tracks
            if (track.IsAudio && track.Indexes.TryGetValue(0, out var index0Lba))
            {
                var (m0, s0, f0) = CcdParser.LbaToMsf(index0Lba);
                sb.AppendLine($"    INDEX 00 {CcdParser.FormatMsf(m0, s0, f0)}");
            }

            // INDEX 01 (track start)
            if (track.Index01Lba >= 0)
            {
                var (m1, s1, f1) = CcdParser.LbaToMsf(track.Index01Lba);
                sb.AppendLine($"    INDEX 01 {CcdParser.FormatMsf(m1, s1, f1)}");
            }

            // INDEX 02+ (sub-indexes)
            foreach (var kvp in track.Indexes.OrderBy(k => k.Key))
            {
                if (kvp.Key <= 1)
                    continue;

                var (m, s, f) = CcdParser.LbaToMsf(kvp.Value);
                sb.Append("    INDEX ")
                    .Append(kvp.Key.ToString("00", CultureInfo.InvariantCulture))
                    .Append(' ')
                    .AppendLine(CcdParser.FormatMsf(m, s, f));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a CloneCD image to CUE/BIN format by writing the .cue file.
    /// The .img file is used directly as the .bin file (renamed or referenced).
    /// </summary>
    /// <param name="disc">The parsed disc image.</param>
    /// <param name="outputCuePath">Path for the output .cue file.</param>
    /// <param name="copyBinFile">If true, copies the .img file to a .bin file next to the .cue.
    /// If false, the .cue references the .img file directly.</param>
    /// <returns>The path to the .cue file created.</returns>
    public static string Write(DiscImage disc, string outputCuePath, bool copyBinFile = false)
    {
        if (disc.ImgFilePath == null || !File.Exists(disc.ImgFilePath))
            throw new FileNotFoundException("IMG data file not found.", disc.ImgFilePath);

        var cueDir = Path.GetDirectoryName(outputCuePath) ?? ".";
        string binFileName;

        if (copyBinFile)
        {
            // Copy .img to .bin next to the .cue file
            binFileName = Path.GetFileNameWithoutExtension(outputCuePath) + ".bin";
            var binPath = Path.Combine(cueDir, binFileName);
            CopyWithRetry(disc.ImgFilePath, binPath);
        }
        else
        {
            // Reference the .img where it already is. A cue FILE entry is resolved against the cue's
            // own directory, so a relative path reaches the image without duplicating it - copying a
            // CloneCD image into temp costs another whole disc worth of disk per conversion.
            // A rooted result means the image is on another volume, which a cue cannot express, so
            // that is the one case where the copy is still needed.
            binFileName = GetReferencePath(cueDir, disc.ImgFilePath);
            if (Path.IsPathRooted(binFileName))
            {
                binFileName = Path.GetFileName(disc.ImgFilePath);
                CopyWithRetry(disc.ImgFilePath, Path.Combine(cueDir, binFileName));
            }
        }

        // Generate and write the CUE sheet. The encoding must not emit a BOM: chdman's cue parser
        // does not skip one and fails with the misleading "couldn't find bin file []".
        var cueContent = GenerateCueSheet(disc, binFileName);
        File.WriteAllText(outputCuePath, cueContent, new UTF8Encoding(false));

        return outputCuePath;
    }

    /// <summary>
    /// Returns the path to <paramref name="imgFilePath"/> relative to <paramref name="cueDir"/>, or a
    /// rooted path when no relative path exists (different volumes, or mismatched root forms).
    /// </summary>
    private static string GetReferencePath(string cueDir, string imgFilePath)
    {
        try
        {
            return Path.GetRelativePath(cueDir, imgFilePath);
        }
        catch (ArgumentException)
        {
            return imgFilePath;
        }
    }

    /// <summary>
    /// Converts a CloneCD image to CUE/BIN format using streams.
    /// </summary>
    /// <param name="disc">The parsed disc image.</param>
    /// <param name="cueWriter">Writer for the .cue sheet content.</param>
    /// <param name="binFileName">The BIN file name to reference in the CUE sheet.</param>
    public static void WriteToStream(DiscImage disc, TextWriter cueWriter, string binFileName)
    {
        var cueContent = GenerateCueSheet(disc, binFileName);
        cueWriter.Write(cueContent);
    }

    private static void CopyWithRetry(string source, string dest)
    {
        for (var attempt = 0; attempt < MaxCopyRetries; attempt++)
        {
            try
            {
                File.Copy(source, dest, true);
                return;
            }
            catch (IOException) when (attempt < MaxCopyRetries - 1)
            {
                Thread.Sleep(300 * (attempt + 1));
            }
        }
    }
}