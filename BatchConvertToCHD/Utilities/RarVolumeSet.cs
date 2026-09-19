using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Locates the volumes of a multi-part RAR archive.
///     SharpCompress can read every volume of a set, but only when it is handed the <b>first</b>
///     volume by path: opening a single stream - or a later part - leaves it without the earlier
///     data and produces decode failures deep inside the decompressor. Input folders hand the app
///     each part as its own file, so the app has to work out which part is the first one before
///     extraction.
///     Recognised layouts: new-style <c>name.partNN.rar</c>, numbered <c>name.NNN</c> sets and
///     old-style <c>name.rar</c> + <c>name.rNN</c> volumes.
/// </summary>
internal static partial class RarVolumeSet
{
    /// <summary>Guards against runaway probing if a folder is pathological.</summary>
    private const int MaxVolumes = 999;

    /// <summary>Matches "name.partNN.rar" (case-insensitive) and captures the set base name and number.</summary>
    [GeneratedRegex(
        @"^(?<base>.+)\.part(?<number>\d+)\.rar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PartNamePattern();

    /// <summary>Matches a three-digit numbered volume such as "name.001".</summary>
    [GeneratedRegex(@"^\.\d{3}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberedExtensionPattern();

    /// <summary>
    ///     True when <paramref name="path" /> is a <c>.partNN.rar</c> volume after the first one.
    /// </summary>
    /// <param name="path">File path to inspect.</param>
    internal static bool IsLaterPart(string path)
    {
        return TryGetPartInfo(path, out _, out var partNumber) && partNumber > 1;
    }

    /// <summary>
    ///     Recognises a <c>name.partNN.rar</c> file name and reports the set's base name and the
    ///     volume number. Returns false for any other name.
    /// </summary>
    /// <param name="path">File path to inspect.</param>
    /// <param name="setBaseName">Name of the set without the ".partNN.rar" suffix.</param>
    /// <param name="partNumber">Volume number parsed from the name.</param>
    internal static bool TryGetPartInfo(string path, out string setBaseName, out int partNumber)
    {
        setBaseName = string.Empty;
        partNumber = 0;

        var match = PartNamePattern().Match(Path.GetFileName(path));
        if (!match.Success) return false;

        setBaseName = match.Groups["base"].Value;
        return int.TryParse(
            match.Groups["number"].ValueSpan,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out partNumber
        );
    }

    /// <summary>
    ///     Returns the path of the first volume of the RAR set <paramref name="path" /> belongs to.
    ///     For a file that is not a <c>.partNN.rar</c> volume (a single archive, a <c>.001</c> set or
    ///     an old-style <c>.rar</c>) the file itself is the answer. For a later part, the folder is
    ///     searched for the <c>part1</c>/<c>part01</c> volume and null is returned when it is not
    ///     there, because the set cannot be decoded from the middle.
    /// </summary>
    /// <param name="path">Path of any volume of the set.</param>
    internal static string? FindFirstVolume(string path)
    {
        if (!TryGetPartInfo(path, out var setBaseName, out var partNumber)) return path;
        if (partNumber == 1) return path;

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

        return EnumerateParts(directory, setBaseName)
            .FirstOrDefault(static p => p.Number == 1)
            .Path;
    }

    /// <summary>
    ///     Every on-disk volume of the set <paramref name="firstVolumePath" /> opens, in order, for
    ///     the copy-to-temp fallback. Single-file archives yield a one-element list.
    /// </summary>
    /// <param name="firstVolumePath">Path of the first volume (or a single archive).</param>
    internal static IReadOnlyList<string> GetVolumePaths(string firstVolumePath)
    {
        if (TryGetPartInfo(firstVolumePath, out var setBaseName, out _))
        {
            var directory = Path.GetDirectoryName(firstVolumePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                var parts = EnumerateParts(directory, setBaseName);
                if (parts.Count > 0) return [.. parts.Select(static p => p.Path)];
            }

            return [firstVolumePath];
        }

        var extension = Path.GetExtension(firstVolumePath);
        if (NumberedExtensionPattern().IsMatch(extension))
        {
            var directory = Path.GetDirectoryName(firstVolumePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                var numbered = EnumerateNumbered(directory, firstVolumePath[..^extension.Length]);
                if (numbered.Count > 0) return numbered;
            }

            return [firstVolumePath];
        }

        if (extension.Equals(FileExtensions.Rar, StringComparison.OrdinalIgnoreCase))
        {
            var oldStyle = EnumerateOldStyle(firstVolumePath);
            if (oldStyle.Count > 0) return oldStyle;
        }

        return [firstVolumePath];
    }

    /// <summary>
    ///     Total size in bytes of every volume of the set <paramref name="path" /> belongs to, or 0
    ///     when a volume cannot be measured.
    /// </summary>
    /// <param name="path">Path of any volume of the set (or a single archive).</param>
    internal static long GetTotalBytes(string path)
    {
        long total = 0;
        foreach (var volume in GetVolumePaths(path))
        {
            try
            {
                total += new FileInfo(volume).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        return total;
    }

    /// <summary>
    ///     The file name a user should supply for the set <paramref name="path" /> belongs to, used in
    ///     log messages ("add hyk-D2-2.part01.rar instead"). Mirrors the zero-padding of the volume
    ///     that was seen.
    /// </summary>
    /// <param name="path">Path of any volume of the set.</param>
    internal static string GetFirstVolumeName(string path)
    {
        if (!TryGetPartInfo(path, out var setBaseName, out var partNumber))
            return Path.GetFileName(path);

        var width = partNumber.ToString(CultureInfo.InvariantCulture).Length;
        return $"{setBaseName}.part{1.ToString("D" + width, CultureInfo.InvariantCulture)}.rar";
    }

    /// <summary>
    ///     Enumerates <c>name.partNN.rar</c> files of one set, ordered by volume number.
    /// </summary>
    private static List<(int Number, string Path)> EnumerateParts(
        string directory,
        string setBaseName
    )
    {
        var result = new List<(int Number, string Path)>();
        foreach (var candidate in EnumerateFileNames(directory))
        {
            if (
                TryGetPartInfo(candidate, out var candidateBase, out var number)
                && string.Equals(candidateBase, setBaseName, StringComparison.OrdinalIgnoreCase)
            )
            {
                result.Add((number, candidate));
            }
        }

        result.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        return result;
    }

    /// <summary>
    ///     Enumerates <c>name.001</c>-style volume files, ordered by number. Numbering is dense, so
    ///     probing stops at the first absent volume without listing the folder.
    /// </summary>
    private static List<string> EnumerateNumbered(string directory, string stem)
    {
        var result = new List<string>();
        for (var number = 1; number <= MaxVolumes; number++)
        {
            var suffix = number.ToString("000", CultureInfo.InvariantCulture);
            var candidate = Path.Combine(directory, $"{stem}.{suffix}");
            if (!File.Exists(candidate)) break;

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    ///     Enumerates an old-style <c>name.rar</c> + <c>name.r00</c>... set, first volume first.
    ///     Continuation numbering is dense, so probing stops at the first absent volume.
    /// </summary>
    private static List<string> EnumerateOldStyle(string firstVolumePath)
    {
        var extension = Path.GetExtension(firstVolumePath);
        var stem = firstVolumePath[..^extension.Length];
        var directory = Path.GetDirectoryName(firstVolumePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return [firstVolumePath];

        var result = new List<string> { firstVolumePath };
        for (var number = 0; number <= MaxVolumes; number++)
        {
            var candidate = stem + ".r" + number.ToString("00", CultureInfo.InvariantCulture);
            if (!File.Exists(candidate)) break;

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    ///     Lists the files of a folder, treating an unreadable or vanishing folder as empty so
    ///     volume discovery never turns a permission problem into a failed conversion.
    /// </summary>
    private static IReadOnlyList<string> EnumerateFileNames(string directory)
    {
        try
        {
            return Directory.GetFiles(directory);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (System.Security.SecurityException)
        {
            return [];
        }
    }
}