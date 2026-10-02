---
title: Getting Started
nav_order: 3
---

# 2. Getting Started

## 2.1 Requirements

### Runtime (end users)
- **OS**: Windows 10 / 11, x64 or ARM64
- **Runtime**: [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Bundled executables** (shipped with the app, must stay next to `BatchConvertToCHD.exe`):
  - Windows: `chdman.exe` / `chdman_arm64.exe` (0.289) — MAME CHD tool (primary encoder and extraction fallback); `7za.exe` / `7za_arm64.exe` — 7-Zip fallback extractor
  - Linux/macOS: `7zz` (official 7-Zip 26.03 console build, copied from `tools/` at publish time) — 7-Zip fallback extractor
- **Built-in encoder**: [CHDSharp](https://www.nuget.org/packages/CHDSharp) (CHDSharpLib 1.4.3) runs in-process on every platform — the automatic fallback behind chdman on Windows and the only encoder on Linux/macOS. Its output is byte-identical to chdman 0.289, and being a managed assembly there is no encoder executable to ship.
- **Nothing else to install** — CSO, ISZ, ECM, Alcohol `.mds`/`.mdf` and split volume sets are all handled inside the application, so x64 and ARM64 get the same feature set.

### Build (developers)
- .NET SDK **10.0.x** (`global.json` pins `10.0.0` with `rollForward: latestMajor`)
- No Windows SDK required — Avalonia 12.1 and the Windows-only NAudio package are restored via NuGet, and the project sets `EnableWindowsTargeting` for the Windows TFM
- No other global tools required

---

## 2.2 Installation (End Users)

1. Download the latest binary from the [Releases page](https://github.com/purelogiccode/BatchConvertToCHD/releases).
2. Extract the contents to a permanent folder (do **not** run from a temp/Downloads folder if you want update/self-containment to behave).
3. **Important** (Windows): keep all `.exe` files (including ARM64 variants) in the same directory as `BatchConvertToCHD.exe` — tool discovery probes the app's base directory first, then `PATH` (`MainWindow.axaml.cs`). On Linux and macOS, `7zz` ships next to the app and other tools are discovered on `PATH`; the built-in CHDSharp encoder needs nothing on disk.
4. Launch `BatchConvertToCHD.exe`.

---

## 2.3 Building from Source

```bash
# Clone
git clone https://github.com/purelogiccode/BatchConvertToCHD.git
cd CSharp_BatchConvertToCHD

# Build the whole solution
dotnet build CSharp_BatchConvertToCHD.sln -c Release

# Run the tests
dotnet test CSharp_BatchConvertToCHD.sln -c Release

# Or just the application
dotnet build BatchConvertToCHD.Avalonia/BatchConvertToCHD.Avalonia.csproj -c Release
```

The solution contains seven projects:

| Project | Kind | Target framework |
|---------|------|------------------|
| `BatchConvertToCHD.Avalonia` | Avalonia application (WinExe) | `net10.0;net10.0-windows` |
| `BatchConvertToCHD.Tests` | xUnit test suite | `net10.0-windows` |
| `MDSSharp` | class library (Alcohol 120% .mds/.mdf parsing) | `net8.0;net9.0;net10.0` |
| `CCDSharp` | class library (CloneCD parsing) | `net8.0;net9.0;net10.0` |
| `CSOSharp` | class library (CSO decompression) | `net8.0;net9.0;net10.0` |
| `PBPSharp` | class library (PBP/SFO parsing) | `net8.0;net9.0;net10.0` |
| `ISZSharp` | class library (ISZ decompression) | `net8.0;net9.0;net10.0` |

> **Note**: on Windows, `chdman.exe` and `7za.exe` are copied to the output directory by the build (`BatchConvertToCHD.Avalonia.csproj`); on Linux and macOS the matching `tools/7zz*` binary (official 7-Zip 26.03) is copied as `7zz`, while a `chdman` on `PATH` is only used for extraction fallback and never for encoding. The libraries are referenced as project references, not NuGet packages, except `CHDSharp` (NuGet 1.4.3) and other packages listed below.

### NuGet dependencies (application)

| Package | Version | Purpose |
|---------|---------|---------|
| CHDSharp | 1.4.3 | Pure C# CHD reading, verification, extraction, and creation (chdman byte-identical output) |
| Avalonia (+ Desktop, Fluent theme, DataGrid) | 12.1.x | Cross-platform Fluent Design UI framework and controls |
| SharpCompress | 0.50.x | Archive extraction (7z/rar), and bzip2 decompression for ISZ chunks |
| NAudio | 3.1.0 | MP3 decoding via Media Foundation |
| Serilog | 4.4.0 | Structured logging |
| Serilog.Sinks.File | 7.0.0 | Rolling file logs |
| Serilog.Sinks.Debug | 3.0.0 | Debugger sink |
| SharpZipLib | 1.4.2 | Reference-compatible inflater for PBP PSAR blocks (via PBPSharp) |
| Meziantou.Analyzer | 3.0.x | Roslyn analyzers (build-time only) |
| Roslynator.Analyzers | 5.0.0 | Roslyn analyzers (build-time only) |

---

## 2.4 Running the Application

### From the command line

The application accepts an optional folder path argument to pre-populate the **Convert to CHD** source folder:

```sh
BatchConvertToCHD.exe "C:\ROMs\MyGames"
```

The path is applied in `MainWindow_LoadedAsync` via `SetInputFolder` (`MainWindow.axaml.cs:148–153`).

### First launch

1. The built-in CHDSharp encoder is always available, so a batch can never be refused for a missing encoder. On Windows only, when the bundled `chdman.exe` is missing or fails the startup probe, a notice explains that every conversion will run on the built-in encoder (status bar indicators + a message box).
2. Usage statistics are recorded once (anonymous `{ applicationId, version }` POST — see [Services Reference](07-services-reference.md#stats-service)).
3. An update check against GitHub releases runs in the background.
4. Leftover temp directories from crashed sessions and legacy files are cleaned up after a short delay.

### Single-instance behavior

Only one instance can run: a global mutex `Global\BatchConvertToCHD_SingleInstance` is acquired at startup; a second launch shows *"Another instance of BatchConvertToCHD is already running."* and exits (`App.axaml.cs:80–105`).

---

## 2.5 First Conversion in 30 Seconds

1. Open the **Convert to CHD** tab.
2. **Source Files** → browse to your folder of images/archives.
3. **Output CHD** → browse to your target folder.
4. Click **Start Conversion**.

Files appear in the list pre-selected; uncheck any you want to skip. The log pane shows live `chdman` output; the status bar and stat cards show progress, speed, and elapsed time.

See the [User Guide](04-user-guide.md) for all options and the other two workflows (extraction and verification).
