# MDSSharp

[![NuGet](https://img.shields.io/nuget/v/MDSSharp.svg)](https://www.nuget.org/packages/MDSSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/MDSSharp.svg)](https://www.nuget.org/packages/MDSSharp)
[![CI](https://img.shields.io/github/actions/workflow/status/purelogiccode/CHDStudio/ci.yml?label=CI)](https://github.com/purelogiccode/CHDStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/purelogiccode/CHDStudio)
[![.NET 8 | 9 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4.svg)](https://dotnet.microsoft.com/download)
[![Platform: Windows | Linux | macOS](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-0078d7.svg)](https://github.com/purelogiccode/CHDStudio)
[![GitHub last commit](https://img.shields.io/github/last-commit/purelogiccode/CHDStudio)](https://github.com/purelogiccode/CHDStudio/commits/master)
[![GitHub stars](https://img.shields.io/github/stars/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/stargazers)
[![GitHub issues](https://img.shields.io/github/issues/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/issues)
[![Release](https://img.shields.io/github/actions/workflow/status/purelogiccode/CHDStudio/release.yml?label=Release)](https://github.com/purelogiccode/CHDStudio/actions/workflows/release.yml)
[![Docs](https://img.shields.io/github/actions/workflow/status/purelogiccode/CHDStudio/docs.yml?label=Docs)](https://github.com/purelogiccode/CHDStudio/actions/workflows/docs.yml)
[![Part of CHD Studio](https://img.shields.io/badge/Part%20of-Batch%20Convert%20to%20CHD-blue.svg)](https://github.com/purelogiccode/CHDStudio)
[![GitHub contributors](https://img.shields.io/github/contributors/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/graphs/contributors)
[![GitHub forks](https://img.shields.io/github/forks/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/network/members)
[![GitHub watchers](https://img.shields.io/github/watchers/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/watchers)
[![GitHub pull requests](https://img.shields.io/github/issues-pr/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/pulls)
[![GitHub repo size](https://img.shields.io/github/repo-size/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio)
[![GitHub code size](https://img.shields.io/github/languages/code-size/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio)
[![Top language](https://img.shields.io/github/languages/top/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/purelogiccode/CHDStudio.svg)](https://github.com/purelogiccode/CHDStudio/graphs/commit-activity)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](https://github.com/purelogiccode/CHDStudio/pulls)
[![Maintained](https://img.shields.io/badge/Maintained-yes-green.svg)](https://github.com/purelogiccode/CHDStudio)

**MDSSharp** is a managed C# library for reading **Alcohol 120%** disc images (`.mds` descriptor + `.mdf` data) and **Daemon Tools MDS v2 / MDX** descriptors. It parses the MDS session and track tables, locates and reassembles the data file (including `.i00`/`.i01` and `.001`/`.002` split sets), strips CD subchannel tails from oversized sectors, and writes CUE sheets so the image can be converted by `chdman` or other tools.

The library is the Alcohol 120% conversion engine used by [CHD Studio](https://github.com/purelogiccode/CHDStudio), where it prepares `.mds` images for conversion to CHD with `chdman` or [CHDSharp](https://www.nuget.org/packages/CHDSharp).

- Repository: <https://github.com/purelogiccode/CHDStudio>
- Package: <https://www.nuget.org/packages/MDSSharp>

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Usage examples](#usage-examples)
  - [Parse an MDS descriptor](#parse-an-mds-descriptor)
  - [Prepare an image for conversion](#prepare-an-image-for-conversion)
  - [Join a split volume set](#join-a-split-volume-set)
  - [Strip subchannel data](#strip-subchannel-data)
  - [Generate a CUE sheet](#generate-a-cue-sheet)
  - [Inspect tracks](#inspect-tracks)
- [Error handling](#error-handling)
- [API reference](#api-reference)
  - [MdsParser](#mdsparser)
  - [MdsInputPreparer](#mdsinputpreparer)
  - [SplitImageJoiner](#splitimagejoiner)
  - [Models](#models)
- [Supported images](#supported-images)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [Version history](#version-history)
- [License](#license)

## Features

- **MDS descriptor parsing** — reads the medium type, session and track tables of Alcohol 120% descriptors, including track number, mode, sector size, start LBA, subchannel mode and ADR/CTL.
- **MDS v2 / Daemon Tools descriptors** — decrypts the AES-256 + zlib-obfuscated descriptor (RIPEMD-160 PBKDF2 key derivation, no external crypto dependency), parses the wider v2 session/track/footer blocks and resolves their UTF-16 data file names. Compressed track data (per-track compression tables with stored/RLE/deflate groups) and encrypted track data (AES-256 LRW with GF(2^128) tweaks, including TAGES-style password-less images) are decoded automatically, and single-file `.mdx` containers are read end to end.
- **Data file resolution** — uses the file names embedded in the track footers (single-byte or UTF-16), falling back to the matching `.mdf`, a split `.i00` first volume, or an unambiguously named file one folder down.
- **Multi-file and split sets** — joins descriptors that name several data files, plus `.i00`/`.i01` (Alcohol) and `.001`/`.002` (byte-splitter) sets, tolerating case differences and archive-looking extensions.
- **Subchannel stripping** — rewrites 2448-byte (2352 data + 96 subchannel) and 2368-byte (2352 + 16) images down to the 2352-byte sectors `chdman` reads.
- **Pregap handling** — when the descriptor records pregaps that the data file does not contain, rebuilds them as zeros so the CUE can place `INDEX 00` without shifting later tracks.
- **CUE sheet generation** — writes a single-file CUE with `TRACK`/`INDEX 00`/`INDEX 01` entries, formatted as `MM:SS:FF` and encoded UTF-8 **without a BOM** for `chdman`.
- **Medium-aware output** — DVD media goes to the encoder as a cooked image; CD media with 2048-byte sectors gets a `MODE1/2048` cue; Mode 2 sectors map by the low nibble of the mode byte, as libmirage's reverse engineering established.
- **No native dependencies** — pure managed code, cross-platform.

## Requirements

| Requirement | Value |
|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Runtime | .NET 8, .NET 9 or .NET 10 |
| Platforms | Windows, Linux, macOS (pure managed code) |
| Dependencies | None |

The package contains a separate assembly for each target framework, so the correct build is selected automatically by NuGet.

> **Read-only:** MDSSharp reads Alcohol 120% images and produces CUE/BIN output. It cannot create or modify `.mds`/`.mdf` files, and it does not produce ISO images.

## Installation

```powershell
dotnet add package MDSSharp
```

or with the Package Manager console:

```powershell
Install-Package MDSSharp
```

## Quick start

An Alcohol image is a `.mds` descriptor next to its `.mdf` data file (or split `.i00`/`.001` volumes). Point the library at the `.mds`; the data file is resolved automatically.

```csharp
using MDSSharp;

var disc = MdsParser.Parse(@"C:\Games\Game.mds");
Console.WriteLine(disc.Summary);

var prepared = await MdsInputPreparer.PrepareAsync(
    disc,
    workDir: @"C:\Temp\mds-work",
    onLog: Console.WriteLine,
    token: CancellationToken.None
);

if (!prepared.Success)
{
    Console.Error.WriteLine(prepared.FailureReason);
    return;
}

// Hand one of these to chdman (createcd for the cue, createdvd for the image).
Console.WriteLine(prepared.CuePath ?? prepared.DvdImagePath);
```

## Usage examples

### Parse an MDS descriptor

`MdsParser.Parse` reads the descriptor without touching the data file, so it is cheap enough for inspection or pre-flight checks.

```csharp
using MDSSharp;

if (!MdsParser.IsMdsFile(mdsPath))
{
    Console.Error.WriteLine("Not an Alcohol MDS descriptor (missing \"MEDIA DESCRIPTOR\" signature).");
    return;
}

var disc = MdsParser.Parse(mdsPath);

Console.WriteLine($"Sessions: {disc.SessionCount}");
Console.WriteLine($"Data:     {disc.MdfPath ?? "missing"}");
Console.WriteLine(disc.Summary);
```

`IsMdsFile` only checks the `MEDIA DESCRIPTOR` signature and never throws, while `Parse` throws when the file is missing or not a usable descriptor.

### Prepare an image for conversion

`MdsInputPreparer.PrepareAsync` performs the whole pipeline: resolves the data file, joins split volumes into the work directory, strips subchannel data when present, and writes a CUE sheet. The returned `Result` tells you exactly what to convert.

```csharp
using MDSSharp;

var disc = MdsParser.Parse(mdsPath);

var result = await MdsInputPreparer.PrepareAsync(
    disc,
    workDir,
    message => Console.WriteLine(message),
    cancellationToken
);

if (result.Success && result.CuePath is not null)
{
    // 2352-byte raw CD (with or without stripped subchannel): convert as a CD.
    RunChdman("createcd", result.CuePath);
}
else if (result.Success && result.DvdImagePath is not null)
{
    // 2048-byte cooked sectors: the .mdf is a DVD image.
    RunChdman("createdvd", result.DvdImagePath);
}
else
{
    Console.Error.WriteLine(result.FailureReason);
}
```

### Join a split volume set

`SplitImageJoiner` detects and concatenates numbered pieces, whether the set uses Alcohol's `.i00` style, the `.001` style, or plain names. A set is only accepted when the next piece actually exists, so a lone file ending in `.001` is left alone.

```csharp
using MDSSharp;

var parts = SplitImageJoiner.TryGetVolumeSet(@"C:\Games\Game.i00");
if (parts is not null)
{
    Console.WriteLine($"{parts.Count} parts, {SplitImageJoiner.GetTotalBytes(parts):N0} bytes");
    var written = await SplitImageJoiner.JoinAsync(parts, @"C:\Temp\Game.bin", cancellationToken);
    Console.WriteLine($"Joined {written:N0} bytes");
}
```

### Strip subchannel data

Alcohol often rips 2448-byte sectors (2352 data + 96 bytes of subchannel). `chdman` does not read them, so `StripSubchannelAsync` keeps only the first 2352 bytes of every sector. It returns `null` on success or a user-facing reason on failure.

```csharp
using MDSSharp;

var failure = await MdsInputPreparer.StripSubchannelAsync(
    @"C:\Games\Game.mdf",
    @"C:\Temp\Game.bin",
    sectorSize: MdsDisc.RawPlusSubchannelSize,
    cancellationToken
);

Console.WriteLine(failure is null ? "Stripped." : failure);
```

### Generate a CUE sheet

`WriteCueAsync` writes a single-file CUE with one `TRACK`/`INDEX 01` entry per track. `FormatMsf` converts an absolute LBA to the `MM:SS:FF` a CUE expects.

```csharp
using MDSSharp;

var disc = MdsParser.Parse(mdsPath);
var cuePath = await MdsInputPreparer.WriteCueAsync(disc, workDir, "Game.bin", cancellationToken);

Console.WriteLine($"Wrote {cuePath}");
foreach (var track in disc.Tracks)
{
    Console.WriteLine($"  TRACK {track.Number:00} {track.CueTrackType} @ {MdsInputPreparer.FormatMsf(track.StartLba)}");
}
```

### Inspect tracks

Each `MdsTrack` exposes its mode byte, sector size and start LBA, plus a CUE track type when the mode can be expressed in a CUE.

```csharp
using MDSSharp;

var disc = MdsParser.Parse(mdsPath);

foreach (var track in disc.Tracks)
{
    Console.WriteLine($"Track {track.Number:00}: {track.CueTrackType ?? "unrepresentable"}");
    Console.WriteLine($"  Audio:  {track.IsAudio}");
    Console.WriteLine($"  Sector: {track.SectorSize} bytes");
    Console.WriteLine($"  Start:  LBA {track.StartLba} ({MdsInputPreparer.FormatMsf(track.StartLba)})");
}
```

## Error handling

| Exception | Thrown when |
|---|---|
| `FileNotFoundException` | The `.mds` passed to `Parse` does not exist. |
| `InvalidDataException` | The file is not an MDS descriptor, reports implausible session counts, contains no readable tracks, or is an MDS v2/MDX file whose encrypted descriptor cannot be decrypted or decompressed. A version-1 descriptor larger than 1 MB is also rejected; a version-2 `.mdx` container of any size is read through a stream and is not subject to that cap. |
| `OperationCanceledException` | The cancellation token is signaled during prepare, strip or join. |

`PrepareAsync` and `StripSubchannelAsync` otherwise report failures through their result (`Result.FailureReason`, `null`/message) rather than exceptions, so batch processing can continue past a bad image. MDS v2/MDX images with compressed or password-less encrypted track data are decoded automatically; images whose track data needs a user password fail preparation unless the password is supplied as the optional `PrepareAsync(..., password:)` argument.

## API reference

All types live in the `MDSSharp` namespace.

### MdsParser

The descriptor parser.

| Member | Description |
|---|---|
| `static bool IsMdsFile(string path)` | Checks whether a file starts with the `MEDIA DESCRIPTOR` signature. Never throws. |
| `static MdsDisc Parse(string mdsPath)` | Parses an `.mds` file and locates its data file. |

### MdsInputPreparer

Turns a parsed image into something `chdman` can read.

| Member | Description |
|---|---|
| `static Task<Result> PrepareAsync(MdsDisc disc, string workDir, Action<string>? onLog, CancellationToken token, string? password = null)` | Runs the full pipeline: decode MDS v2/MDX track data when needed, join, strip and cue. Pass `password` for images whose track data is password-protected. |
| `static Task<string?> StripSubchannelAsync(string sourcePath, string destinationPath, int sectorSize, CancellationToken token)` | Keeps the 2352 data bytes of each oversized sector. `null` on success. |
| `static Task<string> WriteCueAsync(MdsDisc disc, string workDir, string dataFileReference, CancellationToken token, bool pregapsInFile = false, IReadOnlySet<int>? index00Tracks = null)` | Writes a single-file CUE and returns its path. `pregapsInFile` adds `INDEX 00` for tracks whose pregap sectors are present in the referenced file; `index00Tracks` overrides that per track when the pregap layout differs between tracks. |
| `static string FormatMsf(long lba)` | Formats an absolute LBA as `MM:SS:FF`. |

### SplitImageJoiner

| Member | Description |
|---|---|
| `static List<string>? TryGetVolumeSet(string firstVolumePath)` | Returns every piece of a split set in order, or `null` when the file is not a first volume. |
| `static Task<long> JoinAsync(IReadOnlyList<string> parts, string destinationPath, CancellationToken token)` | Concatenates the parts and returns the bytes written. |
| `static long GetTotalBytes(IEnumerable<string> parts)` | Total size of a volume set, or 0 when it cannot be measured. |

### Models

| Type | Description |
|---|---|
| `MdsMedium` | Medium type: `Cd`, `CdR`, `CdRw`, `Dvd`, `DvdMinusR`, or `Unknown`. |
| `MdsDisc` | Parsed image: `SessionCount`, `Tracks`, `MdsPath`, `MdfPath`, `MediumType`, `DataFilePaths`, `HasEncryptedTrackData`, `HasCompressedTrackData`, `IsMdxContainer`, plus constants (`RawSectorSize` 2352, `RawPlusSubchannelSize` 2448, `RawPlusShortSubchannelSize` 2368, `Mode2XaSectorSize` 2336, `CookedSectorSize` 2048) and computed `SectorSize`, `IsDvdImage`, `IsCookedCd`, `IsDvdMedia`, `IsCdMedia`, `NeedsSubchannelStrip`, `IsPlainRawCd`, `AllTracksDescribable`, `HasPregapInfo`, `Summary`. |
| `MdsTrack` | One track: `Number`, `ModeByte`, `SectorSize`, `StartLba`, `PregapSectors`, `LengthSectors`, `StoredDataSectors` (MDS v2 footer count; `LengthSectors` = pregap missing, `LengthSectors` + `PregapSectors` = pregap stored), `SubchannelMode`, `AdrCtl`, plus computed `IsAudio`, `CueTrackType`, `Description`. |
| `MdsInputPreparer.Result` | Preparation outcome: `CuePath`, `DvdImagePath`, `FailureReason`, `Success`. |

## Supported images

- **Descriptors** — `MEDIA DESCRIPTOR` signature, medium type, session table and track table, plus the per-track extra blocks (pregap and length) and footer blocks (data file names). Up to 99 sessions; lead-in and lead-out entries are skipped.
- **MDS v2 / MDX descriptors** — the Daemon Tools variant (`MEDIA DESCRIPTOR` + version 2). The descriptor is decrypted and decompressed transparently, and its 32-byte session, 80-byte track and 32-byte footer blocks are parsed, including UTF-16 file names. Compressed tracks are decoded from their compression tables (stored, run-length and deflate groups); encrypted tracks are decrypted with AES-256 LRW (password-less/TAGES or a supplied password); single-file `.mdx` containers keep their descriptor and data in one file, which is read directly.
- **Per-track pregaps** — each footer's `track_data_length` is the sectors actually stored, so `StoredDataSectors` tells whether a track's pregap is in the data file (`LengthSectors` = missing, `LengthSectors` + `PregapSectors` = stored, the layout libMirage validates). Preparation materializes only the missing, representable pregaps as zeros, so a v2 image that stores one track's pregap and omits another's still gets every `INDEX 00`/`INDEX 01` at the right LBA. The first track's pregap before LBA 0 is never materialized.
- **Medium types** — CD, CD-R, CD-RW (0x00–0x02) and DVD, DVD-R (0x10, 0x12); other values fall back to inspecting the sector sizes.
- **Track modes** — audio, Mode 1 and the Mode 2 forms, selected by the low nibble of the mode byte and mapped to `AUDIO`, `MODE1/2352`/`MODE1/2048` and `MODE2/2352`/`MODE2/2336`.
- **Data files** — one or more files named by the descriptor (single-byte or UTF-16 names, `*.mdf` wildcards), split `.i00`/`.i01` sets, split `.001`/`.002` sets.
- **Sector sizes** — 2352 (raw CD), 2448 (raw + 96 subchannel), 2368 (raw + 16 subchannel), 2336 (cooked Mode 2) and 2048 (cooked CD/DVD data).
- **Pregaps** — `INDEX 00` is written when the data file carries the pregap sectors; descriptors whose pregaps are absent have them rebuilt as zeros first.

## How it works

An Alcohol image is a binary descriptor plus at least one data file. MDSSharp parses the descriptor's medium type, session blocks (24 bytes each) and track blocks (80 bytes each), taking the mode, POINT (track number), sector size and start LBA per track, and reads the per-track extra block (pregap and length) and footer blocks (data file names). The data files are resolved from those names, or by base name in the descriptor's folder or exactly one subfolder when the names cannot be found.

The medium type and the stored sector layout determine what has to happen before conversion:

| Medium (sector size) | Layout | Action |
|---|---|---|
| DVD (2048) | Cooked image | No cue needed; the data file is reported for DVD conversion. |
| CD (2048) | Cooked Mode 1 data | Write a `MODE1/2048` cue referencing the data file. |
| CD (2352) | Raw sectors | Write a cue referencing the data file in place. |
| CD (2448) | 2352 data + 96 subchannel | Strip to 2352 bytes, then write a cue for the stripped file. |
| CD (2368) | 2352 data + 16 subchannel | Strip to 2352 bytes, then write a cue for the stripped file. |
| CD (2336) | Cooked Mode 2 data | Write a `MODE2/2336` cue referencing the data file. |

When the descriptor records pregaps and the track lengths add up to the file size, the data file does not contain the pregap sectors. Those descriptors are rebuilt into a `.pregap.bin` with zero-filled pregaps (stripping subchannel data in the same pass when present) so the cue's `INDEX 00` points at real sectors. If the pregaps are already in the data file, `INDEX 00` points at them directly and nothing is copied. For MDS v2 the decision is made per track from the footer's `StoredDataSectors`, so an image that keeps one track's pregap and omits another's is rebuilt selectively; a pregap before LBA 0 (the first track's) is never materialized.

CUE index values are derived from the descriptor's start LBA with `FormatMsf` (75 frames per second, 60 seconds per minute) and written as UTF-8 without a BOM.

## Building from source

The library lives in the [CHD Studio repository](https://github.com/purelogiccode/CHDStudio) under `MDSSharp/`.

```powershell
git clone https://github.com/purelogiccode/CHDStudio.git
cd CHDStudio
dotnet build MDSSharp/MDSSharp.csproj -c Release
dotnet pack MDSSharp/MDSSharp.csproj -c Release -o artifacts
```

The repository's xUnit suite lives in `CHDStudio.Tests/`:

```powershell
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release
```

## Version history

### 1.2.1

- Package metadata and README now reference the renamed CHD Studio repository (<https://github.com/purelogiccode/CHDStudio>, previously `BatchConvertToCHD`); no functional changes.

### 1.2.0

- MDS v2 and single-file `.mdx` support: the descriptor is decrypted and decompressed, compressed tracks are decoded from their compression tables and encrypted tracks use AES-256 LRW (password-less/TAGES or a supplied password).
- Per-track pregaps: `StoredDataSectors` exposes each footer's stored length, and preparation materializes only the missing, representable pregaps so mixed v2 layouts keep every `INDEX 00`/`INDEX 01` at the right LBA.
- A footer count of zero still reads footer 0 like mdsx, and a later fragment of a split track that names no data file is reported instead of re-reading the first file.
- `MdsDisc` gained `HasEncryptedTrackData`, `HasCompressedTrackData` and `IsMdxContainer`.

### 1.1.0

- Renamed from `Alcohol120Sharp`; package metadata, README and icon added.
- Pregaps are described (`INDEX 00`, rebuilt when absent) and multi-file descriptors are joined in track order.
- Medium type, per-track extra blocks and footer file names are parsed, with the mode taken from the low nibble of the mode byte.

## License

MDSSharp is released under the [MIT license](https://github.com/purelogiccode/CHDStudio).
