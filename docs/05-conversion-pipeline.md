---
title: Conversion Pipeline
nav_order: 6
---

# 5. Conversion Pipeline (Technical)

This page is a deep dive into the conversion machinery. All references are to `BatchConvertToCHD/MainWindow.xaml.cs` unless noted.

---

## 5.1 Entry Point & Batch Orchestration

`StartConversionButton_ClickAsync` (`:1025`) validates both folder paths (`PathUtils.ValidateAndNormalizePath`), reads the option flags, renews the cancellation token source, disables the UI, and calls `PerformBatchConversionAsync` (`:1292`).

`PerformBatchConversionAsync` does, in order:

1. **Executable validation** — `ValidateExecutableAccessAsync` (`:353`): file exists, is `.exe`, and can be opened for reading with the same sharing Windows uses for executable images (read + delete), so a `chdman.exe` running under another app instance or held briefly by an antivirus scan no longer produces a false "locked by another process" abort. `ValidateChdmanCompatibilityAsync` (`:436`) runs `chdman help` and gives specific guidance for old-Windows "not a valid application" errors (Win32 error 193, including files mixed from the win-x64 and win-arm64 releases), access-denied (error 5), and abnormal termination (negative exit codes — see [Exit-code handling](#exit-code-handling) below).
2. **Sorting** — when "process smaller files first" is set, files are ordered by ascending size (`:1300–1313`).
3. **Disk space check** — `CheckDiskSpace` (`:3292`): warns when the output drive's free space is below 50 % of the total input size for conversion (100 % for extraction), and separately checks the temp drive when it differs from the output drive.
4. **Per-file loop** — `ProcessSingleFileForConversionAsync` (`:1395`), with Interlocked ok/failed counters and progress/speed updates.

## 5.2 Per-File Routing

`ProcessSingleFileForConversionAsync` decides the output path (source subfolder structure preserved via `PathUtils.GetSafeRelativePath` + `SanitizeFileName`), then routes in two stages: **content inspection first**, extension dispatch second.

### Stage 1 — content resolution

`TryResolveByContentAsync` reads the input's leading bytes and returns one of three answers:

- `null` — the extension is not lying, so the normal dispatch below applies. This is the common case.
- `ResolvedInput.Convert(path, forceDvd)` — convert *this* file instead (a rejoined image, a decompressed image, or a generated cue).
- `ResolvedInput.Skip(reason)` — a user-facing explanation; the file is not handed to chdman at all.

The order matters:

1. Text descriptors (`.cue`, `.gdi`, `.toc`, `.ccd`, `.mds`) are skipped — they have their own handlers and there is nothing to sniff.
2. An archive extension whose content really is an archive returns `null` (the archive handler owns it).
3. `SplitImageJoiner.TryGetVolumeSet` — a `.001`/`.i00` first volume is rejoined via `ResolveSplitVolumeSetAsync`, then classified.
4. `DiscImageSignature.Detect` decides the rest: `Ecm` → `ResolveEcmAsync`, `Isz` → `ResolveIszAsync`, `Chd` → skip ("this file is already a CHD").
5. An extension promising a container (`.zip`/`.7z`/`.rar`/`.isz`) whose content is a plain image → `ResolveMislabelledContainerAsync`.

`ClassifyRecoveredImageAsync` is the shared tail for anything recovered into a temp directory (joined from parts, decoded from ECM, decompressed from ISZ): raw CD sectors get a generated cue and `createcd`; a whole number of 2048-byte sectors converts as a DVD image; anything else is skipped with a reason naming the likely cause.

### Stage 2 — extension dispatch

| Extension | Handler | What it does |
|-----------|---------|--------------|
| `.cso` | `ProcessCsoFileForConversionAsync` | `_archiveService.ExtractCsoAsync` decompresses to a temp `.iso`, then converts. |
| `.zip`/`.7z`/`.rar` | `ProcessArchiveFileForConversionAsync` | Extracts to a temp dir; drops raw images already covered by a descriptor in the archive (`InputFileFilter.RemoveCompanionDataFilesAsync`); maps auto-cue outputs; validates cue/gdi/toc dependencies; converts each supported file. `.ccd` goes through CCDSharp, `.mds` through the Alcohol parser, `.isz` through `ConvertIszViaImageAsync`. |
| `.pbp` | `ProcessPbpFileForConversionAsync` | `ExtractPbpToCueBinAsync` via PBPSharp; `PbpError.InvalidPsarHeader` → informational skip; converts each disc cue. |
| `.ccd` | `ProcessCcdFileForConversionAsync` | `CcdConverter.Parse` + `ConvertToCueBin` into a temp dir, then converts the cue. |
| `.mds` | `ProcessMdsFileForConversionAsync` | `MdsParser` + `MdsInputPreparer` produce a cue (or a stripped image, or a DVD image), then convert. |
| everything else | direct path | `TryStageCueForRawImageAsync` may generate a cue for a raw CD image, then `ValidateDependentFilesAsync` → `TryDirectConversionAsync`. |

Missing input file: logs "File not found, skipping:" and asks `FileWatcherService.GetContextForMissingFile` for a diagnostic (deleted / renamed / created-then-gone / outside watch / drive disconnected).

### Converting in place

The source and output folders may be the same, and the output may sit inside the source tree. Three properties make that safe for conversion:

- The output name is always `<base>.chd`, and `.chd` is not a conversion input, so a **source file can never be the target**.
- Conversions stage to `.chdtmp` and move into place only on success (§5.3), so an existing CHD of the same name survives a failed run.
- Content inspection recognises an existing CHD and skips it (`"this file is already a CHD"`), so outputs cannot be reprocessed on a later run.

`PathUtils.IsSameOrInsideDirectory` detects the situation and the log notes it once. Extraction is the tab where in-place needs care, because its output shares the source's base name — see [Extraction & Verification](06-extraction-and-verification.md).

### Batch preflight

Before the per-file loop, `WarnAboutOutputCollisions` reports inputs that would map to the same output `.chd` (the name comes from the input's base name alone, so `Game.cue`, `Game.zip` and `Game.ccd` in one folder all target `Game.chd`). This is a warning, not a block — but combined with the staging file in §5.3 it means the second conversion can no longer destroy the first one's output.

### Retry-via-temp-copy fallback

If the direct conversion of the original file fails, `TryRetryConversionViaTempCopyAsync` (`:1863`) copies the input (plus referenced files for cue/gdi/toc via `GameFileParser`) into a temp directory and converts there. This handles network paths and file-locking quirks. It also:

- pre-checks temp-drive free space (`:1917–1935`),
- strips a UTF-8 BOM from cue/toc copies in place (`:1949–1955`; chdman's cue parser chokes on BOMs — see [GameFileParser](08-utilities-reference.md#gamefileparser)),
- copies with `CopyFileWithRetryAsync` (`:3201`; 5 attempts, exponential backoff from 500 ms).

### Result handling

`HandleConversionResultAsync`: on success, optionally deletes originals (see below) and prunes now-empty subfolders (`TryDeleteEmptySubfolderAsync`). On failure the destination is **left alone** — a failure says nothing about the CHD already sitting at that path, which may be a good conversion from another input. Temp dirs are always cleaned in a `finally` block.

> **Why the destination is no longer deleted on failure.** chdman is invoked with `-f` and truncates its output file *before* it can fail, so a second input mapping to the same output name used to wipe out a working CHD. Conversions now write to a `<name>.<8hex>.chdtmp` staging file and are moved into place only after success (see §5.3), and the old unconditional "delete the partial output" calls were removed.

**Deleting originals** — `DeleteOriginalGameFilesAsync` (`:3134`): for `.cue`/`.gdi`/`.toc` it also deletes every referenced data file (`GameFileParser`); for `.ccd` it deletes the `.img`/`.sub`/`.cdt` companions. All deletions go through `RetryingFileOperations.TryDeleteAsync` via `TryDeleteFileAsync` (`:3362`), which additionally kills stray chdman processes after the second failed attempt (`KillChdmanProcesses`, `:3380`).

## 5.3 ConvertToChdAsync — Encoder Selection (CHDSharp first, chdman fallback)

`ConvertToChdAsync` is the single funnel for every conversion. Command and arguments are built once (below) and the same command line is offered to both encoders: `CHDSharp.exe` (or `CHDSharp_arm64.exe` on ARM64) as the primary encoder, bundled `chdman` as the automatic fallback.

### Primary encoder: CHDSharp

When the tool was resolved at startup (`_isChdSharpAvailable`), the run is handed to `CHDSharp.exe` (`:4914`): `RunEncoderProcessAsync` (`:4597`) drives the process with the shared arguments, the exit code is trusted because CHDSharp validates its own output before returning, and the staged `.chdtmp` file is moved into place on success. The log line reads `CHDSharp: createcd game.cue`, mirroring the chdman invocation it replaces.

If CHDSharp is missing or fails, the run falls back to chdman (`:4967` logs `CHDSharp failed ... Falling back to chdman...`) and continues through the chdman process-execution path below. At startup the app warns when `CHDSharp.exe` is not found ("CHDSharp is the primary encoder"), and a batch is refused only when neither encoder is present — the status bar carries an indicator for each.

### Output staging

The conversion writes to `<name>.<8hex>.chdtmp` next to the destination and moves it into place only after the encoder reports success. chdman ignores the output file's extension — verified: `createraw -o out.chdtmp` exits 0 and writes a valid CHD — and keeping the staging name off `.chd` also means a leftover staging file is never mistaken for a finished CHD by the verification and extraction tabs.

### Command & argument selection

```csharp
command = forceCd || hasCue || (!forceDvd && !isIso && !isImg && !isRaw) ? "createcd"
        : forceDvd || isIso                                          ? "createdvd"
        : isImg                                                      ? "createhd"
        :                                                              "createraw";
```

- `hasCue = isImg && File.Exists(Path.ChangeExtension(input, ".cue"))` — an `.img` with a sibling `.cue` is treated as a CD image.
- **Verb choice is still extension-driven here**, but this code is now only reached for images that content inspection (§5.2) did not claim. So `Data size ... is not divisible by sector size` should now mean a genuinely broken file rather than a mislabelled one.
- Base args: `{command} -i "<in>" -o "<out>" -f -np {cores}`.
- **`.raw` inputs get `-us 2352`** (`:2478–2481`) — chdman's `createraw` requires an explicit unit size when no parent CHD is supplied ("Unit size must be specified if no output parent CHD is supplied").
- **`.cue`/`.toc` descriptors referencing `.raw` tracks also get `-us 2352`** — when a cue file references raw audio tracks (e.g. `track02.raw`), the `createcd` command also needs an explicit unit size. `GameFileParser.GetReferencedFilesFromCueAsync` is called to check for `.raw` references, and `-us 2352` is appended to the arguments.
- `-np` (processors) comes from a UI/core setting.

### Pre-flight validations

1. **Sector-size warning for DVD**: `IsoSectorValidator.GetSectorSizeWarning` flags sizes not divisible by 2352/2048/2336/2324/2448/2368, but conversion proceeds — the hard gate is the post-failure check (some legitimate images use non-standard layouts).
2. **Cue work-dir preparation** for `.cue`/`.toc` (`:2503–2527`): `PrepareCueWorkDirAsync` (`:2417`) → `CueWorkDirectory.PrepareAsync` (see [Utilities](08-utilities-reference.md#cueworkdirectory)). If MP3 tracks exist and decoding failed, conversion is aborted with a clear message instead of handing chdman an MP3 cue. Overlong paths (descriptor or referenced files at or beyond MAX_PATH) also trigger the copy-based work directory.
3. **ASCII temp work dir** (`:2508–2546`): if any part of the input or output path is unsafe for chdman — non-ASCII characters *anywhere along the path* (an accented user name, a non-Latin folder name) or a total length at or beyond MAX_PATH (260) — the input is copied into an ASCII-safe GUID-named staging directory and the output is written there too; after success the output is moved to the real destination with `RetryingFileOperations.TryMoveAsync`. Only an unsafe *input* is staged: an input chdman can read in place (e.g. an ASCII cue whose destination path is overlong) keeps resolving its `FILE` entries against its original directory. The staging location itself is chosen by `PathUtils.CreateAsciiSafeTempDirectory`, because the system temp folder lives under the user profile and can contain exactly the characters this fallback exists to avoid (`C:\Users\Kauê Chacon\...`).

### Process execution (chdman fallback)

- `ProcessStartInfo` with redirected stdout/stderr, `UseShellExecute=false`, `CreateNoWindow=true`.
- Output handlers classify lines: "Compression complete"/"final ratio" → success lines; `% complete`/`Compressing`/`Output bytes`/`Compression ratio` → filtered as progress; everything else → `[CHDMAN]` log lines.
- The stderr buffer accumulates **all** stderr lines (including progress, which chdman streams to stderr).
- **Timeout**: when enabled, a linked CTS with `CancelAfter(timeoutMinutes)` aborts the wait; the process is killed and the file marked failed with a `TIMEOUT:` log.
- On cancellation/timeout the process is killed (`process.Kill(true)`), waited up to 5 s, and temp cleanup is deferred 300 ms so file handles are released.

### Exit-code handling

- Success = exit code 0 and no cancellation.
- **createdvd fallback**: if the error output contains "Unrecognized track type" and the command was `createcd` without user-forced CD, the app recurses with `forceDvd=true` (`:2694–2699`). A recursion depth guard limits this to one retry; exceeding it logs `Retry limit reached` instead of recursing further.
- **Valid-output tolerance**: a non-zero exit that still produced a >0-byte output file is treated as success (`:2701–2716`).
- **Sector-size hard check** (`:2761–2794`): for non-descriptor inputs, if the file size is not divisible by any of 2352/2048/2336/2324, the conversion fails with "file size ... is not divisible by any standard sector size ... The file may be corrupt or truncated."
- **Disk-space detection** (`IsDiskSpaceError`, `:3281`): keywords "not enough space", "not enough disk space", "disk full", "no space left", "insufficient disk space".
- **Error line selection** (`SelectChdmanErrorLine`, `:2860`): scans the stderr buffer from the **last** line upward, skipping progress lines (`% complete`, `Compressing,`, `Converting,`, `Output bytes`, `Compression ratio`, `ratio=`) and the `Fatal error occurred: N` exit summary, and returns the last real error line. This fixed the class of bugs where the first line of stderr was a progress line ("Compressing, 0.0% complete... (ratio=100.0%)"). When the only output is a fatal error summary, a descriptive message is returned instead of the cryptic exit code.
- **Abnormal-termination decoding** (`DescribeChdmanCrash`): a negative exit code means Windows killed chdman before it printed anything. Common NTSTATUS codes are named (e.g. `-1073741795` → `0xC000001D, STATUS_ILLEGAL_INSTRUCTION - the CPU executed an unsupported instruction`) with guidance to replace `chdman.exe` with a CPU-appropriate build and check antivirus quarantine. The startup compatibility check (`ValidateChdmanCompatibilityAsync`) runs `chdman help` first and refuses to start a batch when even that crashes, so one clear message replaces a run of per-file failures.
- **Path substitution via quoted matching** (`:3474,3494,3501,3520`): argument paths are replaced using `$"\"{originalPath}\""` quoted-pattern matching instead of bare `string.Replace`, preventing a path that is a substring of another argument from causing corruption.
- **Diagnostics on unexplained errors**: when the selected error line contains "couldn't find bin file" or "Unknown error", a capped, sorted directory listing of the input folder is logged (`GetDirectoryDiagnostics`).

## 5.4 Cue Normalization & Work Directories

Two cooperating mechanisms ensure the encoder never sees malformed cues:

1. **`CueNormalizer.NormalizeAsync`** — detects the file encoding (BOMs → strict UTF-8 → legacy codepages scored by resolvable references), strips BOMs, unquotes/rewrites `FILE` lines, resolves references case-insensitively and with zero-padding tolerance (`(Track 2)` ↔ `(Track 02)`), and produces a canonical UTF-8 (no BOM) CRLF cue.
2. **`CueWorkDirectory.PrepareAsync`** — when the cue needs rewriting (BOM, non-UTF-8, non-ASCII names, MP3 tracks, corrected names), builds an isolated ASCII work directory with the canonical cue and every referenced file under safe `trackNN.ext` names; MP3 tracks are decoded to WAV. BOM-only cues with ASCII names use an **in-place fast path**: a `game.cue` referencing bins via relative paths, avoiding multi-hundred-MB copies.

Details in [Utilities Reference](08-utilities-reference.md#cuenormalizer-and-cueworkdirectory).

## 5.5 Exception Classifiers

Centralized classification used across the pipeline (`:3227–3290`):

| Helper | Matches |
|--------|---------|
| `IsCancellationException` | `OperationCanceledException` |
| `IsDiskSpaceException` | `IOException` HResult `-2147024784` (ERROR_DISK_FULL) or `-2147024783` (ERROR_SEM_TIMEOUT) |
| `IsCrcErrorException` | `IOException` HResult `-2147024809` (ERROR_CRC) or message containing "cyclic redundancy check"/"data error" |
| `IsCorruptionException` | `InvalidDataException`, `IndexOutOfRangeException`, `NullReferenceException`, `CryptographicException`, or SharpCompress archive-corruption types (IncompleteArchive, ArchiveOperation, InvalidFormat, LZMA DataError) |
| `IsDiskSpaceError` (string) | chdman output keywords listed above |

## 5.6 Archive Processing (Summary)

See [Services Reference → ArchiveService](07-services-reference.md#archive-service) for the full extraction semantics. Highlights relevant to the pipeline:

- Pre-extraction disk-space estimate (`CheckTempDiskSpace`): ZIP entry sizes are summed; safety margin = estimate + max(estimate/10, 100 MB).
- Zip-slip protection: every extracted path must stay under the output directory.
- Post-extraction scan for primary targets (`.cue/.iso/.img/.gdi/.toc/.raw/.ccd/.mds/.isz`); if none and bare `.bin` files exist, a `(Track N)` set becomes a multi-FILE cue via `TrackBinCueBuilder`, otherwise `BinCueGenerator` produces a MODE2/2352 auto-cue for the largest bin (auto-cues are retried once with MODE1/2352 on failure).
- Error categorization maps SharpCompress/7za failures to actionable messages (missing RAR volume, encrypted archive, unsupported compression method, disk full, locked file, network unavailable).
- A `.rar` that does not start with `Rar!` is no longer assumed corrupt: content sniffing catches the two real cases, a disc image simply given a `.rar` extension and a `.rar` set that is a plain byte split rather than an archive.

> **Still unhandled**: an `.ecm` inside an archive. `.ecm` is not an archive primary target, so an archive containing only `.ecm` files reports `No supported primary files found in archive`. `.isz` *is* a primary target and shows the pattern to follow.

---

## 5.7 Recovered-Image Formats

These all converge on `ClassifyRecoveredImageAsync` (§5.2) once the image has been reconstructed.

### Split volume sets — `SplitImageJoiner`

`.001`/`.002`… and `.i00`/`.i01`… sets are concatenated into one temp file. Only the **first** volume is a registered input, so a set is offered once rather than once per piece. A multi-part *archive* is detected and refused separately, with instructions, since that needs different tooling. A set whose parts do not join to a whole number of sectors is reported as needing re-download rather than converted.

### ISZ — `Utilities/Isz/`

Written against EZB Systems' ISZ File Format Specification 1.00. `IszHeader` parses the packed 48-byte header; `IszDecoder` walks the chunk table, splitting each entry's top two bits into the storage kind (`ADI_ZERO`, `ADI_DATA`, `ADI_ZLIB`, `ADI_BZ2`) and the remainder into the stored length, then decompresses through `ZLibStream` and SharpCompress's `BZip2Stream`.

- Multi-segment images are read as **one logical stream over a region per file**, so a chunk straddling a segment boundary needs no special case.
- Later segments are matched by volume serial number; a segment belonging to another rip is refused, and a missing one is named.
- Encryption (`has_password` ≠ 0) is refused by name (AES-128/192/256).
- Output is capped at `total_sectors × sect_size` and the total is checked at the end, so a truncated file is reported instead of yielding a short image that would convert and look fine.
- A failed decompression **deletes its partial output**, for the same reason.

Note that `.isz` covers two unrelated things in practice: a real ISZ starts with `IsZ!` and is decompressed, while ordinary images also get renamed to `.isz` and are routed by content like any other mislabelled file.

### ECM — `Utilities/Ecm/`

ECM shrinks a raw CD image by discarding each sector's EDC checksum and Reed-Solomon parity, which are derivable from the user data, and recording only what kind of sector each one was. `EcmImageDecoder` parses the block stream (literal, Mode 1, Mode 2 Form 1, Mode 2 Form 2) and `CdSectorEccEdc` regenerates the discarded fields.

- **No external tool.** An earlier version drove Neill Corlett's UNECM binary, because regenerating parity cannot be trusted without a known-good fixture to check against. That fixture now exists (see [Testing](11-testing.md)), the output is verified byte for byte against the original tool, and the dependency is gone — which also means ARM64 gets ECM like every other format.
- Mode 1 parity covers the sector address; **Mode 2 Form 1 parity is computed over a zeroed address** so it stays valid when the sector is read without its header. That is exactly what lets ECM store Mode 2 sectors as 2336 bytes and emit the 16-byte sync and header as a literal run.
- Every ECM file ends with a checksum of the whole restored image, which is always validated — a damaged file is reported rather than turned into a plausible one.

### Alcohol 120% — `Utilities/Mds/`

`MdsParser` reads the descriptor's session and track tables; `MdsInputPreparer` picks one of three shapes:

- **2352-byte sectors** — only a cue is missing, and the `.mdf` is referenced where it lies.
- **2448 or 2368** — 2352 data plus a subchannel tail chdman will not read, so the tail is stripped into a new image and a cue is written for that.
- **2048** — the `.mdf` is really an ISO and converts as a DVD image.

Split `.i00` data files are joined first. A descriptor whose session count is implausible, or whose track modes cannot be expressed in a cue, is refused with the reason. A `.mdf` is never converted on its own: the `.mds` drives everything, which is why only `.mds` is a registered input.

---

## 5.8 Generated Cues and the Same-Volume Constraint

Several paths above generate a cue that references a disc image **where it already lies**, rather than copying a multi-hundred-megabyte file. That works because chdman resolves a cue's `FILE` entry against the cue's own directory.

The catch, verified against chdman 0.285: chdman **joins** the `FILE` string to the cue's directory unconditionally. An absolute path is therefore looked for at `C:\temp\x\D:\game.iso` and reported as `ERROR: couldn't find bin file [...]`. A bare file name with the image elsewhere fails the same way.

So a generated cue has to sit on the **same volume** as its image. `StageCueForImageAsync` uses `PathUtils.CreateTempDirectoryOnSameVolume` for this, *not* `GetBestTempDirectory` — the latter deliberately picks the roomiest drive, which is right for writing a whole image and wrong for a few hundred bytes of cue with a hard placement constraint. When no writable location exists on the image's volume the image is converted as-is with a warning.

The `.mds` path has the same constraint and resolves it differently: it falls back to **copying** the `.mdf` into the work directory, which is correct but slow.
