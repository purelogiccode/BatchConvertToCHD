# PBPSharp

[![NuGet](https://img.shields.io/nuget/v/PBPSharp.svg)](https://www.nuget.org/packages/PBPSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/PBPSharp.svg)](https://www.nuget.org/packages/PBPSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/purelogiccode/BatchConvertToCHD)
[![.NET 8 | 9 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4.svg)](https://dotnet.microsoft.com/download)

**PBPSharp** is a managed, read-only C# library for reading PlayStation Portable **PBP** (`EBOOT.PBP`) containers and extracting the PlayStation disc images they carry. It handles both single-disc and multi-disc PBPs, parses the embedded `PARAM.SFO` metadata, decodes the PSAR ISO block index, and writes the extracted disc as a BIN file with a matching CUE sheet.

The library is the PBP extraction engine used by [Batch Convert to CHD](https://github.com/purelogiccode/BatchConvertToCHD), where it serves as an intermediate step before converting PBP discs to CHD with `chdman` or [CHDSharp](https://www.nuget.org/packages/CHDSharp).

- Repository: <https://github.com/purelogiccode/BatchConvertToCHD>
- Package: <https://www.nuget.org/packages/PBPSharp>

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Usage examples](#usage-examples)
  - [Read the metadata of a PBP](#read-the-metadata-of-a-pbp)
  - [Extract a single-disc PBP to BIN/CUE](#extract-a-single-disc-pbp-to-bincue)
  - [Extract every disc of a multi-disc PBP](#extract-every-disc-of-a-multi-disc-pbp)
  - [Extract to a stream with progress and cancellation](#extract-to-a-stream-with-progress-and-cancellation)
  - [Open a PBP from a stream](#open-a-pbp-from-a-stream)
  - [Generate a CUE sheet from the disc TOC](#generate-a-cue-sheet-from-the-disc-toc)
  - [Read raw ISO blocks](#read-raw-iso-blocks)
  - [Enumerate all SFO metadata entries](#enumerate-all-sfo-metadata-entries)
- [Error handling](#error-handling)
- [API reference](#api-reference)
  - [PbpFile](#pbpfile)
  - [PbpDiscInfo](#pbpdiscinfo)
  - [CueSheetWriter](#cuesheetwriter)
  - [Models](#models)
- [Supported PBP variants](#supported-pbp-variants)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [License](#license)

## Features

- **Single-disc and multi-disc PBP support** — `PSISOIMG0000` containers open as one disc, `PSTITLEIMG000000` containers expose every disc found in the multi-disc position table (up to 5 discs).
- **SFO metadata parsing** — reads `PARAM.SFO` entries (`TITLE`, `DISC_ID`, `CATEGORY`, `BOOTABLE`, `REGION`, and any custom key) as UTF-8 strings or 32-bit integers, with well-known keys exposed through `SfoData.Keys`.
- **Disc ID and TOC parsing** — reads the PSAR game ID (for example `SCUS94163`) and the full track table, including audio tracks, with best-effort tolerance for malformed data.
- **ISO extraction with CUE generation** — `ExtractToBinCue` writes the disc as a BIN file and generates a matching CUE sheet, including `INDEX 00` positions for audio tracks.
- **Stream-friendly** — extract to any writable `Stream`, or open a PBP from a `Stream` you own, so it works with archives, network streams, and in-memory data.
- **Progress and cancellation** — every long-running extraction accepts a byte-progress callback and a `CancellationToken`.
- **Authoring-tool compatibility** — reads PSAR block indexes written by popstation, PSX2PSP, iPoPS and pop-fe (both the 16-bit and 32-bit index layouts, stored/uncompressed blocks, and zlib-wrapped or raw-deflate blocks).
- **No native dependencies** — pure managed code, cross-platform, read-only by design.

## Requirements

| Requirement | Value |
|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Runtime | .NET 8, .NET 9 or .NET 10 |
| Platforms | Windows, Linux, macOS (pure managed code) |
| Dependencies | [SharpZipLib](https://www.nuget.org/packages/SharpZipLib) 1.4.2 |

The package contains a separate assembly for each target framework, so the correct build is selected automatically by NuGet.

> **Read-only:** PBPSharp can open, inspect and extract PBP files. It cannot create, modify or repack them.

## Installation

```powershell
dotnet add package PBPSharp
```

or with the Package Manager console:

```powershell
Install-Package PBPSharp
```

## Quick start

```csharp
using PBPSharp;
using PBPSharp.Models;

var error = PbpFile.Open(@"C:\Games\EBOOT.PBP", out var pbp);
if (error != PbpError.None || pbp is null)
{
    Console.Error.WriteLine($"Could not open the PBP: {error}");
    return;
}

using (pbp)
{
    Console.WriteLine($"{pbp.Title} ({pbp.DiscId}) - {pbp.Discs.Count} disc(s)");

    var disc = pbp.Discs[0];
    var result = disc.ExtractToBinCue(@"C:\Games\Game.bin");
    Console.WriteLine(result == PbpError.None ? "Extracted." : $"Failed: {result}");
}
```

## Usage examples

### Read the metadata of a PBP

`PbpFile.Open` parses the container, the `PARAM.SFO` metadata and every disc up front, so the overview properties are available without touching the disc payload.

```csharp
using PBPSharp;
using PBPSharp.Models;

var error = PbpFile.Open(pbpPath, out var pbp);
if (error != PbpError.None || pbp is null)
{
    Console.Error.WriteLine($"Could not open '{pbpPath}': {error}");
    return;
}

using (pbp)
{
    Console.WriteLine($"Title:     {pbp.Title ?? "(unknown)"}");
    Console.WriteLine($"Disc ID:   {pbp.DiscId ?? "(unknown)"}");
    Console.WriteLine($"Category:  {pbp.Category ?? "(unknown)"}");
    Console.WriteLine($"Multi-disc:{pbp.IsMultiDisc}");

    foreach (var disc in pbp.Discs)
    {
        Console.WriteLine($"  Disc {disc.Index}: {disc.DiscId}, " +
                          $"{disc.BlockCount} blocks, {disc.IsoSize:N0} bytes, " +
                          $"{disc.Toc.Count} tracks");
    }
}
```

> A missing or corrupt `PARAM.SFO` does not fail the open. Metadata-dependent properties simply return `null` while the disc images remain extractable.

### Extract a single-disc PBP to BIN/CUE

`PbpDiscInfo.ExtractToBinCue` writes the BIN file and, when no cue path is given, a CUE file next to it with the `.cue` extension.

```csharp
using PBPSharp;
using PBPSharp.Models;

var error = PbpFile.Open(pbpPath, out var pbp);
if (error != PbpError.None || pbp is null)
{
    Console.Error.WriteLine($"Could not open '{pbpPath}': {error}");
    return;
}

using (pbp)
{
    var binPath = Path.ChangeExtension(pbpPath, ".bin"); // cue becomes .cue

    var result = pbp.Discs[0].ExtractToBinCue(binPath);
    if (result != PbpError.None)
    {
        Console.Error.WriteLine($"Extraction failed: {result}");
        return;
    }

    Console.WriteLine($"Extracted {pbp.Discs[0].IsoSize:N0} bytes to {binPath}");
}
```

### Extract every disc of a multi-disc PBP

Multi-disc PBPs expose one `PbpDiscInfo` per disc in `PbpFile.Discs`. The `Index` property is 1-based and can be used to build the usual `- Disc N` file names.

```csharp
using PBPSharp;
using PBPSharp.Models;

var outputDir = @"D:\PS1 Rips";
Directory.CreateDirectory(outputDir);

var error = PbpFile.Open(pbpPath, out var pbp);
if (error != PbpError.None || pbp is null)
{
    Console.Error.WriteLine($"Could not open '{pbpPath}': {error}");
    return;
}

using (pbp)
{
    var baseName = Path.GetFileNameWithoutExtension(pbpPath);

    foreach (var disc in pbp.Discs)
    {
        var suffix = pbp.IsMultiDisc ? $" - Disc {disc.Index}" : string.Empty;
        var binPath = Path.Combine(outputDir, $"{baseName}{suffix}.bin");
        var cuePath = Path.ChangeExtension(binPath, ".cue");

        var result = disc.ExtractToBinCue(
            binPath,
            cuePath,
            bytesWritten =>
                Console.WriteLine($"Disc {disc.Index}: {bytesWritten:N0} bytes written")
        );

        Console.WriteLine(
            result == PbpError.None
                ? $"Disc {disc.Index} -> {binPath}"
                : $"Disc {disc.Index} failed: {result}"
        );
    }
}
```

### Extract to a stream with progress and cancellation

`PbpDiscInfo.ExtractTo` writes the raw ISO sectors to any writable stream. The optional callback reports the total bytes written so far, and the cancellation token is checked before every block.

```csharp
using PBPSharp;
using PBPSharp.Models;

using var source = File.OpenRead(pbpPath);
var error = PbpFile.Open(source, ownsStream: false, out var pbp);
if (error != PbpError.None || pbp is null)
{
    Console.Error.WriteLine($"Could not open '{pbpPath}': {error}");
    return;
}

using (pbp)
using (var cts = new CancellationTokenSource())
using (var output = File.Create(@"C:\Games\Game.iso"))
{
    var disc = pbp.Discs[0];

    disc.ExtractTo(
        output,
        bytesWritten =>
        {
            var percent = disc.IsoSize == 0 ? 100 : (bytesWritten * 100.0) / disc.IsoSize;
            Console.Write($"\r{percent,6:0.0}%");
        },
        cts.Token
    );

    Console.WriteLine();
}
```

> `ExtractTo` can throw `OperationCanceledException` when the token is cancelled and `InvalidDataException` when a compressed block cannot be decoded. Use `ExtractToBinCue` when you prefer an error code over exceptions, or trim the partial output file yourself after a failure.

### Open a PBP from a stream

Both overloads require a **readable, seekable** stream. The `ownsStream` flag decides whether disposing the `PbpFile` also disposes the stream.

```csharp
using PBPSharp;
using PBPSharp.Models;

using var file = File.OpenRead(pbpPath);
var error = PbpFile.Open(file, ownsStream: false, out var pbp);

if (error != PbpError.None || pbp is null)
{
    Console.Error.WriteLine($"Could not open the PBP: {error}");
    return;
}

using (pbp)
{
    Console.WriteLine(pbp.Title);
}

// The stream is still usable here because ownsStream was false.
```

You can also open a PBP that is already in memory:

```csharp
var bytes = await File.ReadAllBytesAsync(pbpPath);
using var memory = new MemoryStream(bytes);
var error = PbpFile.Open(memory, ownsStream: false, out var pbp);
```

### Generate a CUE sheet from the disc TOC

`CueSheetWriter.GenerateCueSheet` produces the CUE text without extracting anything, which is useful when the BIN already exists or when you need to inspect the track layout.

```csharp
using PBPSharp;
using PBPSharp.Models;

var error = PbpFile.Open(pbpPath, out var pbp);
if (error != PbpError.None || pbp is null)
    return;

using (pbp)
{
    var disc = pbp.Discs[0];

    foreach (var track in disc.Toc)
    {
        Console.WriteLine(
            $"Track {track.TrackNo:00}: {track.TrackType}, " +
            $"{track.Minutes:00}:{track.Seconds:00}:{track.Frames:00}"
        );
    }

    var cue = CueSheetWriter.GenerateCueSheet("Game.bin", disc.Toc);
    File.WriteAllText(@"C:\Games\Game.cue", cue);
}
```

A generated CUE looks like this:

```cue
FILE "Game.bin" BINARY
  TRACK 01 MODE2/2352
    INDEX 01 00:02:00
  TRACK 02 AUDIO
    INDEX 00 04:11:72
    INDEX 01 04:12:72
```

### Read raw ISO blocks

For advanced scenarios you can read and decompress individual 16-sector blocks. Each block is up to `16 * PbpDiscInfo.IsoBlockSize` bytes. The last block may be shorter; always use the returned `bytesRead`.

```csharp
using PBPSharp;
using PBPSharp.Models;

var error = PbpFile.Open(pbpPath, out var pbp);
if (error != PbpError.None || pbp is null)
    return;

using (pbp)
{
    var disc = pbp.Discs[0];
    var buffer = new byte[16 * PbpDiscInfo.IsoBlockSize];

    for (var i = 0; i < disc.BlockCount; i++)
    {
        disc.ReadBlock(i, buffer, out var bytesRead);

        // Process buffer[0..bytesRead]
    }
}
```

### Enumerate all SFO metadata entries

`SfoData.Entries` contains every entry in file order, including keys that are not in the well-known list.

```csharp
using PBPSharp;
using PBPSharp.Models;

var error = PbpFile.Open(pbpPath, out var pbp);
if (error != PbpError.None || pbp is null)
    return;

using (pbp)
{
    foreach (var entry in pbp.SfoData.Entries)
    {
        var type = entry.Format == 0x0404 ? "uint32" : "string";
        Console.WriteLine($"{entry.Key,-16} {type,-6} ({entry.Length,4} bytes) = {entry.Value}");
    }

    // Well-known keys are available as constants.
    var title = pbp.SfoData.GetString(SfoData.Keys.Title);
    var bootable = pbp.SfoData.GetUInt32(SfoData.Keys.Bootable);
}
```

## Error handling

`PbpFile.Open` and `PbpDiscInfo.ExtractToBinCue` return a `PbpError` value instead of throwing for data-related problems. Corrupt containers, unsupported PSAR headers, truncated downloads and decompression failures are all reported through the enum, which makes batch processing straightforward.

```csharp
var error = PbpFile.Open(path, out var pbp);

switch (error)
{
    case PbpError.None:
        // pbp is ready to use
        break;
    case PbpError.FileNotFound:
        Console.Error.WriteLine("The file does not exist.");
        break;
    case PbpError.TruncatedPsar:
        Console.Error.WriteLine("The PBP looks truncated - re-download it.");
        break;
    case PbpError.InvalidPsarHeader:
        Console.Error.WriteLine("Not a PlayStation disc image (PSP app or unsupported PBP).");
        break;
    default:
        Console.Error.WriteLine($"Could not open the PBP: {error}");
        break;
}
```

| `PbpError` | Value | Meaning |
|---|---:|---|
| `None` | 0 | Success. |
| `InvalidHeader` | 1 | Missing or invalid PBP magic/header. |
| `FileNotFound` | 2 | The file does not exist or could not be opened. |
| `IoError` | 3 | An I/O error occurred (also returned for non-readable/non-seekable streams). |
| `CorruptFile` | 4 | The container is corrupt or its internal structure is invalid. |
| `InvalidPsarHeader` | 5 | The PSAR is not a PlayStation disc image (for example a PSP application). |
| `DiscOutOfRange` | 6 | The requested disc index does not exist. |
| `ResourceNotFound` | 7 | The requested resource is not present. |
| `DecompressionError` | 8 | An ISO block could not be decompressed. |
| `TruncatedPsar` | 9 | The PSAR parses but has no ISO index entries - typically a truncated/incomplete download. |
| `InvalidSfo` | 10 | Retained for API compatibility; `Open` no longer returns it (SFO problems are tolerated). |

Notes:

- `ReadBlock` throws `ArgumentOutOfRangeException` for an out-of-range `blockIndex` and `InvalidDataException` when the index entry is corrupt.
- `ExtractTo` propagates `OperationCanceledException` and `InvalidDataException`.
- `ExtractToBinCue` writes the BIN first. If extraction fails, a partial BIN file may remain; delete it if the returned error is not `PbpError.None`.
- `NoIsoIndexException` is what the PSAR parser throws internally when a valid disc container carries no ISO index. `PbpFile.Open` catches it and reports `PbpError.TruncatedPsar`.
- When a block fails to decompress, `PbpDiagnostics.TakeDetail()` returns a one-line description of the failing block - its index and count, absolute file offset, index-entry length, stored flag, ISO size, disc id and a hex preview of the first bytes, plus the raw-deflate and zlib error messages. The detail is per-thread and cleared when read, so it is never reported twice. Attach it to logs or bug reports; the `PbpError` code alone cannot identify the block.

## API reference

All types live in the `PBPSharp` namespace; the model types live in `PBPSharp.Models`.

### PbpFile

The entry point. Represents an opened PBP container and owns the underlying stream when it was opened from a path.

| Member | Description |
|---|---|
| `static PbpError Open(string path, out PbpFile? pbp)` | Opens a PBP from disk. The returned instance owns the file stream. |
| `static PbpError Open(Stream stream, bool ownsStream, out PbpFile? pbp)` | Opens a PBP from a readable, seekable stream. `ownsStream` controls whether the stream is disposed with the instance. |
| `PbpHeader Header` | Parsed PBP header with the offsets of the embedded resources (SFO, ICON0, ICON1, PIC0, PIC1, SND0, DATA.PSP, DATA.PSAR) and the version. |
| `SfoData SfoData` | Parsed `PARAM.SFO` metadata. Never null; empty when the SFO is missing or corrupt. |
| `IReadOnlyList<PbpDiscInfo> Discs` | All discs found in the PSAR, in disc order. |
| `bool IsMultiDisc` | `true` when more than one disc was found. |
| `string? Title` | `TITLE` from the SFO, or `null`. |
| `string? DiscId` | `DISC_ID` from the SFO (the first disc for multi-disc PBPs), or `null`. |
| `string? Category` | `CATEGORY` from the SFO (for example `ME`), or `null`. |
| `void Dispose()` | Releases the stream when the instance owns it. Safe to call multiple times. |

### PbpDiscInfo

Represents one disc inside a PBP.

| Member | Description |
|---|---|
| `const int IsoBlockSize` | Size of one ISO sector in bytes (`0x930` = 2352). One PSAR block holds 16 sectors. |
| `int Index` | 1-based disc index within the PBP. |
| `string DiscId` | Disc ID read from the PSAR image header (for example `SCUS94163`). |
| `IReadOnlyList<TocEntry> Toc` | Track entries of the disc, including data and audio tracks. Empty when the TOC is malformed. |
| `uint IsoSize` | Total uncompressed ISO size in bytes. |
| `int BlockCount` | Number of indexed ISO blocks. |
| `void ReadBlock(int blockIndex, byte[] buffer, out int bytesRead)` | Reads and decompresses one block. The buffer must be at least `16 * IsoBlockSize` bytes. |
| `void ExtractTo(Stream outputStream, Action<uint>? progress = null, CancellationToken cancellationToken = default)` | Extracts the full ISO to a stream, reporting cumulative bytes written and honoring cancellation. |
| `PbpError ExtractToBinCue(string binPath, string? cuePath = null, Action<uint>? progress = null, CancellationToken cancellationToken = default)` | Extracts to a BIN file and generates a CUE. When `cuePath` is `null`, the BIN path with a `.cue` extension is used. |

### CueSheetWriter

| Member | Description |
|---|---|
| `static string GenerateCueSheet(string binFileName, IReadOnlyList<TocEntry> tocEntries)` | Generates the complete CUE sheet text for the given BIN name and TOC. Data tracks become `MODE2/2352`, audio tracks become `AUDIO`, and audio tracks get an `INDEX 00` position 150 frames before `INDEX 01`. |

### PbpDiagnostics

| Member | Description |
|---|---|
| `static void SetDetail(string detail)` | Records a failure detail for the current thread. Called internally when a block fails to inflate. |
| `static string? TakeDetail()` | Returns the most recent failure detail for the current thread and clears it, or `null` when none was recorded. |

### Models

| Type | Description |
|---|---|
| `PbpHeader` | Read-only struct with the parsed PBP header fields: `Version`, `SfoOffset`, `Icon0Offset`, `Icon1Offset`, `Pic0Offset`, `Pic1Offset`, `Snd0Offset`, `DataPspOffset`, `DataPsarOffset`, `IsValid`, plus the `MagicValue` and `HeaderSize` constants. |
| `PbpError` | Result/error enum returned by `Open` and `ExtractToBinCue` (see the table above). |
| `SfoData` | Parsed `PARAM.SFO`: `Magic`, `Version`, `KeyTableOffset`, `DataTableOffset`, `Entries`, `Size`, `GetString(key)`, `GetUInt32(key)` and the `Keys` constants (`Bootable`, `Category`, `DiscId`, `DiscVersion`, `License`, `ParentalLevel`, `PspSystemVer`, `Region`, `Title`). |
| `SfoEntry` | One SFO entry: `Key`, `Format` (`0x0204` UTF-8 string, `0x0404` uint32), `Length`, `MaxLength`, `Value`. |
| `TocEntry` | One TOC track: `TrackType`, `TrackNo`, `Minutes`, `Seconds`, `Frames`. |
| `TrackType` | `Data = 0x41`, `Audio = 0x01`. |
| `NoIsoIndexException` | Thrown when a valid PSAR disc container has no ISO index entries; surfaces as `PbpError.TruncatedPsar` from `Open`. |

## Supported PBP variants

PBP files are produced by several authoring tools, and PBPSharp reads the layouts each of them writes:

- **popstation / PSX2PSP / iPoPS** — 32-bit ISO index size field, raw deflate blocks.
- **pop-fe** — official 16-bit index size field with a stored/uncompressed flag byte, uncompressed (stored) blocks when compression is disabled, zlib-wrapped deflate blocks, and multi-disc `PSTITLEIMG` headers with zeroed template fields.
- **Incompressible blocks** — deflate streams a few bytes larger than the raw 16-sector block are accepted.
- **Truncated PBPs** — a valid disc container with no ISO index is reported as `PbpError.TruncatedPsar` rather than a generic corruption error.
- **PSP applications** — PBPs whose PSAR is not a PlayStation disc image are reported as `PbpError.InvalidPsarHeader` and are skipped cleanly.

## How it works

A PBP file starts with a 40-byte header whose magic is `0x50425000` ("`\0PBP`") and whose fields are file offsets to `PARAM.SFO`, the icon/picture/sound resources, `DATA.PSP` and `DATA.PSAR`. PBPSharp reads that header, parses the SFO (best effort), and then looks at the PSAR section:

1. `PSISOIMG0000` marks a single-disc image; `PSTITLEIMG000000` marks a multi-disc container whose disc positions are read from the table at PSAR+`0x200`.
2. Each disc has a game ID at PSAR+`0x400`, a TOC at PSAR+`0x800`, an ISO block index at PSAR+`0x4000`, and the ISO data at PSAR+`0x100000`.
3. Each 32-byte index entry gives the block offset, stored length and (in the pop-fe layout) a stored/uncompressed flag. Blocks are either copied verbatim or inflated from raw deflate, with a zlib-wrapped retry for tools that use zlib.
4. The total ISO size is derived from the sector count stored at bytes 104-107 of the second ISO block, matching the popstation reference implementation.

All offsets are computed with 64-bit math, so multi-gigabyte images cannot overflow.

## Building from source

The library lives in the [Batch Convert to CHD repository](https://github.com/purelogiccode/BatchConvertToCHD) under `PBPSharp/`.

```powershell
git clone https://github.com/purelogiccode/BatchConvertToCHD.git
cd BatchConvertToCHD
dotnet build PBPSharp/PBPSharp.csproj -c Release
dotnet pack PBPSharp/PBPSharp.csproj -c Release -o artifacts
```

The test suite for the library lives in `BatchConvertToCHD.Tests/`:

```powershell
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release --filter "FullyQualifiedName~Pbp"
```

## License

PBPSharp is released under the [MIT license](https://github.com/purelogiccode/BatchConvertToCHD).
