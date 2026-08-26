using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Parses a CUE sheet with encoding detection, resolves every referenced file against the filesystem
/// (exact match, then case-insensitive, then zero-padding-tolerant like "(Track 2)" vs "(Track 02)"),
/// and produces a canonical UTF-8 rewrite of the cue that chdman can consume reliably.
/// </summary>
internal static class CueNormalizer
{
    private static readonly Regex TrackNumberRegex = new(
        @"(?<prefix>.*\(Track\s+)(?<num>\d+)(?<suffix>\).*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking);

    private static readonly string[] KnownTrackTypes = ["BINARY", "WAVE", "MP3", "AIFF", "MOTOROLA", "AUDIO"];

    /// <summary>Extensions a cue's data track can legitimately be stored under.</summary>
    private static readonly HashSet<string> DataFileExtensions =
        new([FileExtensions.Bin, FileExtensions.Img, FileExtensions.Iso, FileExtensions.Raw],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes the cue at <paramref name="cuePath"/>.
    /// </summary>
    /// <param name="cuePath">Path of the .cue or .toc descriptor to normalize.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="transform">Optional transform applied to each resolved FILE line.</param>
    internal static async Task<CueNormalizationResult> NormalizeAsync(
        string cuePath, CancellationToken token, CueFileLineTransform? transform = null)
    {
        var (lines, encoding, hasBom) =
            await GameFileParser.ReadLinesWithDetectedEncodingAsync(cuePath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(cuePath) ?? string.Empty;
        var references = new List<CueFileReference>();
        var unresolved = new List<string>();
        var canonicalLines = new List<string>(lines.Length);
        var needsRewrite = false;
        var referencesChanged = false;

        // A cue with exactly one FILE line describes one data file, which allows a last-resort match
        // by elimination when the recorded name is unusable.
        var isSingleFileCue = CountFileLines(lines) == 1;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase) ||
                !GameFileParser.TryGetFileNameFromFileLine(trimmedLine, out var referencedName) ||
                referencedName is null)
            {
                canonicalLines.Add(line);
                continue;
            }

            var trackType = GetTrackType(trimmedLine);
            var reference = ResolveReference(directory, referencedName, trackType, isSingleFileCue);
            references.Add(reference);

            if (!reference.IsResolved)
            {
                unresolved.Add(referencedName);
            }

            var lineName = reference.ResolvedName ?? referencedName;
            var lineType = reference.TrackType;
            if (reference.IsResolved && transform is not null)
            {
                var transformed = transform(reference);
                if (transformed is not null)
                {
                    lineName = transformed.Value.Name;
                    lineType = transformed.Value.TrackType ?? reference.TrackType;
                    referencesChanged = true;
                }
            }

            if (reference.WasNameCorrected)
            {
                referencesChanged = true;
            }

            var canonicalLine = BuildCanonicalFileLine(lineName, lineType);
            if (!string.Equals(canonicalLine, trimmedLine, StringComparison.Ordinal))
            {
                needsRewrite = true;
            }

            canonicalLines.Add(canonicalLine);
        }

        return new CueNormalizationResult(encoding, hasBom, references, unresolved, canonicalLines, needsRewrite,
            referencesChanged);
    }

    /// <summary>
    /// Writes the canonical cue content to <paramref name="outputPath"/> as UTF-8 (no BOM, CRLF line endings).
    /// </summary>
    /// <param name="outputPath">Destination file path for the canonical cue.</param>
    /// <param name="result">The normalization result whose canonical content is written.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task WriteCanonicalCueAsync(string outputPath, CueNormalizationResult result,
        CancellationToken token)
    {
        await File.WriteAllTextAsync(outputPath, result.CanonicalCueText, new UTF8Encoding(false), token)
            .ConfigureAwait(false);
    }

    private static CueFileReference ResolveReference(string directory, string referencedName, string? trackType,
        bool isSingleFileCue)
    {
        var fullPath = Path.Combine(directory, referencedName);
        var referencedFileName = Path.GetFileName(fullPath);
        var referencedDirectory = Path.GetDirectoryName(fullPath) ?? directory;

        // Strategy 1: the reference as written, resolved wherever it points.
        var match = FindMatch(GetFiles(referencedDirectory), referencedFileName, out var wasNameCorrected);

        var cueDirectoryFiles = GetFiles(directory);

        // Strategy 2: the same file name next to the cue, ignoring any directory the reference
        // carried. Cues written elsewhere keep that machine's absolute path - real examples include
        // "C:\DOCUMENTS AND SETTINGS\BILL\DESKTOP\..." - which resolves nowhere here even though the
        // data file is sitting beside the cue.
        if (match is null && !string.Equals(referencedDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            match = FindMatch(cueDirectoryFiles, referencedFileName, out wasNameCorrected);
            if (match is not null)
            {
                wasNameCorrected = true;
            }
        }

        // Strategy 3: same base name, different extension. Rips get re-saved between .bin, .img and
        // .iso without the cue being updated.
        if (match is null)
        {
            match = FindExtensionSwapMatch(cueDirectoryFiles, referencedFileName);
            wasNameCorrected = match is not null;
        }

        // Strategy 4: a cue with a single FILE line, sitting next to exactly one data file, can only
        // mean that file - however little the recorded name resembles it ("SOULEDGE.bin",
        // "Legend Of Legaia Iso"). Restricted to the data track so a missing WAVE or MP3 audio
        // track is never silently answered with the disc image.
        if (match is null && isSingleFileCue && IsBinaryTrack(trackType))
        {
            var dataFiles = cueDirectoryFiles
                .Where(static f => DataFileExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            if (dataFiles.Count == 1)
            {
                match = dataFiles[0];
                wasNameCorrected = true;
            }
        }

        if (match is null)
        {
            return new CueFileReference(referencedName, null, fullPath, trackType, false, directory);
        }

        // Anchor the record on the file that was actually found, so a redirected reference reports
        // the real path rather than the one the cue asked for.
        return new CueFileReference(
            referencedName,
            Path.GetRelativePath(directory, match),
            match,
            trackType,
            wasNameCorrected,
            directory);
    }

    private static int CountFileLines(string[] lines)
    {
        var count = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase) &&
                GameFileParser.TryGetFileNameFromFileLine(trimmed, out var name) &&
                name is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static string[] GetFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Resolves <paramref name="fileName"/> against <paramref name="files"/> by exact name, then
    /// case-insensitively, then tolerating zero-padding differences in a "(Track N)" suffix.
    /// </summary>
    private static string? FindMatch(string[] files, string fileName, out bool wasNameCorrected)
    {
        wasNameCorrected = false;
        if (files.Length == 0)
        {
            return null;
        }

        var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal))
                    ?? files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        match = FindPadTolerantMatch(files, fileName);
        wasNameCorrected = match is not null;

        return match;
    }

    /// <summary>
    /// Finds a file with the same base name as <paramref name="fileName"/> but a different disc
    /// image extension.
    /// </summary>
    private static string? FindExtensionSwapMatch(string[] files, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (baseName.Length == 0)
        {
            return null;
        }

        return files.FirstOrDefault(f =>
            DataFileExtensions.Contains(Path.GetExtension(f)) &&
            string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBinaryTrack(string? trackType)
    {
        return trackType is null || string.Equals(trackType, "BINARY", StringComparison.Ordinal);
    }

    private static string? FindPadTolerantMatch(string[] files, string fileName)
    {
        var match = TrackNumberRegex.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var trackNumber))
        {
            return null;
        }

        var variants = new[]
        {
            trackNumber.ToString(CultureInfo.InvariantCulture),
            trackNumber.ToString("D2", CultureInfo.InvariantCulture),
            trackNumber.ToString("D3", CultureInfo.InvariantCulture)
        };

        foreach (var variant in variants)
        {
            var candidateName = match.Groups["prefix"].Value + variant + match.Groups["suffix"].Value;
            var found = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), candidateName, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string BuildCanonicalFileLine(string name, string? trackType)
    {
        return trackType is null ? $"FILE \"{name}\"" : $"FILE \"{name}\" {trackType}";
    }

    private static string? GetTrackType(string trimmedFileLine)
    {
        var firstQuote = trimmedFileLine.IndexOf('"');
        var lastQuote = trimmedFileLine.LastIndexOf('"');

        string tail;
        if (firstQuote != -1 && lastQuote > firstQuote)
        {
            tail = trimmedFileLine[(lastQuote + 1)..].Trim();
        }
        else
        {
            tail = trimmedFileLine;
        }

        // The track type is the first known type token anywhere in the tail — some descriptors
        // (e.g. cdrdao TOCs) append extra columns after the type.
        foreach (var token in tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (KnownTrackTypes.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                return token.ToUpperInvariant();
            }
        }

        return null;
    }
}