using System.Globalization;
using VideoGameFileSystemParser.Models;

namespace CHDStudio.Models;

/// <summary>
///     One row in the Explorer grid: a file or directory inside the open CHD image, with the
///     display strings the DataGrid columns bind to.
/// </summary>
internal sealed class ChdExplorerItem
{
    /// <summary>
    ///     Gets the entry name (without path).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     Gets a value indicating whether the entry is a directory.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    ///     Gets the formatted size for files (empty for directories).
    /// </summary>
    public string SizeFormatted { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the formatted modified time (empty when the file system does not report one).
    /// </summary>
    public string ModifiedFormatted { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the row type label ("Folder" or "File").
    /// </summary>
    public string Type => IsDirectory ? "Folder" : "File";

    /// <summary>
    ///     Gets the folder/file glyph shown before the name.
    /// </summary>
    public string Icon => IsDirectory ? "📁" : "📄";

    /// <summary>
    ///     Gets the underlying parser entry used for navigation and extraction.
    /// </summary>
    public FileEntry Entry { get; init; } = null!;

    /// <summary>
    ///     Creates a grid row from a parser entry.
    /// </summary>
    /// <param name="entry">The entry to wrap.</param>
    /// <returns>The display row.</returns>
    internal static ChdExplorerItem FromEntry(FileEntry entry)
    {
        return new ChdExplorerItem
        {
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            SizeFormatted = entry.IsDirectory ? string.Empty : FormatSize(entry.Size),
            ModifiedFormatted =
                entry.ModifiedTime == default
                    ? string.Empty
                    : entry.ModifiedTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Entry = entry
        };
    }

    /// <summary>
    ///     Formats a byte count as a human-readable size (e.g. "1.5 GB").
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The formatted size.</returns>
    internal static string FormatSize(ulong bytes)
    {
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        double size = bytes;
        while (size >= 1024 && index < suffix.Length - 1)
        {
            size /= 1024;
            index++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {suffix[index]}");
    }
}