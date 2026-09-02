---
title: Home
nav_order: 1
---

# BatchConvertToCHD — Wiki

Welcome to the official wiki for **Batch Convert to CHD**, a high-performance Windows desktop utility for converting disk images into the **Compressed Hunks of Data (CHD)** format.

This documentation covers the project from both a user and a developer perspective: features, workflows, architecture, services, utilities, embedded libraries, testing, and troubleshooting.

## 📚 Documentation Index

| # | Page | Audience | Contents |
|---|------|----------|----------|
| 1 | [Project Overview](01-overview.md) | Everyone | What the app does, key features, supported formats, content-based routing, technical logic |
| 2 | [Getting Started](02-getting-started.md) | Users & devs | Requirements, installation, building from source, command line usage |
| 3 | [Architecture](03-architecture.md) | Developers | Solution layout, projects, dependency graph, startup sequence, data flow |
| 4 | [User Guide](04-user-guide.md) | Users | The three workflows: conversion, extraction, verification; options and hotkeys |
| 5 | [Conversion Pipeline (Technical)](05-conversion-pipeline.md) | Developers | Content resolution, per-format routing, chdman wrapper, cue normalization, ISZ/ECM/Alcohol/split sets, error handling |
| 6 | [Extraction & Verification (Technical)](06-extraction-and-verification.md) | Developers | CHD extraction internals, verification, file moves, partial-extraction handling |
| 7 | [Services Reference](07-services-reference.md) | Developers | ArchiveService, UpdateService, StatsService, FileWatcherService, AppHttpClient, more |
| 8 | [Utilities Reference](08-utilities-reference.md) | Developers | PathUtils, CueNormalizer, CueWorkDirectory, GameFileParser, BinCueGenerator, content detection, ISZ/ECM/Alcohol, more |
| 9 | [Bug Reporting System](09-bug-reporting.md) | Developers | Bug report API contract, sink, exclusion patterns, environment details |
| 10 | [Embedded Libraries](10-libraries.md) | Developers | CCDSharp, CSOSharp, PBPSharp, Alcohol120Sharp, UltraIsoSharp: purpose, API, integration |
| 11 | [Testing](11-testing.md) | Developers | Test project layout, coverage by file, integration tests, how to run |
| 12 | [Application Data](12-application-data.md) | Users & devs | AppData layout: logs, screenshots, temp directories, cleanup |
| 13 | [Troubleshooting](13-troubleshooting.md) | Users & devs | Common errors, their meaning, and how to resolve them |

## 🔑 Quick Facts

| Fact | Value |
|------|-------|
| **Application name** | `BatchConvertToCHD` |
| **Latest version** | 3.5.1 |
| **Target framework** | .NET 10.0 (`net10.0-windows`), WPF |
| **Platform** | Windows 10 / 11, x64 and ARM64 |
| **License** | GPL v3.0 |
| **Primary encoder** | `chdman` (MAME Project, 0.289), bundled as `chdman.exe` / `chdman_arm64.exe` |
| **Fallback encoder** | `CHDSharp` (PureLogicCode), bundled as `CHDSharp.exe` / `CHDSharp_arm64.exe` — chdman byte-identical output |
| **External tools needed** | None beyond the bundled `CHDSharp`, `chdman` and `7za` — every input format is handled in-process |
| **Output format** | `.chd` (Compressed Hunks of Data) |
| **Logs** | `%LocalAppData%\BatchConvertToCHD\logs` |
| **Screenshots** | `%LocalAppData%\BatchConvertToCHD\screenshots` (F8 hotkey) |
| **Bug reporting** | Automatic, opt-out by design — sent to the PureLogicCode BugReport API |

## 🗺️ Repository Layout

```
CSharp_BatchConvertToCHD/
├── Alcohol120Sharp/                # Alcohol 120% (.mds/.mdf) parsing library
├── BatchConvertToCHD/              # WPF application (net10.0-windows)
│   ├── MainWindow.xaml(.cs)        # Main UI + conversion/extraction/verification logic
│   ├── App.xaml(.cs)               # Startup, Serilog, exception handlers
│   ├── AppConfig.cs                # Central configuration constants
│   ├── Models/                     # FileItem, GitHubRelease, PbpExtractionResult
│   ├── Services/                   # Archive, BugReport, FileWatcher, Stats, Update, ...
│   └── Utilities/                  # PathUtils, CueNormalizer, GameFileParser, ...
│       └── Ecm/                    # in-process ECM decoding
├── BatchConvertToCHD.Tests/        # xUnit test suite (813 tests)
├── CCDSharp/                       # CloneCD (.ccd/.img/.sub) parsing library
├── CSOSharp/                       # CSO/CISO decompression library (deflate + LZ4)
├── PBPSharp/                       # PlayStation PBP extraction + SFO parsing library
├── UltraIsoSharp/                  # UltraISO ISZ decompression library
├── docs/                           # This wiki
├── References/                     # Third-party reference sources (not part of the build)
└── CSharp_BatchConvertToCHD.sln    # Solution
```

## 🚀 Where to Start

- **I just want to use the app** → [Getting Started](02-getting-started.md) and [User Guide](04-user-guide.md)
- **I want to build from source** → [Getting Started → Building from Source](02-getting-started.md#building-from-source)
- **I want to understand the internals** → [Architecture](03-architecture.md) → [Conversion Pipeline](05-conversion-pipeline.md)
- **I want to contribute or add tests** → [Testing](11-testing.md)

---

[← Back to the repository](https://github.com/purelogiccode/BatchConvertToCHD)
