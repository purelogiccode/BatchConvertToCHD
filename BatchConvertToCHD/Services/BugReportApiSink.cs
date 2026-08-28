using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToCHD.Services;

/// <summary>
///     A Serilog log event sink that forwards warning-level and above log events to the
///     <see cref="BugReportService" /> for bug report submission. Events below
///     <see cref="LogEventLevel.Warning" /> are silently ignored. Messages matching
///     known informational patterns are excluded via <see cref="BugReportService.IsExcludedFromBugReport" />.
///     Uses an interlocked flag to prevent concurrent API flood when many warnings fire rapidly.
///     A 10-second send timeout prevents the throttle flag from being held indefinitely.
/// </summary>
internal class BugReportApiSink : ILogEventSink
{
    private static int _isSending;
    private readonly BugReportService _bugReportService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BugReportApiSink" /> class.
    /// </summary>
    /// <param name="bugReportService">The bug report service to forward warning events to.</param>
    internal BugReportApiSink(BugReportService bugReportService)
    {
        _bugReportService = bugReportService;
    }

    /// <summary>
    ///     Emits the provided log event to the sink. Only events at or above
    ///     <see cref="LogEventLevel.Warning" /> are forwarded to the bug report API.
    ///     Messages matching informational exclusion patterns are dropped.
    /// </summary>
    /// <param name="logEvent">The log event to emit.</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        var message = logEvent.RenderMessage();

        if (BugReportService.IsExcludedFromBugReport(message))
            return;

        var ex = logEvent.Exception;

        if (Interlocked.CompareExchange(ref _isSending, 1, 0) == 0)
        {
            // Use a 10-second timeout so a hung HTTP call doesn't permanently block
            // subsequent bug reports. The flag is always reset in the continuation.
            _ = _bugReportService
                .SendBugReportAsync(message, ex)
                .ContinueWith(
                    static _ => { Interlocked.Exchange(ref _isSending, 0); },
                    TaskContinuationOptions.ExecuteSynchronously
                );

            // Safety net: clear the flag after 12 seconds even if SendBugReportAsync
            // never completes (e.g. TCP connection hang). Task.Delay is deliberately
            // not awaited — it runs as an independent fire-and-forget timer.
            _ = Task.Delay(TimeSpan.FromSeconds(12))
                .ContinueWith(
                    static _ => { Volatile.Write(ref _isSending, 0); },
                    TaskContinuationOptions.ExecuteSynchronously
                );
        }
    }
}