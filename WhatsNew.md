# What's New

## 3.8.0 (2026-09-19)

### Multi-part RAR archives now convert end to end (#67305)

*   **A `.partNN.rar` set is decoded from its first volume**, whether the batch lists the first part or a later one. SharpCompress can only follow the whole set when it is opened by path through the first volume, so a later part is redirected to the first volume found beside it; when the first volume is missing the file is skipped with a message naming it instead of failing inside the decoder. This supersedes the 3.7.0 note that multi-part RAR still needed manual extraction.
*   **RAR sets renamed to `.001` are extracted by content** instead of being refused, because the extension hides what the content says.
*   **A set is offered once**: only the first volume stays in the batch, filtered both at the folder scan and again before the batch starts, so a multi-part RAR is no longer extracted once per volume.
*   **Malformed archives are classified as data, not app bugs**: SharpCompress decoder crashes (`NullReferenceException`, `ArgumentOutOfRangeException`, `IndexOutOfRangeException`) on corrupt data no longer trigger the temp-copy retry or an automatic bug report.
*   Volume discovery covers new-style `.partNN.rar`, numbered `.001` sets and old-style `.rar` + `.r00` volumes, and the disk-space preflight measures the whole set. A real WinRAR-built multi-volume fixture is committed so the path is tested without WinRAR at test time.

### Alcohol 120% support upgraded — MDSSharp 1.1.0

*   **The library was renamed `Alcohol120Sharp` → `MDSSharp`** and published as **MDSSharp 1.1.0** (<https://www.nuget.org/packages/MDSSharp>) with package metadata, a README and an icon matching the other embedded libraries.
*   **Pregaps are now described**: when the descriptor records pregap and track length, the generated cue carries `INDEX 00` if the data file already contains the pregap sectors, and pregaps the file does not contain are rebuilt as zeros so the cue can still express them without shifting the tracks that follow.
*   **Multi-file descriptors are joined** in track order before preparation, and the temp-space preflight accounts for the rebuilt or joined image.
*   **The descriptor is read more completely**: medium type (CD/CD-R/CD-RW/DVD/DVD-R), each track's extra block (pregap and length) and the footer data-file names (single-byte or UTF-16, `*.mdf` wildcards) are parsed, and the track mode now uses the low nibble of the mode byte exactly as libMirage's reverse engineering established.
*   A CD descriptor whose sectors are the cooked 2048 bytes is converted through a `MODE1/2048` cue instead of being treated as a DVD image.

### ISZ support upgraded, library renamed to ISZSharp

*   **Genuine UltraISO files now decode.** The ISZ library was checked against libMirage's ISZ filter and isz-tool, the two independent open-source readers, and now handles the real-file behaviours the published specification omits: the segment and chunk tables are de-obfuscated (XOR with `B6 8C A5 DE`, the complement of `IsZ!`) and bzip2 chunks get their stripped `BZh` header restored before decompression. Without those, a real `.isz` could not be decoded at all — the old test fixtures mirrored the same omission, so the suite passed while the reader could not open a genuine file.
*   **More layouts are read**: images whose header declares no chunk table (one raw run), and split images named `game.part01.isz`/`game.part02.isz` as well as the spec's `game.i01`/`game.i02`.
*   **UltraISO's checksum is validated** when the 64-byte header carries one; a mismatch is reported as damaged and the output deleted, like a size shortfall. A failed or cancelled decode now always deletes its partial output.
*   **A split image whose cut lands inside a chunk now decodes.** A later segment stores the tail of the straddling chunk (`left_size`) between its header and its chunk data; the reader started at the chunk data offset and skipped that tail, so a complete split image was rejected as truncated. Fixed in **ISZSharp 1.0.1**, and the split fixtures now use UltraISO's real segment layout, so the round-trip tests exercise it.
*   **The library now matches the other embedded packages' shape**: renamed `UltraIsoSharp` → `ISZSharp`, multi-targeting `net8.0;net9.0;net10.0`, shipping XML docs, a README and an icon, and published on NuGet (<https://www.nuget.org/packages/ISZSharp>) as **1.0.0**, then **1.0.1** with the split-boundary fix.

### Smaller fixes

*   Deleting originals for an Alcohol image now removes every data file the descriptor names and every volume of a split set, not just the first.
*   A multi-file `.mds` descriptor with one declared file missing is reported by name instead of silently falling back to the file that happens to be present, which could convert a truncated image.
*   A folder holding both `.part1.rar` and `.part01.rar` keeps the spelling whose next volume exists, so the set is still decodable.

### Housekeeping

*   Version bumps: application 3.8.0, MDSSharp 1.1.0, ISZSharp 1.0.1.
*   Library updates: Meziantou.Analyzer 3.0.264.
*   Test suite grew to **896 tests** (real multi-part RAR extraction, ISZ real-layout splits and checksums, MDS pregap/multi-file handling, RAR volume filtering).

---

## 3.7.2 (2026-09-16)

### Recovered images with Mode 2 or subchannel layouts now convert (#67139)

*   **A decoded ECM whose source was not a plain 2352-byte image is no longer reported as damaged.** Alcohol `.mdf` rips can store 2336-byte Mode 2 sectors, or 2448/2368-byte sectors carrying subchannel data, and all three were skipped with "the decoded image is not a whole number of 2352-byte CD sectors or 2048-byte data sectors" even though the ECM trailing checksum had already proved the file intact. The new `RecoveredImageClassifier` routes every recovered image (ECM, ISZ, archive, split set) by its actual layout: raw 2352-byte CD sectors get a generated cue as before, 2048-byte sectors convert as a DVD image as before, 2336 and 2324-byte Mode 2 sectors get a `MODE2/2336`/`MODE2/2324` cue (both round-trip losslessly through chdman 0.289), and 2448/2368-byte rips have their subchannel tail stripped to 2352 and are then sniffed and cued — the same strip the `.mds` path already used. Only a size that fits no standard layout is still reported as damaged.
*   The "not a whole number of 2352-byte CD sectors..." skip is now excluded from automatic bug reports, like the other user-data conditions.

### Housekeeping

*   Test suite grew to **851 tests** (recovered-image layout routing: 2336/2324 cues, 2448/2368 stripping, skip reasons).

---

## 3.7.1 (2026-09-16)

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

*   **Release zips are leaner**: the library `.xml` IntelliSense doc files (`CCDSharp.xml`, `CSOSharp.xml`, `PBPSharp.xml`) are no longer included — nothing at runtime uses them.
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

