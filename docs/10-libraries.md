---
title: Embedded Libraries
nav_order: 11
---

# 10. Embedded Libraries

The solution ships three in-house libraries that replace external tools (maxcso, psxpackager) and add CloneCD support. All three multi-target `net10.0;net8.0`, are packable, and expose internals to `BatchConvertToCHD.Tests` via `InternalsVisibleTo`. A fourth in-house library, **CHDSharp**, is consumed as a NuGet package and is covered in [§10.4](#104-chdsharp-nuget).

| Library | Purpose | Replaces |
|---------|---------|----------|
| **CCDSharp** | CloneCD `.ccd`/`.img`/`.sub` parsing + CUE/BIN conversion | — (new capability) |
| **CSOSharp** | CSO/CISO decompression (deflate/zlib + LZ4) | `maxcso.exe` |
| **PBPSharp** | PlayStation PBP extraction + SFO/TOC parsing | `psxpackager.exe` |

---

## 10.1 CCDSharp

**Purpose**: read CloneCD disc-image sets (`.ccd` descriptor + `.img` data + optional `.sub` subchannel) and convert them to CUE/BIN for chdman.

- Main type: `CcdConverter` — `Parse(inputFile)` returns a parsed disc model (`DiscImage` with `ImgFilePath`, subchannel info, track table); `ConvertToCueBin(inputFile, tempCuePath)` writes the CUE/BIN pair.
- Integration: `ProcessCcdFileForConversionAsync` (`MainWindow.xaml.cs:1702`) parses the `.ccd`, converts to CUE/BIN in a temp dir, then converts the cue with chdman. On success the `.ccd`/`.img`/`.sub`/`.cdt` set is deleted when "delete originals" is enabled.
- Archive extractions skip `.img` files that belong to a `.ccd` set to avoid double conversion (`MainWindow.xaml.cs:1562–1573`).
- Failure messages are prefixed `"CCDSharp: Conversion error"` and are excluded from bug reports.
- Reference sources live under `References/` (`ccd2cue-master`, `ccd2iso-main`, `myccd2cue-main`) — third-party material used to build the library, not part of the build.
- **Testing note**: the test project does not reference CCDSharp, so there are currently no CCDSharp unit tests (see [Testing](11-testing.md)).

## 10.2 CSOSharp

**Purpose**: read and decompress **CISO** (Compressed ISO, `.cso`) images.

- Main type: `CsoFile` — `Open(path/stream, out CsoFile)` returns a `CsoError`; exposes `UncompressedSize`, block metadata, `ReadBlock`, `ExtractToIso(path, progress?, token)`, and a seekable `CsoStream` implementing the stream contract.
- Supports **v1 and v2** headers, **deflate/zlib** and **LZ4** compression (`K4os.Compression.LZ4` dependency).
- Integration: `ArchiveService.ExtractCsoAsync` (`Services/ArchiveService.cs:52`) decompresses to a temp ISO for the conversion pipeline.
- Error enum: `CsoError { None, FileNotFound, InvalidHeader, UnsupportedVersion, InvalidBlockSize, ... }`.
- Tests: `CsoFileTests`, `CsoStreamTests`, `CsoHeaderTests`, plus byte-for-byte integration tests against real `.cso`/`.iso` pairs (`CsoFileIntegrationTests`).

## 10.3 PBPSharp

**Purpose**: parse PlayStation **PBP** (PSP/PSX eboot) containers and extract PlayStation disc images to CUE/BIN.

- Main type: `PbpFile` — `Open(path, out PbpFile)` / `Open(stream, ownsStream, out PbpFile)`; properties `Header`, `SfoData`, `Discs` (`IReadOnlyList<PbpDiscInfo>`), `IsMultiDisc`, `Title`, `DiscId`, `Category`.
- Header: magic `0x50425000`, 40-byte header with offsets for SFO/ICON0/ICON1/PIC0/PIC1/SND0/DATA.PSP/DATA.PSAR.
- **Disc detection**: PSAR header `PSISOIMG0000` → single disc; `PSTITLEIMG000000` → multi-disc (reads 5 position slots); anything else → `PbpError.InvalidPsarHeader` (the app treats this as "not a PlayStation disc image — PSP application, unsupported variant, or corrupt file" and skips informatively).
- `PbpDiscInfo` — `ReadBlock`, `ExtractTo(stream, progress?, token)`, `ExtractToBinCue(binPath, cuePath?, progress?, token)`; ISO blocks are raw or raw-deflate decompressed; TOC parsed from the PSAR TOC (A0/A1/A2 markers, BCD track numbers). A disc container whose header parsed but that carries **no ISO index entries** throws `NoIsoIndexException` (public, derives from `Exception`) so callers can report the likely cause: a truncated or incomplete download.
- `CueSheetWriter.GenerateCueSheet(binFileName, tocEntries)` — emits `FILE ... BINARY`, `TRACK nn MODE2/2352` (data) / `AUDIO` (audio), with `INDEX 00` for audio tracks computed as track start **minus 150-frame lead-in** (clamped ≥ 0).
- SFO model: `SfoData` (magic `0x46535000`; `GetString`/`GetUInt32`; static `Keys` with `BOOTABLE`, `CATEGORY`, `DISC_ID`, `DISC_VERSION`, `LICENSE`, `PARENTAL_LEVEL`, `PSP_SYSTEM_VER`, `REGION`, `TITLE`), `SfoEntry` (formats 0x0204 string / 0x0404 uint32), `TocEntry`, `TrackType { Data = 0x41, Audio = 0x01 }`.
- `PbpError` enum: `None=0, InvalidHeader=1, FileNotFound=2, IoError=3, CorruptFile=4, InvalidPsarHeader=5, DiscOutOfRange=6, ResourceNotFound=7, DecompressionError=8, TruncatedPsar=9, InvalidSfo=10`. `TruncatedPsar` is returned when the PSAR container parses but no ISO index follows (see `NoIsoIndexException`); `InvalidSfo` when the PARAM.SFO region lacks the `\0PSF` signature. The app maps these to targeted guidance ("most likely truncated or incomplete — re-download") instead of a generic corrupt-file message.
- Integration: `ExtractPbpToCueBinAsync` (`MainWindow.xaml.cs:2918`) — multi-disc PBPs produce `"{name} - Disc N.bin/.cue"` sets; the result (`PbpExtractionResult`) carries `ErrorCode` + a human-readable `Error` so the caller can distinguish skippable conditions from real failures.
- Tests: `PbpFileTests`, `PbpHeaderTests`, `SfoDataTests`, `SfoEntryTests`, `TocEntryTests`, `CueSheetWriterTests`, plus real-file integration tests (`PbpFileIntegrationTests`).

## 10.4 CHDSharp (NuGet)

**Purpose**: pure C# CHD (Compressed Hunks of Data) reading, verification, extraction, and **creation** — the engine behind the app's extraction and verification tabs.

- Consumed as a NuGet package (`CHDSharp` v1.4.3), not a project reference; the app also bundles the project's CLI (`CHDSharp.exe`) and MAME's `chdman.exe` side by side, preferring the native-architecture binary on ARM64.
- Capabilities: CHD V1–V5, all 10 compression codecs (zlib, lzma, huffman, flac, zstd, avhu + CD variants), parent/child chaining, parallel verification, and full CHD creation (`createcd`/`createdvd`/`createhd`/`copy`) with output that is **byte-identical to `chdman`**.
- The byte-parity claim is validated by the `CHDBattleTest` battleground project (see [Testing §11.6](11-testing.md#116-chdbattletest-battleground)), which on the current corpus reports zero mismatches against `chdman` 0.289 across decode, encode, and cross-verification battles.
- When the library cannot decode a CHD (corrupt file, A/V laserdisc), the app falls back to `chdman` for extraction — see [Extraction & Verification](06-extraction-and-verification.md).
