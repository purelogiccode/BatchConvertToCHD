# CCDSharp

[![NuGet](https://img.shields.io/nuget/v/CCDSharp.svg)](https://www.nuget.org/packages/CCDSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CCDSharp.svg)](https://www.nuget.org/packages/CCDSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/purelogiccode/BatchConvertToCHD)
[![.NET 8 | 9 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4.svg)](https://dotnet.microsoft.com/download)

**CCDSharp** is a managed C# library for parsing and converting **CloneCD** disc images (`.ccd` + `.img` + `.sub`). It reads the CloneCD descriptor, resolves the associated raw image and subchannel files, and converts the image to a standard **ISO** (2048-byte user data sectors) or **CUE/BIN** pair.

The library is the CloneCD conversion engine used by [Batch Convert to CHD](https://github.com/purelogiccode/BatchConvertToCHD), where it turns CloneCD images into CUE/BIN before the image is compressed to CHD with `chdman` or [CHDSharp](https://www.nuget.org/packages/CHDSharp).

- Repository: <https://github.com/purelogiccode/BatchConvertToCHD>
- Package: <https://www.nuget.org/packages/CCDSharp>

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Usage examples](#usage-examples)
  - [Parse a CCD file](#parse-a-ccd-file)
  - [Convert to ISO](#convert-to-iso)
  - [Convert to CUE/BIN](#convert-to-cuebin)
  - [Generate a CUE sheet](#generate-a-cue-sheet)
  - [Inspect tracks and indexes](#inspect-tracks-and-indexes)
  - [Read subchannel data](#read-subchannel-data)
  - [Parse CCD text from a reader](#parse-ccd-text-from-a-reader)
- [Error handling](#error-handling)
- [API reference](#api-reference)
  - [CcdConverter](#ccdconverter)
  - [CcdParser](#ccdparser)
  - [SubcodeParser](#subcodeparser)
  - [IsoWriter](#isowriter)
  - [Models](#models)
- [Supported images](#supported-images)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [License](#license)

## Features

- **CloneCD descriptor parsing** — reads `[CloneCD]`, `[Disc]` and `[TRACK N]` sections, including track modes, index points, flags, ISRC and the media catalog number.
- **ISO conversion** — extracts the 2048-byte user data from Mode 1 and Mode 2 (Form 1 and Form 2) sectors, with a progress callback.
- **CUE/BIN conversion** — writes a CUE sheet for the `.img` data and either references the `.img` in place or copies it to `.bin`.
- **CUE sheet generation** — `CATALOG`, `FLAGS`, `ISRC`, `INDEX 00` pregaps and `INDEX 02+` sub-indexes, formatted as `MM:SS:FF`.
- **Subchannel access** — `SubcodeParser` exposes the raw 96-byte subchannel data of `.sub` files.
- **Model-first API** — parse once into a `DiscImage` and inspect or reuse the track layout.
- **No native dependencies** — pure managed code, cross-platform.

## Requirements

| Requirement | Value |
|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Runtime | .NET 8, .NET 9 or .NET 10 |
| Platforms | Windows, Linux, macOS (pure managed code) |
| Dependencies | None |

The package contains a separate assembly for each target framework, so the correct build is selected automatically by NuGet.

> **Read-only:** CCDSharp reads CloneCD images, produces ISO and CUE/BIN output, and reads `.sub` subchannel data. It cannot create or modify CloneCD images.

## Installation

```powershell
dotnet add package CCDSharp
```

or with the Package Manager console:

```powershell
Install-Package CCDSharp
```

## Quick start

A CloneCD image is three files that share a base name: the `.ccd` descriptor, the `.img` raw data and an optional `.sub` subchannel file. Point the library at the `.ccd` file; the other two are resolved automatically.

```csharp
using CCDSharp;

var disc = CcdConverter.Parse(@"C:\Games\Game.ccd");
Console.WriteLine(CcdConverter.GetSummary(disc));

var isoPath = CcdConverter.ConvertToIso(
    @"C:\Games\Game.ccd",
    @"C:\Games\Game.iso",
    (written, total) => Console.Write($"\r{written:N0} / {total:N0} bytes")
);

Console.WriteLine();
Console.WriteLine($"Wrote {isoPath}");
```

## Usage examples

### Parse a CCD file

`CcdConverter.Parse` reads the descriptor into a `DiscImage` without touching the `.img` payload, so it is cheap enough to call for inspection or pre-flight checks.

```csharp
using CCDSharp;
using CCDSharp.Models;

if (!CcdConverter.IsCcdFile(ccdPath))
{
    Console.Error.WriteLine("Not a CloneCD descriptor (missing [CloneCD] header).");
    return;
}

var disc = CcdConverter.Parse(ccdPath);

Console.WriteLine(CcdConverter.GetSummary(disc));
Console.WriteLine($"Version:  {disc.Version}");
Console.WriteLine($"Sessions: {disc.Sessions}");
Console.WriteLine($"Catalog:  {disc.Catalog ?? "none"}");
Console.WriteLine($"IMG:      {disc.ImgFilePath ?? "missing"}");
Console.WriteLine($"SUB:      {disc.SubFilePath ?? "not present"}");
```

`IsCcdFile` only checks for the `[CloneCD]` header, while `Parse` accepts any descriptor whose lines it recognizes — unknown lines are ignored.

### Convert to ISO

`ConvertToIso` extracts the 2048-byte user data of every recognizable data sector and writes a cooked ISO. The optional callback reports `(bytesWritten, totalBytes)` every 1000 sectors and once at the end.

```csharp
using CCDSharp;

var isoPath = CcdConverter.ConvertToIso(
    ccdPath,
    Path.ChangeExtension(ccdPath, ".iso"),
    (written, total) =>
    {
        var percent = total == 0 ? 100 : (written * 100.0) / total;
        Console.Write($"\r{percent,6:0.0}%");
    }
);

Console.WriteLine();
Console.WriteLine($"Wrote {isoPath}");
```

> ISO output is only meaningful for data discs. Sectors without a valid sync mark (audio) are skipped, and only the first data track is required to exist — an `InvalidOperationException` is thrown when the disc has no data track at all. Multi-track and mixed-mode discs are better served by CUE/BIN.

### Convert to CUE/BIN

`ConvertToCueBin` writes a CUE sheet and decides how the BIN data is provided. With `copyBinFile: false` (the default) the `.img` file is referenced from the `.cue` using a relative path when possible; on a different volume a copy is made. With `copyBinFile: true` the `.img` is always copied next to the `.cue` as `.bin`.

```csharp
using CCDSharp;

// Reference the existing .img from the cue sheet (no duplication when possible).
var cuePath = CcdConverter.ConvertToCueBin(ccdPath, @"C:\Games\Game.cue");

// Or create a self-contained CUE/BIN pair.
var selfContained = CcdConverter.ConvertToCueBin(
    ccdPath,
    @"C:\Games\Game.cue",
    copyBinFile: true
);

Console.WriteLine($"Wrote {cuePath} and {selfContained}");
```

> The CUE sheet is always written as UTF-8 **without a BOM**, because `chdman`'s cue parser fails on a byte-order mark.

### Generate a CUE sheet

If you only need the text, `GenerateCueSheet` produces it from an already parsed `DiscImage`:

```csharp
using System.Text;
using CCDSharp;

var disc = CcdConverter.Parse(ccdPath);
var cue = CcdConverter.GenerateCueSheet(disc, "Game.bin");

File.WriteAllText(@"C:\Games\Game.cue", cue, new UTF8Encoding(false));
```

A mixed-mode disc yields a sheet like this:

```
CATALOG 1234567890123
FILE "Game.bin" BINARY
  TRACK 01 MODE1/2352
    INDEX 01 00:00:00
  TRACK 02 AUDIO
    INDEX 00 03:12:15
    INDEX 01 03:14:15
```

### Inspect tracks and indexes

Each `Track` exposes its mode, flags, ISRC and index points. `CcdParser.LbaToMsf` and `CcdParser.FormatMsf` convert and format the LBA values.

```csharp
using CCDSharp;
using CCDSharp.Models;
using CCDSharp.Parsers;

var disc = CcdConverter.Parse(ccdPath);

foreach (var track in disc.Tracks)
{
    Console.WriteLine($"Track {track.Number:00}: {track.CueTrackType}");
    Console.WriteLine($"  Audio: {track.IsAudio}");

    if (track.Flags is not null)
        Console.WriteLine($"  FLAGS {track.Flags}");

    if (track.Isrc is not null)
        Console.WriteLine($"  ISRC  {track.Isrc}");

    foreach (var (index, lba) in track.Indexes.OrderBy(i => i.Key))
    {
        var (m, s, f) = CcdParser.LbaToMsf(lba);
        Console.WriteLine($"  INDEX {index:00} {CcdParser.FormatMsf(m, s, f)} (LBA {lba})");
    }
}
```

### Read subchannel data

`SubcodeParser` gives raw access to the 96 bytes of P–W subchannel data that CloneCD stores per sector. It works with a path or with a stream you already own.

```csharp
using CCDSharp.Parsers;

using var sub = new SubcodeParser(@"C:\Games\Game.sub");

Console.WriteLine($"{sub.SectorCount:N0} sectors of subchannel data");

var first = sub.ReadSubchannel(0);
if (first is not null)
    Console.WriteLine($"First sector: {first.Length} bytes");

// Or read a contiguous range (stops early at the end of the file).
var batch = sub.ReadSubchannels(startSector: 0, count: 16);
Console.WriteLine($"Read {batch.Count} sectors");
```

> The subchannel bytes are returned raw. CCDSharp does not decode the Q channel (position, track, index, CRC) for you.

### Parse CCD text from a reader

`CcdParser.Parse(TextReader)` parses descriptor text without a file on disk. The optional path argument is only used to resolve sibling `.img`/`.sub` files.

```csharp
using CCDSharp.Parsers;

var ccdText = File.ReadAllText(@"C:\Games\Game.ccd");

using var reader = new StringReader(ccdText);
var disc = CcdParser.Parse(reader);

Console.WriteLine($"{disc.Tracks.Count} track(s) parsed");
```

## Error handling

CCDSharp reports failures with the standard exceptions instead of an error enum, which keeps the API small and idiomatic.

| Exception | Thrown when |
|---|---|
| `FileNotFoundException` | The `.ccd` file passed to `Parse` does not exist, the `.img` data file is missing for ISO/CUE output, or the `.sub` file passed to `SubcodeParser` does not exist. |
| `InvalidOperationException` | ISO extraction finds no data track, or the `.img` ends with a partial raw sector (not a multiple of 2352 bytes). |
| `IOException` | An I/O operation fails, or the CUE/BIN copy still fails after four retries. |
| `ArgumentNullException` | `SubcodeParser(Stream)` is constructed with a `null` stream. |

Parsing itself is lenient: unrecognized lines and missing fields are ignored, and the corresponding model properties keep their defaults. If you need strict validation, check the returned `DiscImage` (for example `Tracks.Count` or `ImgFilePath`) after parsing.

## API reference

All types live in the `CCDSharp` namespace; the model types live in `CCDSharp.Models` and the parsers in `CCDSharp.Parsers`.

### CcdConverter

The main entry point. A static facade over the parsers and writers.

| Member | Description |
|---|---|
| `static DiscImage Parse(string ccdFilePath)` | Parses a `.ccd` file and resolves the sibling `.img`/`.sub` paths. |
| `static string ConvertToIso(string ccdFilePath, string isoFilePath, Action<long, long>? progress = null)` | Converts a CloneCD image to a standard ISO, reporting `(bytesWritten, totalBytes)`. |
| `static string ConvertToCueBin(string ccdFilePath, string outputCuePath, bool copyBinFile = false)` | Writes a CUE sheet and either references or copies the `.img` as `.bin`. |
| `static string GenerateCueSheet(DiscImage disc, string binFileName)` | Returns the CUE sheet content for a parsed disc. |
| `static bool IsCcdFile(string filePath)` | Checks whether a file starts with the `[CloneCD]` header. Never throws. |
| `static string GetSummary(DiscImage disc)` | Returns a one-line human-readable summary (track counts, sessions, catalog). |

### CcdParser

The descriptor parser, also usable on its own.

| Member | Description |
|---|---|
| `static DiscImage Parse(string ccdFilePath)` | Parses a `.ccd` file. Throws `FileNotFoundException` when missing. |
| `static DiscImage Parse(TextReader reader, string? ccdFilePath = null)` | Parses descriptor text from any reader. |
| `static (int Minutes, int Seconds, int Frames) LbaToMsf(int lba)` | Converts an LBA frame count to MSF. |
| `static string FormatMsf(int minutes, int seconds, int frames)` | Formats an MSF tuple as `MM:SS:FF`. |

### SubcodeParser

Read-only access to the subchannel data of a `.sub` file. Implements `IDisposable`.

| Member | Description |
|---|---|
| `SubcodeParser(string subFilePath)` | Opens a `.sub` file. The instance owns the file stream. |
| `SubcodeParser(Stream stream, bool ownsStream = false)` | Reads from a stream you provide; `ownsStream` controls disposal. |
| `const int SubchannelSize` | Bytes of subchannel data per sector (96). |
| `long SectorCount` | Total number of sectors in the file. |
| `byte[]? ReadSubchannel(long sectorIndex)` | Returns the 96 raw bytes for one sector, or `null` when out of range. |
| `IList<byte[]> ReadSubchannels(long startSector, int count)` | Returns up to `count` sectors, stopping at the end of the file. |
| `void Dispose()` | Disposes the stream when the instance owns it. |

### IsoWriter

Lower-level ISO conversion for callers that already have streams or a parsed disc.

| Member | Description |
|---|---|
| `static string Write(string imgFilePath, string isoFilePath, Action<long, long>? progress = null)` | Converts a raw `.img` file to ISO. |
| `static string Write(DiscImage disc, string isoFilePath, Action<long, long>? progress = null)` | Converts the first data track of a parsed disc to ISO. |
| `static void WriteToStream(Stream input, Stream output, long totalSectors = -1, Action<long, long>? progress = null)` | Converts raw sectors from a stream to user data in another stream. |

### Models

| Type | Description |
|---|---|
| `DiscImage` | Parsed disc: `Version`, `TocEntries`, `Sessions`, `DataTracksScrambled`, `CdTextLength`, `Catalog`, `Tracks`, `FilePath`, `ImgFilePath`, `SubFilePath`. |
| `Track` | One track: `Number`, `Mode`, `Indexes` (index number → LBA), `Flags`, `Isrc`, plus the computed `Index01Lba`, `CueTrackType` and `IsAudio`. |
| `TrackMode` | Enum: `Audio` (0), `Mode1` (1), `Mode2` (2). |
| `SectorConstants` | Constants: `RawSectorSize` (2352), `UserDataSize` (2048), `ModeOffset` (15), `Mode1DataOffset` (16), `Mode2Form1DataOffset` (24), `FramesPerSecond` (75), `SecondsPerMinute` (60), `FramesPerMinute` (4500), `LeadInSectors` (150) and `SyncMark`. |

## Supported images

- **CloneCD descriptors** — `[CloneCD]` version, `[Disc]` fields (TOC entries, sessions, scrambled data flag, CD-TEXT length, catalog) and `[TRACK N]` fields (mode, indexes, flags, ISRC). `[Session N]` and `[Entry N]` sections are recognized and skipped.
- **Track modes** — audio (2352-byte PCM), Mode 1 and Mode 2 data tracks.
- **Raw `.img` data** — 2352-byte sectors. ISO extraction handles Mode 1, Mode 2 Form 1 and Mode 2 Form 2; sectors without a valid sync mark are skipped.
- **Subchannel `.sub` data** — 96 bytes per sector, exposed raw.
- **CUE/BIN output** — one BIN per disc, with `CATALOG`, `FLAGS`, `ISRC`, `INDEX 00` (audio pregaps) and `INDEX 02+` entries.

## How it works

A CloneCD image is a text descriptor plus raw data files. CCDSharp parses the descriptor sections:

| Section | Fields used |
|---|---|
| `[CloneCD]` | `Version` |
| `[Disc]` | `TocEntries`, `Sessions`, `DataTracksScrambled`, `CDTextLength`, `CATALOG` |
| `[Session N]`, `[Entry N]` | Recognized and skipped |
| `[TRACK N]` | `MODE` (0 = audio, 1 = Mode 1, 2 = Mode 2), `INDEX n = <LBA>`, `FLAGS`, `ISRC` |

The `.img` and `.sub` files are resolved by replacing the `.ccd` extension with `.img` and `.sub` next to the descriptor.

The `.img` file stores raw 2352-byte sectors. CCDSharp recognizes the CD sync mark (`00 FF × 10 00`) and extracts user data according to the mode byte:

| Mode | Layout |
|---|---|
| Mode 1 | sync (12) · header (4) · **user data (2048)** · EDC (4) · zero (8) · ECC-P (172) · ECC-Q (104) |
| Mode 2 Form 1 | sync (12) · header (4) · subheader (8) · **user data (2048)** · EDC (4) · ECC-P (172) · ECC-Q (104) |
| Mode 2 Form 2 | sync (12) · header (4) · subheader (8) · **user data (2324)** · EDC (4) |

ISO output copies the first 2048 bytes of the user data area; for Mode 2 Form 2 sectors the remaining 276 bytes have no ISO equivalent and are dropped.

CUE index values are derived from LBA frames with `LbaToMsf` (75 frames per second, 60 seconds per minute) and formatted as `MM:SS:FF`.

## Building from source

The library lives in the [Batch Convert to CHD repository](https://github.com/purelogiccode/BatchConvertToCHD) under `CCDSharp/`.

```powershell
git clone https://github.com/purelogiccode/BatchConvertToCHD.git
cd BatchConvertToCHD
dotnet build CCDSharp/CCDSharp.csproj -c Release
dotnet pack CCDSharp/CCDSharp.csproj -c Release -o artifacts
```

The repository's xUnit suite lives in `BatchConvertToCHD.Tests/`:

```powershell
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release
```

## License

CCDSharp is released under the [MIT license](https://github.com/purelogiccode/BatchConvertToCHD).
