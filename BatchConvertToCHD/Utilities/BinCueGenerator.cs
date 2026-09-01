using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Generates and manages auto-generated CUE sheets for archives that contain only a .bin file
///     (no .cue/.iso/.img descriptor). chdman needs a cue to know the track layout; a single raw bin
///     is treated as one data track. The default mode is MODE2/2352 (PlayStation/Redump style), with
///     MODE1/2352 as the fallback for other systems.
/// </summary>
internal static class BinCueGenerator
{
    internal const string Mode2 = "MODE2/2352";
    internal const string Mode1 = "MODE1/2352";

    /// <summary>Marker embedded in the auto-generated cue file name so the app can recognize it.</summary>
    private const string AutoCueMarker = ".autocue";

    /// <summary>Returns the path of the auto-generated cue for <paramref name="binPath" />.</summary>
    /// <param name="binPath">Path of the .bin file the cue is generated for.</param>
    internal static string GetAutoCuePath(string binPath)
    {
        var directory = Path.GetDirectoryName(binPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(binPath);
        return Path.Combine(directory, baseName + AutoCueMarker + FileExtensions.Cue);
    }

    /// <summary>True when the cue file was auto-generated for a bin-only archive.</summary>
    internal static bool IsAutoCue(string cuePath)
    {
        return Path.GetFileName(cuePath)
            .EndsWith(AutoCueMarker + FileExtensions.Cue, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildCueContent(string binFileName, string mode)
    {
        return $"FILE \"{binFileName}\" BINARY\r\n  TRACK 01 {mode}\r\n    INDEX 01 00:00:00\r\n";
    }

    internal static string GetAlternateMode(string mode)
    {
        return string.Equals(mode, Mode2, StringComparison.Ordinal) ? Mode1 : Mode2;
    }

    /// <summary>
    ///     Reads the track mode of an auto-generated cue (e.g. "MODE2/2352").
    /// </summary>
    /// <param name="cuePath">Path of the auto-generated cue.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<string> ReadTrackModeAsync(string cuePath, CancellationToken token)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(cuePath, token).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (
                    trimmed.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase)
                    && trimmed.Contains('/')
                )
                {
                    var mode = trimmed[(trimmed.LastIndexOf(' ') + 1)..].Trim();
                    if (mode.Length > 0) return mode;
                }
            }
        }
#pragma warning disable RCS1075
        catch (Exception)
#pragma warning restore RCS1075
        {
            // ignored
        }

        return Mode2;
    }

    /// <summary>
    ///     Rewrites an auto-generated cue with a different track mode, preserving the referenced bin name.
    /// </summary>
    /// <param name="cuePath">Path of the auto-generated cue to rewrite.</param>
    /// <param name="mode">The track mode to write (e.g. "MODE1/2352").</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task RewriteCueAsync(string cuePath, string mode, CancellationToken token)
    {
        var binFileName =
            await ReadReferencedBinNameAsync(cuePath, token).ConfigureAwait(false)
            ?? GetFallbackBinName(cuePath);
        await File.WriteAllTextAsync(cuePath, BuildCueContent(binFileName, mode), token)
            .ConfigureAwait(false);
    }

    private static string GetFallbackBinName(string cuePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(cuePath); // "Game.autocue"
        if (baseName.EndsWith(AutoCueMarker, StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^AutoCueMarker.Length]; // "Game"

        return baseName + FileExtensions.Bin;
    }

    private static async Task<string?> ReadReferencedBinNameAsync(
        string cuePath,
        CancellationToken token
    )
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(cuePath, token).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (
                    trimmed.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)
                    && GameFileParser.TryGetFileNameFromFileLine(trimmed, out var fileName)
                    && fileName is not null
                )
                    return fileName;
            }
        }
#pragma warning disable RCS1075
        catch (Exception)
#pragma warning restore RCS1075
        {
            // ignored
        }

        return null;
    }
}