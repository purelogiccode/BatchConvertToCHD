---
title: Troubleshooting
nav_order: 14
---

# 13. Troubleshooting

Common messages, their meaning, and what to do.

## 13.1 Conversion Errors

| Message (as shown in the log) | Meaning | Action |
|--------------------------------|---------|--------|
| `Failed to convert '<file>': ERROR: couldn't find bin file [<path>]` | chdman cannot find a track file referenced by the cue (missing, wrong name, or on a different drive). The app logs a directory listing to help. | Verify the `.bin` files are next to the cue with matching names. Non-ASCII names/BOMs, wrong names and zero-padding mismatches are normally handled automatically; re-scan the folder. If the bracketed path looks like two paths joined together, see the note below. |
| `Failed to convert '<file>': file size (N bytes) is not divisible by any standard sector size (2048/2324/2336/2352/2368/2448). The file may be corrupt or truncated.` | The image size doesn't match any CD/DVD sector geometry. Raw CD dumps mislabelled as `.iso`/`.img` are detected by content and no longer reach this check, so this now means a genuinely damaged file. | Re-download or re-rip the image; verify with the original disc. |
| `<file> is named as an archive but contains a raw CD disc image; converting it as a CD.` / `... named as a compressed ISZ image but ...` | Informational. The extension disagreed with the content, and the content won. | Nothing to do — the conversion proceeds correctly. |
| `<file>: no writable location on the same volume for a generated cue; converting the image as-is.` | A raw CD image needs a generated cue, and chdman can only reach the image from a cue on the **same volume** — but no writable temp location exists there. | Free space on the image's own drive, or move the image to a drive where a temp folder can be created. |
| `The output folder is inside the source folder, so CHDs will be written alongside the originals.` | Informational. Converting in place is supported. | Nothing to do. An existing CHD of the same name is only replaced after a successful conversion. |
| `Files named after this disc already exist here; extracting into "<name>" so they are kept.` / `<name>.iso already exists here; extracting into "<name>" so the existing file is kept.` | Informational. Extracted output takes the CHD's base name, so it would have replaced files of the same name — most likely when extracting into the folder the CHDs already live in. The disc was written to a subfolder instead. | Nothing to do. The existing files are untouched and the new ones are in the named subfolder; a `.cue`/`.gdi` set there is still valid, because its track references are relative. |
| `this is part 1 of an N-part split image ... A part is missing or truncated` | The `.001`/`.i00` parts do not join to a whole number of sectors. | Re-download the complete set; all parts must be in the same folder. |
| `the image is split across N segments and <name>.i01 is not in the same folder` | A segmented ISZ is missing a piece. | Put every `.isz`/`.i01`/`.i02`… segment together. |
| `segment <name>.i01 belongs to a different ISZ image (volume serial number does not match)` | Segments from two different rips were mixed. | Collect the segments of one image together. |
| `the ISZ image is encrypted (AES-256) and this tool cannot decrypt it.` | The ISZ was saved with a password. | Open it in UltraISO with the password and save it as an ISO first. |
| `the ISZ decompressed to N bytes but its header declares M` / `the ECM file's checksum does not match the data it decoded to` | The compressed file is truncated or damaged. The partial output is deleted rather than converted, because a short image would convert and look fine. | Re-download the file. |
| `the .mdf data file was not found next to the .mds descriptor` | An Alcohol descriptor without its data file. | Keep the `.mds` and `.mdf` together; a lone `.mdf` is skipped because the descriptor drives the conversion. |
| `WARNING: <file> and <other> both convert to <name>.chd` | Two inputs map to the same output name. | Informational, reported at batch start. The first result is no longer destroyed by the second, but only one of them will survive as `<name>.chd` — rename an input if you need both. |

> **On "couldn't find bin file" with a doubled path.** chdman joins a cue's `FILE` entry to the cue's own directory unconditionally, so an absolute `FILE` path produces something like `C:\temp\x\D:\game.iso`. Generated cues therefore always use a relative path and are written on the image's own volume. Seeing a doubled path means a hand-written cue contains an absolute `FILE` line — make it relative to the cue.
| `Failed to convert '<file>': Unit size must be specified if no output parent CHD is supplied` | A `.raw` input was converted without a unit size, or a `.cue` referencing raw audio tracks was passed to chdman without the unit-size flag. | Should not occur in current versions (raw inputs and cues referencing `.raw` tracks both get `-us 2352` automatically); if it does, use a `.cue` descriptor instead. |
| `Failed to convert '<file>': Compressing, 0.0% complete...` | (Old versions) a chdman progress line was shown instead of the real error. | Update the app; the real error line is now selected from the end of chdman's output. |
| `Failed to convert '<file>': Error creating CHD file (...): Unknown error` | chdman could not create the output file (drive issues, permissions, full disk). | Check the output drive is writable, has free space, and the path isn't overlong. |
| `TIMEOUT: Conversion of '<file>' exceeded N minute(s). Marking as failed.` | The per-file time limit fired. | Increase the limit (max 4 hours) or convert fewer/larger files at once. |
| `Failed to convert '<file>': chdman terminated abnormally (exit code -1073741795; 0xC000001D, STATUS_ILLEGAL_INSTRUCTION ...)` | Windows killed chdman before it could print anything. Most often the bundled build uses CPU instructions this computer lacks (older CPUs without SSE4.2/AVX); antivirus quarantine damage produces the same class of crash. | Replace `chdman.exe`/`chdman_arm64.exe` with a build that matches your CPU (e.g. an official MAME tools release) and add an antivirus exclusion for it. |
| `chdman.exe terminated abnormally during the startup check (...)` and a message box | Same crash as above, detected before the batch starts — every conversion would have failed the same way. | Same action as the previous row. |
| `The output folder is not writable: <path>` | Detected before the batch starts: writing there needs administrator rights (e.g. inside `Program Files`). | Choose an output folder you can write to (Documents, a data drive); nothing was converted yet. |
| `chdman.exe cannot be opened - it is held with incompatible access by another process.` | Rare: something holds chdman with sharing that blocks even read access. A second app instance or a normal antivirus scan no longer triggers this. | Close other instances of the app and any antivirus scan in progress, then retry. |
| `<name> is a folder, not a disc image file - skipping.` | Something that looks like an image name (`Game.BIN.ISO`) is actually a directory. Folders are skipped instead of being handed to chdman ("Is a directory"). | Point the app at the files inside it, or rename the folder so it does not end in an image extension. |
| `PBPSharp: Extraction failed - ... TruncatedPsar (code 9)` / `The PlayStation data section has no readable tracks - the file is most likely truncated or incomplete.` | The PBP's PlayStation data area ends before any track index — the signature of a truncated download. | Re-download the `.pbp`. |
| `PBPSharp: Extraction failed - ... DecompressionError (code 8)` | A PSAR block failed to inflate as raw deflate and as a zlib-wrapped stream — the PBP's compressed data is damaged. | Re-download the `.pbp`. |
| A `.pbp` extracts but the log shows no game title / disc ID | The PARAM.SFO inside the PBP is missing or corrupt. Extraction no longer requires the SFO — only the metadata is empty. | Informational; the disc converts anyway. |
| `Retrying with createdvd (unrecognized track type)...` | A CD attempt failed; the app retries as DVD. | Usually succeeds automatically. If it fails again, force CD/DVD manually. |
| `chdman exited with code N but produced a valid output file...` | Non-zero exit but a valid output; treated as success. | Informational — nothing to do. |

## 13.2 Archive Errors

| Message | Meaning | Action |
|---------|---------|--------|
| `... multi-part RAR with a missing volume ...` | A multi-part `.rar` is missing one or more parts. | Download all `.partNN.rar` volumes into the same folder. |
| `... Archive is encrypted ...` | The archive is password-protected. | Password-protected archives are not supported; extract manually first. |
| `... compression method that is not supported ...` | The ZIP uses Deflate64/LZMA/PPMd, which the extractor can't read. | Re-zip with standard Deflate, or extract manually first. |
| `... archive file may be corrupted or incomplete ...` | The archive failed CRC/structure checks. | Re-download the archive. |
| `No supported primary files found in archive.` | The archive contains no convertible image/descriptor (and no bare `.bin`). | Check the archive contents. |
| `... archive file appears to be incomplete ...` / `... could not validate referenced files ...` | Archive entries reference data files that aren't in the archive (split-bin sets, CRC-skipped entries). | Get the complete archive set; the app skips the entry with a warning instead of failing hard. |

## 13.3 CHD Extraction & Verification

| Message | Meaning | Action |
|---------|---------|--------|
| `Failed to open '<file>.chd': Not a valid CHD file` | The file isn't a CHD (bad magic). | The file is corrupt or misnamed; re-acquire it. |
| `Failed to open '<file>.chd': Invalid or corrupt data` | CHD structure is broken. | Re-acquire the file; verify it with `chdman verify`. |
| `Failed to open '<file>.chd': Cannot open file` | The file is locked/unreadable. | Close any program holding the file (emulator, antivirus scan) and retry. |
| `Partial extraction: N file(s) remain in temp directory: <dir>` | A multi-track extraction failed partway; the temp dir is kept for inspection. | Check the listed `_extract_temp_*` folder, delete leftovers, and retry with a valid CHD. |
| `Failed to move file <path>: The process cannot access the file because it is being used by another process` | The move was blocked by a lock. | Current versions retry for ~45 s; if it still fails, close file-holding programs and retry. |

## 13.4 Environment & Startup

| Message | Meaning | Action |
|---------|---------|--------|
| `chdman.exe not found at '<path>'` | chdman is missing or was moved. | Keep `chdman.exe`/`chdman_arm64.exe` in the app folder. |
| `chdman.exe is not compatible with this OS.` (Win32 error 193) | The exe cannot run on this Windows, or files from the win-arm64 release were copied into a win-x64 install (or vice versa). | Keep the two releases separate; use a chdman build for your Windows version. |
| Startup log `Process Architecture:` / `OS Architecture:` / `chdman executable:` lines | Informational: which build is running, what the machine is, and which tool binary was resolved. On ARM64 machines the native build is preferred even when the app itself runs emulated as x64. | Include these when reporting a crash — they make the report instantly classifiable. |
| Status bar CHDMAN indicator red | Same as above. | See previous row. |
| `Selected temp root "X:\" is not writable, falling back to system temp` | The preferred temp drive can't be written (e.g. `E:\` is a card reader / locked). | Informational; the app uses the system temp instead. Free space on `C:` matters then. |
| `Another instance of BatchConvertToCHD is already running.` | Single-instance mutex. | The first instance is still running; close it first. |
| `Update check skipped: GitHub API rate limit exceeded.` | GitHub API 403/429 (shared IP). | Wait and restart; no action needed. |
| `Failed to record usage statistics: HTTP 429` | Stats endpoint rate-limited. | Expected; silently ignored (Debug log only). |

## 13.5 Data & Safety Questions

**Are my originals deleted automatically?** Only when **"Delete originals after a successful conversion"** is enabled, and only after the CHD was produced successfully. Cue-set deletions also remove referenced `.bin`/`.sub` files; CCD deletions remove `.img`/`.sub`/`.cdt`.

**What happens to temp files on crash?** Leftover `BatchConvertToCHD_Temp_*` folders are deleted at next startup.

**Where are the logs?** `%LocalAppData%\BatchConvertToCHD\logs` (daily files, 7 days retained). Click the **AppData** button in the title bar.

**Does the app phone home?** It sends: anonymous usage stats (application name + version, once per launch), bug reports for warning-level events (see [Bug Reporting System](09-bug-reporting.md)), and GitHub update checks. No personal data is collected (the bug report includes the Windows user name as `userInfo`).

**Why do some bugs keep showing the same message?** Corrupt input files (bad CHDs, incomplete archives) are user-data conditions — the app now excludes those messages from bug reports; the in-app log remains the source of truth for them.
