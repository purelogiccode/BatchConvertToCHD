---
title: User Guide
nav_order: 5
---

# 4. User Guide

The main window has four tabs: **Convert to CHD**, **Verify CHD Files**, **Extract CHD Files**, and **Explorer**. A terminal-style log view sits on the right, stat cards and the progress bar at the bottom, and a status bar with the CHDSharp and CHDMAN encoder indicators at the very bottom.

Title-bar buttons: **Donate** (opens the project's donation page), **About** (info dialog, scrollable), **AppData** (opens `%LocalAppData%\CHDStudio`), **Exit**.

---

## 4.1 Convert to CHD

### Workflow

1. **Source Files** — select the folder containing images/archives (or pass it as a command-line argument).
2. **Output CHD** — select the destination folder.
3. Adjust options (below).
4. Review the file list, uncheck anything you don't want, then click **Start Conversion**.

### Options

| Option | Effect |
|--------|--------|
| **Search subfolders (recursive search)** | Recursively scans the source folder; the output mirrors the directory hierarchy (relative paths are preserved). |
| **Delete originals after a successful conversion** | Removes the source file — and its dependencies (`.bin`, `.sub`, etc. for cue sets; `.img`/`.sub` for CCD sets) — **only after** the CHD was produced successfully. |
| **Process smaller files first** | Sorts the batch by ascending file size so quick conversions finish first. |
| **Time limit per file** (minutes, default 15) | Aborts a single conversion that exceeds the limit; the file is marked failed and the batch continues. The app enforces a hard cap of 4 hours (`AppConfig.MaxConversionTimeoutHours`). |
| **Force CD** / **Force DVD** | Overrides automatic command detection (`createcd` / `createdvd`). The two checkboxes are mutually exclusive. |

### File list

- Every file matching the supported extensions (see [Overview → Supported Formats](01-overview.md#12-supported-formats)) is listed pre-selected.
- **Select All** / **Deselect All** toggle the whole list.
- The list is loaded in chunks (100 items at background priority) to stay responsive on huge folders.
- When recursive search is on, the **File Name** column shows the path relative to the source folder.

### What happens during conversion

1. **Archives** (`.zip`/`.7z`/`.rar`) are extracted to a temp directory first (SharpCompress, with a bundled 7-Zip fallback — `7za` on Windows, `7zz` on Linux/macOS — for archives the built-in extractor cannot read), then each supported file inside is converted. Multi-part RAR sets (`.partNN.rar`, `.001` volumes) are decoded from their first volume; only that volume is offered as an input. Cue/GDI/TOC entries whose referenced data files are missing are skipped with a warning.
2. **`.cso`** is decompressed to a temp ISO (CSOSharp), then converted.
3. **`.pbp`** is extracted to CUE/BIN (PBPSharp), then converted. Files without a PlayStation disc image (PSP homebrew, corrupt variants) are skipped with an informational message.
4. **`.ccd`** is converted to CUE/BIN (CCDSharp), then converted.
5. **Everything else** (`.cue`, `.gdi`, `.toc`, `.iso`, `.img`, `.raw`, `.avi`) goes straight to the encoder — `chdman` on Windows, the built-in CHDSharp encoder on Linux/macOS — after cue normalization when applicable and a dependent-file check. On Windows a missing or failing `chdman` also routes the file to the built-in encoder. A **laserdisc AVI** (`.avi`) is encoded with `createld` (the A/V `avhu` codec, one frame per hunk); this is how Daphne/laserdisc dumps are stored.

Each file's encoder mode is chosen automatically (see [Technical Logic](01-overview.md#13-technical-logic-command-selection)) unless Force CD/DVD is set.

### Advanced behaviors you may observe in the log

- **"Retrying with createdvd (unrecognized track type)"** — a `createcd` attempt failed because chdman did not recognize the track type; the app automatically retries with `createdvd`.
- **"chdman exited with code N but produced a valid output file"** — a non-zero exit that still produced a non-empty output CHD is treated as success.
- **"Falling back to the built-in CHDSharp encoder..."** and **"CHDSHARP: createcd game.cue"** — Windows: chdman failed or is missing, so the in-process CHDSharp encoder is encoding the file instead. This is routine and conversion continues automatically.
- **"CHDSHARP: Compressing, N% complete... (ratio=...)"** — progress from the built-in encoder, logged every 10% (chdman progress is shown as `[CHDMAN]` lines).
- **"Prepared self-contained cue set ..."** — the cue was normalized (BOM, encoding, zero-padding, MP3 tracks) into a work directory before conversion.
- **"Falling back to system temp"** — the preferred temp drive was not writable; the system temp is used instead.
- **"TIMEOUT: Conversion ... exceeded N minute(s). Marking as failed."** — the per-file timeout fired.

---

## 4.2 Verify CHD Files

### Workflow

1. **CHD Files** — select the folder containing `.chd` files.
2. Enable **Search subfolders** if needed.
3. Optionally enable **Move successful to 'Success' folder**, **Move failed to 'Failed' folder**, and/or **Write a checksum report (.checksums.txt) next to each verified CHD**.
4. Click **Start Verification**.

### Details

- Verification is fully local: it uses the **CHDSharp** library (`Chd.CheckFile`) to check structural integrity and checksums — no `chdman` process is launched.
- Success lines show the CHD version and SHA-1, e.g. `V5 — SHA1: 1f2e3d...`.
- **Checksum report**: when enabled, each verified CHD gets a `<name>.checksums.txt` next to it (following the file into the Success folder) with the whole-image SHA-1, CRC-32 and XXH3-64 plus SHA-1, CRC-32 and XXH3-64 for every track of a CD/GD-ROM image. The whole-image hashes cover the decompressed image (the CHD header's combined SHA-1 is not a content hash, so it is not reused). The hashing pass logs `CHDSHARP: Hashing, N% complete...` and a failure to write the report is a warning, never a verification failure.
- Moved files land in `inputFolder\Success` and `inputFolder\Failed` (created automatically). These folders are excluded from subsequent recursive scans.
- With subfolder search, the relative directory structure is preserved under `Success`/`Failed`.
- Moving uses retry-with-backoff so transient locks (antivirus/indexer) don't fail the move; a persistent failure is logged and reported but does **not** abort the batch.

---

## 4.3 Extract CHD Files

### Workflow

1. **CHD Files** — select the folder containing `.chd` files.
2. **Output Folder** — where the extracted files go (structure is preserved when searching subfolders).
3. Choose the output format.
4. Click **Start Extraction**.

### Output format

| Choice | Command | Output |
|--------|---------|--------|
| **Auto** (default) | Detected from CHD flags | `.cue`, `.iso`, `.gdi`, `.img`, or `.avi` |
| **CD (.cue)** | `extractcd` | `.cue` + track `.bin` files |
| **DVD (.iso)** | `extractdvd` | single `.iso` |
| **GDI (.gdi)** | `extractcd` | `.gdi` + track `.bin` files |
| **HDD (.img)** | `extracthd` | single `.img` |
| **Laserdisc (.avi)** | `extractld` | single `.avi` (A/V CHD) |

Auto-detection reads the CHD flags (via CHDSharp): DVD → `.iso`, hard disk → `.img`, CD/GD-ROM → `.cue` (or `.gdi` when the metadata says `gd-rom`), and a CHD with no CD/DVD/HDD metadata is treated as an **A/V (laserdisc)** image and extracted to `.avi`.

### Notes

- Multi-track (CD/GDI) extraction writes into a `_extract_temp_<guid>` directory inside the target folder, then moves the files out; on success the temp dir is removed, on failure it is kept and a warning tells you how many files remain.
- A/V (laserdisc) CHDs are extracted **in-process** by CHDSharp (`ExtractLaserDisc`, MAME-parity AVI output) — chdman is only a fallback.
- Corrupt CHD files fail fast with the CHDSharp error message ("Not a valid CHD file", "Invalid or corrupt data", "Cannot open file", …) and the batch continues.
- **Delete original CHD after a successful extraction** removes the source CHD only on success.
- Cancellation deletes partially extracted single-file (DVD/HDD/laserdisc) outputs.
- Extraction enables CHDSharp read-ahead (16 hunks) so sequential decompression overlaps the disk writes.

---

## 4.4 Explorer

1. **CHD File** — pick a `.chd` image.
2. Choose the **File System** parser matching the console/system (PlayStation (Auto) is the default and parses ISO 9660 on both CD and DVD images).
3. Browse folders in the grid; double-click a folder to enter it, a file to extract and open it, or use **Extract...** to save the selected file/folder.

**Image Info** (expander under the parser drop-down) shows a read-only report built from the CHD header and map: version, hunk/unit sizes, logical size, image type, codecs, metadata tags, the CD/GD-ROM track table, and the per-codec hunk distribution (the scan is capped at the first 1,000,000 hunks for very large images).

---

## 4.5 Screenshot Hotkey (F8)

While the application window is focused, pressing **F8** captures that window and saves it as `screenshot_yyyy-MM-dd_HH-mm-ss-fff.png` in `%LocalAppData%\CHDStudio\screenshots`. When AppData is not writable, the capture falls back to a `screenshots` folder next to the executable.

The path is shown in the log ("Screenshot saved: ..."). Capture uses Avalonia's `RenderTargetBitmap`; if the capture fails, a message is logged instead.

## 4.6 Status Bar & Stats

- **Status bar**: current operation message + the CHDSharp and CHDMAN encoder indicators. CHDSharp is always green (built-in, always available). CHDMAN is green when found, red when missing on Windows (conversions then use the built-in encoder) and gray on Linux/macOS, where it is optional and never used for encoding.
- **Stat cards**: TOTAL FILES, SUCCESS, FAILED, ELAPSED, SPEED (disk write/read MB/s, sampled while an operation runs; for a chdman conversion the speed is read from the `chdman` child process, and for the in-process CHDSharp encoder from the app itself).
- **Progress bar**: per-batch progress with a **Cancel** button that stops the current operation (cancelling chdman kills the process and cleans up temp files).
