using System.Globalization;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Logs progress for long-running built-in CHDSharp operations at ten-percent steps, so the
///     activity log shows that encoding, verification or extraction is still running without
///     printing one line per hunk.
/// </summary>
internal sealed class ChdSharpProgressLogger : IProgress<ChdProgress>
{
    /// <summary>Progress is logged once per this many percent.</summary>
    private const int ProgressStepPercent = 10;

    private readonly string _activity;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    private int _lastPercent;
    private int _nextPercent = ProgressStepPercent;
    private long _rawBytes;
    private long _storedBytes;

    /// <summary>Initializes a new progress logger.</summary>
    /// <param name="log">Callback that receives the progress messages.</param>
    /// <param name="activity">Activity name used in the messages (e.g. <c>"Compressing"</c>).</param>
    internal ChdSharpProgressLogger(Action<string> log, string activity)
    {
        _log = log;
        _activity = activity;
    }

    /// <summary>Reports progress from a CHDSharp encoder hunk callback.</summary>
    /// <param name="progress">The per-hunk report delivered by <see cref="ChdEncodeOptions.HunkCompleted" />.</param>
    internal void ReportHunk(HunkProgress progress)
    {
        string? message = null;
        lock (_lock)
        {
            _rawBytes += progress.RawBytes;
            _storedBytes += progress.StoredBytes;

            var percent = Percent(progress.HunkIndex + 1, progress.HunkCount);
            if (ShouldReport(percent))
            {
                var ratio = _rawBytes == 0 ? 100.0 : 100.0 * _storedBytes / _rawBytes;
                message =
                    $" CHDSHARP: {_activity}, {PercentText(percent)}% complete... (ratio={ratio.ToString("F1", CultureInfo.InvariantCulture)}%)";
            }
        }

        if (message is not null) _log(message);
    }

    /// <summary>
    ///     Reports byte-level progress for reader operations that do not expose per-hunk reports.
    /// </summary>
    /// <param name="processedBytes">Decompressed bytes processed so far.</param>
    /// <param name="totalBytes">Total decompressed size of the image.</param>
    internal void ReportBytes(long processedBytes, long totalBytes)
    {
        string? message = null;
        lock (_lock)
        {
            var percent = totalBytes <= 0 ? 100 : (int)(processedBytes * 100 / totalBytes);
            if (ShouldReport(percent))
            {
                message =
                    $" CHDSHARP: {_activity}, {PercentText(percent)}% complete... ({processedBytes.ToString("N0", CultureInfo.InvariantCulture)} / {totalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
            }
        }

        if (message is not null) _log(message);
    }

    /// <inheritdoc />
    public void Report(ChdProgress value)
    {
        ReportBytes(value.BytesProcessed, value.TotalBytes);
    }

    /// <summary>
    ///     True when the percent crossed the next ten-percent step, advancing it. A backward jump
    ///     means a reader restarted its count for a new region (e.g. per-track hashing), so a fresh
    ///     milestone sequence begins and later regions are reported too.
    /// </summary>
    /// <param name="percent">The completed percentage.</param>
    /// <returns>True when a message should be logged for this report.</returns>
    private bool ShouldReport(int percent)
    {
        if (percent < _lastPercent) _nextPercent = ProgressStepPercent;

        _lastPercent = percent;

        if (percent < _nextPercent) return false;

        _nextPercent = percent - percent % ProgressStepPercent + ProgressStepPercent;
        return true;
    }

    /// <summary>Computes the completed percentage of an encoder run from its hunk counts.</summary>
    /// <param name="completedHunks">Hunks completed so far.</param>
    /// <param name="totalHunks">Total hunks in the image.</param>
    /// <returns>The completed percentage (100 for an empty image).</returns>
    private static int Percent(uint completedHunks, uint totalHunks)
    {
        return totalHunks == 0 ? 100 : (int)(completedHunks * 100 / totalHunks);
    }

    /// <summary>Formats a percentage with the invariant culture.</summary>
    /// <param name="percent">The percentage to format.</param>
    /// <returns>The formatted percentage.</returns>
    private static string PercentText(int percent)
    {
        return percent.ToString(CultureInfo.InvariantCulture);
    }
}
