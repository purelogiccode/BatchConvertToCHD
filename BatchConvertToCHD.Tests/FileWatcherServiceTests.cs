using BatchConvertToCHD.Models;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class FileWatcherServiceTests : IDisposable
{
    private readonly FileWatcherService _service;
    private readonly string _tempDir;

    public FileWatcherServiceTests()
    {
        _service = new FileWatcherService();
        _tempDir = Path.Combine(Path.GetTempPath(), $"FileWatcherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignored
        }

        GC.SuppressFinalize(this);
    }

    #region Constructor / Initial state

    [Fact]
    public void InitiallyIsNotWatching()
    {
        Assert.False(_service.IsWatching);
        Assert.Null(_service.WatchedFolder);
    }

    #endregion

    #region GetContextForMissingFile - not watching

    [Fact]
    public void GetContextForMissingFile_WhenNotWatching_ReturnsNull()
    {
        var result = _service.GetContextForMissingFile(@"C:\test.iso");

        Assert.Null(result);
    }

    #endregion

    #region RecordEvent - event type storage (via reflection)

    [Fact]
    public void RecordEvent_StoresCorrectEventTypes()
    {
        var createdPath = Path.Combine(_tempDir, "c.bin");
        var deletedPath = Path.Combine(_tempDir, "d.bin");
        var renamedFromPath = Path.Combine(_tempDir, "rf.bin");
        var renamedToPath = Path.Combine(_tempDir, "rt.bin");

        _service.RecordEvent(createdPath, FileWatchEventType.Created, null);
        _service.RecordEvent(deletedPath, FileWatchEventType.Deleted, null);
        _service.RecordEvent(renamedFromPath, FileWatchEventType.RenamedFrom, "newname.bin");
        _service.RecordEvent(renamedToPath, FileWatchEventType.RenamedTo, "oldname.bin");

        Assert.True(_service.TryGetRecordedEvent(createdPath, out var cr));
        Assert.Equal(FileWatchEventType.Created, cr!.EventType);

        Assert.True(_service.TryGetRecordedEvent(deletedPath, out var dr));
        Assert.Equal(FileWatchEventType.Deleted, dr!.EventType);

        Assert.True(_service.TryGetRecordedEvent(renamedFromPath, out var rfr));
        Assert.Equal(FileWatchEventType.RenamedFrom, rfr!.EventType);
        Assert.Equal("newname.bin", rfr.RelatedName);

        Assert.True(_service.TryGetRecordedEvent(renamedToPath, out var rtr));
        Assert.Equal(FileWatchEventType.RenamedTo, rtr!.EventType);
        Assert.Equal("oldname.bin", rtr.RelatedName);
    }

    #endregion

    #region StartWatching

    [Fact]
    public void StartWatching_ValidFolder_SetsIsWatching()
    {
        _service.StartWatching(_tempDir);

        Assert.True(_service.IsWatching);
        Assert.Equal(_tempDir, _service.WatchedFolder);
    }

    [Fact]
    public void StartWatching_InvalidFolder_DoesNotThrowAndDoesNotWatch()
    {
        var ex = Record.Exception(() =>
            _service.StartWatching(@"Z:\NonExistent\Path\DoesNotExist")
        );

        Assert.Null(ex);
        Assert.False(_service.IsWatching);
    }

    [Fact]
    public void StartWatching_SecondCallStopsFirst()
    {
        var secondDir = Path.Combine(_tempDir, "second");
        Directory.CreateDirectory(secondDir);

        _service.StartWatching(_tempDir);
        _service.StartWatching(secondDir);

        Assert.True(_service.IsWatching);
        Assert.Equal(secondDir, _service.WatchedFolder);
    }

    #endregion

    #region StopWatching

    [Fact]
    public void StopWatching_ClearsState()
    {
        _service.StartWatching(_tempDir);
        _service.StopWatching();

        Assert.False(_service.IsWatching);
        Assert.Null(_service.WatchedFolder);
    }

    [Fact]
    public void StopWatching_WhenNotWatching_DoesNotThrow()
    {
        var ex = Record.Exception(() => _service.StopWatching());

        Assert.Null(ex);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_StopsWatching()
    {
        _service.StartWatching(_tempDir);
        _service.Dispose();

        Assert.False(_service.IsWatching);
    }

    [Fact]
    public void Dispose_WhenNotWatching_DoesNotThrow()
    {
        var ex = Record.Exception(() => _service.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        _service.Dispose();

        var ex = Record.Exception(() => _service.Dispose());

        Assert.Null(ex);
    }

    #endregion

    #region GetContextForMissingFile - never observed file

    [Fact]
    public void GetContextForMissingFile_OutsideWatchedFolder_ReturnsOutsideDiagnostic()
    {
        _service.StartWatching(_tempDir);

        var result = _service.GetContextForMissingFile(@"C:\some\other\path\file.iso");

        Assert.NotNull(result);
        Assert.Contains("outside", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetContextForMissingFile_SiblingFolderNamePrefix_DetectsAsOutside()
    {
        _service.StartWatching(_tempDir);

        var siblingPath = _tempDir + "2" + Path.DirectorySeparatorChar + "file.iso";
        var result = _service.GetContextForMissingFile(siblingPath);

        Assert.NotNull(result);
        Assert.Contains("outside", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetContextForMissingFile_WatchedFolderDeleted_ReturnsNotAccessible()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        _service.StartWatching(subDir);
        Directory.Delete(subDir, true);

        var filePath = Path.Combine(subDir, "missing.iso");
        var result = _service.GetContextForMissingFile(filePath);

        Assert.NotNull(result);
        Assert.Contains("no longer accessible", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetContextForMissingFile_FileNeverObserved_ReturnsNotObserved()
    {
        _service.StartWatching(_tempDir);

        var filePath = Path.Combine(_tempDir, "never_existed.iso");
        var result = _service.GetContextForMissingFile(filePath);

        Assert.NotNull(result);
        Assert.Contains("not observed", result, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region GetContextForMissingFile - real file system events

    [Fact]
    public async Task GetContextForMissingFile_DeletedFile_ReturnsDeleteDiagnostic()
    {
        var filePath = Path.Combine(_tempDir, "to_delete.bin");
        await File.WriteAllTextAsync(filePath, "test");

        _service.StartWatching(_tempDir);
        await Task.Delay(150);

        File.Delete(filePath);
        await Task.Delay(300);

        var result = _service.GetContextForMissingFile(filePath);

        Assert.NotNull(result);
        Assert.Contains("deleted", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetContextForMissingFile_RenamedFile_ReturnsRenameFromDiagnostic()
    {
        var oldPath = Path.Combine(_tempDir, "old.bin");
        var newPath = Path.Combine(_tempDir, "new.bin");
        await File.WriteAllTextAsync(oldPath, "test");

        _service.StartWatching(_tempDir);
        await Task.Delay(150);

        File.Move(oldPath, newPath);
        await Task.Delay(300);

        var result = _service.GetContextForMissingFile(oldPath);

        Assert.NotNull(result);
        Assert.Contains("renamed to", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new.bin", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContextForMissingFile_RenamedThenDeleted_LastEventWins()
    {
        var oldPath = Path.Combine(_tempDir, "source.bin");
        var newPath = Path.Combine(_tempDir, "destination.bin");
        await File.WriteAllTextAsync(oldPath, "test");

        _service.StartWatching(_tempDir);
        await Task.Delay(150);

        File.Move(oldPath, newPath);
        await Task.Delay(300);

        File.Delete(newPath);
        await Task.Delay(300);

        var result = _service.GetContextForMissingFile(newPath);

        Assert.NotNull(result);
        Assert.Contains("deleted", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetContextForMissingFile_CreatedThenDeleted_LastEventWins()
    {
        var filePath = Path.Combine(_tempDir, "transient.bin");

        _service.StartWatching(_tempDir);
        await Task.Delay(150);

        await File.WriteAllTextAsync(filePath, "test");
        await Task.Delay(300);

        File.Delete(filePath);
        await Task.Delay(300);

        var result = _service.GetContextForMissingFile(filePath);

        Assert.NotNull(result);
        Assert.Contains("deleted", result, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region RecordEvent - eviction

    [Fact]
    public void RecordEvent_EvictsOldestEntriesWhenOverLimit()
    {
        const int maxHistory = 1000;

        for (var i = 0; i < maxHistory + 1; i++)
        {
            var filePath = Path.Combine(_tempDir, $"file{i:D4}.bin");
            _service.RecordEvent(filePath, FileWatchEventType.Created, null);
        }

        Assert.False(_service.TryGetRecordedEvent(Path.Combine(_tempDir, "file0000.bin"), out _));
        Assert.True(_service.TryGetRecordedEvent(Path.Combine(_tempDir, "file1000.bin"), out _));
    }

    [Fact]
    public void RecordEvent_UnderLimit_KeepsAllEntries()
    {
        for (var i = 0; i < 10; i++)
        {
            var filePath = Path.Combine(_tempDir, $"keep{i}.bin");
            _service.RecordEvent(filePath, FileWatchEventType.Deleted, null);
        }

        Assert.Equal(10, _service.RecordedEventCount);
        for (var i = 0; i < 10; i++)
            Assert.True(_service.TryGetRecordedEvent(Path.Combine(_tempDir, $"keep{i}.bin"), out _));
    }

    [Fact]
    public void RecordEvent_UpdateExistingKey_ReusesQueueSlot()
    {
        var filePath = Path.Combine(_tempDir, "reused.bin");

        _service.RecordEvent(filePath, FileWatchEventType.Created, null);
        _service.RecordEvent(filePath, FileWatchEventType.Deleted, null);

        Assert.Equal(1, _service.RecordedEventCount);
        Assert.True(_service.TryGetRecordedEvent(filePath, out var record));
        Assert.Equal(FileWatchEventType.Deleted, record!.EventType);
    }

    #endregion

    #region OnError - buffer overflow

    [Fact]
    public void OnError_BufferOverflow_ClearsHistory()
    {
        _service.RecordEvent(
            Path.Combine(_tempDir, "test.bin"),
            FileWatchEventType.Created,
            null
        );
        Assert.Equal(1, _service.RecordedEventCount);

        var overflowEx = new InternalBufferOverflowException("Buffer overflow");
        var errorEventArgs = new ErrorEventArgs(overflowEx);
        _service.OnError(null!, errorEventArgs);

        Assert.Equal(0, _service.RecordedEventCount);
    }

    [Fact]
    public void OnError_NonBufferOverflow_DoesNotClearHistory()
    {
        var filePath = Path.Combine(_tempDir, "keep_me.bin");
        _service.RecordEvent(filePath, FileWatchEventType.Created, null);
        Assert.Equal(1, _service.RecordedEventCount);

        var fileNotFoundEx = new FileNotFoundException("File not found");
        var errorEventArgs = new ErrorEventArgs(fileNotFoundEx);
        _service.OnError(null!, errorEventArgs);

        Assert.Equal(1, _service.RecordedEventCount);
        Assert.True(_service.TryGetRecordedEvent(filePath, out _));
    }

    #endregion
}