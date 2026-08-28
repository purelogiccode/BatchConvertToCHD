using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class RetryingFileOperationsTests : IDisposable
{
    private readonly string _tempDir;

    public RetryingFileOperationsTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"RetryingFileOperationsTests_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryDeleteAsyncExistingFileDeletesAndReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "game.bin");
        await File.WriteAllTextAsync(path, "data");

        var deleted = await RetryingFileOperations.TryDeleteAsync(path, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TryDeleteAsyncMissingFileReturnsTrue()
    {
        var deleted = await RetryingFileOperations.TryDeleteAsync(
            Path.Combine(_tempDir, "missing.bin"),
            CancellationToken.None
        );

        Assert.True(deleted);
    }

    [Fact]
    public async Task TryDeleteAsyncReadOnlyFileClearsAttributeAndDeletes()
    {
        var path = Path.Combine(_tempDir, "readonly.bin");
        await File.WriteAllTextAsync(path, "data");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var deleted = await RetryingFileOperations.TryDeleteAsync(
                path,
                CancellationToken.None,
                backoffMsProvider: static _ => 1
            );

            Assert.True(deleted);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task TryDeleteAsyncLockedFileRetriesThenGivesUp()
    {
        var path = Path.Combine(_tempDir, "locked.bin");
        await File.WriteAllTextAsync(path, "data");
        var retries = 0;

        // Hold an exclusive lock for the whole call so every attempt fails.
        await using var lockStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );
        var deleted = await RetryingFileOperations.TryDeleteAsync(
            path,
            CancellationToken.None,
            _ => { retries++; },
            static _ => 1
        );

        Assert.False(deleted);
        Assert.Equal(RetryingFileOperations.MaxDeleteAttempts - 1, retries);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task TryDeleteAsyncLockedFileSucceedsAfterLockReleased()
    {
        var path = Path.Combine(_tempDir, "released.bin");
        await File.WriteAllTextAsync(path, "data");
        var attempts = 0;
        var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            var deleted = await RetryingFileOperations.TryDeleteAsync(
                path,
                CancellationToken.None,
                _ =>
                {
                    attempts++;
                    if (attempts == 2)
                        // Release the lock so the next attempt succeeds.
                        lockStream.Dispose();
                },
                static _ => 1
            );

            Assert.True(deleted);
            Assert.False(File.Exists(path));
        }
        finally
        {
            lockStream.Dispose();
        }
    }

    [Fact]
    public async Task TryMoveAsyncMovesFileAndReturnsTrue()
    {
        var source = Path.Combine(_tempDir, "move-src.bin");
        var dest = Path.Combine(_tempDir, "move-dst.bin");
        await File.WriteAllTextAsync(source, "data");

        var moved = await RetryingFileOperations.TryMoveAsync(source, dest, CancellationToken.None);

        Assert.True(moved);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task TryMoveAsyncMissingSourceReturnsTrue()
    {
        var moved = await RetryingFileOperations.TryMoveAsync(
            Path.Combine(_tempDir, "missing-src.bin"),
            Path.Combine(_tempDir, "missing-dst.bin"),
            CancellationToken.None
        );

        Assert.True(moved);
    }

    [Fact]
    public async Task TryMoveAsyncMissingDestinationDirectoryReturnsFalse()
    {
        var source = Path.Combine(_tempDir, "move-no-dir-src.bin");
        var dest = Path.Combine(_tempDir, "does-not-exist", "move-no-dir-dst.bin");
        await File.WriteAllTextAsync(source, "data");

        var moved = await RetryingFileOperations.TryMoveAsync(
            source,
            dest,
            CancellationToken.None,
            static _ => { },
            static _ => 1
        );

        Assert.False(moved);
        Assert.True(File.Exists(source), "source must remain in place when the move fails");
    }

    [Fact]
    public async Task TryMoveAsyncLockedFileRetriesThenGivesUp()
    {
        var source = Path.Combine(_tempDir, "move-locked.bin");
        var dest = Path.Combine(_tempDir, "move-locked-dst.bin");
        await File.WriteAllTextAsync(source, "data");
        var retries = 0;

        // Hold an exclusive lock for the whole call so every attempt fails.
        await using var lockStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );
        var moved = await RetryingFileOperations.TryMoveAsync(
            source,
            dest,
            CancellationToken.None,
            _ => { retries++; },
            static _ => 1
        );

        Assert.False(moved);
        Assert.Equal(RetryingFileOperations.MaxDeleteAttempts - 1, retries);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task TryMoveAsyncLockedFileSucceedsAfterLockReleased()
    {
        var source = Path.Combine(_tempDir, "move-released.bin");
        var dest = Path.Combine(_tempDir, "move-released-dst.bin");
        await File.WriteAllTextAsync(source, "data");
        var attempts = 0;
        var lockStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            var moved = await RetryingFileOperations.TryMoveAsync(
                source,
                dest,
                CancellationToken.None,
                _ =>
                {
                    attempts++;
                    if (attempts == 2)
                        // Release the lock so the next attempt succeeds.
                        lockStream.Dispose();
                },
                static _ => 1
            );

            Assert.True(moved);
            Assert.False(File.Exists(source));
            Assert.True(File.Exists(dest));
        }
        finally
        {
            lockStream.Dispose();
        }
    }
}