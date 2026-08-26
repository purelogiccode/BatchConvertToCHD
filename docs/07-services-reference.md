---
title: Services Reference
nav_order: 8
---

# 7. Services Reference

All classes live in `BatchConvertToCHD/Services/`. Namespaces are `BatchConvertToCHD.Services` unless noted.

---

## 7.1 AppHttpClient

`internal static class AppHttpClient` (`AppHttpClient.cs:13`)

Thread-safe singleton `HttpClient` used by BugReportService, StatsService, and UpdateService.

- `internal static HttpClient Client` — double-checked locking; builds a `SocketsHttpHandler` with:
  - TLS 1.2 + TLS 1.3 only (`EnabledSslProtocols`),
  - `PooledConnectionLifetime = 10 minutes`,
  - default header `Accept: application/json`.
- `ServerCertificateValidationCallback` (`:50`):
  - clean chain → accept;
  - **name mismatch** (`RemoteCertificateNameMismatch`) → warn ("may be caused by a proxy or firewall intercepting the connection") and **accept**;
  - any other error (expired, revoked, chain) → warn and **reject**.
- `internal static void Dispose()` — disposes client+handler; the next `Client` access rebuilds them. Called from `App_Exit`.

---

## 7.2 ArchiveService

`internal class ArchiveService : IDisposable` (`ArchiveService.cs:23`)

Decompresses archives and compressed images for the conversion pipeline.

### Construction

`ArchiveService(string sevenZipExePath, bool isSevenZipAvailable)` — the app passes `Path.Combine(baseDirectory, AppConfig.SevenZipExeName)` plus whether the file exists.

### API

| Member | Purpose |
|--------|---------|
| `ExtractCsoAsync(originalCsoPath, tempOutputIsoPath, tempDirectoryRoot, onLog, token)` | Decompresses a `.cso`/`.ciso` to an ISO via CSOSharp (`CsoFile.Open` → `ExtractToIso`). Returns `(Success, FilePath, TempDir, ErrorMessage)`. |
| `ExtractArchiveAsync(originalArchivePath, tempDirectoryRoot, onLog, token)` | Dispatches by extension: `.zip` → `ExtractZipWith7ZaFallbackAsync`; `.7z` → `ExtractSevenZipArchiveAsync`; `.rar` → `ExtractRarArchive`. Returns `(Success, List<string> FilePaths, TempDir, ErrorMessage)`. |
| `ExtractArchiveWithFallback<TArchive>(...)` (static) | Shared SharpCompress extraction with temp-copy fallback; `TArchive : IArchive, IDisposable`. |
| `IsMultiPartRarError(Exception)` (static) | True for SharpCompress multi-part RAR messages. |
| `IsNetworkUnavailableError(Exception)` (static) | True for network-unavailable messages (disconnected drive). |

### Key behaviors

- **Pre-extraction disk check** (`CheckTempDiskSpace`): estimates the uncompressed size (sum of ZIP entry lengths; otherwise the archive file size) and requires `estimated + max(est/10, 100 MB)` free, else extraction is refused with a clear message.
- **Zip-slip protection**: every entry destination must be under the normalized output directory, otherwise `SecurityException` ("Attempted to extract file outside of the target directory.").
- **7za.exe fallback**: zip/7z failures (except cancellation) fall back to `7za x "<archive>" -o"<output>" -y` when the exe is available; RAR has no fallback. 7za exit code 2 or "Is not archive"/"Cannot open" output → `InvalidDataException` "archive is invalid or corrupt".
- **Retries**: ZIP open/entry writes retry 3 times on `IOException`/`UnauthorizedAccessException` with `attempt * 1000 ms` sleeps; SharpCompress temp-copy fallback covers locked source files.
- **Error categorization** (converted to user-facing messages): unsupported ZIP compression method (Deflate64/LZMA/PPMd — re-compress advice), corrupt/incomplete archive, encrypted archive (`CryptographicException`), missing multi-part RAR volume, disk full (HResult `-2147024784`/`-2147024783`), locked file, network unavailable.
- **Post-extraction scan**: collects files matching `PrimaryTargetExtensionsSet` (`.cue/.iso/.img/.gdi/.toc/.raw/.ccd/.mds/.isz`); if none and `.bin` files exist, a `(Track N)` set becomes a multi-FILE cue (`TrackBinCueBuilder`), otherwise a MODE2/2352 auto-cue is generated for the largest bin (`BinCueGenerator`); if nothing supported → "No supported primary files found in archive."
- **Companion filtering**: `InputFileFilter.RemoveCompanionDataFilesAsync` drops raw images that a descriptor in the archive already covers, so a cue/bin or CloneCD set inside an archive converts once through its descriptor rather than once per file with both attempts aimed at the same output name.
- `.ecm` is **not** a primary target, so an archive containing only `.ecm` files still reports nothing supported. Loose `.ecm` files convert normally.
- **Cancellation**: observed throughout; `OperationCanceledException` is rethrown.

---

## 7.3 BugReportApiSink

`internal class BugReportApiSink : ILogEventSink` (`BugReportApiSink.cs:13`)

Serilog sink that forwards **Warning and above** log events to `BugReportService.SendBugReportAsync`.

- Ignores `LogEventLevel.Information` and below.
- Drops messages matching `BugReportService.IsExcludedFromBugReport`.
- **Flood control**: a static interlocked flag allows only one in-flight send; the flag is released in a `ContinueWith(ExecuteSynchronously)` continuation. Fire-and-forget.

---

## 7.4 BugReportService

`internal class BugReportService` (`BugReportService.cs:14`)

Client for the PureLogicCode BugReport API. Full details and the exclusion list in [Bug Reporting System](09-bug-reporting.md).

- `SendBugReportAsync(message, ex?, token)` — POSTs `{ message (formatted report), applicationName, version, userInfo (Environment.UserName), environment, stackTrace }` with header `X-API-KEY`; returns whether the server accepted it. Excluded messages return `false` without any HTTP call; send failures are logged at Debug and swallowed.
- `IsExcludedFromBugReport(message)` — case-insensitive substring match against 38 known-noise patterns.
- `BuildFormattedReport` — builds the `=== Environment Details ===` / `=== Error Details ===` / `=== Exception Details ===` sections (inner-exception chain, max depth 5).

---

## 7.5 FileWatcherService

`internal sealed class FileWatcherService : IDisposable` (`FileWatcherService.cs:12`)

Explains why a file disappeared mid-batch.

- `StartWatching(folderPath)` — `FileSystemWatcher` with `IncludeSubdirectories=true`, `NotifyFilter = FileName | DirectoryName`, 64 KB internal buffer; handles Deleted/Renamed/Created/Error; tolerates bad paths and disconnected drives.
- `StopWatching()` / `Dispose()`.
- `GetContextForMissingFile(filePath)` — returns a human-readable diagnostic: not watched, folder inaccessible, deleted at time, renamed from/to, created-then-gone, or never observed. History is capped at 1,000 events (FIFO); buffer overflow clears history to avoid stale diagnostics.
- Supporting types: `FileEventRecord { Timestamp, EventType, RelatedName }`, `FileWatchEventType { Deleted, RenamedFrom, RenamedTo, Created }`.

The watcher is started when the user picks the conversion input folder and is consumed in `ProcessSingleFileForConversionAsync` when a selected file is gone.

---

## 7.6 LegacyCleanupService

`internal static class LegacyCleanupService` (`LegacyCleanupService.cs:10`)

Runs once at startup on a background thread and deletes leftovers from older versions next to the executable:

- Folders: `logs`, `Resources`, `Screenshot`
- Files: `maxcso.exe`, `psxpackager.exe`

All failures are silently ignored (files may be in use).

---

## 7.7 ScreenshotService

`internal class ScreenshotService` (`ScreenshotService.cs:16`)

Captures the foreground window via GDI (`GetForegroundWindow` → `GetWindowRect` → `BitBlt`) and saves a PNG:

- Location: `%LocalAppData%\BatchConvertToCHD\screenshots` (created on demand).
- Filename: `screenshot_yyyy-MM-dd_HH-mm-ss-fff.png`.
- `TakeScreenshot()` returns the saved path, or `null` (no foreground window / zero-size / failure).
- Triggered by the global F8 hotkey (see [User Guide](04-user-guide.md#44-global-hotkey--screenshot-f8)).

---

## 7.8 StatsService

`internal class StatsService` (`StatsService.cs:8`)

Records anonymous usage statistics once per launch.

- `RecordUsageAsync()` — POSTs `{ applicationId, version }` with `Authorization: Bearer {apiKey}` to `https://www.purelogiccode.com/ApplicationStats/stats`.
- **HTTP 429** → `Logger.Debug("Usage statistics rate-limited (HTTP 429) - this is expected behavior")` — treated as expected, no retry, no bug report.
- Other non-success statuses → `Logger.Information` (below the bug-report threshold).
- Network errors → `Logger.Debug` with the exception, silently swallowed.

---

## 7.9 UpdateService

`internal class UpdateService` (`UpdateService.cs:13`)

Checks GitHub for new releases at startup.

- `CheckForNewVersionAsync(onLog, onStatusUpdate, onBugReport)` — wrapper; core overload takes `(HttpClient, Version? currentVersion, ...)` for testing.
- Flow:
  1. GET the configured release URL (`AppConfig.GitHubApiLatestReleaseUrls`: `https://api.github.com/repos/purelogiccode/BatchConvertToCHD/releases/latest`) with a User-Agent. Rate limits (403/429) skip the check entirely because they are per-IP; other failures fall through to error handling.
  2. **403/429** → "GitHub API rate limit exceeded. Skipping update check." — no bug report.
  3. **5xx** → "Update check skipped: GitHub server error." — no bug report.
  4. Deserialize `GitHubRelease` (`tag_name`, `html_url`, `name`, `body`, `prerelease`, `draft`).
  5. Skip draft/prerelease/empty tags.
  6. Compare versions (`TryNormalizeVersions` — 4-part versions with `-1` parts normalized to 0; `ParseVersionFromTag` strips prefixes like `v`/`release`/`version` and leading non-digits).
  7. Newer version → Dispatcher message box ("A new version ... Would you like to go to the download page?"); on **Yes** opens `html_url`. If the browser fails, the URL is copied to the clipboard (with its own bug-report path on failure) and a "Browser Launch Failed" dialog shows the URL.
  8. Network/SSL errors → logged, no bug report. HTTP errors with a status code and generic exceptions → logged **and** reported via `onBugReport`.
- `TryNormalizeVersions` / `ParseVersionFromTag` are `internal static` and heavily unit-tested.
