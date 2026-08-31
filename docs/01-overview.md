---
title: Overview
nav_order: 2
---

# 1. Project Overview

**Batch Convert to CHD** is a high-performance Windows desktop utility designed to streamline the conversion of various disk image formats into the **Compressed Hunks of Data (CHD)** format — the format used by MAME, and increasingly by emulation frontends for PlayStation, Dreamcast, and other systems.

Developed by [Pure Logic Code](https://www.purelogiccode.com), the application combines a modern WPF-UI dashboard with battle-tested MAME tooling (`chdman`) and pure-C# libraries (CHDSharp, CCDSharp, CSOSharp, PBPSharp) for a fully local, offline-capable conversion experience.

---

## 1.1 Key Features

### Modern Side-by-Side Dashboard
- **Dual-pane interface** — settings and file list on the left, real-time terminal-style log on the right.
- **Interactive file selection** — automatically scans folders; the user picks exactly which files to process via a detailed list with checkboxes.
- **Chunked file loading** — directory scans with thousands of files are loaded in chunks of 100 items at background priority to keep the UI responsive (`MainWindow.xaml.cs:820–975`).
- **Resizable layout** — built-in grid splitter between file explorer and log view.

### Multi-Architecture Support
- **Native x64 & ARM64** — `AppConfig.IsArm64` selects `chdman_arm64.exe`/`7za_arm64.exe` on ARM64 hardware, `chdman.exe`/`7za.exe` elsewhere (`AppConfig.cs:17–29`).

### Intelligent Conversion & Extraction
- **Automated batch processing** — convert entire directories with real-time progress, immediate cancellation, and per-file timeouts.
- **Recursive structure preservation** — the output folder mirrors the input folder's directory hierarchy (`PathUtils.GetSafeRelativePath`).
- **Robust extraction** — CHD → `.cue` (CD), `.iso` (DVD), `.gdi` (Dreamcast/Naomi), `.img` (HDD), with automatic metadata-based command detection via CHDSharp.
- **Archive integration** — `.zip`, `.7z`, `.rar` are extracted and processed transparently (SharpCompress, with a `7za.exe` fallback).
- **CloneCD support** — `.ccd` sets are parsed by CCDSharp and converted via an auto-generated CUE/BIN.
- **CSO decompression** — `.cso`/`.ciso` via CSOSharp (deflate/zlib and LZ4).
- **PBP extraction** — PlayStation `.pbp` via PBPSharp; PSP-homebrew-style files (no PlayStation disc image) are detected and skipped with a clear message.
- **Smart CUE normalization** — encoding detection (UTF-8, Shift-JIS, Korean CP949, Cyrillic CP1251, Latin-1, …), UTF-8 BOM stripping, case-insensitive and zero-padding-tolerant reference resolution, canonicalization into a self-contained work directory.
- **Archive dependency validation** — cue/GDI/TOC entries extracted from archives are validated up front; entries with missing referenced files are skipped with a warning instead of failing inside chdman.
- **MP3 audio track support** — cue/MP3 sets are decoded to chdman-compatible WAV (44.1 kHz, 16-bit, stereo) automatically, with a built-in decoder fallback.
- **bin-only archives** — archives containing only `.bin` files get an auto-generated MODE2/2352 cue (with MODE1/2352 fallback) and convert automatically.

### Content-Based Format Detection
A file's extension is the least reliable thing about it. Every input's leading bytes are inspected **before** the extension is trusted (`MainWindow.TryResolveByContentAsync`), which turns several "corrupt file" failures into successful conversions.

- **Raw CD dumps with the wrong name** — a 2352-bytes-per-sector CD dump saved as `.iso`, `.img`, `.bin` or `.isz` is recognised from its sector sync mark and mode byte and converted as the CD it is, with a generated cue. Previously these went to `createdvd`/`createhd` and failed on `Data size ... is not divisible by sector size`.
- **Disc images wearing an archive extension** — a `.rar`/`.zip` that is really a plain disc image, or a byte-split set that was never an archive, is detected and converted instead of being reported as corrupt.
- **Bare `.bin` files** — accepted as a standalone input with a generated cue. When a sibling `.cue`/`.ccd`/`.mds` already covers the `.bin`, it is dropped from the batch so the disc converts once, through its descriptor (`InputFileFilter`).
- **Honest reporting** — a truncated download, a mislabelled file and a genuinely unsupported format each read differently in the log, naming what was found and what to do about it.

### Awkward Format Support
- **Alcohol 120%** — `.mds`/`.mdf` sets convert directly. The descriptor's track table is parsed into a matching cue; images storing 2448 or 2368 bytes per sector have their subchannel tail stripped first (chdman cannot read those); a `.mdf` that is really an ISO converts as a DVD image.
- **ISZ decompression** — UltraISO `.isz` images are decompressed in-process (zlib, bzip2, stored and all-zero chunks), including images split across `.i01`, `.i02` and further segments. Segments are matched by volume serial number, a missing one is named, and an encrypted image says so.
- **ECM decoding** — `.ecm` files are decoded in-process, with no external tool to install. The per-sector EDC and Reed-Solomon parity that ECM discards are regenerated, verified byte for byte against Neill Corlett's reference implementation.
- **Split volume sets** — images split into `.001`/`.002` or `.i00`/`.i01` pieces are rejoined before conversion; a set with a missing part is reported rather than handed to chdman half-complete. Only the first volume appears in the file list.
- **Split-track discs** — a `(Track 1)`, `(Track 2)`, … bin set gets a multi-track cue, so discs with CDDA keep their audio instead of converting as a single data track.
- **Broken cue descriptors** — a `FILE` line naming something that is not there is resolved against what is actually on disk: by name beside the cue, by extension swap, and for a single-FILE cue by elimination. Audio tracks and multi-FILE cues are left alone, because guessing there could silently drop a track.

### Integrity, Safety & Verification
- **A good CHD is never destroyed** — conversions are written to a `.chdtmp` staging file and moved into place only on success. chdman runs with `-f` and truncates its output before it can fail, so without staging a second input mapping to the same output name could wipe out a working CHD produced by the first.
- **Output collision warnings** — the output name comes from the input's base name, so `Game.cue`, `Game.zip` and `Game.ccd` in one folder all target `Game.chd`. Colliding inputs are reported at the start of the batch, before time is spent on them.
- **In-place conversion and extraction** — the output folder may be the same as the source folder, or inside it. Conversion is inherently safe there (the output is always `<base>.chd`, which is never an input, and it is staged before replacing anything). Extraction takes the CHD's base name, so when its output would land on existing files the whole disc is diverted into a subfolder named after it instead. Nothing is overwritten, nothing is asked, and the layout only changes for the discs that actually clash.
- **Disk-space preflight** — free space on the output drive is checked immediately before chdman starts; clearly insufficient space skips the file with both figures named instead of failing an hour in.
- **Output-folder preflight** — the destination is probed for write access before a batch starts; an unwritable folder (e.g. inside `Program Files` without elevation) produces one clear message instead of a run of per-file "Permission denied" failures.
- **chdman-safe path handling** — non-ASCII characters anywhere along a path (`C:\Users\Kauê Chacon\...`, `D:\Emulátory\...`) and paths at or beyond MAX_PATH are routed through short ASCII staging directories, because older chdman builds mangle or cannot open such paths. Cue work directories avoid a non-ASCII system temp folder the same way.
- **Crash-aware error reporting** — when Windows kills chdman outright (e.g. exit code `0xC000001D` on a CPU lacking the build's instruction sets), the code is decoded into plain language with guidance, and a startup check refuses to begin a batch that would fail on every file. Startup logs record the process and OS architectures and which tool binary was selected.
- **Safe deletion** — source files (and dependencies such as `.bin`, `.sub`) are only deleted after confirmed success.
- **Batch verification** — checksums and structural integrity of existing CHD files via CHDSharp.
- **Automated organization** — optionally move verified/failed files into `Success`/`Failed` subfolders; these folders are excluded from subsequent scans.
- **Empty-folder cleanup** — empty subdirectories are removed after files are moved or deleted.
- **Dependency check at startup** — the user is notified if `chdman.exe` is missing.
- **File-system monitoring** — the input folder is watched during batch processing to explain why a file went missing mid-operation.
- **Corrupt-image early warning** — ISO sizes that don't match any standard sector layout are flagged before conversion.
- **Resilient file operations** — deletions and moves retry with backoff (~45 s) against transient locks (antivirus, indexer) and clear read-only attributes when needed.

### Performance & UI
- **Real-time telemetry** — disk write/read speeds and elapsed time during operations.
- **High-performance logging** — Serilog with UI log truncation at 100,000 characters.
- **WPF-UI theming** — dark Fluent theme with a static dark background and rounded corners on Windows 11.

### Updates & Stability
- **Automatic update checks** — GitHub releases are checked at startup; the user is offered the download page.
- **Automated bug reporting** — warning-and-above log events are forwarded to the PureLogicCode BugReport API (see [Bug Reporting System](09-bug-reporting.md)).

---

## 1.2 Supported Formats

| Category | Formats |
|----------|---------|
| **Standard images** | `.iso`, `.cue` (+`.bin`), `.img`, `.ccd` (+`.img`), `.raw`, `.toc`, bare `.bin` |
| **Console-specific** | `.gdi` (Dreamcast), `.pbp` (PlayStation) |
| **Compressed** | `.cso` (Compressed ISO), `.isz` (UltraISO), `.ecm` (Error Code Modeler) |
| **Alcohol 120%** | `.mds` (+`.mdf`), including 2448-byte subchannel sectors |
| **Split sets** | `.001`/`.002`…, `.i00`/`.i01`… (add the first volume; the rest are found) |
| **Archives** | `.zip`, `.7z`, `.rar` |
| **Output** | `.chd` |

The full input set is defined in `FileExtensions.AllSupportedInputExtensionsForConversion`: `.cue`, `.iso`, `.img`, `.gdi`, `.toc`, `.raw`, `.ccd`, `.bin`, `.mds`, `.ecm`, `.isz`, `.001`, `.i00`, `.zip`, `.7z`, `.rar`, `.cso`, `.pbp`. All extension checks are case-insensitive.

Only the descriptor or first volume of a multi-file set is listed for conversion: the `.mdf` behind a `.mds`, the `.bin` behind a `.cue`, and the later parts of a split set are found automatically, so each disc converts once. Every format above is handled in-process — apart from the bundled `chdman` and `7za` there is nothing else to install, and x64 and ARM64 get the same feature set.

---

## 1.3 Technical Logic (Command Selection)

Content is inspected first; the extension only decides the outcome for files whose content did not settle it.

1. **Content inspection** — the leading bytes are read. A raw CD image, an Alcohol descriptor, an ISZ, an ECM, an archive or an existing CHD is routed on what it *is*, whatever it is called. A raw CD image gets a generated cue and goes to `createcd`.
2. **Split volume sets** — numbered pieces are rejoined into one image, which is then classified as above.
3. **Compressed containers** — `.isz`, `.ecm` and `.cso` are decompressed in-process and the restored image is classified as above.
4. **Descriptors** — `.cue`/`.gdi`/`.toc` → `createcd` after cue normalization. `.ccd` becomes a cue via CCDSharp, `.mds` via the Alcohol parser, `.pbp` is extracted to CUE/BIN via PBPSharp.
5. **`.iso` (DVD images)** → `createdvd`, once content inspection has ruled out a mislabelled raw CD dump
6. **`.img` (hard disk images)** → `createhd`, unless an accompanying `.cue` exists → `createcd`
7. **`.raw` (raw data)** → `createraw` (with an explicit unit size `-us 2352`). Cue descriptors referencing `.raw` audio tracks also receive `-us 2352` automatically.

The user can override 5–7 via **Force CD** / **Force DVD** checkboxes. PBP always extracts first.

Generated cue sheets reference the disc image where it already lies rather than copying it, because chdman resolves a cue's `FILE` entry against the cue's own directory. That also means such a cue must be written on the **same volume** as the image: chdman joins the `FILE` string to the cue's directory unconditionally, so an absolute path becomes `C:\temp\D:\game.iso` and fails.

---

## 1.4 Project History Highlights

- Migrated from external `chdman`-based verification to the pure C# **CHDSharp** library.
- Replaced `maxcso` and `psxpackager` executables with the in-house **CSOSharp** and **PBPSharp** libraries.
- Added CloneCD support via **CCDSharp**.
- Introduced CUE normalization, MP3 decoding, archive dependency validation, and a file watcher for missing-file diagnostics.
- Added content-based format detection, so inputs are routed by their leading bytes rather than their extension.
- Added Alcohol 120% (`.mds`/`.mdf`), split volume sets, in-process ISZ decompression and in-process ECM decoding, removing the last external-tool dependency.
- Made conversion output non-destructive by staging to `.chdtmp` and moving into place only on success.
- Added raw-audio-track detection in cue files, auto-applying `-us 2352` to chdman arguments.
- Added retry logic to CloneCD bin-file copies and overflow-safe arithmetic in PBP TOC parsing.
- Normalised endianness across CSO/PBP parsers (replaced endianness-dependent `BitConverter` with explicit `BinaryPrimitives.ReadUInt32LittleEndian`).
