using System.Globalization;
using System.IO;
using System.Text;
using Serilog;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Provides utility methods for path manipulation and validation.
/// </summary>
internal static class PathUtils
{
    /// <summary>
    ///     Maximum path length chdman handles reliably. chdman's CRT file APIs use ANSI paths
    ///     capped at MAX_PATH (260); longer input/output paths fail with "No such file or directory"
    ///     even when the file exists.
    /// </summary>
    internal const int MaxChdmanPath = 260;

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly ILogger Logger = Log.ForContext(typeof(PathUtils));

    /// <summary>
    ///     True when every character of <paramref name="path" /> is ASCII (&lt;= 127). chdman converts
    ///     its UTF-16 command line down to the ANSI code page, so paths containing non-ASCII
    ///     characters (accented user names such as "C:\Users\Kauê", non-Latin folder names) can be
    ///     mangled before they reach its file APIs and fail with "No such file or directory".
    /// </summary>
    internal static bool IsAsciiPath(string path)
    {
        foreach (var c in path)
            if (c > 127)
                return false;

        return true;
    }

    /// <summary>
    ///     True when a path is safe to hand to chdman as-is: pure ASCII and below
    ///     <see cref="MaxChdmanPath" />.
    /// </summary>
    internal static bool IsChdmanSafePath(string path)
    {
        return path.Length < MaxChdmanPath && IsAsciiPath(path);
    }

    /// <summary>
    ///     Creates a unique temporary directory whose full path is pure ASCII and well below
    ///     MAX_PATH, for staging files that cannot be handed to chdman directly because their own
    ///     path contains non-ASCII characters or is too long. The system temp directory is preferred,
    ///     but it lives under the user profile and can be unsafe itself (e.g.
    ///     "C:\Users\José\AppData\Local\Temp"), so an ASCII-named folder on the root of a fixed drive
    ///     is used as fallback - the same folder <see cref="GetPossibleTempBasePaths" /> cleans up at
    ///     startup.
    /// </summary>
    internal static string CreateAsciiSafeTempDirectory(string tempDirPrefix)
    {
        var guid = Guid.NewGuid().ToString("N");

        try
        {
            var systemTemp = Path.GetTempPath();
            if (IsChdmanSafePath(systemTemp))
            {
                var candidate = Path.Combine(systemTemp, $"{tempDirPrefix}{guid}");
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
        catch (Exception ex)
        {
            Logger.Verbose(ex, "Failed to create a temp directory under the system temp path");
        }

        foreach (
            var drive in DriveInfo
                .GetDrives()
                .Where(static d => d is { IsReady: true, DriveType: DriveType.Fixed })
                .OrderByDescending(static d => d.AvailableFreeSpace)
        )
        {
            var candidate = Path.Combine(
                drive.RootDirectory.FullName.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ),
                "BatchConvertToCHD_Temp",
                $"{tempDirPrefix}{guid}"
            );
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                Logger.Verbose(ex, "Failed to create a temp directory under {Path}", candidate);
            }
        }

        // Best effort: nothing ASCII-safe worked; use the system temp location anyway so the
        // operation still gets a chance to run.
        return Path.Combine(Path.GetTempPath(), $"{tempDirPrefix}{guid}");
    }

    /// <summary>
    ///     Sanitizes a file name by replacing invalid characters with underscores.
    ///     Also removes trailing periods which are problematic on Windows.
    /// </summary>
    /// <param name="name">The file name to sanitize.</param>
    /// <returns>A sanitized file name safe for use in the file system.</returns>
    internal static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (Array.IndexOf(InvalidFileNameChars, c) >= 0)
                sb.Append('_');
            else
                sb.Append(c);

        // Windows silently strips trailing periods from file names, so make the last character a
        // safe underscore instead. Replacing (rather than trimming all of them) keeps the name as
        // close to the original as possible: "file..." becomes "file.._", which is a valid name,
        // and an all-periods name like "..." stays recognizable instead of collapsing to nothing
        // and triggering the random-name fallback below.
        while (sb.Length > 0 && sb[^1] == '.') sb[^1] = '_';

        var sanitizedName = sb.ToString();

        if (sanitizedName.Length == 0 || sanitizedName.All(static c => c == '_'))
            sanitizedName = Guid.NewGuid().ToString("N");

        return sanitizedName;
    }

    /// <summary>
    ///     Generates a safe temporary file name based on the original file name.
    /// </summary>
    /// <param name="originalFileNameWithExtension">The original file name with extension.</param>
    /// <param name="desiredExtensionWithoutDot">The desired extension without the dot (e.g., "iso").</param>
    /// <param name="tempDirectory">The temporary directory path.</param>
    /// <returns>A full path to a safe temporary file.</returns>
    internal static string GetSafeTempFileName(
        string originalFileNameWithExtension,
        string desiredExtensionWithoutDot,
        string tempDirectory
    )
    {
        var sanitizedName = SanitizeFileName(
            Path.GetFileNameWithoutExtension(originalFileNameWithExtension)
        );
        var safeBaseName = string.IsNullOrEmpty(sanitizedName)
            ? Guid.NewGuid().ToString("N")
            : sanitizedName;
        var ext = desiredExtensionWithoutDot.TrimStart('.');
        return Path.Combine(tempDirectory, safeBaseName + "." + ext);
    }

    /// <summary>
    ///     Computes a relative path from <paramref name="relativeTo" /> to <paramref name="path" />,
    ///     falling back to "." when the paths are on different drives/roots (which
    ///     <see cref="Path.GetRelativePath" /> does not support and will throw for).
    /// </summary>
    internal static string GetSafeRelativePath(string relativeTo, string path)
    {
        try
        {
            var root1 = Path.GetPathRoot(relativeTo);
            var root2 = Path.GetPathRoot(path);
            if (string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(relativeTo, path);
        }
        catch (Exception ex)
        {
            Logger.Verbose(ex, "Failed to query drive during candidate enumeration");
        }

        return ".";
    }

    /// <summary>
    ///     Returns the full path to a new unique temporary directory, selecting the drive
    ///     with the most available free space from among the input file's drive,
    ///     the output folder's drive, the system temp drive, and any other fixed drives.
    ///     When <paramref name="requiredBytes" /> is specified, prefers a drive that has
    ///     enough free space for the operation, even if it is not the drive with the most
    ///     total free space. Falls back to the system temp path if no suitable alternative is found.
    /// </summary>
    internal static string GetBestTempDirectory(
        string? inputFilePath,
        string? outputFolderPath,
        string tempDirPrefix,
        long requiredBytes = 0
    )
    {
        const long minFreeBytes = 1024L * 1024 * 1024; // 1 GB minimum to consider a drive viable

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidateRoot(inputFilePath);
        AddCandidateRoot(outputFolderPath);

        var systemTempRoot = Path.GetPathRoot(Path.GetTempPath());
        if (!string.IsNullOrEmpty(systemTempRoot))
            candidates.Add(systemTempRoot);

        foreach (var drive in DriveInfo.GetDrives())
            try
            {
                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                    candidates.Add(drive.RootDirectory.FullName);
            }
            catch
            {
                // ignored
            }

        string? bestRoot = null;
        long bestFree = 0;
        string? bestRootMeetingRequirement = null;
        long bestFreeMeetingRequirement = 0;

        foreach (var root in candidates)
            try
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.DriveType != DriveType.Network)
                {
                    if (drive.AvailableFreeSpace > bestFree)
                    {
                        bestFree = drive.AvailableFreeSpace;
                        bestRoot = root;
                    }

                    if (
                        requiredBytes > 0
                        && drive.AvailableFreeSpace >= requiredBytes
                        && drive.AvailableFreeSpace > bestFreeMeetingRequirement
                    )
                    {
                        bestFreeMeetingRequirement = drive.AvailableFreeSpace;
                        bestRootMeetingRequirement = root;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Verbose(
                    ex,
                    "Failed to query drive {Root} during best-root enumeration",
                    root
                );
            }

        var selectedRoot = bestRootMeetingRequirement ?? bestRoot;
        var selectedFree =
            bestRootMeetingRequirement != null ? bestFreeMeetingRequirement : bestFree;

        if (selectedRoot != null && !IsRootDirectoryWritable(selectedRoot))
        {
            // Informational: the fallback is expected behavior, not an error condition.
            Logger.Information(
                "Selected temp root {Root} is not writable, falling back to system temp",
                selectedRoot
            );
            selectedRoot = null;
            selectedFree = 0;
        }

        var guid = Guid.NewGuid().ToString("N");
        string basePath;

        if (selectedRoot != null && selectedFree >= minFreeBytes)
            // Prefer the system temp folder when it sits on the selected volume AND its own path
            // is safe to hand to chdman. %TEMP% lives under the user profile and can contain
            // non-ASCII characters (e.g. "C:\Users\Kauê Chacon\...") or approach MAX_PATH, which
            // old chdman builds cannot open ("No such file or directory"); in that case use the
            // ASCII-safe drive-root folder instead.
            basePath =
                string.Equals(selectedRoot, systemTempRoot, StringComparison.OrdinalIgnoreCase)
                && IsChdmanSafePath(Path.GetTempPath())
                    ? Path.GetTempPath()
                    : Path.Combine(
                        selectedRoot.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        ),
                        "BatchConvertToCHD_Temp"
                    );
        else
            basePath = Path.GetTempPath();

        return Path.Combine(basePath, $"{tempDirPrefix}{guid}");

        void AddCandidateRoot(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(root))
                    candidates.Add(root);
            }
            catch (Exception ex)
            {
                Logger.Verbose(ex, "Failed to get path root for drive candidate {Path}", path);
            }
        }
    }

    /// <summary>
    ///     Returns a path under <paramref name="parentDirectory" /> named after <paramref name="baseName" />
    ///     that nothing currently occupies, adding " (2)", " (3)" and so on until one is free. The
    ///     directory is not created.
    ///     Used to give an extraction somewhere to land when files of the same name are already present,
    ///     so existing files are kept without the user having to choose anything.
    /// </summary>
    /// <param name="parentDirectory">Directory the new subdirectory will sit in.</param>
    /// <param name="baseName">Preferred name, sanitised before use.</param>
    internal static string ReserveFreeSubdirectory(string parentDirectory, string baseName)
    {
        var safeName = SanitizeFileName(baseName);
        if (safeName.Length == 0) safeName = Guid.NewGuid().ToString("N");

        var candidate = Path.Combine(parentDirectory, safeName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;

        // A bounded search: a folder holding this many same-named discs is pathological, and looping
        // forever would be worse than falling back to a name that cannot collide.
        for (var suffix = 2; suffix <= 999; suffix++)
        {
            candidate = Path.Combine(
                parentDirectory,
                $"{safeName} ({suffix.ToString(CultureInfo.InvariantCulture)})"
            );
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }

        return Path.Combine(parentDirectory, $"{safeName}_{Guid.NewGuid():N}");
    }

    /// <summary>
    ///     True when <paramref name="candidate" /> is the same directory as <paramref name="root" />, or is
    ///     nested inside it. Used to tell the user when results will land among their source files.
    ///     Comparing the normalized full paths matters: "D:\Games" and "D:\Games\" and "D:\Games\..\Games"
    ///     are the same folder, and a plain string equality test on the raw text would miss that. The
    ///     separator is appended before the prefix test so "D:\Games2" is not read as being inside
    ///     "D:\Games".
    /// </summary>
    /// <param name="root">The directory to test against.</param>
    /// <param name="candidate">The directory that may be the same or nested.</param>
    internal static bool IsSameOrInsideDirectory(string? root, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;

        try
        {
            var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var rootFull = Path.GetFullPath(root).TrimEnd(separators);
            var candidateFull = Path.GetFullPath(candidate).TrimEnd(separators);

            if (string.Equals(rootFull, candidateFull, StringComparison.OrdinalIgnoreCase)) return true;

            return candidateFull.StartsWith(
                rootFull + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (Exception ex)
        {
            Logger.Verbose(ex, "Failed to compare {Root} and {Candidate}", root, candidate);
            return false;
        }
    }

    /// <summary>
    ///     Creates a temporary directory on the same volume as <paramref name="referencePath" /> and
    ///     returns it, or null when none could be created there.
    ///     <see cref="GetBestTempDirectory" /> deliberately picks the roomiest drive, which is right when
    ///     a whole image is being written but wrong for a generated cue: chdman resolves a cue's FILE
    ///     entry by joining it to the cue's own directory, so a cue that is not on the image's volume can
    ///     only reach it by an absolute path, and chdman concatenates that too - producing
    ///     "C:\temp\D:\game.iso" and "couldn't find bin file". A few hundred bytes of cue therefore has
    ///     to sit on the image's volume, however little free space that volume has.
    /// </summary>
    /// <param name="referencePath">File whose volume the directory must be on.</param>
    /// <param name="tempDirPrefix">Prefix for the directory name.</param>
    internal static string? CreateTempDirectoryOnSameVolume(
        string referencePath,
        string tempDirPrefix
    )
    {
        string? volumeRoot;
        try
        {
            volumeRoot = Path.GetPathRoot(Path.GetFullPath(referencePath));
        }
        catch (Exception ex)
        {
            Logger.Verbose(ex, "Failed to get the volume root of {Path}", referencePath);
            return null;
        }

        if (string.IsNullOrEmpty(volumeRoot)) return null;

        foreach (var basePath in GetSameVolumeTempBasePaths(volumeRoot))
        {
            var candidate = Path.Combine(basePath, $"{tempDirPrefix}{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                Logger.Verbose(
                    ex,
                    "Failed to create a same-volume temp directory at {Path}",
                    candidate
                );
            }
        }

        return null;
    }

    /// <summary>
    ///     Places to try for a temp directory on <paramref name="volumeRoot" />, best first. The system
    ///     temp directory is preferred when it happens to be on that volume AND its path is safe to
    ///     hand to chdman (pure ASCII, below MAX_PATH) - it needs no special permissions; otherwise
    ///     the same drive-root folder <see cref="GetBestTempDirectory" /> uses comes first, so startup
    ///     cleanup already knows to look there.
    /// </summary>
    private static IEnumerable<string> GetSameVolumeTempBasePaths(string volumeRoot)
    {
        var systemTemp = Path.GetTempPath();
        string? systemTempRoot = null;
        try
        {
            systemTempRoot = Path.GetPathRoot(systemTemp);
        }
        catch (Exception ex)
        {
            Logger.Verbose(ex, "Failed to get the volume root of the system temp directory");
        }

        var systemTempOnVolume =
            !string.IsNullOrEmpty(systemTempRoot)
            && string.Equals(systemTempRoot, volumeRoot, StringComparison.OrdinalIgnoreCase);

        if (systemTempOnVolume && IsChdmanSafePath(systemTemp)) yield return systemTemp;

        yield return Path.Combine(
            volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar,
            "BatchConvertToCHD_Temp"
        );

        // Last resort: an unsafe %TEMP% path still usually beats failing outright - the generated
        // cue may still convert if chdman tolerates the path, and the failure message will name it.
        if (systemTempOnVolume && !IsChdmanSafePath(systemTemp)) yield return systemTemp;
    }

    private static bool IsRootDirectoryWritable(string rootPath)
    {
        var testDir = Path.Combine(rootPath, $"writetest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testDir);
            Directory.Delete(testDir);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Verbose(ex, "Failed to test write access for root {RootPath}", rootPath);
            try
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir);
            }
            catch
            {
                /* ignored */
            }

            return false;
        }
    }

    /// <summary>
    ///     Collects all base paths that may contain BatchConvertToCHD temp directories,
    ///     for use by startup cleanup. Includes the system temp path and the
    ///     BatchConvertToCHD_Temp folder on the root of every ready fixed drive.
    /// </summary>
    internal static IEnumerable<string> GetPossibleTempBasePaths()
    {
        var paths = new List<string> { Path.GetTempPath() };

        foreach (var drive in DriveInfo.GetDrives())
            try
            {
                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                {
                    var altPath = Path.Combine(
                        drive.RootDirectory.FullName.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        ),
                        "BatchConvertToCHD_Temp"
                    );
                    if (Directory.Exists(altPath))
                        paths.Add(altPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Verbose(ex, "Failed to enumerate drive during temp-path discovery");
            }

        return paths;
    }

    /// <summary>
    ///     Validates and normalizes a directory path. Returns null if invalid.
    /// </summary>
    internal static string? ValidateAndNormalizePath(
        string? path,
        string pathName,
        Action<string> onError,
        Action<string> onLog
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                onError($"Please select a {pathName}.");
                return null;
            }

            var normalizedPath = Path.GetFullPath(path);

            if (!Directory.Exists(normalizedPath))
            {
                onLog($"ERROR: {pathName} does not exist: {normalizedPath}");
                onError(
                    $"The {pathName} does not exist or is not accessible:\n\n{normalizedPath}\n\nPlease verify the path and try again."
                );
                return null;
            }

            onLog($"Validated {pathName}: {normalizedPath}");
            return normalizedPath;
        }
        catch (Exception ex)
        {
            onLog($"ERROR: Invalid path for {pathName}: {path}. {ex.Message}");
            onError($"The {pathName} path is invalid:\n\n{path}\n\nError: {ex.Message}");
            return null;
        }
    }
}