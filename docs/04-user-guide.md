---
title: User Guide
nav_order: 5
---

# 4. User Guide

The main window has three tabs: **Convert to CHD**, **Verify CHD Files**, and **Extract CHD Files**. A terminal-style log view sits on the right, stat cards and the progress bar at the bottom, and a status bar with the CHDMAN dependency indicator at the very bottom.

Title-bar buttons: **About** (info dialog), **AppData** (opens `%LocalAppData%\BatchConvertToCHD`), **Exit**.

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

1. **Archives** (`.zip`/`.7z`/`.rar`) are extracted to a temp directory first (SharpCompress, with a `7za.exe` fallback for zip/7z), then each supported file inside is converted. Multi-part RAR sets (`.partNN.rar`, `.001` volumes) are decoded from their first volume; only that volume is offered as an input. Cue/GDI/TOC entries whose referenced data files are missing are skipped with a warning.
2. **`.cso`** is decompressed to a temp ISO (CSOSharp), then converted.
3. **`.pbp`** is extracted to CUE/BIN (PBPSharp), then converted. Files without a PlayStation disc image (PSP homebrew, corrupt variants) are skipped with an informational message.
4. **`.ccd`** is converted to CUE/BIN (CCDSharp), then converted.
5. **Everything else** (`.cue`, `.gdi`, `.toc`, `.iso`, `.img`, `.raw`) is handed directly to `chdman` — after cue normalization when applicable and a dependent-file check.

Each file's `chdman` command is chosen automatically (see [Technical Logic](01-overview.md#13-technical-logic-command-selection)) unless Force CD/DVD is set.

### Advanced behaviors you may observe in the log

- **"Retrying with createdvd (unrecognized track type)"** — a `createcd` attempt failed because chdman did not recognize the track type; the app automatically retries with `createdvd`.
- **"chdman exited with code N but produced a valid output file"** — a non-zero exit that still produced a non-empty output CHD is treated as success.
- **"Prepared self-contained cue set ..."** — the cue was normalized (BOM, encoding, zero-padding, MP3 tracks) into a work directory before conversion.
- **"Falling back to system temp"** — the preferred temp drive was not writable; the system temp is used instead.
- **"TIMEOUT: Conversion ... exceeded N minute(s). Marking as failed."** — the per-file timeout fired.

---

## 4.2 Verify CHD Files

### Workflow

1. **CHD Files** — select the folder containing `.chd` files.
2. Enable **Search subfolders** if needed.
3. Optionally enable **Move successful to 'Success' folder** and/or **Move failed to 'Failed' folder**.
4. Click **Start Verification**.

### Details

- Verification is fully local: it uses the **CHDSharp** library (`Chd.CheckFile`) to check structural integrity and checksums — no `chdman` process is launched.
- Success lines show the CHD version and SHA-1, e.g. `V5 — SHA1: 1f2e3d...`.
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
| **Auto** (default) | Detected from CHD metadata | `.cue`, `.iso`, `.gdi`, or `.img` |
| **CD (.cue)** | `extractcd` | `.cue` + track `.bin` files |
| **DVD (.iso)** | `extractdvd` | single `.iso` |
| **GDI (.gdi)** | `extractcd` | `.gdi` + track `.bin` files |
| **HDD (.img)** | `extracthd` | single `.img` |

Auto-detection scans the CHD metadata (via CHDSharp): `dvd` → DVD, `gd-rom` → CD/GDI, `hard disk`/`hdd` → HDD. When the result is a CD and the metadata contains `gd-rom`, the output extension becomes `.gdi` instead of `.cue`.

### Notes

- Multi-track (CD/GDI) extraction writes into a `_extract_temp_<guid>` directory inside the target folder, then moves the files out; on success the temp dir is removed, on failure it is kept and a warning tells you how many files remain.
- Corrupt CHD files fail fast with the CHDSharp error message ("Not a valid CHD file", "Invalid or corrupt data", "Cannot open file", …) and the batch continues.
- **Delete original CHD after a successful extraction** removes the source CHD only on success.
- Cancellation deletes partially extracted single-file (DVD/HDD) outputs.

---

## 4.4 Global Hotkey — Screenshot (F8)

Pressing **F8** anywhere (the hotkey is registered globally via `RegisterHotKey`) captures the current foreground window and saves it as

```
%LocalAppData%\BatchConvertToCHD\screenshots\screenshot_yyyy-MM-dd_HH-mm-ss-fff.png
```

The path is shown in the log ("Screenshot saved: ..."). Capture uses GDI `BitBlt`; if no foreground window exists, a message is logged instead.

## 4.5 Status Bar & Stats

- **Status bar**: current operation message + the CHDMAN dependency indicator (green = available, red = missing).
- **Stat cards**: TOTAL FILES, SUCCESS, FAILED, ELAPSED, SPEED (disk write/read MB/s, sampled via performance counters while an operation runs).
- **Progress bar**: per-batch progress with a **Cancel** button that stops the current operation (cancelling chdman kills the process and cleans up temp files).
