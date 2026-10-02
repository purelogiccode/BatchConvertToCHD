---
title: Extraction & Verification
nav_order: 7
---

# 6. Extraction & Verification (Technical)

This page covers the internals of the two CHD-consuming workflows. References are to `BatchConvertToCHD.Avalonia/MainWindow.axaml.cs` unless noted.

---

## 6.1 Extraction

### Entry & batch loop

`StartExtractionButton_ClickAsync` (`:693`) validates paths, reads options (subfolders, delete original), and calls `PerformBatchExtractionAsync` (`:1355`), which runs `CheckDiskSpace` (extraction mode: warns when output free space < total input size) and then loops per file with `ExtractChdAsync` (`:2123`).

### Format selection

`GetSelectedExtractCommandAsync` (`:2325`):

| UI choice | chdman command |
|-----------|----------------|
| Auto | `DetectChdExtractCommandAsync` (`:2370`) — metadata scan |
| CD (.cue) | `extractcd` |
| DVD (.iso) | `extractdvd` |
| GDI (.gdi) | `extractcd` |
| HDD (.img) | `extracthd` |

Metadata detection scans the CHD's metadata entries (CHDSharp): `dvd` → `extractdvd`; `gd-rom` → `extractcd`; `hard disk`/`hdd` → `extracthd`; default `extractcd`. Output extension: explicit per radio button, or for Auto derived from the detected command — and when Auto yields `extractcd` plus the metadata contains `gd-rom` (`IsGdiChdAsync`, `:2338`), the extension becomes `.gdi` instead of `.cue`.

### Output path

The subfolder structure of the input is preserved under the output folder (`GetSafeRelativePath`). Nothing existing is deleted to make room: an extraction whose output would land on files of the same name is diverted into a subfolder instead (see below).

### Extracting into the source folder

The output folder may be the same as the input folder. Extraction is the workflow where that needs care, because **the output takes the CHD's base name**: extracting `Game.chd` produces `Game.cue` plus its track files, so left alone it would replace a cue/bin set kept beside the CHD.

Rather than overwrite those files, or stop to ask, the extraction is diverted. When any file it is about to write already exists, the whole set goes into a subfolder named after the disc — `Game\Game.cue`, `Game\Game (Track 1).bin` and so on — and the existing files are left untouched. `PathUtils.ReserveFreeSubdirectory` chooses the name, stepping to `Game (2)`, `Game (3)` and so on when something already occupies it.

The diversion happens only when there is a real clash. Extractions with nothing in their way still land directly in the output folder, so the layout is unchanged for everyone else, and no setting controls this. One line in the log says where the files went.

Two properties make it safe to do without asking:

*   A descriptor's `FILE` entries are relative and the track files travel with it, so a diverted `.cue`/`.gdi` set stays valid with no rewriting.
*   The clash is tested after extraction into the temp directory but before anything is moved, so the decision uses the real output names rather than a guess at the extension.

### Single-file extraction (DVD/HDD)

`ExtractChdToSingleFile` (`:2233`): a `FileStream` is created with `FileMode.Create`, and the CHD is streamed out in 4 MB buffers with a per-chunk `token.ThrowIfCancellationRequested()`. Cancellation deletes the partially extracted file.

### Multi-track extraction (CD/GDI)

`ExtractChdTracksToDirectory` (`:2252`):

1. Creates `_extract_temp_<guid>` **inside the target directory**.
2. Calls `chd.ExtractToDirectory(tempExtractDir, baseFileName)` (CHDSharp).
3. Picks the destination: the target dir, or a fresh subfolder named after the disc when any extracted file would clash with something already there (`ReserveFreeSubdirectory`). Then moves each extracted file into it.
4. On success the temp dir is deleted; on failure the temp dir is **kept** and a warning logs the number of remaining files ("Partial extraction: N file(s) remain in temp directory: ...") so the user can inspect/clean up.
5. Moves go through `RetryingFileOperations.TryMoveAsync` (retry with backoff, ~45 s) so transient locks (antivirus/indexer) don't abort the whole disc extraction; a move that still fails after retries throws and the partial-extraction path handles the rest. The `TryDeleteAsync` on the destination remains only as a guard against a file appearing between the clash test and the move — after step 3 the destination is expected to be free.

### CHD open failures

`ChdFile.Open` errors are logged with the CHDSharp message and the file is marked failed; the batch continues. Typical messages: "Not a valid CHD file" (bad magic), "Invalid or corrupt data" (structure broken), "Cannot open file" (locked/unreadable). These are user-data conditions — the app never crashes on them and they are excluded from bug reports (see [Bug Reporting System](09-bug-reporting.md)).

### Decompression failures and the chdman fallback

When CHDSharp fails to decode a hunk during extraction ("Failed to read hunk N: Chderrdecompressionerror"), the error is mapped through `GetChdExtractionErrorMessage` (`:6188`) into a user-friendly message, logged at informational level, **and the extraction is retried with chdman** (`TryExtractWithChdmanAsync`, `:6260`) when a chdman binary is available — the bundled one on Windows, or a system `chdman` on `PATH` elsewhere (verification stays library-only):

1. chdman runs the user's selected command (`extractcd`/`extractdvd`/`extracthd`, `-f` to force overwrite; `extractcd` also pins the bin name with `-ob`).
2. If the CHD carries **no CD/DVD/HDD metadata** (`IsAvChdAsync`, `:3042`) it is an A/V (laserdisc) CHD: `extractcd` cannot handle it, so chdman is retried with `extractld` (writes an `.avi`, MAME 0.285+) and then `extractraw` (raw dump) for older chdman builds.
3. On failure, truncated outputs are deleted; on success the file is marked extracted and the batch continues normally (including the "delete original" option).

The CHDSharp failure is reported to the bug API **only when the chdman fallback also fails** — if chdman extracts the file, the extraction succeeded and nothing went wrong that needs the maintainer's attention. The reader's failure reason still reaches the user's log file at informational level.

---

## 6.2 Verification

### Entry & batch loop

`StartVerificationButton_ClickAsync` (`:1110`) reads the move options, creates the `Success`/`Failed` folders up front when requested (`:2022–2030`), and calls `PerformBatchVerificationAsync` (`:2011`).

### VerifyChdAsync

`VerifyChdAsync` (`:3020`) opens the file read-only and calls `Chd.CheckFile(stream, fileName, true)` (CHDSharp, in-process — **no** chdman process). On success it logs `V{version} — SHA1: {hex}`; failures log `result.Error.GetMessage()` or the exception message. The per-file read speed is sampled via the read performance counter.

### Moving verified files

`MoveVerifiedFileAsync` (`:2076`):

- Destination: `inputFolder\Success` or `inputFolder\Failed`; with subfolder search the relative directory is preserved under the target folder.
- Existing destination files are deleted with `RetryingFileOperations.TryDeleteAsync` (result checked — a locked destination fails fast with a clear error instead of a misleading move failure).
- The move uses `RetryingFileOperations.TryMoveAsync` (10 attempts, backoff 500 ms → 8 s, ~45 s total) because the freshly verified file may still be held by antivirus or the indexer.
- On persistent failure, the exception is logged and reported via `ReportBugAsync` ("Failed to move file ..."), but the batch continues.

### Scan exclusions

The verification and extraction file lists exclude anything under a first-level `Success` or `Failed` subfolder when recursive search is on (`:884–896`, `:941–952`), so organized output isn't reprocessed.

---

## 6.3 Startup & Shutdown Housekeeping

- **Leftover temp directories** from crashed sessions are deleted at startup: `CleanupLeftoverTempDirectories` (`:304`) scans `PathUtils.GetPossibleTempBasePaths()` (system temp + any existing `X:\BatchConvertToCHD_Temp` folders on fixed drives) for `BatchConvertToCHD_Temp_*` entries.
- **Legacy files** next to the exe are removed by `LegacyCleanupService` (`logs`, `Resources`, `Screenshot` folders; `maxcso.exe`, `psxpackager.exe`).
- On `Dispose` (`:3552`) the app unregisters the F8 hotkey, cancels the operation token, disposes services, and calls `KillOrphanedProcesses` (`:3579`) to kill leftover `chdman`/`7za` processes before exiting.
