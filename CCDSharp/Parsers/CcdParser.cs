using System.Globalization;
using System.Text.RegularExpressions;
using CCDSharp.Models;

namespace CCDSharp.Parsers;

/// <summary>
///     Parses CloneCD .ccd descriptor files into a DiscImage model.
/// </summary>
public static partial class CcdParser
{
    [GeneratedRegex(@"^\s*\[CloneCD\]")]
    private static partial Regex CloneCdIdentifier();

    [GeneratedRegex(@"^\s*\[Disc\]")]
    private static partial Regex DiscIdentifier();

    [GeneratedRegex(@"^\s*\[Session\s*(\d+)\]")]
    private static partial Regex SessionIdentifier();

    [GeneratedRegex(@"^\s*\[Entry\s*(\d+)\]")]
    private static partial Regex EntryIdentifier();

    [GeneratedRegex(@"^\s*\[TRACK\s*(\d+)\]")]
    private static partial Regex TrackIdentifier();

    [GeneratedRegex(@"^\s*MODE\s*=\s*(\d+)")]
    private static partial Regex TrackModeRegex();

    [GeneratedRegex(@"^\s*INDEX\s*(\d+)\s*=\s*(\d+)")]
    private static partial Regex TrackIndexRegex();

    [GeneratedRegex(@"^\s*FLAGS\s*=\s*(.+)")]
    private static partial Regex TrackFlagsRegex();

    [GeneratedRegex(@"^\s*ISRC\s*=\s*(\S+)")]
    private static partial Regex TrackIsrcRegex();

    [GeneratedRegex(@"^\s*Version\s*=\s*(\d+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^\s*TocEntries\s*=\s*(\d+)")]
    private static partial Regex TocEntriesRegex();

    [GeneratedRegex(@"^\s*Sessions\s*=\s*(\d+)")]
    private static partial Regex SessionsRegex();

    [GeneratedRegex(@"^\s*DataTracksScrambled\s*=\s*(\d+)")]
    private static partial Regex DataTracksScrambledRegex();

    [GeneratedRegex(@"^\s*CDTextLength\s*=\s*(\d+)")]
    private static partial Regex CdTextLengthRegex();

    [GeneratedRegex(@"^\s*CATALOG\s*=\s*(\S+)")]
    private static partial Regex CatalogRegex();

    /// <summary>
    ///     Parses a .ccd file and returns a DiscImage model.
    /// </summary>
    /// <param name="ccdFilePath">Path to the .ccd file.</param>
    /// <returns>The parsed disc image.</returns>
    /// <exception cref="FileNotFoundException">If the .ccd file does not exist.</exception>
    /// <exception cref="FormatException">If the .ccd file is malformed.</exception>
    public static DiscImage Parse(string ccdFilePath)
    {
        if (!File.Exists(ccdFilePath))
            throw new FileNotFoundException("CCD file not found.", ccdFilePath);

        var lines = File.ReadAllLines(ccdFilePath);
        return ParseLines(lines, ccdFilePath);
    }

    /// <summary>
    ///     Parses CCD content from a TextReader.
    /// </summary>
    /// <param name="reader">The TextReader to read CCD content from.</param>
    /// <param name="ccdFilePath">Optional path to the .ccd file for resolving associated files.</param>
    /// <returns>The parsed disc image.</returns>
    public static DiscImage Parse(TextReader reader, string? ccdFilePath = null)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        return ParseLines(lines.ToArray(), ccdFilePath);
    }

    private static DiscImage ParseLines(string[] lines, string? ccdFilePath)
    {
        var disc = new DiscImage { FilePath = ccdFilePath };

        // Resolve associated .img and .sub file paths
        if (ccdFilePath != null)
        {
            var basePath = Path.ChangeExtension(ccdFilePath, null);
            var imgPath = basePath + ".img";
            var subPath = basePath + ".sub";

            disc.ImgFilePath = File.Exists(imgPath) ? imgPath : null;
            disc.SubFilePath = File.Exists(subPath) ? subPath : null;
        }

        var inCcd = false;
        var inDisc = false;
        var currentTrack = -1;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            // Section headers
            if (CloneCdIdentifier().IsMatch(line))
            {
                inCcd = true;
                inDisc = false;
                currentTrack = -1;
                continue;
            }

            if (DiscIdentifier().IsMatch(line))
            {
                inCcd = false;
                inDisc = true;
                currentTrack = -1;
                continue;
            }

            var sessionMatch = SessionIdentifier().Match(line);
            if (sessionMatch.Success)
            {
                inCcd = false;
                inDisc = false;
                currentTrack = -1;
                continue;
            }

            var entryMatch = EntryIdentifier().Match(line);
            if (entryMatch.Success)
            {
                inCcd = false;
                inDisc = false;
                currentTrack = -1;
                continue;
            }

            var trackMatch = TrackIdentifier().Match(line);
            if (trackMatch.Success)
            {
                inCcd = false;
                inDisc = false;
                currentTrack = int.Parse(trackMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                // Ensure track exists in the list
                while (disc.Tracks.Count < currentTrack)
                    disc.Tracks.Add(new Track { Number = disc.Tracks.Count + 1 });

                disc.Tracks[currentTrack - 1].Number = currentTrack;
                continue;
            }

            // Parse fields based on current section
            if (inCcd)
                ParseCcdSection(line, disc);
            else if (inDisc)
                ParseDiscSection(line, disc);
            else if (currentTrack > 0) ParseTrackSection(line, disc.Tracks[currentTrack - 1]);
        }

        return disc;
    }

    private static void ParseCcdSection(string line, DiscImage disc)
    {
        var versionMatch = VersionRegex().Match(line);
        if (versionMatch.Success) disc.Version = int.Parse(versionMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static void ParseDiscSection(string line, DiscImage disc)
    {
        var match = TocEntriesRegex().Match(line);
        if (match.Success)
        {
            disc.TocEntries = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }

        match = SessionsRegex().Match(line);
        if (match.Success)
        {
            disc.Sessions = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }

        match = DataTracksScrambledRegex().Match(line);
        if (match.Success)
        {
            disc.DataTracksScrambled =
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) != 0;
            return;
        }

        match = CdTextLengthRegex().Match(line);
        if (match.Success)
        {
            disc.CdTextLength = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }

        match = CatalogRegex().Match(line);
        if (match.Success) disc.Catalog = match.Groups[1].Value;
    }

    private static void ParseTrackSection(string line, Track track)
    {
        var modeMatch = TrackModeRegex().Match(line);
        if (modeMatch.Success)
        {
            track.Mode = int.Parse(modeMatch.Groups[1].Value, CultureInfo.InvariantCulture) switch
            {
                0 => TrackMode.Audio,
                1 => TrackMode.Mode1,
                2 => TrackMode.Mode2,
                _ => TrackMode.Mode1
            };
            return;
        }

        var indexMatch = TrackIndexRegex().Match(line);
        if (indexMatch.Success)
        {
            var indexNum = int.Parse(indexMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var lbaValue = int.Parse(indexMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            track.Indexes[indexNum] = lbaValue;
            return;
        }

        var flagsMatch = TrackFlagsRegex().Match(line);
        if (flagsMatch.Success)
        {
            track.Flags = flagsMatch.Groups[1].Value.Trim();
            return;
        }

        var isrcMatch = TrackIsrcRegex().Match(line);
        if (isrcMatch.Success) track.Isrc = isrcMatch.Groups[1].Value.Trim();
    }

    /// <summary>
    ///     Converts an LBA (Logical Block Address) frame count to MSF (Minutes:Seconds:Frames) format.
    /// </summary>
    /// <param name="lba">The LBA frame count.</param>
    /// <returns>A tuple of (minutes, seconds, frames).</returns>
    public static (int Minutes, int Seconds, int Frames) LbaToMsf(int lba)
    {
        var minutes = lba / SectorConstants.FramesPerMinute;
        var remainder = lba % SectorConstants.FramesPerMinute;
        var seconds = remainder / SectorConstants.FramesPerSecond;
        var frames = remainder % SectorConstants.FramesPerSecond;
        return (minutes, seconds, frames);
    }

    /// <summary>
    ///     Formats an MSF tuple as a CUE-compatible string (MM:SS:FF).
    /// </summary>
    public static string FormatMsf(int minutes, int seconds, int frames)
    {
        return $"{minutes:00}:{seconds:00}:{frames:00}";
    }
}