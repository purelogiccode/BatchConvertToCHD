---
title: Architecture
nav_order: 4
---

# 3. Architecture

This page describes the solution structure, the runtime startup sequence, and the high-level data flow of the application. Detailed deep dives live in [Conversion Pipeline](05-conversion-pipeline.md), [Extraction & Verification](06-extraction-and-verification.md), [Services Reference](07-services-reference.md), and [Utilities Reference](08-utilities-reference.md).

---

## 3.1 Solution Structure

```
CSharp_BatchConvertToCHD.sln
├── BatchConvertToCHD/                     (WPF app, net10.0-windows)
│   ├── App.xaml(.cs)                      → startup, Serilog, exception handlers
│   ├── AppConfig.cs                       → central configuration
│   ├── MainWindow.xaml(.cs)               → UI + all batch logic (~3,600 lines)
│   ├── AboutWindow.xaml(.cs)              → about dialog
│   ├── Models/
│   │   ├── FileItem.cs                    → bindable file row (name, size, selected)
│   │   ├── GitHubRelease.cs               → GitHub API release model
│   │   └── PbpExtractionResult.cs         → PBP extraction outcome
│   ├── Services/
│   │   ├── AppHttpClient.cs               → singleton HttpClient (TLS 1.2/1.3)
│   │   ├── ArchiveService.cs              → zip/7z/rar extraction, CSO, 7za fallback
│   │   ├── BugReportApiSink.cs            → Serilog sink → bug API
│   │   ├── BugReportService.cs            → bug report client + exclusion list
│   │   ├── FileEventRecord.cs / FileWatchEventType.cs
│   │   ├── FileWatcherService.cs          → missing-file diagnostics
│   │   ├── LegacyCleanupService.cs        → removes legacy files/folders
│   │   ├── ScreenshotService.cs           → GDI screenshot capture
│   │   ├── StatsService.cs                → anonymous usage stats
│   │   └── UpdateService.cs               → GitHub update checks
│   └── Utilities/
│       ├── BinCueGenerator.cs             → auto-cue generation for bin-only archives
│       ├── CueFileLineTransform.cs / CueFileReference.cs / CueNormalizationResult.cs
│       ├── CueNormalizer.cs               → encoding detection + canonicalization
│       ├── CueWorkDirectory.cs(.Result)   → self-contained ASCII cue work dirs
│       ├── DiscImageKind.cs               → what a file turned out to be
│       ├── DiscImageSignature.cs          → magic-byte content identification
│       ├── FileExtensions.cs              → all extension constants and sets
│       ├── GameFileParser.cs              → cue/gdi/toc referenced-file resolution
│       ├── IMp3Decoder.cs / Mp3ToWavDecoder.cs
│       ├── InputFileFilter.cs             → drops raw images a descriptor already covers
│       ├── IsoSectorValidator.cs          → sector-size alignment checks
│       ├── PathUtils.cs                   → temp dirs, path sanitizing, relative paths
│       ├── RawCdImageDetector.cs          → raw 2352 sector sniffing + cue staging
│       ├── RetryingFileOperations.cs      → retry-with-backoff delete/move
│       ├── SplitImageJoiner.cs            → rejoins .001/.002 and .i00/.i01 sets
│       ├── TrackBinCueBuilder.cs          → multi-FILE cue for "(Track N)" bin sets
│       ├── Ecm/                           → in-process ECM decoding
│       │   ├── CdSectorEccEdc.cs          → regenerates sector EDC + Reed-Solomon parity
│       │   ├── EcmImageDecoder.cs         → ECM block-stream decoder
│       │   └── EcmDecodeResult.cs
│       ├── Isz/                           → in-process ISZ decompression
│       │   ├── IszHeader.cs               → the packed 48-byte header
│       │   ├── IszDecoder.cs              → chunk table + zlib/bzip2/stored/zero chunks
│       │   ├── IszSegment.cs / IszChunkType.cs / IszDecodeResult.cs
│       └── Mds/                           → Alcohol 120% support
│           ├── MdsParser.cs               → .mds session/track table parsing
│           ├── MdsInputPreparer.cs        → cue / subchannel strip / DVD decision
│           └── MdsDisc.cs / MdsTrack.cs
├── BatchConvertToCHD.Tests/               (xUnit, 777 tests; Fixtures/ holds ecm-sample.ecm)
├── CCDSharp/                              (CloneCD .ccd/.img/.sub parsing; net10.0;net8.0)
├── CSOSharp/                              (CSO/CISO decompression; net10.0;net8.0)
├── PBPSharp/                              (PBP/SFO parsing; net10.0;net8.0)
└── References/                            (third-party sources — not part of the build)
```

### Dependency graph

```
                 ┌─────────────────────────────┐
                 │      BatchConvertToCHD      │  (WPF app)
                 └───┬────────┬────────┬───────┘
     Project refs    │        │        │
        ┌────────────▼──┐  ┌──▼────────▼─────┐
        │    CCDSharp    │  │  CSOSharp       │  PBPSharp
        └───────────────┘  └─────────────────┘
     NuGet: CHDSharp 1.4.3, WPF-UI, SharpCompress, NAudio, Serilog
```

- The app references `CCDSharp`, `CSOSharp`, `PBPSharp` as project references (`BatchConvertToCHD.csproj:58–60`).
- All three libraries multi-target `net10.0;net8.0`, are packable, and expose internals to `BatchConvertToCHD.Tests` via `InternalsVisibleTo`.
- `BatchConvertToCHD.Tests` references the app (internals visible) plus `CSOSharp` and `PBPSharp` — but **not** `CCDSharp` (there are no CCDSharp unit tests today; see [Testing](11-testing.md)).

> **Why ISZ, ECM and Alcohol support are not libraries.** CCDSharp, CSOSharp and PBPSharp are separate packable projects, but `Utilities/Isz`, `Utilities/Ecm` and `Utilities/Mds` live inside the app. The deciding factor is testability: the test project references the app and sees its internals, but does not reference `CCDSharp`, so a new library project would need solution and csproj wiring before a single test could run against it. Nothing about these three needs to be redistributable on its own.

---

## 3.2 Startup Sequence

```
App ctor
 ├─ Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)   ← legacy codepages (CP932/CP949/CP1251...)
 ├─ new BugReportService(...)  → App.SharedBugReportService
 ├─ new StatsService(...)
 ├─ ConfigureSerilog()
 │    ├─ file sink: %LocalAppData%\BatchConvertToCHD\logs\BatchConvertToCHD-.log (daily, 7 retained)
 │    ├─ debug sink
 │    └─ BugReportApiSink (forwards Warning+ to the bug API)
 └─ subscribe: AppDomain.UnhandledException, DispatcherUnhandledException,
               TaskScheduler.UnobservedTaskException, Exit

OnStartup
 ├─ acquire global mutex "Global\BatchConvertToCHD_SingleInstance" (second instance → exit)
 ├─ ShutdownMode = OnMainWindowClose
 ├─ apply dark theme (WPF-UI)
 ├─ delete legacy 7z_x64.dll / 7z_arm64.dll
 ├─ _statsService.RecordUsageAsync()        (fire-and-forget)
 └─ type preloading on background thread

MainWindow ctor
 ├─ probe chdman/7za in BaseDirectory
 ├─ construct services (ArchiveService, ScreenshotService, FileWatcherService)
 ├─ RegisterHotKey (F8) on SourceInitialized
 ├─ InitializeStatusBar
 ├─ after 2 s: CleanupLeftoverTempDirectories + LegacyCleanupService.RunInBackground
 └─ log environment details

MainWindow Loaded
 ├─ create performance counters (write/read speed)
 ├─ apply CLI folder argument if present
 ├─ CheckDependenciesAndNotifyUser (chdman presence)
 └─ UpdateService.CheckForNewVersionAsync (background)
```

Line references: `App.xaml.cs:35–145`, `MainWindow.xaml.cs:87–172`.

---

## 3.3 Runtime Data Flow — Conversion

```
User clicks Start Conversion
  └─ StartConversionButton_ClickAsync              (MainWindow.xaml.cs:1025)
       ├─ validate paths (ValidateAndNormalizePath)
       ├─ read options (delete originals, smaller-first, force CD/DVD, timeout)
       ├─ RenewCancellationTokenSource
       ├─ SetControlsState(false)
       └─ PerformBatchConversionAsync              (:1292)
            ├─ validate chdman access + compatibility
            ├─ optional sort by size (smaller first)
            ├─ CheckDiskSpace (free space warnings)
            ├─ InputFileFilter + WarnAboutOutputCollisions (batch preflight)
            └─ per file: ProcessSingleFileForConversionAsync
                 ├─ missing file? → FileWatcherService diagnostics
                 ├─ TryResolveByContentAsync  ← content before extension
                 │    ├─ split volume set → SplitImageJoiner → classify
                 │    ├─ Isz  → ResolveIszAsync   (Utilities/Isz)  → classify
                 │    ├─ Ecm  → ResolveEcmAsync   (Utilities/Ecm)  → classify
                 │    ├─ Chd  → skip ("already a CHD")
                 │    └─ container extension, plain image inside → generated cue
                 ├─ else route by extension:
                 │    .cso   → ProcessCsoFileForConversionAsync
                 │    archive→ ProcessArchiveFileForConversionAsync
                 │    .pbp   → ProcessPbpFileForConversionAsync
                 │    .ccd   → ProcessCcdFileForConversionAsync
                 │    .mds   → ProcessMdsFileForConversionAsync   (Utilities/Mds)
                 │    other  → TryStageCueForRawImageAsync → direct conversion
                 ├─ ValidateDependentFilesAsync (cue/gdi/toc)
                 ├─ TryDirectConversionAsync
                 │    └─ ConvertToChdAsync  → writes <name>.<hex>.chdtmp, moves on success
                 ├─ fallback: TryRetryConversionViaTempCopyAsync
                 └─ HandleConversionResultAsync
                      ├─ success → optionally delete originals + prune empty dirs
                      └─ failure → leave the destination alone, keep source
```

## 3.4 Runtime Data Flow — Extraction & Verification

```
Extraction: StartExtractionButton_ClickAsync (:693)
  └─ PerformBatchExtractionAsync (:1355)
       └─ per file: ExtractChdAsync (:2123)
            ├─ pick command: auto-detect via CHD metadata, or explicit CD/DVD/HDD
            ├─ ChdFile.Open (CHDSharp) — corrupt CHD → clear error, continue
            ├─ DVD/HDD → ExtractChdToSingleFile (streamed 4 MB buffer)
            └─ CD/GDI → ExtractChdTracksToDirectory (temp dir → retrying moves)

Verification: StartVerificationButton_ClickAsync (:1110)
  └─ PerformBatchVerificationAsync (:2011)
       └─ per file: VerifyChdAsync (:3020) — CHDSharp Chd.CheckFile
            └─ optional move to Success/Failed via MoveVerifiedFileAsync (:2076)
                 └─ RetryingFileOperations.TryMoveAsync (retries ~45 s on locks)
```

## 3.5 Concurrency & Threading Model

- **UI thread**: all WPF controls; dispatcher invocations are used from worker contexts (`Dispatcher.Invoke`, `Dispatcher.InvokeAsync` with `DispatcherPriority.Background` for chunked list loading).
- **Worker threads**: `Task.Run` for file scanning, archive extraction, chdman process orchestration, GDI screenshots.
- **Cancellation**: one `CancellationTokenSource` per operation, guarded by a `Lock` (`_cts`, `_ctsLock`, `MainWindow.xaml.cs:31–32`); cancellation is observed at every loop iteration and propagated into chdman via a linked timeout CTS.
- **Chdman process**: stdout/stderr are redirected and parsed asynchronously (`OutputDataReceived`/`ErrorDataReceived`); the process is killed (`process.Kill(true)`) on cancellation/timeout, and the app waits 300 ms before temp cleanup so file handles are released.
- **Speed telemetry**: `PerformanceCounter`-based disk write/read rates sampled every second (`AppConfig.WriteSpeedUpdateIntervalMs = 1000`).
- **Operation state**: an interlocked `_operationRunningState` plus `SetControlsState` guards the UI against re-entrancy; a `_pendingClose` flag lets the window close gracefully mid-operation.

## 3.6 Logging Pipeline

```
LogMessage / LogWarning / LogError (MainWindow)
   └─ Serilog (Log.Information/Warning/Error)
        ├─ Debug sink
        ├─ File sink   → %LocalAppData%\BatchConvertToCHD\logs\BatchConvertToCHD-YYYYMMDD.log
        └─ BugReportApiSink → BugReportService.SendBugReportAsync
             (Warning+ only; exclusion patterns drop known-noise; single in-flight send)
```

See [Bug Reporting System](09-bug-reporting.md) for the full contract.
