---
title: Bug Reporting System
nav_order: 10
---

# 9. Bug Reporting System

The application has a built-in, automatic bug-reporting channel that forwards **warning-and-above** log events to the PureLogicCode BugReport API. This page documents the full pipeline, the API contract, and — importantly — the noise-filtering rules.

---

## 9.1 Pipeline

```
LogMessage / LogWarning / LogError        (MainWindow.xaml.cs:606–622)
        │  (Serilog: Information / Warning / Error)
        ▼
Serilog Logger                             (App.xaml.cs:56–78)
        ├── Debug sink
        ├── File sink  → %LocalAppData%\BatchConvertToCHD\logs\BatchConvertToCHD-YYYYMMDD.log
        └── BugReportApiSink.Emit           (Services/BugReportApiSink.cs:33)
             ├─ ignore events below Warning
             ├─ ignore messages matching exclusion patterns
             └─ fire-and-forget SendBugReportAsync (single in-flight send via interlocked flag)
                      │
                      ▼
              BugReportService.SendBugReportAsync  (Services/BugReportService.cs:94)
                      │  POST https://www.purelogiccode.com/bugreport/api/send-bug-report
                      ▼
              BugReport API (server)
```

Additionally, **unhandled exceptions** are reported directly (not via the sink):

- `AppDomain.CurrentDomain.UnhandledException` → `Log.Fatal` + synchronous `ReportException` (the process is about to terminate, so the report must complete inline — `App.xaml.cs:236–250`). For dispatcher and task-scheduler exceptions the report is fire-and-forget to avoid blocking the UI thread.
- `DispatcherUnhandledException` → `Log.Error` + `ReportException`; a small allowlist of known-benign exceptions is suppressed (`App.xaml.cs:207–233`): WPF rendering errors (`GlyphTypeface` URI errors, PresentationCore OOM in `DUCE.Channel`/`HwndTarget`) and the WPF-internal `FileNotFoundException` from `PopupSecurityHelper.ForceMsaaToUiaBridge` (ToolTip/Popup opening when the OS accessibility bridge cannot be loaded — the tooltip simply never appears). These are suppressed at the handler level and never reach the log sink.
- `TaskScheduler.UnobservedTaskException` → `Log.Error` + `ReportException`, then `SetObserved()`.
- **Stats-rate-limit handling**: `StatsService.RecordUsageAsync` returns early on HTTP 429 (Too Many Requests) and logs at Debug level, so these transient conditions never reach the warning-level sink.

## 9.2 API Contract

`SendBugReportAsync` (`BugReportService.cs:94`) POSTs JSON with header `X-API-KEY`:

| Field | Source |
|-------|--------|
| `message` | `BuildFormattedReport` — three sections: `=== Environment Details ===` (includes both the **process** and the **OS** architecture, so a crash report from an emulated build is instantly classifiable), `=== Error Details ===` (the raw message), `=== Exception Details ===` (inner-exception chain, max depth 5) |
| `applicationName` | `AppConfig.ApplicationName` = `"BatchConvertToCHD"` |
| `version` | Assembly version (e.g. `3.4.0.0`) |
| `userInfo` | `Environment.UserName` |
| `environment` | `"Production"` (release) / `"Development"` (DEBUG build) |
| `stackTrace` | Formatted exception details, or `"N/A"` |

Environment Details includes: date/time, app name + version, OS version, architecture, bitness, Windows version, processor count, base directory, temp path.

## 9.3 Flood Control & Failure Semantics

- Only **one** bug report is in flight at a time (`BugReportApiSink` interlocked flag); bursts of warnings are coalesced. A 12-second safety timer clears the flag even if the HTTP call hangs, preventing indefinite throttling.
- **Duplicate suppression**: an identical message already forwarded within the last 10 minutes (`DuplicateWindow`) is dropped — a failing batch that retries the same input, or a loop logging the same warning per file, sends one report instead of one per occurrence.
- Sending is fire-and-forget from the sink; failures are logged at `Debug` and never surface to the user.
- Cancellation tokens are respected; `OperationCanceledException` is rethrown only when the caller's token is cancelled.

## 9.4 Exclusion Patterns

`IsExcludedFromBugReport` (`BugReportService.cs:63`) performs a case-insensitive substring match against `ExcludedMessagePatterns` (`:21–61`). Any match → the report is **dropped entirely** (no HTTP call). The categories:

| Category | Example patterns |
|----------|------------------|
| **Stats noise** | `"Failed to record usage statistics"` |
| **Drive / temp-space info** | `"Temp drive ("`, `"Output drive ("`, `"drive has "`, `"drive ("`, `"input files total"`, `"CHD files total"`, `"You may run out of disk space"`, `"disk space"`, `"disk full"` |
| **Extraction outcomes** | `"No supported primary files found in archive"`, `"Partial extraction:"`, `"File not found, skipping:"` |
| **Tooling** | `"chdman.exe not found"`, `"CRITICAL ERROR: The following required component"` |
| **Corrupt/unopenable CHD data** | `"Not a valid CHD file"`, `"Invalid or corrupt data"`, `"Cannot open file"` |
| **chdman output (user data)** | `"Fatal error occurred"` (chdman exit summary), `"cannot create std::vector"` (chdman C++ crash on user input) |
| **Cue/dependency validation** | `"referenced files are missing"`, `"could not be resolved"`, `"could not validate referenced files"`, `"MP3 audio track could not be decoded"`, `"is not divisible by"`, `"The file or directory is corrupted and unreadable"`, `"Retry via temp failed"` |
| **Archive errors** | `"archive file may be corrupted"`, `"archive is invalid or corrupt"`, `"archive file appears to be incomplete"`, `"multi-part RAR with a missing volume"`, `"unavailable network location"`, `"Archive is encrypted"`, `"compression method that is not supported"`, `"CCDSharp: Conversion error"` |

**Design intent**: the exclusion list only contains messages that describe **user-data or environmental conditions** (corrupt files, full disks, missing volumes, rate limits) — conditions the application handles gracefully and that would otherwise flood the bug database. Genuine code defects (exceptions, unexpected failures) still reach the API — including **CHDSharp and PBPSharp extraction failures**, which are reported by design (with debug details such as file size, disc index/count, and numeric error codes) so the library maintainer can fix them. Every exclusion is covered by unit tests (`BugReportServiceTests`).

## 9.5 Server-Side Notes

- **Timestamps** in reports are local time formatted `yyyy-MM-dd HH:mm:ss` inside the message body; the API's own `reportedAt` is UTC ISO-8601.
- Bug reports are stored per application and can be retrieved/deleted via the agent API (`https://www.purelogiccode.com/bugreport/api/agent/...`) — see `InstructionsToRetrieveBugs.md` in the AspNet_BugReportEmailService repository.
- Depending on server-side storage settings, non-ASCII characters in messages (e.g. the em dash in "— referenced files are missing") can be mangled (U+FFFD) on retrieval; messages containing them are excluded from reporting anyway.
