# ISZSharp

[![NuGet](https://img.shields.io/nuget/v/ISZSharp.svg)](https://www.nuget.org/packages/ISZSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ISZSharp.svg)](https://www.nuget.org/packages/ISZSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/purelogiccode/BatchConvertToCHD)
[![.NET 8 | 9 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4.svg)](https://dotnet.microsoft.com/download)

**ISZSharp** is a managed C# library for decompressing **UltraISO ISZ** disc images back to the plain images
they were made from. It reads whole images and split sets, all four chunk storage kinds (zero-elided, stored,
zlib, bzip2), the obfuscated tables and stripped bzip2 headers real UltraISO files carry, and validates
UltraISO's own checksum when the file provides one.

The library is the ISZ decompression engine used by
[Batch Convert to CHD](https://github.com/purelogiccode/BatchConvertToCHD), where it restores an ISZ to a
plain image before converting it to CHD with `chdman` or [CHDSharp](https://www.nuget.org/packages/CHDSharp).

- Repository: <https://github.com/purelogiccode/BatchConvertToCHD>
- Package: <https://www.nuget.org/packages/ISZSharp>

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Usage examples](#usage-examples)
  - [Inspect a header without decoding](#inspect-a-header-without-decoding)
  - [Decompress with progress and cancellation](#decompress-with-progress-and-cancellation)
  - [Locate the segments of a split image](#locate-the-segments-of-a-split-image)
- [Error handling](#error-handling)
- [API reference](#api-reference)
  - [IszDecoder](#iszdecoder)
  - [IszHeader](#iszheader)
  - [IszChunkType](#iszchunktype)
  - [IszSegment](#iszsegment)
  - [IszDecodeResult](#iszdecoderesult)
- [Supported ISZ layouts](#supported-isz-layouts)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [References](#references)
- [License](#license)

## Features

- **Whole and split images** — `.isz` on its own, or a set split across `.i01`, `.i02`, … segments, all read
  as one logical stream.
- **Every segment naming scheme in the wild** — the specification's `game.isz`/`game.i01`/`game.i02`, plus
  the `game.part01.isz` and `game.part001.isz` forms other writers use.
- **All four chunk kinds** — all-zero chunks (`ADI_ZERO`), stored data (`ADI_DATA`), zlib (`ADI_ZLIB`) and
  bzip2 (`ADI_BZ2`).
- **Real-file behaviours the specification omits** — the segment and chunk tables are stored obfuscated and
  are de-obfuscated on read; bzip2 chunks are stored without their `BZh` stream header and it is restored
  before decompression; a zero pointer offset means there is no chunk table and the image is one raw run.
- **Checksum validation** — when the file carries UltraISO's 64-byte header, the CRC32 of the restored image
  is verified and a mismatch fails the decode.
- **Refuses rather than guesses** — encrypted images (AES-128/192/256 or password) are reported by name,
  truncated files and damaged tables are reported, and a failed decode deletes its partial output because a
  short image would convert and look fine.
- **No native dependencies** — pure managed code; bzip2 comes from
  [SharpCompress](https://github.com/adamhathcock/sharpcompress), zlib from the BCL.

## Requirements

| Requirement | Value |
|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Runtime | .NET 8, .NET 9 or .NET 10 |
| Platforms | Windows, Linux, macOS (pure managed code) |
| Dependencies | [SharpCompress](https://www.nuget.org/packages/SharpCompress) 0.50.4 (bzip2) |

The package contains a separate assembly for each target framework, so the correct build is selected
automatically by NuGet.

> **Read-only:** ISZSharp can open, inspect and decompress ISZ files. It cannot create or modify them, and
> it cannot decrypt an encrypted image — UltraISO itself has to save one as a plain ISO first.

## Installation

```powershell
dotnet add package ISZSharp
```

or with the Package Manager console:

```powershell
Install-Package ISZSharp
```

## Quick start

```csharp
using ISZSharp;

var iszPath = @"C:\Games\Breath of Fire IV.isz";
var isoPath = @"C:\Games\Breath of Fire IV.iso";

var header = await IszDecoder.TryReadHeaderAsync(iszPath, CancellationToken.None);
if (header is null)
{
    Console.Error.WriteLine("Not an ISZ image.");
    return;
}

var unusable = header.GetUnusableReason();
if (unusable is not null)
{
    Console.Error.WriteLine($"Cannot decode it: {unusable}");
    return;
}

var result = await IszDecoder.DecodeAsync(
    iszPath,
    isoPath,
    message => Console.WriteLine(message),
    CancellationToken.None
);

Console.WriteLine(result.Success ? $"Wrote {result.OutputPath}" : $"Failed: {result.FailureReason}");
```

## Usage examples

### Inspect a header without decoding

`TryReadHeaderAsync` reads only the first 64 bytes, so a file can be checked before any space is committed
to the restored image. It returns `null` when the file does not start with an ISZ header.

```csharp
using ISZSharp;

var header = await IszDecoder.TryReadHeaderAsync(iszPath, CancellationToken.None);
if (header is not null)
{
    Console.WriteLine($"Version:           {header.Version}");
    Console.WriteLine($"Sector size:       {header.SectorSize} bytes ({header.TotalSectors:N0} sectors)");
    Console.WriteLine($"Restored size:     {header.ImageSizeBytes:N0} bytes");
    Console.WriteLine($"Chunks:            {header.ChunkCount:N0} x {header.ChunkSize:N0} bytes");
    Console.WriteLine($"Pointer width:     {header.PointerLength} bytes");
    Console.WriteLine($"Split:             {header.IsSegmented}");
    Console.WriteLine($"Encrypted:         {header.IsEncrypted} ({header.EncryptionDescription})");
    Console.WriteLine($"UltraISO checksum: {(header.HasChecksums ? "present" : "absent")}");

    // What the decoder would say about it, or null when it can be decoded.
    Console.WriteLine($"Usable:            {header.GetUnusableReason() ?? "yes"}");
}
```

> The header's offsets are absolute file offsets into the first segment, so `ChunkTableOffset`,
> `SegmentTableOffset` and `DataOffset` can be inspected directly.

### Decompress with progress and cancellation

`DecodeAsync` writes the restored image and reports progress through the log callback roughly every 10%,
plus notes about the header and any split. Cancellation is checked between chunks; a cancelled or failed
decode deletes the partial output before returning or throwing.

```csharp
using ISZSharp;

var result = await IszDecoder.DecodeAsync(
    iszPath,
    isoPath,
    message => Console.WriteLine(message),
    cancellationToken
);

if (result.Success)
{
    // result.SectorSize tells you how the restored image should be classified:
    // 2048 is a plain ISO/DVD image, 2352 is a raw CD image, and so on.
    Console.WriteLine($"Restored {result.OutputPath} ({result.SectorSize}-byte sectors).");
}
else
{
    Console.Error.WriteLine(result.FailureReason);
}
```

### Locate the segments of a split image

`GetSegmentPath` returns the file that holds segment *n* (the `.isz` itself is segment 0), following
whichever naming scheme the first segment uses. `GetDecodedFileName` gives the restored image's name.

```csharp
using ISZSharp;

foreach (var path in new[] { @"D:\roms\Game.isz", @"D:\roms\Game.part001.isz" })
{
    for (var segment = 0; segment < 3; segment++)
        Console.WriteLine(IszDecoder.GetSegmentPath(path, segment));
}

// D:\roms\Game.isz
// D:\roms\Game.i01
// D:\roms\Game.i02
// D:\roms\Game.part001.isz
// D:\roms\Game.part002.isz
// D:\roms\Game.part003.isz

Console.WriteLine(IszDecoder.GetDecodedFileName(@"D:\roms\Game.part001.isz")); // Game.part001.iso
```

Segments must sit **in the same folder** as the first file. Missing segments are named in the failure
reason, and a segment whose volume serial number does not match the first file's is refused rather than
spliced in.

## Error handling

`DecodeAsync` never throws for file-content problems; it returns an `IszDecodeResult` whose `FailureReason`
is written for the end user (it says what to do about the problem, not just what went wrong). An
`OperationCanceledException` still propagates when the token is cancelled.

| Situation | `FailureReason` says |
|---|---|
| Not an ISZ | the file does not start with an ISZ header |
| Encrypted | the image is encrypted (AES-256) and this tool cannot decrypt it |
| Later segment opened directly | this is segment N of a split image, not the first one |
| Missing segment | the image is split across N segments and `<name>.i01` is not in the same folder |
| Foreign segment | segment `<name>.i01` belongs to a different ISZ image (volume serial number does not match) |
| Truncated file or short segment | the ISZ decompressed to N bytes but its header declares M |
| Checksum mismatch | the restored image does not match the checksum the ISZ header declares |
| Damaged compressed data | the compressed data inside the ISZ is damaged |
| Unknown version | the ISZ header declares format version N, and only version 1 is understood |

A decode that fails after writing has started deletes the partial image before returning, so a failed
`DecodeAsync` never leaves a short image behind.

## API reference

All types live in the `ISZSharp` namespace.

### IszDecoder

The entry point. A static class, because an ISZ is decoded in one pass to a file.

| Member | Description |
|---|---|
| `static Task<IszHeader?> TryReadHeaderAsync(string path, CancellationToken token)` | Reads and parses the header, or returns `null` when the file is not an ISZ image. |
| `static Task<IszDecodeResult> DecodeAsync(string iszPath, string destinationPath, Action<string> onLog, CancellationToken token)` | Decompresses the image (whole or split) to `destinationPath`, reporting progress through `onLog`. |
| `static string GetDecodedFileName(string iszPath)` | The name the restored image should be given: the stem plus `.iso`. |
| `static string GetSegmentPath(string firstSegmentPath, int segmentIndex)` | Path of the given segment, following the first file's naming scheme. |
| `static (IszChunkType Type, int StoredLength) ReadChunkEntry(byte[] chunkTable, int index, int pointerLength)` | Decodes one (already de-obfuscated) chunk table entry; exposed for testing the bit-packing. |

`DecodeAsync` reads the whole chunk table up front but streams the chunk data, so memory use is bounded by
the chunk size, not by the image size.

### IszHeader

The parsed header of the first segment. A read-only record.

| Member | Description |
|---|---|
| `const int Length` / `const int ExtendedLength` | 48 (the specification's header) and 64 (with UltraISO's checksum fields). |
| `const string Signature` | `"IsZ!"`. |
| `int HeaderSize`, `int Version`, `uint VolumeSerialNumber` | Header shape and the serial that ties segments together. |
| `int SectorSize`, `uint TotalSectors` | The stored image's sector geometry. |
| `int PasswordMode` | 0 none, 1 password, 2–4 AES-128/192/256. |
| `long SegmentSize`, `uint ChunkCount`, `uint ChunkSize`, `int PointerLength` | Splitting and chunk-table layout. |
| `int SegmentNumber`, `uint ChunkTableOffset`, `uint SegmentTableOffset`, `uint DataOffset` | This segment's identity and the absolute offsets of its tables and data. |
| `uint? UncompressedCrc`, `uint? DataSize`, `uint? StoredCrc` | The 64-byte header's checksum fields, `null` for a 48-byte header. |
| `long ImageSizeBytes` | `TotalSectors × SectorSize`, computed in 64-bit. |
| `bool IsEncrypted`, `bool IsSegmented`, `bool HasChecksums` | Header classification. |
| `string EncryptionDescription`, `string Summary` | Human-readable descriptions for logs. |
| `static bool HasSignature(ReadOnlySpan<byte> header)` | True when the bytes open with `IsZ!`. |
| `static IszHeader? TryRead(ReadOnlySpan<byte> header)` | Parses a header from at least 48 bytes; reads the checksums when 64 bytes and a 64-byte header size are present. |
| `string? GetUnusableReason()` | Why the image cannot be decoded, phrased for the user, or `null` when it can. |

### IszChunkType

How one chunk is stored, from the top two bits of its table entry:

| Value | Spec name | Meaning |
|---|---|---|
| `Zero` | `ADI_ZERO` | The chunk is all zeros and stores no bytes; the entry records its uncompressed length. |
| `Stored` | `ADI_DATA` | Stored verbatim. |
| `ZLib` | `ADI_ZLIB` | Deflate inside a zlib wrapper. |
| `BZip2` | `ADI_BZ2` | bzip2, stored without the `BZh` header. |

### IszSegment

One entry of a split image's segment table: `Size`, `ChunkCount`, `FirstChunkNumber`, `ChunkOffset`,
`LeftSize`, and `IsTerminator` for the zero-size entry that ends the table.

### IszDecodeResult

| Member | Description |
|---|---|
| `bool Success` | True when `OutputPath` holds the complete image. |
| `string? OutputPath` | The written image, or `null` on failure. |
| `int SectorSize` | Sector size the header declared, for classifying the restored image. |
| `string? FailureReason` | User-facing explanation, or `null` on success. |

## Supported ISZ layouts

ISZ files from UltraISO and its imitators vary in ways the published specification does not describe; the
behaviours below come from comparing the two independent open-source readers, libMirage's ISZ filter and
isz-tool, both of which were checked against real files.

- **Obfuscated tables** — the segment and chunk tables are XORed with the complement of `IsZ!`
  (`B6 8C A5 DE`, cycling). ISZSharp de-obfuscates every table it reads.
- **Stripped bzip2 header** — a bzip2 chunk is stored with its first three bytes cleared; ISZSharp writes
  `BZh` back before decompressing, exactly as the reference readers do.
- **No chunk table** — a zero pointer offset means the data is one uncompressed run; the chunk count and
  size in the header drive the read.
- **Split naming** — `game.isz`/`game.i01`, `game.part01.isz`/`game.part02.isz` and
  `game.part001.isz`/`game.part002.isz`.
- **Zero chunks** — the entry may or may not record the uncompressed length; either way a whole chunk (or
  the correct partial final chunk) of zeros is produced and no data bytes are consumed.
- **Encrypted images** — recognised (`AES-128`, `AES-192`, `AES-256`, password) and refused by name.
  Decryption would need the user's password and is deliberately not implemented.

## How it works

An ISZ file starts with a 48-byte header, which UltraISO extends to 64 bytes:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | Signature `IsZ!` |
| 4 | 1 | Header size (48 or 64) |
| 5 | 1 | Version (1) |
| 6 | 4 | Volume serial number |
| 10 | 2 | Sector size |
| 12 | 4 | Total sectors |
| 16 | 1 | Encryption mode |
| 17 | 8 | Segment size |
| 25 | 4 | Chunk count |
| 29 | 4 | Chunk size |
| 33 | 1 | Chunk pointer width |
| 34 | 1 | Segment number (first = 0) |
| 35 | 4 | Chunk table offset (0 = none) |
| 39 | 4 | Segment table offset (0 = whole file) |
| 43 | 4 | Data offset |
| 47 | 1 | Reserved |
| 48 | 4 | CRC32 of the restored image (64-byte header only) |
| 52 | 4 | Data size (64-byte header only) |
| 56 | 4 | Reserved (64-byte header only) |
| 60 | 4 | CRC32 of the stored data (64-byte header only) |

A split image follows with a segment table of 24-byte entries (size, chunk count, first chunk, chunk
offset, left-over bytes), terminated by a zero-size entry. Then comes the chunk table: one little-endian
entry per chunk whose top two bits are the storage kind and whose remaining bits are the stored length.
The rest of the file is chunk data, stored back to back; a chunk may straddle a segment boundary, which is
why ISZSharp reads all segments as one stream. A file with no chunk table has no table at all and its data
begins straight after the header.

Decoding walks the chunk table once, decompresses each chunk, and writes the image capped at
`TotalSectors × SectorSize`, so a writer that padded its final chunk cannot lengthen the image. The bytes
written are counted and checked against the declared size, and fed to the CRC32 when the header carries
one; either mismatch deletes the output and reports the file as truncated or damaged.

## Building from source

The library lives in the [Batch Convert to CHD repository](https://github.com/purelogiccode/BatchConvertToCHD)
under `ISZSharp/`.

```powershell
git clone https://github.com/purelogiccode/BatchConvertToCHD.git
cd BatchConvertToCHD
dotnet build ISZSharp/ISZSharp.csproj -c Release
dotnet pack ISZSharp/ISZSharp.csproj -c Release -o artifacts
```

The test suite for the library lives in `BatchConvertToCHD.Tests/`:

```powershell
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release --filter "FullyQualifiedName~Isz"
```

## References

- EZB Systems' [ISZ File Format Specification 1.00](https://www.ezbsystems.com/isz/iszspec.txt) — the
  published format the header, segment table and chunk table layouts come from.
- [libMirage's ISZ filter](https://github.com/cdemu/cdemu) (GPL-2+) and
  [isz-tool](https://github.com/oserres/isz-tool) (GPL-3) — the independent readers the real-file
  behaviours (obfuscated tables, stripped bzip2 header, no chunk table, checksum calculation) were checked
  against.

## License

ISZSharp is released under the [MIT license](https://github.com/purelogiccode/BatchConvertToCHD).
