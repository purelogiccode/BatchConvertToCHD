using VideoGameFileSystemParser.Models;
using VideoGameFileSystemParser.Parsers;

namespace BatchConvertToCHD.Services;

/// <summary>
///     UI-agnostic explorer over one CHD image. The image is opened with CHDSharp through
///     VideoGameFileSystemParser and parsed once with the selected console file system parser;
///     directory listings and file reads are then served from that parsed tree. Not thread-safe:
///     callers must serialize access (the Explorer tab leases the service for every operation).
/// </summary>
internal sealed class ChdExplorerService : IDisposable
{
    private const int CopyBufferSize = 256 * 1024;

    private readonly ChdContainer _container;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ChdExplorerService" /> class.
    /// </summary>
    /// <param name="chdPath">Path of the CHD image that was opened.</param>
    /// <param name="container">The parsed container backing this explorer.</param>
    private ChdExplorerService(string chdPath, ChdContainer container)
    {
        ChdPath = chdPath;
        _container = container;
    }

    /// <summary>
    ///     Gets the path of the CHD image that is open.
    /// </summary>
    public string ChdPath { get; }

    /// <summary>
    ///     Gets the volume label reported by the parsed file system.
    /// </summary>
    public string VolumeName => _container.VolumeName;

    /// <summary>
    ///     Gets the size of the parsed volume in bytes.
    /// </summary>
    public ulong VolumeSize => _container.VolumeSize;

    /// <summary>
    ///     Gets the console type the image was parsed as.
    /// </summary>
    public ConsoleType ConsoleType => _container.ConsoleType;

    /// <summary>
    ///     Opens a CHD image and parses it with the given console parser.
    /// </summary>
    /// <param name="chdPath">Path of the CHD image to open.</param>
    /// <param name="consoleType">The file system parser to use.</param>
    /// <param name="error">Receives the failure reason when the image cannot be parsed.</param>
    /// <returns>The opened explorer, or <see langword="null" /> when parsing fails.</returns>
    public static ChdExplorerService? TryOpen(string chdPath, ConsoleType consoleType, out string? error)
    {
        ChdContainer? container = null;
        try
        {
            container = new ChdContainer(chdPath);
            if (!container.MountAndParse(consoleType))
            {
                container.Dispose();
                error = "the file system could not be parsed";
                return null;
            }

            error = null;
            return new ChdExplorerService(chdPath, container);
        }
        catch (Exception ex)
        {
            container?.Dispose();
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    ///     Lists the direct children of a directory inside the image.
    /// </summary>
    /// <param name="internalPath">Directory path inside the image (<c>"\"</c> or <c>"/"</c> for the root).</param>
    /// <returns>The entries directly inside the directory.</returns>
    public IReadOnlyList<FileEntry> ListDirectory(string internalPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _container.ListDirectory(internalPath).ToList();
    }

    /// <summary>
    ///     Extracts a file or directory (recursively) from the image to a local directory.
    /// </summary>
    /// <param name="entry">The entry to extract.</param>
    /// <param name="destinationDirectory">Local directory that receives the extracted entry.</param>
    /// <param name="progress">Receives the extraction progress as a 0-1 fraction, when supplied.</param>
    /// <param name="token">Cancellation token checked between read operations.</param>
    /// <returns>The local path the entry was extracted to.</returns>
    public string ExtractEntry(
        FileEntry entry,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken token
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var destinationPath = Path.Combine(destinationDirectory, SanitizeName(entry.Name));

        if (entry.IsDirectory)
        {
            var total = GetDirectorySize(entry, token);
            var copied = 0UL;
            ExtractDirectory(entry, destinationPath, total, ref copied, progress, token);
        }
        else
        {
            var copied = 0UL;
            ExtractFile(entry, destinationPath, entry.Size, ref copied, progress, token);
        }

        return destinationPath;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _container.Dispose();
    }

    /// <summary>
    ///     Validates an entry name from the image before it is used as a local file name.
    /// </summary>
    /// <param name="name">The raw entry name.</param>
    /// <returns>The trimmed, safe name.</returns>
    /// <exception cref="InvalidDataException">Thrown when the name could escape the destination directory.</exception>
    private static string SanitizeName(string name)
    {
        var trimmed = name.Trim();
        if (
            trimmed.Length == 0
            || trimmed is "." or ".."
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmed.Contains('/')
            || trimmed.Contains('\\')
        )
        {
            throw new InvalidDataException($"The image contains an unsafe entry name: '{name}'.");
        }

        return trimmed;
    }

    /// <summary>
    ///     Recursively sums the sizes of all files inside a directory entry.
    /// </summary>
    /// <param name="entry">The directory entry to measure.</param>
    /// <param name="token">Cancellation token checked between listing operations.</param>
    /// <returns>The total size in bytes.</returns>
    private ulong GetDirectorySize(FileEntry entry, CancellationToken token)
    {
        var size = 0UL;
        foreach (var child in _container.ListDirectory(entry.FullPath))
        {
            token.ThrowIfCancellationRequested();
            size += child.IsDirectory ? GetDirectorySize(child, token) : child.Size;
        }

        return size;
    }

    /// <summary>
    ///     Extracts a directory entry recursively.
    /// </summary>
    /// <param name="entry">The directory entry to extract.</param>
    /// <param name="destinationPath">Local directory to create.</param>
    /// <param name="total">Total bytes of the whole extraction, for progress reporting.</param>
    /// <param name="copied">Running byte count shared across the recursion.</param>
    /// <param name="progress">Receives the extraction progress as a 0-1 fraction, when supplied.</param>
    /// <param name="token">Cancellation token checked between operations.</param>
    private void ExtractDirectory(
        FileEntry entry,
        string destinationPath,
        ulong total,
        ref ulong copied,
        IProgress<double>? progress,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationPath);

        foreach (var child in _container.ListDirectory(entry.FullPath))
        {
            var childPath = Path.Combine(destinationPath, SanitizeName(child.Name));
            if (child.IsDirectory)
            {
                ExtractDirectory(child, childPath, total, ref copied, progress, token);
            }
            else
            {
                ExtractFile(child, childPath, total, ref copied, progress, token);
            }
        }
    }

    /// <summary>
    ///     Extracts a single file entry in chunks.
    /// </summary>
    /// <param name="entry">The file entry to extract.</param>
    /// <param name="destinationPath">Local file path to create.</param>
    /// <param name="total">Total bytes of the whole extraction, for progress reporting.</param>
    /// <param name="copied">Running byte count shared across the recursion.</param>
    /// <param name="progress">Receives the extraction progress as a 0-1 fraction, when supplied.</param>
    /// <param name="token">Cancellation token checked between read operations.</param>
    private void ExtractFile(
        FileEntry entry,
        string destinationPath,
        ulong total,
        ref ulong copied,
        IProgress<double>? progress,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        var buffer = new byte[CopyBufferSize];
        using var output = File.Create(destinationPath);

        var offset = 0UL;
        while (offset < entry.Size)
        {
            token.ThrowIfCancellationRequested();
            var read = _container.ReadFile(entry, offset, buffer, 0, buffer.Length);
            if (read <= 0) break;

            output.Write(buffer, 0, read);
            offset += (ulong)read;
            copied += (ulong)read;

            if (progress is not null && total > 0)
            {
                progress.Report(Math.Min(1.0, (double)copied / total));
            }
        }
    }
}
