---
title: Utilities Reference
nav_order: 9
---

# 8. Utilities Reference

All classes live in `BatchConvertToCHD/Utilities/` (and `Models/` where noted).

---

## 8.1 PathUtils

`internal static class PathUtils` (`PathUtils.cs:12`)

| Member | Behavior |
|--------|----------|
| `MaxChdmanPath` | Path length chdman handles reliably (260). Its CRT file APIs use ANSI paths capped at MAX_PATH; longer input/output paths fail with "No such file or directory" even when the file exists. |
| `IsAsciiPath(path)` | True when every character is ASCII (`&lt;=` 127). chdman converts its UTF-16 command line down to the ANSI code page, so non-ASCII characters anywhere along a path (accented user names such as `C:\Users\Kauê`, non-Latin folder names) can be mangled before they reach its file APIs. |
| `IsChdmanSafePath(path)` | True when the path can be handed to chdman as-is: pure ASCII **and** below `MaxChdmanPath`. |
| `CreateAsciiSafeTempDirectory(tempDirPrefix)` | Creates a unique staging directory whose full path is pure ASCII and well below MAX_PATH. Prefers the system temp location but falls back to `{drive}\BatchConvertToCHD_Temp` on fixed drives (roomiest first), because `%TEMP%` lives under the user profile and can contain exactly the characters this exists to avoid. The drive-root folders are the same ones startup cleanup knows about. |
| `SanitizeFileName(name)` | Replaces invalid filename chars with `_`; makes the final trailing period an `_` (one pass: `"file..."` → `"file.._"`, `"..."` → `".."_`), keeping names recognizable instead of collapsing them; falls back to a GUID when the result is empty/all underscores. |
| `GetSafeTempFileName(original, desiredExt, tempDir)` | Sanitized base name + desired extension (leading dot stripped), combined under `tempDir`. |
| `GetSafeRelativePath(relativeTo, path)` | `Path.GetRelativePath` when both paths share a root; otherwise `"."` (same folder). Used to preserve the directory structure in outputs. |
| `IsSameOrInsideDirectory(root, candidate)` | True when `candidate` is the same directory as `root` or nested inside it. Compares **normalized full paths**, so `D:\Games`, `D:\Games\` and `D:\Games\..\Games` all match, and appends the separator before the prefix test so `D:\Games2` is not read as being inside `D:\Games`. Never throws — a bad path returns false, because callers only use it to decide whether to log a note. |
| `ReserveFreeSubdirectory(parentDirectory, baseName)` | Returns a path under `parentDirectory` named after a sanitized `baseName` that nothing currently occupies, stepping through `Name (2)`, `Name (3)` … up to 999 and then falling back to a GUID suffix. Tests for both a directory **and** a file of that name, since an extension-less file would block the directory just as a folder would. Does **not** create the directory — the caller decides whether it is needed. Used by extraction to divert a disc that would otherwise overwrite existing files (see [Extraction §6](06-extraction-and-verification.md#extracting-into-the-source-folder)). |
| `GetBestTempDirectory(inputFilePath, outputFolderPath, tempDirPrefix, requiredBytes)` | Selects the best temp root: candidates = input-file root, output-folder root, system temp root, and every ready fixed drive. Requires ≥ 1 GiB free; when `requiredBytes > 0` prefers a drive with enough free space (most free among those); probes writability (create+delete a `writetest_<guid>` dir); falls back to system temp with an informational log. When the chosen root *is* the system-temp volume, `%TEMP%` is used only if its own path is chdman-safe — otherwise the base path is `{root}\BatchConvertToCHD_Temp`, so work directories never land under e.g. `C:\Users\Kauê Chacon\AppData\Local\Temp`. Final: `{base}\{tempDirPrefix}{guid}`. |
| `GetPossibleTempBasePaths()` | System temp plus every existing `X:\BatchConvertToCHD_Temp` on fixed drives — used by startup cleanup. |
| `CreateTempDirectoryOnSameVolume(referencePath, tempDirPrefix)` | Creates and returns a temp directory **on the same volume as `referencePath`**, or `null` when none can be created there. Prefers the system temp directory when it already happens to be on that volume **and** its path is chdman-safe (no special permissions needed), otherwise `{root}\BatchConvertToCHD_Temp` — the same layout `GetBestTempDirectory` uses, so startup cleanup already finds it. An unsafe `%TEMP%` remains the last resort so a generated cue still gets a chance to convert. |
| `ValidateAndNormalizePath(path, pathName, onLog, onError)` | `GetFullPath` + existence check with friendly errors. |

> **`GetBestTempDirectory` vs `CreateTempDirectoryOnSameVolume`.** The first picks the roomiest drive, which is what you want when a whole disc image is about to be written. The second pins the directory to one volume, which is what a **generated cue** needs: chdman joins a cue's `FILE` entry to the cue's own directory and cannot follow an absolute path, so a cue on the wrong volume simply cannot reach its image (see [Conversion Pipeline §5.8](05-conversion-pipeline.md#58-generated-cues-and-the-same-volume-constraint)). Using the roomiest-drive helper for cue staging silently disabled the generated-cue feature whenever the source drive was not the emptiest drive.

---

## 8.2 CueNormalizer & CueWorkDirectory

### CueNormalizer (`CueNormalizer.cs`)

`internal static class CueNormalizer` — produces a canonical, chdman-safe cue.

- `NormalizeAsync(cuePath, token, transform?)`:
  1. Reads lines with `GameFileParser.ReadLinesWithDetectedEncodingAsync` (encoding + BOM detection).
  2. Processes only lines starting with `FILE ` (case-insensitive); everything else passes through verbatim.
  3. Resolves each reference: **exact case-sensitive** match → **case-insensitive** match → **zero-padding-tolerant** match (`(Track 2)` ↔ `(Track 02)`/`(Track 002)` via `TrackNumberRegex`).
  4. Applies an optional transform (used to rewrite MP3 → WAV names and track types).
  5. Compares the canonical line with the original; flags `NeedsRewrite`/`ReferencesChanged`.
- `WriteCanonicalCueAsync` — writes **UTF-8 without BOM**, CRLF line endings.
- `GetTrackType` — token after the last quote, matched against `[BINARY, WAVE, MP3, AIFF, MOTOROLA, AUDIO]` (case-insensitive), upper-cased; tolerates cdrdao TOC extra columns.
- Result model `CueNormalizationResult` carries `SourceEncoding`, `HasBom`, `References`, `UnresolvedNames`, `CanonicalLines`, `NeedsRewrite`, `ReferencesChanged`, `CanonicalCueText`.
- Reference model `CueFileReference(ReferencedName, ResolvedName, FullPath, TrackType, WasNameCorrected)`.

### CueWorkDirectory (`CueWorkDirectory.cs`)

`internal static class CueWorkDirectory` — builds a self-contained ASCII work directory when the cue can't be handed to chdman as-is.

- `PrepareAsync(cuePath, tempDirPrefix, mp3Decoder?, onLog?, token)` → `CueWorkDirectoryResult(WorkCuePath, WorkDir, UnresolvedNames)`:
  - No work needed (UTF-8, no BOM, ASCII names, no corrections, no MP3, safe path lengths) → `(null, null, [])`.
  - **BOM-only fast path**: writes a BOM-free canonical `game.cue` into the work dir that references bins **in place via relative paths** — no bin copies. Declined when any bin is on another drive or when the descriptor/references are at or beyond MAX_PATH (the relative references would rejoin into the same overlong paths).
  - Full path: copies every referenced file under safe `trackNN.ext` names (MP3 tracks decoded to `trackNN.wav` via the MP3 decoder, track type rewritten to `WAVE`), then writes the canonical cue. Overlong paths force this copy-based route so chdman only ever sees short ASCII names.
  - Unresolved references → returned in `UnresolvedNames` (caller skips conversion).
  - On failure the work dir is deleted and the exception rethrown.
- `TryWriteInPlaceWorkCueAsync` — the fast path above; `CopyWithRetryAsync` copies bins with up to 4 attempts (300 ms × attempt backoff).

### Why this exists

chdman's cue parser does **not** skip a UTF-8 BOM — the first token becomes `"\uFEFFFILE"` and chdman reports `couldn't find bin file []` even when every bin exists. Non-UTF-8 text (Korean/Cyrillic), non-ASCII names/paths, and zero-padding name mismatches produce the same class of failure. Normalization + work directories eliminate all of them.

---

## 8.3 GameFileParser

`internal static class GameFileParser` (`GameFileParser.cs:11`)

- `GetReferencedFilesFromCueAsync` / `FromGdiAsync` / `FromTocAsync` — extract referenced file names from descriptors:
  - **cue/toc**: lines starting with `FILE `; quoted or unquoted names; the last space-delimited token is stripped when it is a known track type.
  - **gdi**: skips line 0 (track-count header); quoted names between first/last quote; unquoted lines need ≥ 5 whitespace parts, with names spanning parts 4..end when > 6 parts (spaces in filenames).
- `ReadLinesWithDetectedEncodingAsync` — BOM detection (UTF-8 → **UTF-32LE before UTF-16LE** → UTF-16LE → UTF-16BE), then strict UTF-8, then legacy codepages `[932, 949, 936, 1251, 866, 1252]` ("ordered by likelihood for game rips") scored +10 per `FILE` line whose name resolves to an existing file; ties broken by declared order; last resort `Encoding.Default`.
- Used by the conversion pipeline for dependency validation, by `CueNormalizer`, and by `DeleteOriginalGameFilesAsync`.

---

## 8.4 BinCueGenerator

`internal static class BinCueGenerator` (`BinCueGenerator.cs:13`)

Generates cue files for **bin-only archives** (no descriptor in the archive).

- Constants: `Mode2 = "MODE2/2352"`, `Mode1 = "MODE1/2352"`, auto-cue marker `".autocue"`.
- `GetAutoCuePath(binPath)` → `{bin}.autocue.cue`; `IsAutoCue(path)` → filename ends with `.autocue.cue`.
- `BuildCueContent(binFileName, mode)` → single-track `FILE ... BINARY / TRACK 01 {mode} / INDEX 01 00:00:00`.
- `ReadTrackModeAsync(cuePath)` — scans `TRACK ` lines for a `/` and returns the mode token after the last space; default MODE2/2352.
- `RewriteCueAsync(cuePath, mode)` — rewrites the whole auto-cue with a new mode.
- `GetAlternateMode(mode)` — MODE2 ↔ MODE1 swap.
- Auto-cue outputs map to `Game.chd` (not `Game.autocue.chd`), and a failed auto-cue conversion is retried once with the alternate track mode (`MainWindow.xaml.cs:1579–1627`).

---

## 8.5 IsoSectorValidator

`internal static class IsoSectorValidator` (`IsoSectorValidator.cs:14`)

- `StandardSectorSizes = [2352, 2048, 2336, 2324, 2448, 2368]` — raw CD, DVD/data, Mode 2 XA, Mode 2 Form 1, and the two subchannel-bearing layouts Alcohol images use (2352 + 96, 2352 + 16).
- `GetSectorSizeWarning(path)` — `null` for `.cue`/`.gdi`/`.toc` descriptors and for missing/unreadable files; otherwise warns when the size isn't divisible by any standard size. Used as an early warning (conversion still proceeds; the hard check happens after chdman fails).

---

## 8.6 Mp3ToWavDecoder & IMp3Decoder

`internal sealed class Mp3ToWavDecoder : IMp3Decoder` (`Mp3ToWavDecoder.cs:16`)

- `DecodeAsync(mp3Path, wavPath, onLog?, token)` — decodes an MP3 to a 16-bit PCM WAV; throws when undecodable.
- **Primary path**: NAudio Media Foundation (`MediaFoundationReader`), serialized under a static `Lock` because `MediaFoundationApi.Startup/Shutdown` are not thread-safe.
- **Fallback path**: `Mp3FileReader` (ACM codec) for Windows N / Server Core without Media Foundation.
- `NormalizeForChdman` — resamples to exactly **44 100 Hz** (`WdlResamplingSampleProvider`) and converts mono → stereo (`MonoToStereoSampleProvider`); `WaveFileWriter.CreateWaveFile16` forces 16-bit PCM (some MF codecs emit IEEE float, which chdman can't read).
- Both decoders failing → `InvalidDataException` (with the MF exception as inner).

---

## 8.7 RetryingFileOperations

`internal static class RetryingFileOperations` (`RetryingFileOperations.cs:10`)

File operations that survive transient locks (antivirus, indexer, explorer):

- `MaxDeleteAttempts = 10`; backoff `[500, 1000, 2000, 4000, 6000, 8000, 8000, ...]` ms — ≈ 45 s total.
- `TryDeleteAsync(path, token, onRetry?, backoffMsProvider?)`:
  - `FileNotFoundException`/`DirectoryNotFoundException` → `true` (already gone).
  - `IOException` → retry with backoff; `false` after the last attempt.
  - `UnauthorizedAccessException` → clears the **ReadOnly attribute once**, retries, then fails.
- `TryMoveAsync(source, dest, token, onRetry?, backoffMsProvider?)`:
  - `FileNotFoundException` → `true` (source already gone — nothing to move).
  - `IOException` (including `DirectoryNotFoundException`) → retry with backoff; `false` after the last attempt. A failed move is **never** reported as success — the source file still exists.
  - `UnauthorizedAccessException` → fail fast (ACL problems won't resolve).
- Used by: `TryDeleteFileAsync`/`TryDeleteDirectoryAsync`, `MoveVerifiedFileAsync`, `ExtractChdTracksToDirectory`, the ASCII-output move in `ConvertToChdAsync`, and destination-deletion before moves.

---

## 8.8 FileExtensions

`internal static class FileExtensions` (`FileExtensions.cs:11`)

All constants are lowercase; every lookup is case-insensitive (`StringComparer.OrdinalIgnoreCase`).

| Constant | Value | Constant | Value |
|----------|-------|----------|-------|
| `Cue` | `.cue` | `Zip` | `.zip` |
| `Iso` | `.iso` | `SevenZip` | `.7z` |
| `Img` | `.img` | `Rar` | `.rar` |
| `Gdi` | `.gdi` | `Cso` | `.cso` |
| `Toc` | `.toc` | `Pbp` | `.pbp` |
| `Raw` | `.raw` | `Isz` | `.isz` |
| `Ccd` | `.ccd` | `Bin` | `.bin` |
| `Mds` | `.mds` | `Sub` | `.sub` |
| `Ecm` | `.ecm` | `Chd` | `.chd` |
| `SplitFirstNumbered` | `.001` | `SplitFirstAlcohol` | `.i00` |

Sets (with `...Set` case-insensitive twins):

- `AllSupportedInputExtensionsForConversion` = `[.cue, .iso, .img, .gdi, .toc, .raw, .ccd, .bin, .mds, .ecm, .isz, .001, .i00, .zip, .7z, .rar, .cso, .pbp]`
- `ArchiveExtensions` = `[.zip, .7z, .rar]`
- `PrimaryTargetExtensions` (extraction targets from archives) = `[.cue, .iso, .img, .gdi, .toc, .raw, .ccd, .mds, .isz]`

Notes:

- `.sub` is a sidecar format and `.chd` is an output, so neither is an input. `.bin` **is** a standalone input now — a bare `.bin` gets a generated cue — but `InputFileFilter` drops it from the batch when a sibling descriptor already covers it.
- Only the **first** volume of a split set is registered (`.001`, `.i00`); later parts are found from it, so a set is offered once rather than once per piece.
- A `.mdf` is deliberately absent: the `.mds` descriptor drives Alcohol conversion, so an orphaned `.mdf` is skipped.
- The `.cdt` sibling of CCD sets is referenced literally in `MainWindow.xaml.cs` (no constant).

> **An extension missing from `AllSupportedInputExtensionsForConversion` is invisible.** This set gates the folder scan, so content-based handling for an unregistered extension can never run. `.isz` demonstrated the failure mode: the "genuinely compressed ISZ is not supported" message existed but was unreachable for actual `.isz` files, because they were never offered in the first place.

---

## 8.9 Content Identification

### DiscImageSignature & DiscImageKind (`DiscImageSignature.cs`, `DiscImageKind.cs`)

`internal static class DiscImageSignature` — identifies what a file actually is from its leading bytes, regardless of its name.

- `Detect(path)` → `DiscImageKind`: `Unknown`, `RawCd`, `AlcoholDescriptor`, `Rar`, `Zip`, `SevenZip`, `Isz`, `Ecm`, `Cso`, `Pbp`, `Chd`.
- `IsArchive(kind)` — true for `Rar`/`Zip`/`SevenZip`, used to tell a real archive from a disc image wearing an archive extension.
- `Describe(kind)` — a human phrase ("a raw CD disc image", "a ZIP archive") used in log messages when the content contradicts the name.

### RawCdImageDetector (`RawCdImageDetector.cs`)

Recognises raw 2352-byte CD sectors and stages a cue for them.

- `RawSectorSize = 2352`. The 12-byte sync mark (`00` + ten `FF` + `00`) and the mode byte at offset 15 are private details.
- `IsCandidateExtension(extension)` — whether an extension is one raw CD dumps get mislabelled with (`.iso`, `.img`, `.bin`).
- `DetectTrackMode(path)` — checks the sync mark, reads the mode byte, and confirms the file is a whole number of 2352-byte sectors; returns `MODE1/2352`, `MODE2/2352` or `null` (a cooked 2048-byte image, a DVD image, or an unknown layout).
- `TryWriteCueAsync(imagePath, trackMode, workDir, token)` — writes a cue in `workDir` that references the image **in place** via a relative path, returning `null` when the image cannot be reached relatively (different volume). BOM-free UTF-8, as always.

### SplitImageJoiner (`Alcohol120Sharp`)

- `TryGetVolumeSet(firstVolumePath)` — finds a numbered volume set (`.001`/`.002`…, `.i00`/`.i01`…) in order, or `null`.
- `GetTotalBytes(set)` / `JoinAsync(set, destination, token)` — concatenates the parts into one image and returns the byte count, so the caller can check it against a sector boundary.

### TrackBinCueBuilder (`TrackBinCueBuilder.cs`)

- `TryGetTrackSet(binFiles)` — recognises a `(Track 1)`, `(Track 2)`, … bin set and orders it.
- `WriteCueAsync(set, dataTrackMode, token)` — writes a multi-FILE cue so a split-track disc keeps its CDDA instead of converting as a single data track.

> **Pregaps cannot be recovered from file names.** Each track is declared at the start of its own file, so an audio track whose pregap lived at the end of the previous track can start up to two seconds early. Nothing is lost, and the log states the assumption.

### InputFileFilter (`InputFileFilter.cs`)

- `RemoveCompanionDataFilesAsync(paths, onLog, token)` — drops a raw `.bin`/`.img`/`.iso`/`.raw` when a descriptor in the **same directory** covers it, matched by base name and then by the descriptor's text. Applied at the folder scan, at batch start, and in the archive loop, so a cue/bin or CloneCD set converts once through its descriptor instead of once per file with both attempts aimed at the same output name.
- `ResolveOutputCollisions(files, outputPathSelector)` — groups inputs that would all be written to the same output `.chd` and keeps only the **first non-archive input** of each colliding group (or the first input when every member is an archive). Returns the kept inputs plus one `SkippedDuplicate(SkippedFile, KeptFile, OutputPath)` per dropped input so the caller can log the resolution. Converting both would only overwrite one product with the other, so the redundant conversion — and, for archives, the redundant extraction — is skipped up front.

---

## 8.10 ISZ Support (`UltraIsoSharp`)

Decompresses UltraISO `.isz` images. Lives in the standalone `UltraIsoSharp` library (multi-targeted `net10.0;net8.0`, MIT-licensed, packable) and is written against EZB Systems' ISZ File Format Specification 1.00.

| Type | Role |
|------|------|
| `IszHeader` | The packed 48-byte header, every field read at its documented offset. `ImageSizeBytes` (= `TotalSectors × SectorSize`, computed in 64-bit so dual-layer DVDs don't overflow), `IsEncrypted`, `IsSegmented`, `EncryptionDescription`, `Summary`, and `GetUnusableReason()` which refuses encryption, a zero-sector or zero-chunk header, an implausible chunk size, an unreadable pointer width and a missing chunk table. |
| `IszChunkType` | The four storage kinds: `Zero`, `Stored`, `ZLib`, `BZip2` (spec names `ADI_ZERO`, `ADI_DATA`, `ADI_ZLIB`, `ADI_BZ2`). |
| `IszSegment` | One segment-table entry: size, chunk count, first chunk number, chunk offset, left-over bytes. `IsTerminator` marks the zero-size entry that ends the table. |
| `IszDecoder` | `TryReadHeaderAsync`, `GetDecodedFileName`, `GetSegmentPath`, `ReadChunkEntry` and `DecodeAsync`. |
| `IszDecodeResult` | `Success`, `OutputPath`, `SectorSize`, `FailureReason`. |

Behaviour worth knowing:

- A chunk table entry is a little-endian integer `PointerLength` bytes wide whose **top two bits are the storage kind**; the rest is the stored length. `ReadChunkEntry` is exposed for testing precisely because that bit-packing is easy to get subtly wrong.
- Segment naming follows the spec: segment 1 is `game.isz`, segment 2 is `game.i01`, segment *n* is `game.i(n-1)`.
- Multi-segment images are read as one logical stream over a region per file, so a chunk straddling a boundary needs no special case. `left_size` is read but not used to drive reading — it would only be a redundant cross-check.
- The spec caps a stored chunk at the chunk size, so a larger one is treated as a damaged table rather than read into a bigger buffer. Real writers keep a chunk verbatim (`ADI_DATA`) when compressing it would not shrink it, which is why incompressible content never produces an oversized chunk.

---

## 8.11 ECM Support (`Utilities/Ecm/`)

Decodes ECM (Error Code Modeler) files in-process — no external tool.

| Type | Role |
|------|------|
| `CdSectorEccEdc` | Regenerates a raw sector's error detection and correction fields: `ComputeEdc`, `WriteSyncAndMode`, `GenerateMode1`, `GenerateMode2Form1`, `GenerateMode2Form2`. `SectorSize = 2352`, `Mode2DataSize = 2336`. |
| `EcmImageDecoder` | `Signature` (`"ECM\0"`), `GetDecodedFileName`, `DecodeAsync`. Parses the block stream and writes the restored image. |
| `EcmDecodeResult` | `Success`, `OutputPath`, `BytesWritten`, `FailureReason`. |

The format: after the 4-byte signature comes a sequence of blocks, each introduced by a variable-length number whose low two bits give the kind — literal bytes, Mode 1, Mode 2 Form 1, Mode 2 Form 2 — and whose remaining bits give the count. A 4-byte checksum of the whole restored image closes the file.

- **Mode 1 parity covers the sector address; Mode 2 Form 1 parity does not** — Form 1 parity is computed with the address zeroed so it stays valid when the sector is read without its header, which is what lets ECM store Mode 2 sectors as 2336 bytes and emit the 16-byte sync and header as a literal run. Swapping those two behaviours yields an image that reads fine and never matches a known-good dump.
- The variable-length count is 5 bits plus 7 per continuation byte. A fifth continuation byte would shift by 33, which in C# wraps to 1 and would silently corrupt the count, so it is rejected as corrupt.
- The trailing checksum is **always** validated — it is the only end-to-end check that the regenerated parity and the recovered data are right.
- Decoding runs as one blocking job off the UI thread (`Task.Run`), because the format is a byte-at-a-time state machine over the whole image.

> **Why this is safe to hand-write.** Getting Reed-Solomon parity subtly wrong produces data that reads correctly while the image's hash never matches a known-good dump — which is why an earlier version drove Neill Corlett's external UNECM binary instead. The decoder is now verified byte for byte against that tool using a committed fixture the tool itself produced; see [Testing](11-testing.md).

---

## 8.12 Alcohol 120% Support (`Alcohol120Sharp`)

Lives in the standalone `Alcohol120Sharp` library (multi-targeted `net10.0;net8.0`, MIT-licensed, packable) together with `SplitImageJoiner`.

| Type | Role |
|------|------|
| `MdsParser` | Parses the `.mds` descriptor: signature, session table, track table; locates the `.mdf`; rejects descriptors whose session count is implausible. |
| `MdsDisc` | The parsed model. `RawSectorSize = 2352`, `RawPlusSubchannelSize = 2448`, `CookedSectorSize = 2048`; `IsDvdImage`, `IsPlainRawCd`, `NeedsSubchannelStrip`, `AllTracksDescribable`. |
| `MdsTrack` | One track: number, mode, sector size, start LBA, and `CueTrackType` (`null` when the mode cannot be expressed in a cue). |
| `MdsInputPreparer` | `PrepareAsync` → `Result(CuePath, DvdImagePath, FailureReason)`, plus `StripSubchannelAsync`, `WriteCueAsync` and `FormatMsf`. |

The three shapes it handles, and the reasoning, are in [Conversion Pipeline §5.7](05-conversion-pipeline.md#57-recovered-image-formats).

The `.mds` layout is not published; it was recovered by inspection and is documented here so the parser can be re-derived: signature `MEDIA DESCRIPTOR` at `0x00`; `u16` session count at `0x14`; `u32` session-block offset at `0x50`; session blocks are 24 bytes with the track count at `+0x0A` and a `u32` track-block offset at `+0x14`; track blocks are 80 bytes with the mode at `+0x00`, POINT at `+0x04`, a `u16` sector size at `+0x10` and a `u32` start LBA at `+0x24`. Mode bytes: `0xA9` audio, `0xAA` Mode 1, `0xEC` Mode 2, `0xE2` Mode 2 Form 1, `0xE3` Mode 2 Form 2. A POINT outside 1–99 is lead-in or lead-out.

---

## 8.13 Models

### FileItem (`Models/FileItem.cs`)

Bindable row for the file list DataGrids: `FileName` (relative path when searching subfolders), `FullPath`, `FileSize` (long), `IsSelected` (INotifyPropertyChanged). `DisplaySize` formats bytes with binary units `B/KB/MB/GB/TB` (`{size:0.##} {suffix}`).

### PbpExtractionResult (`Models/PbpExtractionResult.cs`)

`Success`, `CueFilePaths` (list), `OutputFolder`, `ErrorCode` (`PbpError?` — distinguishes "not a PlayStation disc image" from real failures), `Error` (human-readable failure description preserved from PBPSharp).
