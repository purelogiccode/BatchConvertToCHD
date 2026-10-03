---
title: Application Data
nav_order: 13
---

# 12. Application Data

Everything the application persists lives under the per-user AppData folder:

```
%LocalAppData%\BatchConvertToCHD\
├── logs\                          # Serilog rolling log files
│   └── BatchConvertToCHD-YYYYMMDD.log   (daily roll, 10 MB size roll, 7 files retained)
└── screenshots\                   # F8 captures
    └── screenshot_yyyy-MM-dd_HH-mm-ss-fff.png
```

`%LocalAppData%` resolves to `C:\Users\<user>\AppData\Local` on a standard install. F8 screenshots are saved into the `screenshots` folder beside `logs`; a `screenshots` folder next to the executable is used only when AppData cannot be written.

## 12.1 Logs

- Configured in `App.axaml.cs` `ConfigureSerilog`.
- **File sink**: `%LocalAppData%\BatchConvertToCHD\logs\BatchConvertToCHD-.log`, daily rolling (`RollingInterval.Day`) with a 10 MB size roll (`rollOnFileSizeLimit`), **7 files retained**, minimum level **Debug**, invariant-culture timestamps `{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}`.
- **Debug sink** (visible in the Visual Studio debugger output) and the **BugReportApiSink** (warning+, see [Bug Reporting System](09-bug-reporting.md)).
- The in-app **LogViewer** shows the same messages. Lines are queued (oldest dropped past 2,000) and flushed in batches of at most 200 lines every 100 ms, a single line is capped at 2,000 characters, and the text is capped at 50,000 characters (`MaxLogLength`), so a large log cannot freeze the window; the **AppData** title-bar button opens `%LocalAppData%\BatchConvertToCHD` in Explorer.

## 12.2 Screenshots

- While the application window is focused, **F8** captures that window with Avalonia's `RenderTargetBitmap` and saves it as `screenshot_yyyy-MM-dd_HH-mm-ss-fff.png` in `%LocalAppData%\BatchConvertToCHD\screenshots` (folder created on demand).
- When that folder cannot be written, the capture falls back to a `screenshots` folder next to the executable.
- The saved path is logged in the app ("Screenshot saved: ...").

## 12.3 Temporary Directories

Temp directories use the **system temp folder** by default; a folder at a drive root is only created when the system temp path is unsafe to hand to chdman (non-ASCII or near MAX_PATH) or its volume cannot hold the operation:

- Pattern: `BatchConvertToCHD_Temp_<guid>` under the system temp folder, or `{drive}\BatchConvertToCHD_Temp\` as the fallback (`PathUtils.GetBestTempDirectory`, see [Utilities Reference](08-utilities-reference.md#81-pathutils)).
- Raw CD images that need a generated cue are staged in `{drive}\BatchConvertToCHD_Temp\` on the image's own volume, because chdman resolves a cue's `FILE` entry relative to the cue's directory.
- Used for: archive extraction, CSO decompression, PBP/CCD cue generation, retry-via-temp-copy fallback, and cue work directories.
- **Cleanup**: temp dirs are deleted after each file is processed; an emptied `BatchConvertToCHD_Temp` folder is removed with them, and at startup leftover `BatchConvertToCHD_Temp_*` folders (plus stale Explorer extractions older than a day) are removed (`CleanupLeftoverTempDirectories`, `MainWindow.axaml.cs`).

## 12.4 What Lives Next to the Executable

| Item | Purpose |
|------|---------|
| `BatchConvertToCHD.exe` | The application |
| `chdman.exe` / `chdman_arm64.exe` | MAME CHD tool (Windows; must stay next to the exe) |
| `7za.exe` / `7za_arm64.exe` | 7-Zip fallback extractor (Windows) |
| `7zz` + `7-Zip-License.txt` | Official 7-Zip 26.03 console build (Linux/macOS) |
| `CHDSharp.dll`, `Avalonia` assemblies, etc. | Managed dependencies (copy-local); CHDSharp powers the built-in in-process encoder |
| `CCDSharp.dll`, `CSOSharp.dll`, `PBPSharp.dll`, `MDSSharp.dll`, `ISZSharp.dll` | In-house libraries |

Legacy leftovers (`logs` and `Resources` folders; `maxcso.exe`, `psxpackager.exe`) are deleted automatically at startup by `LegacyCleanupService` (see [Services Reference](07-services-reference.md#76-legacycleanupservice)). A `Screenshot` folder next to the executable is left alone because it is the fallback location for F8 captures.

## 12.5 Network Endpoints

| Endpoint | Purpose |
|----------|---------|
| `https://www.purelogiccode.com/bugreport/api/send-bug-report` | Bug reports (POST, `X-API-KEY` header) |
| `https://www.purelogiccode.com/ApplicationStats/stats` | Anonymous usage stats (POST, Bearer token) |
| `https://api.github.com/repos/purelogiccode/BatchConvertToCHD/releases/latest` | Update checks (GET, User-Agent) |

All HTTP traffic goes through the shared `AppHttpClient` singleton (TLS 1.2/1.3, see [Services Reference](07-services-reference.md#71-apphttpclient)).
