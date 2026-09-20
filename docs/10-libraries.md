---
title: Embedded Libraries
nav_order: 11
---

# 10. Embedded Libraries

The solution ships five in-house libraries that replace external tools (maxcso, psxpackager), add CloneCD support, and cover Alcohol 120% and UltraISO images. The app references them as project references. All five are published as NuGet packages for outside consumers — **PBPSharp** (<https://www.nuget.org/packages/PBPSharp>, 1.1.1), **CSOSharp** (<https://www.nuget.org/packages/CSOSharp>, 1.0.0), **CCDSharp** (<https://www.nuget.org/packages/CCDSharp>, 1.0.0), **MDSSharp** (<https://www.nuget.org/packages/MDSSharp>, 1.1.0) and **ISZSharp** (<https://www.nuget.org/packages/ISZSharp>, 1.0.1) — and they all multi-target `net8.0;net9.0;net10.0`, each shipping its XML docs, README and icon; releases are manual (see the repository's AGENTS.md). All five expose internals to `BatchConvertToCHD.Tests` via `InternalsVisibleTo`. A sixth in-house library, **CHDSharp**, is consumed as a NuGet package and is covered in [§10.4](#104-chdsharp-nuget).

| Library | Purpose | Replaces |
|---------|---------|----------|
| **CCDSharp** | CloneCD `.ccd`/`.img`/`.sub` parsing + CUE/BIN conversion | — (new capability) |
| **CSOSharp** | CSO/CISO decompression (deflate/zlib + LZ4) | `maxcso.exe` |
| **PBPSharp** | PlayStation PBP extraction + SFO/TOC parsing | `psxpackager.exe` |
| **MDSSharp** | Alcohol 120% `.mds`/`.mdf` parsing, subchannel stripping, split-volume joining, cue writing | — (new capability) |
| **ISZSharp** | UltraISO ISZ decompression back to the plain image | — (new capability) |

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
- **Disc detection**: PSAR header `PSISOIMG0000` → single disc; `PSTITLEIMG000000` → multi-disc (reads 5 position slots at +0x200); anything else → `PbpError.InvalidPsarHeader` (the app treats this as "not a PlayStation disc image — PSP application, unsupported variant, or corrupt file" and skips informatively). The four "template" DWORDs of the `PSTITLEIMG` header (fixed values in popstation/PSX2PSP/iPoPS output) are **not validated**: pop-fe writes zeros there and its multi-disc PBPs parse from the position table alone.
- `PbpDiscInfo` — `ReadBlock`, `ExtractTo(stream, progress?, token)`, `ExtractToBinCue(binPath, cuePath?, progress?, token)`; TOC parsed from the PSAR TOC (A0/A1/A2 markers, BCD track numbers, best effort — a bad TOC never aborts extraction). A disc container whose header parsed but that carries **no ISO index entries** throws `NoIsoIndexException` (public, derives from `Exception`) so callers can report the likely cause: a truncated or incomplete download.
- **Index entries** (32 bytes at PSAR+0x4000, data at PSAR+0x100000) are read in both authoring layouts: the popstation/PSX2PSP/iPoPS layout writes the block size as a 32-bit int at bytes 4–7, the pop-fe layout writes a 16-bit size at bytes 4–5, a stored-flag byte at 6 (bit 0 = uncompressed block) and a SHA-1 at 8–23. Block offsets use 64-bit math so multi-gigabyte files cannot overflow. Corrupt entries are rejected with `InvalidDataException` (oversized lengths, stored blocks larger than a full 16-sector block).
- **Block decompression** uses SharpZipLib's raw `Inflater` (the same decompressor the popstation reference implementation uses for PSAR blocks, `windowBits −15`); a block that fails raw inflation is retried as a **zlib-wrapped stream** (2-byte header + Adler-32, as written by tools using `zlib.compress`). Blocks flagged stored, or exactly one full block (16 × 0x930) in size, are copied verbatim. A failed block surfaces as `PbpError.DecompressionError`.
- `CueSheetWriter.GenerateCueSheet(binFileName, tocEntries)` — emits `FILE ... BINARY`, `TRACK nn MODE2/2352` (data) / `AUDIO` (audio), with `INDEX 00` for audio tracks computed as track start **minus 150-frame lead-in** (clamped ≥ 0).
- SFO model: `SfoData` (magic `0x46535000`; `GetString`/`GetUInt32`; `Size` — the total SFO section size derived from the largest data entry, populated on parse; static `Keys` with `BOOTABLE`, `CATEGORY`, `DISC_ID`, `DISC_VERSION`, `LICENSE`, `PARENTAL_LEVEL`, `PSP_SYSTEM_VER`, `REGION`, `TITLE`), `SfoEntry` (formats 0x0204 string / 0x0404 uint32), `TocEntry`, `TrackType { Data = 0x41, Audio = 0x01 }`.
- **SFO parsing is best effort**: a missing or corrupt PARAM.SFO (bad magic, malformed table, offsets beyond EOF) leaves `Title`/`DiscId` null with empty `Entries` instead of failing `Open` — none of the reference tools read the SFO when extracting disc images.
- `PbpError` enum: `None=0, InvalidHeader=1, FileNotFound=2, IoError=3, CorruptFile=4, InvalidPsarHeader=5, DiscOutOfRange=6, ResourceNotFound=7, DecompressionError=8, TruncatedPsar=9, InvalidSfo=10`. `TruncatedPsar` is returned when the PSAR container parses but no ISO index follows (see `NoIsoIndexException`). `InvalidSfo` is retained for API compatibility but is no longer returned by `Open` (SFO problems are tolerated). The app maps these to targeted guidance ("most likely truncated or incomplete — re-download") instead of a generic corrupt-file message.
- **Block decompression** uses SharpZipLib's raw `Inflater` (the same decompressor the popstation reference implementation uses for PSAR blocks), which tolerates a few streams the stricter .NET `DeflateStream` rejects; a failed block surfaces as `PbpError.DecompressionError`.
- Integration: `ExtractPbpToCueBinAsync` (`MainWindow.xaml.cs:2959`) — multi-disc PBPs produce `"{name} - Disc N.bin/.cue"` sets; the result (`PbpExtractionResult`) carries `ErrorCode` + a human-readable `Error` so the caller can distinguish skippable conditions from real failures.
- Tests: `PbpFileTests`, `PbpHeaderTests`, `SfoDataTests`, `SfoEntryTests`, `TocEntryTests`, `CueSheetWriterTests`, plus real-file integration tests (`PbpFileIntegrationTests`).

## 10.4 CHDSharp (NuGet)

**Purpose**: pure C# CHD (Compressed Hunks of Data) reading, verification, extraction, and **creation** — the engine behind the app's extraction and verification tabs.

- Consumed as a NuGet package (`CHDSharp` v1.4.3), not a project reference; the app also bundles the project's CLI (`CHDSharp.exe`) and MAME's `chdman.exe` side by side, preferring the native-architecture binary on ARM64.
- Capabilities: CHD V1–V5, all 10 compression codecs (zlib, lzma, huffman, flac, zstd, avhu + CD variants), parent/child chaining, parallel verification, and full CHD creation (`createcd`/`createdvd`/`createhd`/`copy`) with output that is **byte-identical to `chdman`**.
- The byte-parity claim was validated by the (since-removed) `CHDBattleTest` battleground project — see [Testing §11.6](11-testing.md#116-chdbattletest-battleground-historical) — which reported zero mismatches against `chdman` 0.289 across decode, encode, and cross-verification battles on a 56-disc corpus.
- In the conversion pipeline CHDSharp is the **automatic fallback**: the bundled `chdman` is the primary encoder, and a file that chdman cannot convert is retried with `CHDSharp.exe` — see [Conversion Pipeline §5.3](05-conversion-pipeline.md#53-converttochdasync--encoder-selection-chdman-first-chdsharp-fallback).
- When the library cannot decode a CHD (corrupt file, A/V laserdisc), the app falls back to `chdman` for extraction — see [Extraction & Verification](06-extraction-and-verification.md).

## 10.5 MDSSharp

**Purpose**: turn an Alcohol 120% image (`.mds` descriptor + `.mdf` data, including split `.i00`/`.i01` volumes) into something the encoder can read.

- Main types: `MdsParser` (`IsMdsFile`, `Parse`), `MdsMedium`, `MdsDisc`/`MdsTrack` (parsed model with medium type, sector-size classification and pregap/length fields), `MdsInputPreparer` (`PrepareAsync` → cue / DVD image / failure; `StripSubchannelAsync`, `WriteCueAsync`, `FormatMsf`), and `SplitImageJoiner` (`TryGetVolumeSet`, `JoinAsync`, `GetTotalBytes` for `.001`/`.i00` volume sets).
- Reads the medium type, the track extra blocks (pregap/length) and the footer blocks that name the data files (single-byte or UTF-16), so renamed and multi-file descriptors resolve without guessing; several declared files are joined in order.
- Track modes follow libmirage's reverse engineering (low nibble, folded by 8): audio, Mode 1 and the Mode 2 forms; CD media with 2048-byte sectors becomes a `MODE1/2048` cue, DVD media stays a direct image.
- Pregaps the data file does not contain are rebuilt as zeros into a `.pregap.bin` so `INDEX 00` can be written; pregaps already in the file are referenced in place.
- The three preparation shapes (plain 2352, subchannel strip, ISO-as-DVD) plus the cooked-CD and pregap paths are documented in [Utilities Reference §8.12](08-utilities-reference.md#812-alcohol-120-support-mdsssharp).
- Integration: `ProcessMdsFileForConversionAsync` prepares the work set, then the conversion funnel takes the cue (or DVD image).
- Tests: `MdsTests.cs`, `SplitImageJoinerTests.cs`.

## 10.6 ISZSharp

**Purpose**: decompress UltraISO `.isz` images back to the plain images they were made from, per EZB Systems' ISZ File Format Specification 1.00 plus the real-file behaviours the specification omits.

- Main types: `IszHeader` (48/64-byte header with `TryRead` validation and the extended UltraISO checksum fields), `IszDecoder` (`TryReadHeaderAsync`, `DecodeAsync`, `GetSegmentPath`, `GetDecodedFileName`, `ReadChunkEntry`), `IszSegment`/`IszChunkType`/`IszDecodeResult`.
- Supports whole and multi-segment images (the spec's `.i01`/`.i02` naming and the `.part01.isz`/`.part001.isz` forms), zlib / bzip2 / stored / zero-elided chunks, de-obfuscates the segment and chunk tables (XOR with `B6 8C A5 DE`), restores the `BZh` header stripped from bzip2 chunks, and decodes images whose header declares no chunk table (one raw run).
- Validates UltraISO's CRC32 of the restored image when the 64-byte header carries one; a mismatch deletes the output and fails like a size shortfall. Encryption is refused by name (AES-128/192/256, password), later segments are matched by volume serial number, and truncation or damaged tables are reported rather than guessed at. A decode that fails after writing starts deletes its partial output.
- Integration: `ResolveIszAsync` decodes the ISZ to a temp image, which is then classified and converted like any other image.
- Tests: `IszHeaderTests.cs`, `IszDecoderTests.cs`.
