using System.Collections.Concurrent;
using System.Globalization;
using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Monitors the input folder for file changes (deletes, renames, creates) to
///     provide diagnostic context when a "File not found" error occurs during
///     batch processing.
/// </summary>
internal sealed class FileWatcherService : IDisposable
{
    private const int MaxFileHistory = 1000;
    private const int BufferSize = 65536;

    private readonly ConcurrentDictionary<string, FileEventRecord> _lastEventByFile = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly Lock _historyLock = new();
    private readonly ConcurrentQueue<string> _trackedKeys = new();

    private FileSystemWatcher? _watcher;

    internal bool IsWatching { get; private set; }

    internal string? WatchedFolder { get; private set; }

    /// <summary>
    ///     Gets the number of files with a recorded event. Exposed for diagnostics and tests.
    /// </summary>
    internal int RecordedEventCount => _lastEventByFile.Count;

    /// <summary>
    ///     Gets the recorded event for a file path, when one exists. Exposed for diagnostics and tests.
    /// </summary>
    /// <param name="filePath">Full path of the file.</param>
    /// <param name="record">When this method returns, contains the recorded event.</param>
    /// <returns><see langword="true" /> when an event was recorded for the path.</returns>
    internal bool TryGetRecordedEvent(string filePath, out FileEventRecord? record)
    {
        return _lastEventByFile.TryGetValue(filePath, out record);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopWatching();
    }

    /// <summary>
    ///     Starts monitoring the specified folder for file changes.
    ///     Stops any previous watch first.
    /// </summary>
    /// <param name="folderPath">The root folder to watch (subdirectories included).</param>
    internal void StartWatching(string folderPath)
    {
        StopWatching();

        if (!Directory.Exists(folderPath))
            return;

        try
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = BufferSize
            };

            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Created += OnCreated;
            watcher.Error += OnError;

            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            WatchedFolder = folderPath;
            IsWatching = true;
        }
        catch (ArgumentException)
        {
            // Path is invalid or on an unsupported drive (e.g., network with no share)
        }
        catch (FileNotFoundException)
        {
            // Drive disconnected during setup
        }
    }

    /// <summary>
    ///     Stops monitoring and clears any pending state.
    /// </summary>
    internal void StopWatching()
    {
        IsWatching = false;
        WatchedFolder = null;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Created -= OnCreated;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        _lastEventByFile.Clear();
        while (_trackedKeys.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    ///     Queries the watcher history for a given file path and returns a
    ///     human-readable summary of recent events, or <c>null</c> if no
    ///     relevant history was found.
    /// </summary>
    /// <param name="filePath">The full path of the file that could not be found.</param>
    /// <returns>A diagnostic message, or <c>null</c>.</returns>
    internal string? GetContextForMissingFile(string filePath)
    {
        if (!IsWatching)
            return null;

        if (!_lastEventByFile.TryGetValue(filePath, out var record))
        {
            // File was never seen by the watcher. This could mean:
            // - The drive was disconnected before watcher started
            // - The path is outside the watched folder tree
            // - The watcher buffer overflowed
            if (WatchedFolder != null && !IsPathUnderFolder(filePath, WatchedFolder))
                return "The file path is outside the monitored input folder.";

            if (!Directory.Exists(WatchedFolder))
            {
                return
                    "The input folder is no longer accessible (drive may have been disconnected or network share lost).";
            }

            return
                "This file was not observed by the file watcher. It may have been removed before monitoring started or the input folder may be on a removable/external drive.";
        }

        return record.EventType switch
        {
            FileWatchEventType.Deleted => string.Format(
                CultureInfo.InvariantCulture,
                "This file was detected as deleted at {0:HH:mm:ss} (during batch processing). It may have been moved or removed by another process.",
                record.Timestamp
            ),

            FileWatchEventType.RenamedFrom => string.Format(
                CultureInfo.InvariantCulture,
                "This file was renamed to '{0}' at {1:HH:mm:ss} (during batch processing).",
                record.RelatedName ?? "(unknown)",
                record.Timestamp
            ),

            FileWatchEventType.RenamedTo => string.Format(
                CultureInfo.InvariantCulture,
                "This file was renamed from '{0}' at {1:HH:mm:ss} (during batch processing).",
                record.RelatedName ?? "(unknown)",
                record.Timestamp
            ),

            FileWatchEventType.Created => string.Format(
                CultureInfo.InvariantCulture,
                "This file was seen created at {0:HH:mm:ss} but is no longer present.",
                record.Timestamp
            ),

            _ => null
        };
    }

    /// <summary>Records a deletion event for the affected file.</summary>
    /// <param name="sender">The file-system watcher.</param>
    /// <param name="e">Event data carrying the file path.</param>
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        RecordEvent(e.FullPath, FileWatchEventType.Deleted, null);
    }

    /// <summary>Records both sides of a rename event.</summary>
    /// <param name="sender">The file-system watcher.</param>
    /// <param name="e">Event data carrying the old and new paths.</param>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        RecordEvent(e.OldFullPath, FileWatchEventType.RenamedFrom, e.Name);
        RecordEvent(e.FullPath, FileWatchEventType.RenamedTo, e.OldName);
    }

    /// <summary>Records a creation event for the affected file.</summary>
    /// <param name="sender">The file-system watcher.</param>
    /// <param name="e">Event data carrying the file path.</param>
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        RecordEvent(e.FullPath, FileWatchEventType.Created, null);
    }

    /// <summary>
    ///     Handles watcher errors; an internal buffer overflow clears the history so stale events
    ///     are not presented as accurate.
    /// </summary>
    /// <param name="sender">The file-system watcher.</param>
    /// <param name="e">Event data carrying the exception.</param>
    internal void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        // Internal buffer overflow - some events were lost. This is expected
        // on folders with very high file churn; we degrade gracefully by
        // clearing history so stale data is not presented as accurate.
        if (ex is InternalBufferOverflowException)
        {
            lock (_historyLock)
            {
                _lastEventByFile.Clear();
                while (_trackedKeys.TryDequeue(out _))
                {
                }
            }
        }
    }

    /// <summary>
    ///     Returns whether <paramref name="filePath" /> lies inside <paramref name="folderPath" />.
    /// </summary>
    /// <param name="filePath">The file path to test.</param>
    /// <param name="folderPath">The watched folder.</param>
    /// <returns><see langword="true" /> when the file is under the folder.</returns>
    private static bool IsPathUnderFolder(string filePath, string folderPath)
    {
        var folderWithSep = folderPath.EndsWith(Path.DirectorySeparatorChar)
            ? folderPath
            : folderPath + Path.DirectorySeparatorChar;

        return filePath.StartsWith(folderWithSep, StringComparison.OrdinalIgnoreCase)
               || string.Equals(filePath, folderPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Stores the latest event for a file, trimming the oldest entries once the history cap is
    ///     reached.
    /// </summary>
    /// <param name="fullPath">Full path of the affected file.</param>
    /// <param name="eventType">Kind of event.</param>
    /// <param name="relatedName">Previous or new name for rename events; null otherwise.</param>
    internal void RecordEvent(string fullPath, FileWatchEventType eventType, string? relatedName)
    {
        var record = new FileEventRecord(DateTime.Now, eventType, relatedName);

        // The add/update and the eviction bookkeeping must be atomic together; otherwise a
        // concurrent history clear can leave a re-added key that is never enqueued (and so never
        // evicted) or evict a newer record.
        lock (_historyLock)
        {
            if (_lastEventByFile.TryAdd(fullPath, record))
            {
                _trackedKeys.Enqueue(fullPath);
            }
            else
            {
                _lastEventByFile[fullPath] = record;
            }

            while (_trackedKeys.Count > MaxFileHistory)
            {
                if (_trackedKeys.TryDequeue(out var key))
                    _lastEventByFile.TryRemove(key, out _);
            }
        }
    }
}