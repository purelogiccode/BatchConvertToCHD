# CSOSharp

[![NuGet](https://img.shields.io/nuget/v/CSOSharp.svg)](https://www.nuget.org/packages/CSOSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CSOSharp.svg)](https://www.nuget.org/packages/CSOSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/purelogiccode/BatchConvertToCHD)
[![.NET 8 | 9 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4.svg)](https://dotnet.microsoft.com/download)

**CSOSharp** is a managed C# library for reading and extracting **CSO/CISO** (Compressed ISO) files. It supports both the classic CSO v1 (deflate/zlib) format and CSO v2, also known as **ZSO** (LZ4), and can decode individual blocks, expose the decompressed image as a seekable `Stream`, or extract the whole ISO to disk.

The library is the CSO extraction engine used by [Batch Convert to CHD](https://github.com/purelogiccode/BatchConvertToCHD), where it serves as an intermediate step before converting CSO images to CHD with `chdman` or [CHDSharp](https://www.nuget.org/packages/CHDSharp).

- Repository: <https://github.com/purelogiccode/BatchConvertToCHD>
- Package: <https://www.nuget.org/packages/CSOSharp>

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Usage examples](#usage-examples)
  - [Open and inspect a CSO header](#open-and-inspect-a-cso-header)
  - [Read individual blocks](#read-individual-blocks)
  - [Stream the decompressed ISO](#stream-the-decompressed-iso)
  - [Extract a CSO to ISO with progress and cancellation](#extract-a-cso-to-iso-with-progress-and-cancellation)
  - [Open a CSO from a stream](#open-a-cso-from-a-stream)
  - [Copy the ISO data to another stream](#copy-the-iso-data-to-another-stream)
- [Error handling](#error-handling)
- [API reference](#api-reference)
  - [CsoFile](#csofile)
  - [CsoStream](#csostream)
  - [Models](#models)
- [Supported CSO variants](#supported-cso-variants)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [License](#license)

## Features

- **CSO v1 (deflate/zlib) and CSO v2/ZSO (LZ4)** — both formats are decoded transparently; the header tells you which one you are reading.
- **Block-level access** — `ReadBlock` decompresses a single block at a time, with an overload that writes at a byte offset in your buffer.
- **Seekable stream** — `OpenStream` returns a read-only `Stream` (including a `Span<byte>` overload on modern .NET) that decompresses blocks on demand and caches the current block.
- **One-call ISO extraction** — `ExtractToIso` writes the full image to disk with a progress callback and cancellation support.
- **Stream-friendly** — open a CSO from a path or from any readable, seekable `Stream` you own.
- **Stored blocks** — uncompressed blocks flagged in the index table are copied verbatim.
- **No native dependencies** — pure managed code, cross-platform.

## Requirements

| Requirement | Value |
|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Runtime | .NET 8, .NET 9 or .NET 10 |
| Platforms | Windows, Linux, macOS (pure managed code) |
| Dependencies | [K4os.Compression.LZ4](https://www.nuget.org/packages/K4os.Compression.LZ4) 1.3.8 |

The package contains a separate assembly for each target framework, so the correct build is selected automatically by NuGet.

> **Read-only:** CSOSharp can open, inspect and extract CSO files. It cannot create, modify or repack them.

## Installation

```powershell
dotnet add package CSOSharp
```

or with the Package Manager console:

```powershell
Install-Package CSOSharp
```

## Quick start

```csharp
using CSOSharp;
using CSOSharp.Models;

var error = CsoFile.Open(@"C:\Games\Game.cso", out var cso);
if (error != CsoError.None || cso is null)
{
    Console.Error.WriteLine($"Could not open the CSO: {error}");
    return;
}

using (cso)
{
    Console.WriteLine(
        $"{cso.Header.UncompressedSize:N0} bytes in {cso.Header.TotalBlocks} blocks " +
        $"({(cso.IsLz4 ? "CSO v2/LZ4" : "CSO v1/deflate")})"
    );

    var result = cso.ExtractToIso(
        @"C:\Games\Game.iso",
        (processed, total) => Console.Write($"\r{processed}/{total} blocks")
    );

    Console.WriteLine();
    Console.WriteLine(result == CsoError.None ? "Extracted." : $"Failed: {result}");
}
```

## Usage examples

### Open and inspect a CSO header

`CsoFile.Open` reads and validates the 24-byte header and the block index table up front, so every header property is available without touching the compressed payload.

```csharp
using CSOSharp;
using CSOSharp.Models;

var error = CsoFile.Open(csoPath, out var cso);
if (error != CsoError.None || cso is null)
{
    Console.Error.WriteLine($"Could not open '{csoPath}': {error}");
    return;
}

using (cso)
{
    var header = cso.Header;

    Console.WriteLine($"Magic:            0x{header.Magic:X8} (valid: {header.IsValid})");
    Console.WriteLine($"Version:          {header.Version} ({(cso.IsLz4 ? "LZ4" : "deflate")})");
    Console.WriteLine($"Block size:       {header.BlockSize:N0} bytes");
    Console.WriteLine($"Total blocks:     {header.TotalBlocks:N0}");
    Console.WriteLine($"Uncompressed size:{header.UncompressedSize:N0} bytes");
    Console.WriteLine($"Index shift:      {header.IndexOffsetShift} bits");
}
```

> The high bit of an index entry marks a block that is stored uncompressed; the remaining 31 bits are the file offset, left-shifted by `IndexOffsetShift`. CSOSharp handles this for you.

### Read individual blocks

Each block decompresses to exactly `Header.BlockSize` bytes (typically 2,048). Use the returned `bytesRead` value rather than assuming the buffer was filled.

```csharp
using CSOSharp;
using CSOSharp.Models;

var error = CsoFile.Open(csoPath, out var cso);
if (error != CsoError.None || cso is null)
    return;

using (cso)
{
    var buffer = new byte[cso.Header.BlockSize];

    for (uint i = 0; i < cso.Header.TotalBlocks; i++)
    {
        error = cso.ReadBlock(i, buffer, out var bytesRead);
        if (error != CsoError.None)
        {
            Console.Error.WriteLine($"Block {i} failed: {error}");
            break;
        }

        // Process buffer[0..bytesRead]
    }
}
```

The offset overload lets you fill a larger buffer without a copy:

```csharp
var batch = new byte[cso.Header.BlockSize * 16];
var offset = 0;

for (uint i = 0; i < 16; i++)
{
    error = cso.ReadBlock(i, batch, offset, out var bytesRead);
    if (error != CsoError.None)
        break;

    offset += bytesRead;
}
```

### Stream the decompressed ISO

`OpenStream` returns a read-only, seekable `CsoStream` over the decompressed image. Blocks are decoded on demand and the most recently used block is cached, so sequential reads do not re-decompress the same block.

```csharp
using CSOSharp;
using CSOSharp.Models;

var error = CsoFile.Open(csoPath, out var cso);
if (error != CsoError.None || cso is null)
    return;

using (cso)
using (var iso = cso.OpenStream())
{
    Console.WriteLine($"ISO length: {iso.Length:N0} bytes");

    // Seek to a sector and read it.
    const int sectorSize = 2048;
    iso.Position = 1_000L * sectorSize;

    var sector = new byte[sectorSize];
    var read = iso.Read(sector, 0, sector.Length);
    Console.WriteLine($"Read {read} bytes at sector 1000.");
}
```

On .NET, the `Read(Span<byte>)` override is also available:

```csharp
Span<byte> header = stackalloc byte[2048];
var read = iso.Read(header);
```

> `CsoStream` is read-only: `Write` and `SetLength` throw `NotSupportedException`. A block that cannot be decompressed surfaces as an `IOException` from `Read`.

### Extract a CSO to ISO with progress and cancellation

`ExtractToIso` decompresses every block and writes the ISO in one call. The optional callback reports `(processedBlocks, totalBlocks)`, and the cancellation token is checked before each block.

```csharp
using CSOSharp;
using CSOSharp.Models;

var error = CsoFile.Open(csoPath, out var cso);
if (error != CsoError.None || cso is null)
    return;

using (cso)
using (var cts = new CancellationTokenSource())
{
    var outputPath = Path.ChangeExtension(csoPath, ".iso");

    try
    {
        error = cso.ExtractToIso(
            outputPath,
            (processed, total) =>
            {
                var percent = total == 0 ? 100 : (processed * 100.0) / total;
                Console.Write($"\r{percent,6:0.0}%");
            },
            cts.Token
        );
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancelled - delete the partial ISO if you do not need it.");
        return;
    }

    Console.WriteLine();
    Console.WriteLine(error == CsoError.None ? $"Wrote {outputPath}" : $"Failed: {error}");
}
```

> `ExtractToIso` writes the ISO first. If decompression fails partway, a partial file may remain; delete it when the returned error is not `CsoError.None`.

### Open a CSO from a stream

Both `Open` overloads require a **readable, seekable** stream. The `ownsStream` flag decides whether disposing the `CsoFile` also disposes the stream.

```csharp
using CSOSharp;
using CSOSharp.Models;

using var file = File.OpenRead(csoPath);
var error = CsoFile.Open(file, ownsStream: false, out var cso);

if (error != CsoError.None || cso is null)
{
    Console.Error.WriteLine($"Could not open the CSO: {error}");
    return;
}

using (cso)
{
    Console.WriteLine($"{cso.Header.TotalBlocks} blocks");
}

// The stream is still usable here because ownsStream was false.
```

You can also open a CSO that is already in memory:

```csharp
var bytes = await File.ReadAllBytesAsync(csoPath);
using var memory = new MemoryStream(bytes);
var error = CsoFile.Open(memory, ownsStream: false, out var cso);
```

### Copy the ISO data to another stream

`CsoStream` is an ordinary `Stream`, so it composes with the rest of the BCL — for example `CopyToAsync`:

```csharp
using CSOSharp;

CsoFile.Open(csoPath, out var cso);
using (cso)
using (var iso = cso.OpenStream())
using (var output = File.Create(isoPath))
{
    await iso.CopyToAsync(output);
}
```

## Error handling

`CsoFile.Open`, `ReadBlock` and `ExtractToIso` return a `CsoError` value instead of throwing for data-related problems. Corrupt headers, unsupported versions, truncated index tables and decompression failures are all reported through the enum, which keeps batch processing straightforward.

```csharp
var error = CsoFile.Open(path, out var cso);

switch (error)
{
    case CsoError.None:
        // cso is ready to use
        break;
    case CsoError.FileNotFound:
        Console.Error.WriteLine("The file does not exist.");
        break;
    case CsoError.InvalidHeader:
        Console.Error.WriteLine("Not a CSO/CISO file (bad or missing magic).");
        break;
    case CsoError.UnsupportedVersion:
        Console.Error.WriteLine("Only CSO v1 and CSO v2/ZSO are supported.");
        break;
    default:
        Console.Error.WriteLine($"Could not open the CSO: {error}");
        break;
}
```

| `CsoError` | Value | Meaning |
|---|---:|---|
| `None` | 0 | Success. |
| `InvalidHeader` | 1 | Missing or invalid CISO magic/header. |
| `UnsupportedVersion` | 2 | The header version is not 1 or 2. |
| `FileNotFound` | 3 | The file does not exist or could not be opened. |
| `IoError` | 4 | An I/O error occurred (also returned for non-readable/non-seekable streams). |
| `CorruptIndex` | 5 | The index table is truncated or references invalid offsets. |
| `DecompressionError` | 6 | A block could not be decompressed, or the target buffer was smaller than `BlockSize`. |
| `BlockOutOfRange` | 7 | The requested block index is past `Header.TotalBlocks - 1`. |
| `InvalidBlockSize` | 8 | The header declares a zero block size. |

Notes:

- `ReadBlock` returns `BlockOutOfRange` for an index past the end and `DecompressionError` when the destination buffer is too small (the buffer must hold `BlockSize` bytes starting at `offset`).
- `ReadBlock` and `OpenStream` on a disposed instance return `CsoError.IoError` and throw `ObjectDisposedException` respectively.
- `CsoStream.Read` throws `ObjectDisposedException` after disposal, `IOException` when a block fails to decompress, and `ArgumentOutOfRangeException` for an invalid buffer offset/count.
- `ExtractToIso` propagates `OperationCanceledException` when the token is cancelled.

## API reference

All types live in the `CSOSharp` namespace; the model types live in `CSOSharp.Models`.

### CsoFile

The entry point. Represents an opened CSO container and owns the underlying stream when it was opened from a path.

| Member | Description |
|---|---|
| `static CsoError Open(string path, out CsoFile? cso)` | Opens a CSO from disk. The returned instance owns the file stream. |
| `static CsoError Open(Stream stream, bool ownsStream, out CsoFile? cso)` | Opens a CSO from a readable, seekable stream. `ownsStream` controls whether the stream is disposed with the instance. |
| `CsoHeader Header` | Parsed CSO header: magic, header size, uncompressed size, block size, version, index shift and total block count. |
| `bool IsLz4` | `true` when the file uses CSO v2/ZSO (LZ4) compression. |
| `bool IsDeflate` | `true` when the file uses CSO v1 (deflate/zlib) compression. |
| `CsoError ReadBlock(uint blockIndex, byte[] buffer, out int bytesRead)` | Reads and decompresses one block. The buffer must be at least `Header.BlockSize` bytes. |
| `CsoError ReadBlock(uint blockIndex, byte[] buffer, int offset, out int bytesRead)` | Reads one block into `buffer` at `offset`. |
| `CsoStream OpenStream()` | Creates a read-only, seekable stream over the decompressed ISO data. |
| `CsoError ExtractToIso(string outputPath, Action<uint, uint>? progress = null, CancellationToken cancellationToken = default)` | Decompresses the whole CSO and writes the ISO to `outputPath`, reporting `(processedBlocks, totalBlocks)`. |
| `void Dispose()` | Releases the stream when the instance owns it. Safe to call multiple times. |

### CsoStream

A read-only `Stream` over the decompressed ISO inside a CSO file.

| Member | Description |
|---|---|
| `bool CanRead` / `bool CanSeek` | Always `true`. |
| `bool CanWrite` | Always `false`. |
| `long Length` | Total uncompressed ISO size in bytes (`Header.UncompressedSize`). |
| `long Position` | Current absolute position. Assigning a negative value throws `ArgumentOutOfRangeException`. |
| `int Read(byte[] buffer, int offset, int count)` | Reads up to `count` bytes, decompressing blocks on demand. |
| `int Read(Span<byte> buffer)` | Span overload (available on .NET 8+). |
| `long Seek(long offset, SeekOrigin origin)` | Seeks within the decompressed image. Seeking before the start throws `IOException`. |
| `void Flush()` | No-op (read-only stream). |
| `void SetLength(long value)` / `void Write(...)` | Throw `NotSupportedException`. |

### Models

| Type | Description |
|---|---|
| `CsoHeader` | Read-only struct with the parsed header: `Magic`, `HeaderSize`, `UncompressedSize`, `BlockSize`, `Version`, `IndexOffsetShift`, `TotalBlocks`, `IsValid`, `IsV1`, `IsV2`, plus the `MagicValue` and `ExpectedHeaderSize` constants. |
| `CsoError` | Result/error enum returned by `Open`, `ReadBlock` and `ExtractToIso` (see the table above). |

## Supported CSO variants

CSO files come from several compressors, and CSOSharp reads all the common layouts:

- **CSO v1** — deflate blocks, either zlib-wrapped (recognized by the `0x78` header and skipped) or raw deflate.
- **CSO v2 / ZSO** — LZ4 blocks, decoded with [K4os.Compression.LZ4](https://www.nuget.org/packages/K4os.Compression.LZ4).
- **Stored blocks** — index entries with bit 31 set point at uncompressed data and are copied verbatim.
- **Shifted index tables** — entries are left-shifted by the header's `IndexOffsetShift`, which is common in large images to address offsets beyond 4 GB.
- **Truncated files** — a short header or index table is reported as `InvalidHeader` or `CorruptIndex` rather than crashing.

## How it works

A CSO file starts with a 24-byte header:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | Magic `CISO` (`0x4F534943`, little-endian) |
| 4 | 4 | Header size (24) |
| 8 | 8 | Uncompressed ISO size in bytes |
| 16 | 4 | Block size (typically 2,048) |
| 20 | 1 | Version (1 = deflate, 2 = LZ4) |
| 21 | 1 | Index offset shift |
| 22 | 2 | Reserved |

The header is followed by a `TotalBlocks + 1` entry index table of 32-bit little-endian values. For a block at index `i`:

1. `entry[i] & 0x80000000` marks the block as stored/uncompressed.
2. The file offset is `(entry[i] & 0x7FFFFFFF) << IndexOffsetShift`.
3. The compressed length is the difference between the offsets of entries `i + 1` and `i`.
4. An empty span (`next == current`) represents a block of zeroes.

CSOSharp validates the magic, block size and version, reads the whole index table up front, then decompresses blocks on demand by seeking to the computed offset.

## Building from source

The library lives in the [Batch Convert to CHD repository](https://github.com/purelogiccode/BatchConvertToCHD) under `CSOSharp/`.

```powershell
git clone https://github.com/purelogiccode/BatchConvertToCHD.git
cd BatchConvertToCHD
dotnet build CSOSharp/CSOSharp.csproj -c Release
dotnet pack CSOSharp/CSOSharp.csproj -c Release -o artifacts
```

The test suite for the library lives in `BatchConvertToCHD.Tests/`:

```powershell
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release --filter "FullyQualifiedName~Cso"
```

## License

CSOSharp is released under the [MIT license](https://github.com/purelogiccode/BatchConvertToCHD).
