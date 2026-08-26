using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Builds a cue for a set of split track bins that arrived without one.
///
/// Redump-style rips store each track in its own file - "Game (Track 1).bin",
/// "Game (Track 2).bin" and so on. Without a cue the tool previously converted only the largest
/// bin, which silently discarded every CDDA audio track. Track numbers recovered from the file
/// names are enough to describe the set: track 1 is the data track, later tracks are audio.
///
/// The pregap is the one thing the file names cannot reveal. Each track is therefore declared to
/// start at the beginning of its own file, which is what chdman assumes in the absence of an
/// INDEX 00. A disc whose audio pregap was stored at the end of the previous track will have its
/// audio start up to two seconds off; nothing is lost or corrupted. Callers are expected to say so
/// in the log.
/// </summary>
internal static class TrackBinCueBuilder
{
    /// <summary>Matches a trailing "(Track 2)" or "(Track 02)" in a file name.</summary>
    private static readonly Regex TrackNumberRegex = new(
        @"\(\s*track\s*(?<num>\d{1,3})\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking
    );

    /// <summary>One track file and the number recovered from its name.</summary>
    /// <param name="Path">Full path of the bin.</param>
    /// <param name="Number">Track number.</param>
    internal sealed record TrackBin(string Path, int Number);

    /// <summary>
    /// Returns the "(Track N)" bins among <paramref name="binPaths"/>, ordered by track number.
    /// Returns null unless at least two were found, every number is distinct, and they share a base
    /// name - anything less is not a coherent split set and should be left to the caller's
    /// single-bin fallback.
    /// </summary>
    /// <param name="binPaths">Candidate .bin files from one directory.</param>
    internal static List<TrackBin>? TryGetTrackSet(IEnumerable<string> binPaths)
    {
        var found = new List<TrackBin>();
        string? sharedStem = null;

        foreach (var path in binPaths)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var match = TrackNumberRegex.Match(fileName);
            if (
                !match.Success
                || !int.TryParse(
                    match.Groups["num"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number
                )
            )
            {
                continue;
            }

            // Everything before "(Track N)" must agree, so two different games in one folder are
            // never merged into a single disc.
            var stem = fileName[..match.Index].Trim();
            if (sharedStem is null)
            {
                sharedStem = stem;
            }
            else if (!string.Equals(sharedStem, stem, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            found.Add(new TrackBin(path, number));
        }

        if (found.Count < 2)
        {
            return null;
        }

        if (found.Select(static t => t.Number).Distinct().Count() != found.Count)
        {
            return null;
        }

        return [.. found.OrderBy(static t => t.Number)];
    }

    /// <summary>
    /// Renders a cue for <paramref name="tracks"/>. The lowest-numbered track is the data track and
    /// takes <paramref name="dataTrackMode"/>; the rest are AUDIO.
    /// </summary>
    /// <param name="tracks">Track bins in order.</param>
    /// <param name="dataTrackMode">Cue mode for the data track, e.g. "MODE2/2352".</param>
    internal static string BuildCueContent(IReadOnlyList<TrackBin> tracks, string dataTrackMode)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            builder.Append("FILE \"").Append(Path.GetFileName(track.Path)).Append("\" BINARY\r\n");
            builder
                .Append("  TRACK ")
                .Append(track.Number.ToString("00", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(i == 0 ? dataTrackMode : "AUDIO")
                .Append("\r\n");
            builder.Append("    INDEX 01 00:00:00\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes a cue for <paramref name="tracks"/> beside the bins and returns its path. The cue is
    /// written without a BOM, which chdman's parser cannot skip.
    /// </summary>
    /// <param name="tracks">Track bins in order.</param>
    /// <param name="dataTrackMode">Cue mode for the data track.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<string> WriteCueAsync(
        IReadOnlyList<TrackBin> tracks,
        string dataTrackMode,
        CancellationToken token
    )
    {
        var cuePath = BinCueGenerator.GetAutoCuePath(tracks[0].Path);
        await File.WriteAllTextAsync(
                cuePath,
                BuildCueContent(tracks, dataTrackMode),
                new UTF8Encoding(false),
                token
            )
            .ConfigureAwait(false);

        return cuePath;
    }
}