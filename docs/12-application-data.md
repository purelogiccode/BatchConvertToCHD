---
title: Application Data
nav_order: 13
---

# 12. Application Data

Everything the application persists lives under the per-user AppData folder:

```
%LocalAppData%\BatchConvertToCHD\
├── logs\                          # Serilog rolling log files
│   └── BatchConvertToCHD-YYYYMMDD.log   (daily roll, 7 files retained)
└── screenshots\                   # F8 screenshots
    └── screenshot_yyyy-MM-dd_HH-mm-ss-fff.png
```

`%LocalAppData%` resolves to `C:\Users\<user>\AppData\Local` on a standard install.

## 12.1 Logs

- Configured in `App.xaml.cs` `ConfigureSerilog` (`:56–78`).
- **File sink**: `%LocalAppData%\BatchConvertToCHD\logs\BatchConvertToCHD-.log`, daily rolling (`RollingInterval.Day`), **7 files retained**, minimum level **Debug**, invariant-culture timestamps `{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}`.
- **Debug sink** (visible in the Visual Studio debugger output) and the **BugReportApiSink** (warning+, see [Bug Reporting System](09-bug-reporting.md)).
- The in-app **LogViewer** shows the same messages (truncated at 100,000 characters, `MaxLogLength`); the **AppData** title-bar button opens `%LocalAppData%\BatchConvertToCHD` in Explorer.

## 12.2 Screenshots

- Global hotkey **F8** captures the foreground window (GDI `BitBlt`) and saves it as `screenshot_yyyy-MM-dd_HH-mm-ss-fff.png` under `%LocalAppData%\BatchConvertToCHD\screenshots` (folder created on demand — `ScreenshotService.cs:97–104`).
- The saved path is logged in the app ("Screenshot saved: ...").

## 12.3 Temporary Directories

Temp directories are **not** under AppData — they live on the drive with the most free space:

- Pattern: `BatchConvertToCHD_Temp_<guid>` on `{drive}\BatchConvertToCHD_Temp\`, or directly under the system temp when the preferred root isn't usable (`PathUtils.GetBestTempDirectory`, see [Utilities Reference](08-utilities-reference.md#81-pathutils)).
- Used for: archive extraction, CSO decompression, PBP/CCD cue generation, retry-via-temp-copy fallback, and cue work directories.
- **Cleanup**: temp dirs are deleted after each file is processed; at startup, leftover `BatchConvertToCHD_Temp_*` folders from crashed sessions are removed (`CleanupLeftoverTempDirectories`, `MainWindow.xaml.cs:304`).

## 12.4 What Lives Next to the Executable

| Item | Purpose |
|------|---------|
| `BatchConvertToCHD.exe` | The application |
| `chdman.exe` / `chdman_arm64.exe` | MAME CHD tool (must stay next to the exe) |
| `7za.exe` / `7za_arm64.exe` | 7-Zip fallback extractor |
| `CHDSharp.dll`, `WPF-UI` assemblies, etc. | Managed dependencies (copy-local) |
| `CCDSharp.dll`, `CSOSharp.dll`, `PBPSharp.dll` | In-house libraries |

Legacy leftovers (`logs`, `Resources`, `Screenshot` folders; `maxcso.exe`, `psxpackager.exe`) are deleted automatically at startup by `LegacyCleanupService` (see [Services Reference](07-services-reference.md#76-legacycleanupservice)).

## 12.5 Network Endpoints

| Endpoint | Purpose |
|----------|---------|
| `https://www.purelogiccode.com/bugreport/api/send-bug-report` | Bug reports (POST, `X-API-KEY` header) |
| `https://www.purelogiccode.com/ApplicationStats/stats` | Anonymous usage stats (POST, Bearer token) |
| `https://api.github.com/repos/purelogiccode/BatchConvertToCHD/releases/latest` | Update checks (GET, User-Agent) |

All HTTP traffic goes through the shared `AppHttpClient` singleton (TLS 1.2/1.3, see [Services Reference](07-services-reference.md#71-apphttpclient)).
