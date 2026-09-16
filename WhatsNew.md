# What's New

## 3.7.1 (unreleased)

### MDS descriptors find renamed and nested data files (#66955, #66989)

*   **A `.mdf` no longer has to sit beside its `.mds` under the exact same name.** The descriptor's data file is now found when it was renamed with a decoration (`Game.mds` beside `Game (USA).mdf` — but never when the name continues with a letter or digit, so `Game 2` never matches `Game`), when it sits one folder down (only an unambiguous exact-name match, so a sibling game's image is never picked up), and split `.i00` sets are located by base name even when other sets share the folder. File names that differ only in Unicode composition (`Cafe\u0301` vs `Café`) are treated as equal.
*   **Ambiguity is refused, not guessed**: with `Game (Disc 1).mdf` and `Game (Disc 2).mdf` both beside `Game.mds`, the disc is skipped with the existing "data file was not found" message instead of converting the wrong disc.

### Transient network/NAS failures no longer abort a batch (#66854)

*   **Archive copies retry transient I/O errors** before the temp-copy fallback runs: a failed direct extraction (for example an SMB hiccup while streaming from a NAS) is demoted to a debug notice, and the automatic copy that precedes the fallback retries up to four times with increasing delays. Missing-source errors are never retried.
*   **Network-unavailability detection is locale-independent**: the Win32 error codes (bad netpath, unexpected network error, netname deleted, …) are now read from the exception's `HResult`, so non-English Windows builds (e.g. French `"Erreur réseau inattendue."`) are recognized without relying on localized message text.

### Startup crash suppressed: desktop composition disabled (#67014, #67084)

*   A user with desktop composition disabled (registry/DWM tweak or third-party theming tool) got a `COMException 0x80263001` from `WindowChromeWorker.DwmExtendFrameIntoClientArea` reported as an unhandled dispatcher exception. It is now suppressed like the other known-benign WPF-internal exceptions: the window simply runs without the glass frame effect.

### Bug-report noise reduction (#67027)

*   **"The output folder is not writable"** (root of `C:\`, `Program Files`, a read-only drive) is excluded from automatic reports — the batch-start probe already shows one actionable dialog and nothing was converted.

### Housekeeping

*   Library updates: Meziantou.Analyzer 3.0.259, Microsoft.NET.Test.Sdk 18.10.1.
*   Test suite grew to **842 tests** (decorated/ambiguous/subdirectory/split/Unicode `.mdf` lookup, transient network copy retries, SFO size bounds, the new bug-report exclusion).

---

## 3.7.0 (2026-09-09)

### Split archive volume sets now convert end to end

*   **Extract split 7-Zip and ZIP sets in-app** (`.7z.001`/`.zip.001` style): what used to be skipped with "extract the set manually" is now extracted with the bundled `7za.exe` and converted like any other input. The whole set is located from the first volume, disk space is checked against the *total* size of all volumes, and the extracted image is classified (cue/gdi/toc convert directly, bare images get the same recovery path as joined sets). Multi-part **RAR** still needs manual extraction, since the app does not carry RAR tooling.
*   Split-set skip notices (missing volume, no convertible image inside, missing `7za`) are user-data conditions and are excluded from automatic bug reports.

### Output folder convenience and resilience

*   **Output folder auto-fill**: selecting a source folder (browse or command line) now sets the **Output CHD** destination to the same folder when the output path is still empty. This is safe by construction — outputs are staged `<name>.chdtmp` and moved into place only on success, and existing CHDs are never inputs.
*   **Disconnected output drive detected up front**: a batch whose output folder sits on an unplugged USB stick or a dropped network drive now probes the folder once and reports a single actionable message, instead of failing every file with a confusing path error mid-batch.
*   **Temp-folder cleanup is best effort**: staged files carrying the read-only attribute (or locked by antivirus) no longer abort the batch or get reported as app bugs — attributes are cleared before deleting, directory read-only flags included, and a leftover temp folder is only logged as a warning.

### PBP extraction — PBPSharp 1.1.0

The bundled PBP library was aligned with all four reference implementations (popstation, PSX2PSP, iPoPS, pop-fe), so PBP files that earlier versions rejected now extract:

*   **pop-fe multi-disc PBPs no longer rejected**: pop-fe leaves the four "template" DWORDs of the `PSTITLEIMG000000` header zero (popstation/PSX2PSP write fixed magic values there). The header values are no longer validated — the disc position table, which is what actually locates the discs, is honoured regardless.
*   **PARAM.SFO is no longer required**: a missing or corrupt SFO leaves title/disc-id metadata empty instead of rejecting the whole file. None of the reference tools read the SFO when extracting disc images.
*   **zlib-wrapped PSAR blocks decompress**: blocks authored with `zlib.compress` (2-byte header + Adler-32) instead of raw deflate are retried as zlib streams after the raw inflate fails.
*   **pop-fe index layout supported**: the official 16-bit index entry (size `uint16` at bytes 4–5, stored-flag byte at 6, SHA-1 at 8–23) is read directly; tools that write the size as a 32-bit int (popstation/PSX2PSP/iPoPS) are still covered because bytes 6–7 stay zero there.
*   **Stored/uncompressed blocks flagged in the index** (pop-fe `--compression 0`) are copied verbatim instead of being inflated.
*   **Incompressible blocks accepted**: index entries a few bytes *larger* than the raw 16-sector block (deflate framing on incompressible data) are no longer rejected.
*   **64-bit offset math** for block reads, so multi-gigabyte files cannot overflow the 32-bit offset computation.
*   Hardened against corrupt indexes: an entry claiming to be a stored block larger than a full block is reported as corrupt data instead of overrunning the read buffer, and the split-set disk-space check now uses the whole volume set rather than the first volume alone.

### Bug-report filtering additions

*   chdman crashes during the **startup probe** (entry-point-not-found / illegal-instruction exit codes — an installation incompatibility, not app logic) are excluded.
*   "Output folder is not available" notices (unplugged drive) are excluded.

### Housekeeping

*   Version bumps: application 3.7.0, PBPSharp 1.1.0.
*   Library updates: Meziantou.Analyzer 3.0.231, Microsoft.NET.Test.Sdk 18.10.0.
*   Test suite grew to **835 tests** (split-volume-set extraction, PBP index/deflate variants, pop-fe multi-disc, SFO tolerance, bug-report exclusions).

---

## 3.6.0 (2026-09-07)

### Encoder resilience

*   **A batch runs on the CHDSharp fallback when `chdman` is missing or fails its startup probe**, instead of refusing to start. The startup dialog severity now reflects encoder priority (critical only when *both* encoders are unusable).

### PBP extraction fixes

*   **Corrupt deflate streams are classified as data errors**: `IndexOutOfRangeException` leaking from SharpZipLib's inflater is normalized to `PbpError.DecompressionError`, so a garbage PSAR block is reported as corrupt data instead of an app bug (regression-tested).
*   PBPSharp version bumps with the hardened block reader.

### Shutdown & noise

*   **Shutdown cancellation is treated as normal exit**: cancelling the window during startup no longer reports a `TaskCanceledException` bug report.
*   **Bug-report noise reduction**: encoder-missing notices, chdman fallback notices, CHD open/read failures, file-move failures, encoder start failures and low-disk-space warnings are excluded from automatic reports (low disk space is now informational, since the conversion proceeds).
*   **`0xC0000139` (entry point not found) crash decoding** added alongside the existing illegal-instruction mapping.

### Housekeeping

*   Library updates: NAudio 3.1.0, Meziantou.Analyzer 3.0.226; bundled 7-Zip binaries refreshed.
*   Application version bumped to 3.6.0; test suite at 820 tests.

