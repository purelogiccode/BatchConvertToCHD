using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     File operations that retry with backoff, used when files may be temporarily locked by
///     another process (e.g. antivirus scanning a freshly written CHD before the original file
///     is deleted).
/// </summary>
internal static class RetryingFileOperations
{
    internal const int MaxDeleteAttempts = 10;

    /// <summary>Backoff schedule in milliseconds per attempt (0-based). Total ≈ 45 s.</summary>
    internal static int GetDeleteBackoffMs(int attempt)
    {
        return attempt switch
        {
            0 => 500,
            1 => 1000,
            2 => 2000,
            3 => 4000,
            4 => 6000,
            _ => 8000
        };
    }

    /// <summary>
    ///     Attempts to delete <paramref name="path" />, retrying with backoff while the file is
    ///     locked. Returns true when deleted or already gone, false after all attempts failed.
    ///     Read-only files have the ReadOnly attribute cleared once so the deletion can proceed;
    ///     other permanent failures (e.g. access denied) fail fast instead of retrying pointlessly.
    /// </summary>
    /// <param name="path">Path of the file to delete.</param>
    /// <param name="token">Cancellation token; cancelling aborts the retry loop.</param>
    /// <param name="onRetry">Called with the 0-based attempt number before each retry.</param>
    /// <param name="backoffMsProvider">Optional backoff override (used by tests).</param>
    internal static async Task<bool> TryDeleteAsync(
        string path,
        CancellationToken token,
        Action<int>? onRetry = null,
        Func<int, int>? backoffMsProvider = null
    )
    {
        var clearedReadOnly = false;
        for (var attempt = 0; attempt < MaxDeleteAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => File.Delete(path), token).ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
                if (attempt >= MaxDeleteAttempts - 1) return false;

                onRetry?.Invoke(attempt);
                await DelayAsync(backoffMsProvider, attempt, token).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // Typically the ReadOnly attribute (or an ACL). Clear the attribute once and
                // retry; if it still fails, the file is ACL-protected and retrying won't help.
                if (clearedReadOnly) return false;

                clearedReadOnly = true;
                TryClearReadOnly(path);
                onRetry?.Invoke(attempt);
                await DelayAsync(backoffMsProvider, attempt, token).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    ///     Attempts to move <paramref name="sourcePath" /> to <paramref name="destinationPath" />,
    ///     retrying with backoff while the source is temporarily locked (e.g. antivirus scanning a
    ///     freshly written CHD, or another process still holding the file open). Returns true when
    ///     the move succeeded or the source is already gone, false after all attempts failed.
    /// </summary>
    /// <param name="sourcePath">Path of the file to move.</param>
    /// <param name="destinationPath">Destination path for the move.</param>
    /// <param name="token">Cancellation token; cancelling aborts the retry loop.</param>
    /// <param name="onRetry">Called with the 0-based attempt number before each retry.</param>
    /// <param name="backoffMsProvider">Optional backoff override (used by tests).</param>
    internal static async Task<bool> TryMoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken token,
        Action<int>? onRetry = null,
        Func<int, int>? backoffMsProvider = null
    )
    {
        for (var attempt = 0; attempt < MaxDeleteAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => File.Move(sourcePath, destinationPath), token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                // Source already gone — nothing to move.
                return true;
            }
            catch (IOException)
            {
                // Includes DirectoryNotFoundException (missing destination directory, e.g.
                // disconnected network path): retry in case it resolves, then report failure.
                // Never treat a failed move as success — the source file still exists.
                if (attempt >= MaxDeleteAttempts - 1) return false;

                onRetry?.Invoke(attempt);
                await DelayAsync(backoffMsProvider, attempt, token).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // ACL-protected paths won't resolve with retries; fail fast.
                return false;
            }
        }

        return false;
    }

    private static async Task DelayAsync(
        Func<int, int>? backoffMsProvider,
        int attempt,
        CancellationToken token
    )
    {
        var delayMs = (backoffMsProvider ?? GetDeleteBackoffMs)(attempt);
        if (delayMs > 0) await Task.Delay(delayMs, token).ConfigureAwait(false);
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
#pragma warning disable RCS1075
        catch (Exception)
#pragma warning restore RCS1075
        {
            // ignored
        }
    }
}