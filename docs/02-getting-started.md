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
  - `chdman.exe` / `chdman_arm64.exe` (0.289) — MAME CHD tool (primary encoder, and extraction fallback)
  - `CHDSharp.exe` / `CHDSharp_arm64.exe` — managed encoder (automatic conversion fallback; chdman byte-identical output)
  - `7za.exe` / `7za_arm64.exe` — 7-Zip fallback extractor
- **Nothing else to install** — CSO, ISZ, ECM, Alcohol `.mds`/`.mdf` and split volume sets are all handled inside the application, so x64 and ARM64 get the same feature set.

### Build (developers)
- .NET SDK **10.0.x** (`global.json` pins `10.0.0` with `rollForward: latestMajor`)
- Windows SDK for WPF development
- No other global tools required

---

## 2.2 Installation (End Users)

1. Download the latest binary from the [Releases page](https://github.com/purelogiccode/BatchConvertToCHD/releases).
2. Extract the contents to a permanent folder (do **not** run from a temp/Downloads folder if you want update/self-containment to behave).
3. **Important**: keep all `.exe` files (including ARM64 variants) in the same directory as `BatchConvertToCHD.exe` — `chdman.exe` and `7za.exe` are located relative to the app's base directory (`MainWindow.xaml.cs:96–101`).
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
dotnet build BatchConvertToCHD/BatchConvertToCHD.csproj -c Release
```

The solution contains seven projects:

| Project | Kind | Target framework |
|---------|------|------------------|
| `BatchConvertToCHD` | WPF application (WinExe) | `net10.0-windows` |
| `BatchConvertToCHD.Tests` | xUnit test suite | `net10.0-windows` |
| `Alcohol120Sharp` | class library (Alcohol 120% .mds/.mdf parsing) | `net10.0;net8.0` |
| `CCDSharp` | class library (CloneCD parsing) | `net10.0;net8.0` |
| `CSOSharp` | class library (CSO decompression) | `net10.0;net8.0` |
| `PBPSharp` | class library (PBP/SFO parsing) | `net10.0;net8.0` |
| `UltraIsoSharp` | class library (ISZ decompression) | `net10.0;net8.0` |

> **Note**: `chdman.exe` and `7za.exe` are copied to the output directory by the build (`BatchConvertToCHD.csproj:26–40`). The libraries are referenced as project references, not NuGet packages, except `CHDSharp` (NuGet 1.4.3) and other packages listed below.

### NuGet dependencies (application)

| Package | Version | Purpose |
|---------|---------|---------|
| CHDSharp | 1.4.3 | Pure C# CHD reading, verification, extraction, and creation (chdman byte-identical output) |
| WPF-UI | 4.3.0 | Fluent Design theming and controls |
| SharpCompress | 0.50.x | Archive extraction (7z/rar), and bzip2 decompression for ISZ chunks |
| NAudio | 3.0.1 | MP3 decoding via Media Foundation |
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

The path is applied in `MainWindow_LoadedAsync` via `SetInputFolder` (`MainWindow.xaml.cs:148–153`).

### First launch

1. The app checks the bundled encoders: a critical error is shown only when neither `chdman.exe` nor `CHDSharp.exe` is present; when just one is missing, a warning explains which encoder will carry conversions (status bar indicators + a message box). A batch likewise refuses to start only when neither encoder is usable.
2. Usage statistics are recorded once (anonymous `{ applicationId, version }` POST — see [Services Reference](07-services-reference.md#stats-service)).
3. An update check against GitHub releases runs in the background.
4. Leftover temp directories from crashed sessions and legacy files are cleaned up after a short delay.

### Single-instance behavior

Only one instance can run: a global mutex `Global\BatchConvertToCHD_SingleInstance` is acquired at startup; a second launch shows *"Another instance of BatchConvertToCHD is already running."* and exits (`App.xaml.cs:80–105`).

---

## 2.5 First Conversion in 30 Seconds

1. Open the **Convert to CHD** tab.
2. **Source Files** → browse to your folder of images/archives.
3. **Output CHD** → browse to your target folder.
4. Click **Start Conversion**.

Files appear in the list pre-selected; uncheck any you want to skip. The log pane shows live `chdman` output; the status bar and stat cards show progress, speed, and elapsed time.

See the [User Guide](04-user-guide.md) for all options and the other two workflows (extraction and verification).
