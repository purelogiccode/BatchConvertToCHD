# CHD Battleground — Discrepancies Report

> **Audience:** LLM agent tasked with fixing `CSharp_CHDSharp` (`CHDSharpLib` + `CHDSharpCli`) to reach parity with `chdman`.
> **Date:** 2026-08-26
> **Corpus:** 56 CHDs from `H:\CHDTest` — see `CHDBattleTest:1` harness + `H:\CHDBattleResults\` outputs.

---

## 1. Context

### 1.1 What was tested

`CHDBattleTest` (`CHDBattleTest\Program.cs:1`, `BattleEngine.Core.cs:1`, `BattleEngine.Decode.cs:1`, `BattleEngine.Encode.cs:1`) benchmarks **chdman 0.289** (`BatchConvertToCHD\chdman.exe`, `CSharp_CHDSharp\chdman.exe`) against **CHDSharp 1.4.0** (`CHDSharp\CHDSharp.exe` / NuGet `CHDSharp 1.4.0`) over:

| Phase | Battles (both tools, timed, SHA-256 hashed) |
|---|---|
| **Decode** | `extractraw` (all 56), `extractcd` (43 CD + 3 GD-ROM), `extractdvd` (10 DVD), `extracthd` (3 HDD), `decode-lib` (in-process `CHDSharpLib` hunk loop) |
| **Encode** | `copy -c zstd` (all 56, re-compress), `createcd -c cdzl` (43 CD), `createdvd -c zstd` (10 DVD), `createhd -c zstd` (3 HDD) — plus 224 cross-verifications (`chdman verify` + `CHDSharp verify` on each product) |

Artifacts are hashed per file and per extracted directory (`ToolRunner.cs:108`, `Hashing.Sha256DirectoryAsync`). CSV is append-safe for resume (`ReportWriter.cs:1`). Latest full run: **56/56 files, 03:32:52, 1177 rows, 0 failures, 0 timeouts** — `H:\CHDBattleResults\results.csv:1`, `H:\CHDBattleResults\report.md:1`, `H:\CHDBattleResults\console.log:1`.

### 1.2 What is already byte-identical (do NOT regress)

| Battle | Parity |
|---|---|
| `extractraw` | 56/56 byte-identical, both tools cross-verify |
| `copy -c zstd` (re-compress) | 56/56 byte-identical |
| `createdvd -c zstd` | 10/10 byte-identical |
| `extractdvd` / `extracthd` | 13/13 byte-identical |
| All 224 `verify` cross-checks | `chdman verify` and `CHDSharp verify` agree on every product |

Fixes below must preserve these. The claims in `docs\encoder.md:1` and `docs\extraction.md:1` that `copy`/`createdvd`/`extractraw` are byte-identical are *confirmed* on this 65 GiB logical corpus.

### 1.3 How to reproduce

```powershell
# From CSharp_CHDSharp\CHDSharpCli or CHDBattleTest binary dir:
dotnet bin\Release\net10.0\chdbattle.dll -o H:\CHDBattleResults --lib-decode --include-av
# Resume after interruption:
dotnet bin\Release\net10.0\chdbattle.dll -o H:\CHDBattleResults --resume
# Classify only (fast, no I/O):
dotnet bin\Release\net10.0\chdbattle.dll --list
```

Spot-repro for a single file (example: smallest CD):

```powershell
$b="C:\Users\HomePC\Dropbox\source\repos\CSharp_BatchConvertToCHD\CHDBattleTest\bin\Release\net10.0"
$w="$env:TEMP\opencode\chk"; mkdir $w -Force | Out-Null
& "$b\chdman.exe" extractcd -i "H:\CHDTest\Akai Shizuku - The Legend of Heroes IV (Japan).chd" -o "$w\d.cue" -f
& "$b\chdman.exe" createcd -i "$w\d.cue" -o "$w\m.chd" -c cdzl -f -np 24
& "$b\CHDSharp.exe" createcd -i "$w\d.cue" -o "$w\s.chd" -c cdzl -f -np 24
& "$b\chdman.exe" info -i "$w\m.chd"
& "$b\chdman.exe" info -i "$w\s.chd"
# Compare Data SHA1 / overall SHA1 / reported CHD size
```

---

## 2. Discrepancy taxonomy

| ID | Battle | Parity observed | Severity | Kind |
|---|---|---|---|---|
| **D1** | `extractcd` | **0/43** parity — every CD/GD-ROM flagged | **Convention difference, not data loss** | Library + CLI behavior diverging from chdman — see §3.1 |
| **D2** | `createhd -c zstd` from `.img` | **0/3** parity — exact **51-byte delta** every file | **Real metadata bug** — missing `GDDD` tag | Encoder/CLI — see §3.2 |
| **D3** | `createcd -c cdzl` | **25/43 identical, 18/43 differ** by +65 … +9,098 B (+0.000 … +0.114%) | **Benign codec divergence** — no data/metadata loss | Vendored codec port — see §3.3 |

No verifier disagreement in any battle (`extractraw-parity`, `copy-parity`, `createdvd-parity` etc. all show `ok=1` cross-verification in `H:\CHDBattleResults\results.csv:1`).

---

## 3. Discrepancies — root cause, fix target, and verification

### D1 — `extractcd` output convention differs (all 43 CDs + 3 GD-ROMs)

**Symptom.**
`BattleEngine.Decode.cs:21` runs `extractcd -i src.chd -o disc.cue` (CD) / `disc.gdi` (GD-ROM) into separate work dirs and hashes the directory via `Hashing.Sha256DirectoryAsync` (`ToolRunner.cs:30`). Every CD pair yields different hashes with this harness message:

```
output convention differs (chdman=8773030 B vs chdsharp=9136006 B total)
```

Example — `Akai Shizuku - The Legend of Heroes IV (Japan).chd` (8.7 MiB logical):

* chdman `disc.bin` = **8,773,030 B** (2,352 × 3,730 sectors, cooked)
* CHDSharp `disc.bin` = **9,136,006 B** (2,448 × 3,732 frames incl. subcode)
* CUEs identical; **raw decode `extractraw` for the same CHD is byte-identical** on both tools.

**Verification that data is not lost.**
`extractraw` for the same file hashes identically on both tools (`H:\CHDBattleResults\results.csv:1` → `extractraw-parity 56/56`). The raw decompressed CHD payload is the same; only the *structured extraction container* differs.

**Root cause.**
*CHDSharpLib* stores CD images as 2,448-byte frames (2,352 data + 96 subcode) per `UnitBytes = 2448` (`CHDFile.cs:200`, `docs\extraction.md:1` — "whole disc, 2352-byte frames + subcode") and `CHDFile.cs:3505` `ExtractToDirectory` writes the full frames. MAME's `chdman` `extractcd` strips/isologs the 96-byte subcode and writes *cooked* 2,352-byte sectors (or 2,048 for MODE1) into `disc.bin`, per track `TRACK TYPE`/`SUBTYPE`. CHDSharp's docs acknowledge the CHGT/CHT2/CHGD legacy-GD-ROM differences but the extraction path does not offer a "cooked" 2352-only mode.

**What the LLM should fix.**

*Primary target — library:*
- `CHDSharpLib\CHDFile.cs:3505` `ExtractToDirectory` / `ExtractToDirectoryWithReporting` (`CHDFile.cs:3550`) — add a `bool stripSubcode = false` (or `CueStyle`-aware) option that writes 2,352-byte sectors when requested, matching chdman for CDs. Track layout already parsed via `ChdTocParser.cs:18` and exposed as `Tracks` (`CHDFile.cs:379`), so the frame-to-sector mapping is available. When stripping, write only `sector[0..2351]` of each 2448-byte frame (skip the 96-byte subcode tail); GD-ROMs handled separately via `IsLittleEndianAudio` / `CHGT` byte-swap path already present in `CHDFile.cs:533`.

*Secondary target — CLI:*
- `CHDSharpCli\Program.cs` `extractcd` handler — expose the same option as `--cooked` / default to chdman-compatible cooked output, or at minimum document the difference in `docs\extraction.md:1` and `CHDSharpCli\README.md:1`. The battleground harness (`BattleEngine.Decode.cs:21`) should then be updated to compare like-for-like (or to record `FORMAT-DIFFERENCE` explicitly rather than `FAIL` — already done in `BattleEngine.Decode.cs:21`).

**Acceptance criteria.**
- New option `ExtractToDirectory(..., bool cooked = false)` exists; when `cooked=true`, `disc.bin` size and SHA-256 for the Akai Shizuku example match `chdman` (8,773,030 B, hash `7F8DAA307355...`) within the same directory-hash harness.
- Default behavior may remain full-frame to avoid breaking existing callers, but the CLI default for `extractcd` should match chdman cooked output (add `--raw-frames` to keep full 2448 behavior). Document the flag.
- `H:\CHDBattleResults`-style regression: `extractcd` on the 43 CDs still passes `chdman verify` on both products after re-extract.

**LLM instruction.**
> Do NOT change `extractraw` — it is already correct and byte-identical. Scope the fix to `CHDFile.ExtractToDirectory`'s per-track frame writer and the `CHDSharpCli` `extractcd` dispatch. Add a unit test in `CHDSharpTest\` that round-trips a small CD (e.g. `ATR - All Terrain Racing`) through `CHDSharp extractcd --cooked` and `chdman extractcd` and asserts byte equality. Update `docs\extraction.md:1` to describe both modes.

---

### D2 — `createhd` from raw `.img` drops `GDDD` geometry metadata (3/3 HDDs)

**Symptom.**
`BattleEngine.Encode.cs:30` runs `createhd -i <extracted disc.img> -o <.chd> -c zstd` via both tools. All three HDD files produce **Data SHA1 identical, overall SHA1 different, file size delta = exactly 51 bytes** (`H:\CHDBattleResults\results.csv:1`):

| File | chdman size | CHDSharp size | delta | chdman hash12 | CHDSharp hash12 | Data SHA1 |
|---|---|---|---|---|---|---|
| `pc98-542mb.chd` | 74,879,706 | 74,879,655 | +51 | 6684F91C | 6218F163 | identical |
| `a6plus.chd` | 104,213,820 | 104,213,769 | +51 | AB7F3425 | DBE30CC2 | `3628d09350c0cf216f981eacbef417fc5fc46653` both |
| `dvp-0027a.chd` | 3,317,212,283 | 3,317,212,232 | +51 | 1BB46616 | 848EA56D | identical |

Spot repro confirms with `chdman info`:

```
=== m.chd (chdman) ===  Metadata: Tag='GDDD' Index=0 Length=35 bytes  CYLS:2012,HEADS:16,SECS:32,BPS:512.
=== s.chd (CHDSharp) ===  (no GDDD entry)
```

Both products pass `chdman verify` and `CHDSharp verify` on all four cross-checks (`results.csv:1` `createhd:zstd:verify-*` all `ok=1`).

**Root cause.**
- `CHDSharpLib\Encoder\ChdEncoder.cs:63` `EncodeRaw` only synthesizes `GDDD` when `options.AutoClassify == true` (`ChdEncoder.cs:96` — `BuildHardDiskMetadata(logicalBytes, unitBytes)`). Otherwise metadata must be supplied explicitly via `options.Metadata`.
- `CHDSharpCli\Program.cs:1229` `CreateHdTest`'s `--input` path (`Program.cs:1229` → `Program.cs:1271` `ChdEncoder.EncodeRaw(inputPath, outputPath, ...)`) constructs an empty `new ChdEncodeOptions()` with **no `AutoClassify`** and **no explicit `GDDD`** entry, then calls `EncodeRaw` directly — so no `GDDD` is written. The blank-image path (`Program.cs:1359` `CreateBlank` / `CreateBlankWithChs`) correctly stamps `GDDD` via `ChdEncoder.cs:148` `CreateBlank` (`ChdEncoder.cs:173` `All(e => e.Tag != HardDisk... )` guard).
- `CHDSharpLib\Encoder\MetadataWriter.cs:33` `HardDiskMetadataTag = 0x47444444` and `MetadataWriter.cs:100` `BuildHardDiskMetadata(ulong totalBytes, uint bytesPerSector)` correctly implement MAME's `guess_chs` algorithm; it just isn't invoked for the `--input` case.

**What the LLM should fix.**

*Primary — CLI (`CHDSharpCli\Program.cs:1229`):*
When `CreateHdTest` is invoked with `--input <file>` and no explicit `--size`/`-chs`/`-tp`, set `encodeOptions.AutoClassify = true` **and** ensure the inferred `unitBytes` (sector size) is used for the `GDDD` payload, matching MAME's `chdman createhd -i file.img` behavior. Alternatively, explicitly push the synthesized entry before the call:

```csharp
// In Program.cs:1229 block, before ChdEncoder.EncodeRaw:
encodeOptions.AutoClassify = true;
// or, if not using AutoClassify:
encodeOptions.Metadata ??= new List<MetadataEntry>();
if (!encodeOptions.Metadata.Any(e => e.Tag == MetadataWriter.HardDiskMetadataTag))
    encodeOptions.Metadata.Add(MetadataWriter.BuildHardDiskMetadata(
        (ulong)new FileInfo(inputPath).Length, unitBytes));
```

*Secondary — library guard (`CHDSharpLib\Encoder\ChdEncoder.cs:63`):*
Consider making `EncodeRaw`'s hard-disk fallback unconditional for raw (non-ISO9660) sources when `AutoClassify` is not set but no `GDDD` was supplied — or document that callers intending `createhd` semantics must pass `GDDD` explicitly. The CLI fix alone suffices for parity; the library guard is defensive.

**Acceptance criteria.**
- After fix, `a6plus.chd` spot-repro: `CHDSharp createhd -i a6plus.img -o s.chd -c zstd` produces `GDDD` length 35, `chdman info` shows `CYLS:2012,HEADS:16,SECS:32,BPS:512.` on both `m.chd` and `s.chd`, and `results.csv` `createhd:zstd-parity` becomes 3/3 (or the reported delta drops to 0 and CHD size matches within rounding).
- Data SHA1 remains identical; overall SHA1 now also matches (since `ComputeCombinedSha1` in `MetadataWriter.cs:363` covers checksummed entries).
- Existing `CHDSharpEncoderTest\RawEncodeMetadataTests.cs:78` (`Metadata = [BuildHardDiskMetadata(...)]`, `Assert.Equal("GDDD", ...)`) still passes; add a new test `CreateHdFromRawWritesGddd` that calls `ChdEncoder.EncodeRaw` with a 1 MiB temp file and asserts `file.Metadata.Any(m => m.Tag=="GDDD")`.

**LLM instruction.**
> Edit only `CHDSharpCli\Program.cs:1229` (and optionally `CHDSharpLib\Encoder\ChdEncoder.cs:63`). Do not touch `MetadataWriter.cs:100` logic — it is already MAME-parity. Verify with the 3 HDDs in `H:\CHDTest` (`pc98-542mb.chd`, `a6plus.chd`, `dvp-0027a.chd`) after re-extracting via `extracthd`. Run `dotnet test CHDSharpEncoderTest` to ensure `GDDD` tests still pass.

---

### D3 — `createcd -c cdzl` compressed bytes diverge on 18/43 CDs (no data loss)

**Symptom.**
`BattleEngine.Encode.cs:30` runs `createcd -i <decoded disc.cue> -o <.chd> -c cdzl -f -np 24` on the 43 CDs, hashing products (`results.csv:1` `createcd:cdzl`). Outcome: **25/43 byte-identical, 18/43 differ** by +65 … +9,098 B (+0.000% … +0.114%). CHDSharp is **always smaller** (better ratio).

All 18 still pass every cross-verification (`createcd:cdzl:verify-*` all `ok=1`). Spot repro on three diverse CDs shows **overall SHA1 and Data SHA1 identical** on every divergent pair:

```
Akai Shizuku … : overall eed27e5a9abc0b44ec7cb3a4c6c922405ee400f0 both | data 3a71ba789b0aba8b00cb9092029e04f3663cbdae both  (+7,719 B)
Akira (Europe) : overall 1abe30f210f4ee779a75796727f5fe3ef1781692 both | data 71f063983d554bd4f33af767642cb59d2d3ef18d both (+3,977 B)
Metal Slug 2   : overall cf08d387da5004922bd0c6bdb9d5258f1b39575c both | data 707a5e1a5f069616c43b26dbd3d34ae3337199de both (+9,098 B)
```

Full 18 list (CHDSharp always smaller — see `H:\CHDBattleResults\results.csv:1`):

```
Akira +3,977, Akai +7,719, actdesu +1,353, two shot diary +65,
3 ninjas kick back +8,038, Arcade Gears Vol2 +8,858, 3 count bout +8,791,
Chiki Chiki Boys +3,836, Akiko Gold +7,801, amateur teikyou +770,
Amiga CDTV (both copies) +8,193 each, Metal Slug 2 +9,098, Club 3DO +84,
Aero Dancing +613, imsa racing +68, Addams Family Disc 2 +8,854, 4 wheel thunder +960
```

**Root cause.**
`cdzl` is a *compound* codec per `CHDSharpLib\Encoder\CdCompoundCodec.cs:1` / `CdflCodec.cs:1` / `FlacCodec.cs:1`: per-hunk it picks `zlib` (data tracks) or `FLAC` (audio tracks) whichever compresses better. MAME uses native `libFLAC` + `zlib`; CHDSharp vendors pure-C# ports (`VendoredFlac`, `VendoredZLib`, `CHDSharpLib\Encoder\RawDeflate.cs:1`). For audio tracks, the FLAC encoder performs an *exhaustive subframe search* (fixed/LPC). A tiny difference in residual/partition choice changes the compressed hunk bytes while decoding to identical PCM — hence identical `Data SHA1` but different container `CHD size`. The docs' claim "byte-for-byte identical to `chdman createcd`" (`docs\encoder.md:1`, `ChdEncoder.cs:8`) holds only for corpora without audio or with audio where the search happens to coincide; real Redump/TOSEC discs vary.

**Severity.**
**Low — not data loss.** Both tools verify, and `Data SHA1` / `overall SHA1` prove logical equivalence. The battleground's `products differ` flag for these 18 is informational; the harness correctly reports `verify=* ok` on both products. No immediate fix required unless strict chdman-byte-parity is contractually required.

**What the LLM should fix — two options (pick one, document the choice).**

*Option A — chase byte parity (higher cost):*
- Audit `CHDSharpLib\Encoder\FlacCodec.cs:1` and vendored `VendoredFlac\` against MAME's `libFLAC` subframe selection: compare exhaustive search order, fixed-predictor coefficients, LPC precision, and Rice partition limits. Ensure per-frame `FLAC__stream_encoder` equivalent in managed code makes the same decision. Add differential tests in `CHDSharpEncoderTest\` that compress the same 2,352-byte audio buffer with both encoders and assert byte equality (capture native `libFLAC` output via a one-off `chdman` helper or by calling the vendored native DLL in-test).

*Option B — accept and document (recommended for now):*
- Weaken the byte-parity assertion for `createcd` to **SHA1-parity**: update `docs\encoder.md:1` to state that `createcd -c cdzl` is *logically identical* (Data SHA1 + CHT2 metadata + `chdman verify` pass) but not guaranteed byte-identical for audio-bearing discs due to FLAC search non-determinism — add a note alongside the existing "`cdzs` is byte-identical" claim.
- In `CHDBattleTest\BattleEngine.Encode.cs:30`, change the parity verdict for `createcd:cdzl` to check **Data SHA1 equality** (via `chdman info` parsing or by post-verify hash) instead of file SHA-256 when reporting `products differ` — or add a second `logical-parity` row so the report distinguishes "container differs, logical matches."
- In `CHDSharpEncoderTest`, change the 43-CD byte-parity loop to assert `Data SHA1` / `overall SHA1` equality rather than file-hash equality for audio CDs.

**Acceptance criteria (Option B).**
- `docs\encoder.md:1` updated with a `createcd` caveat paragraph.
- `CHDBattleTest` `createcd:cdzl-parity` shows `ok=1` when Data SHA1 matches (or a new `createcd-logical-parity 43/43` row appears), while file-hash differences are reported as `note: compressed bytes differ, logical identical`.
- No regression on the 25 already-identical files; `CHDSharpEncoderTest` passes.

**LLM instruction.**
> Do NOT touch `ChdEncoder.EncodeCd` (`ChdEncoder.cs:1083`), `MetadataWriter.BuildCdMetadataEntries` (`MetadataWriter.cs:321`), or the track-padding logic (`ChdEncoder.cs:1110` `TrackPadding`). The metadata and logical frame layout are already correct — `chdman info` proves `overall SHA1` matches. Scope to either (A) `FlacCodec`/`CdCompoundCodec` compression choice or (B) docs + harness verdict. If choosing (B), edit `docs\encoder.md:1` and `CHDBattleTest\BattleEngine.Encode.cs:30` / `ReportWriter.cs:1`. If choosing (A), add a FLAC parity unit test under `CHDSharpEncoderTest\` first and confirm it fails before patching.

---

## 4. Guidance for the fixing LLM

### 4.1 Fix order (cheapest first)

1. **D2** — ~10 lines in `CHDSharpCli\Program.cs:1229`. Immediate byte-parity win on all 3 HDDs. No codec risk.
2. **D1** — library option + CLI flag in `CHDFile.cs:3505` / `ChdTocParser.cs:18` + `Program.cs` extract dispatch. Medium effort, high harness visibility (43 files flip from `FAIL` to `ok`).
3. **D3** — either docs-only (1 hour) or FLAC audit (days). Defer if time-boxed; file as known acceptable divergence.

### 4.2 Do / do not

**Do:**
- Preserve `extractraw` / `copy` / `createdvd` / `extractdvd` / `extracthd` byte parity — add them as regression tests (`H:\CHDBattleResults\results.csv:1` snapshots).
- Keep `CHDSharp verify` on every product: the battleground's 224 cross-verifications must remain `ok=1`.
- Use `MetadataWriter.cs:100` for GDDD synthesis — do not reimplement CHS guessing.

**Do not:**
- Change hunk size defaults (`CdConstants.FramesPerHunk * FrameSize` = 19584, `DefaultHunkBytes = 4096` in `ChdEncoder.cs:35`) to chase size — hunks are correct; only codec selection differs.
- Modify `ChdTocParser` track parsing for the GDDD case — that parser is for CD/GD-ROM, not HDD.
- Silence the battleground by hashing only `overall SHA1` for D1 — the container difference is still worth reporting as `convention differs`.

### 4.3 Validation after each fix

```powershell
# Unit tests (must stay green)
dotnet test CHDSharpTest
dotnet test CHDSharpEncoderTest
dotnet test BatchConvertToCHD.Tests

# Battleground smoke (4 smallest files, ~1 min)
dotnet bin\Release\net10.0\chdbattle.dll -o H:\CHDBattleSmoke --max-files 4 -v

# Full run after D2+D1 (3.5 h — schedule overnight)
dotnet bin\Release\net10.0\chdbattle.dll -o H:\CHDBattleResults --lib-decode --include-av

# Spot checks requested by this doc
$w="$env:TEMP\opencode\chk"; $b="...\chdbattle\bin\Release\net10.0"
# D2: extracthd→createhd round-trip on a6plus
& "$b\chdman.exe" extracthd -i "H:\CHDTest\a6plus.chd" -o "$w\a.img" -f
& "$b\chdman.exe" createhd -i "$w\a.img" -o "$w\m.chd" -c zstd -f -np 24
& "$b\CHDSharp.exe" createhd -i "$w\a.img" -o "$w\s.chd" -c zstd -f -np 24
& "$b\chdman.exe" info -i "$w\m.chd"
& "$b\chdman.exe" info -i "$w\s.chd"   # expect GDDD on both, sizes equal, Data SHA1 equal

# D1: extractcd cooked parity on Akai Shizuku
& "$b\chdman.exe" extractcd -i "H:\CHDTest\Akai Shizuku - The Legend of Heroes IV (Japan).chd" -o "$w\d.cue" -f
& "$b\CHDSharp.exe" extractcd --cooked -i "H:\CHDTest\Akai Shizuku - The Legend of Heroes IV (Japan).chd" -o "$w\d2.cue" -f
# (after fix) hashes of $w\disc.bin should match
```

### 4.4 Repo layout cheat-sheet

```
CSharp_CHDSharp\
  CHDSharpLib\CHDFile.cs:3505          — ExtractToDirectory (D1)
  CHDSharpLib\ChdTocParser.cs:18       — HardDiskMetadataTag, track parsing
  CHDSharpLib\Encoder\ChdEncoder.cs:63 — EncodeRaw / CreateBlank (D2 guard)
  CHDSharpLib\Encoder\MetadataWriter.cs:33,100 — HardDiskMetadataTag, BuildHardDiskMetadata
  CHDSharpLib\Encoder\CdCompoundCodec.cs:1, FlacCodec.cs:1, CdflCodec.cs:1 — D3 FLAC divergence
  CHDSharpCli\Program.cs:1229          — createhd --input path (D2 fix site)
  docs\encoder.md:1, docs\extraction.md:1 — update if choosing D3 Option B / D1 docs
CSharp_BatchConvertToCHD\
  CHDBattleTest\:1                    — harness (already distinguishes format-differ)
  H:\CHDBattleResults\results.csv:1 / report.md:1 — ground truth
```

---

## 5. Expectations for the PR that fixes this

* One PR per discrepancy (D2 → D1 → D3) or one PR with three isolated commits — do not mix unrelated codec changes with CLI metadata fixes.
* Each commit must include a regression test: `CHDSharpEncoderTest\CreateHdFromRawWritesGddd`, `CHDSharpTest\ExtractCdCookedParity`, `CHDSharpEncoderTest\CreateCdLogicalParity`.
* Update `docs\ReleaseNotes.md:1` and `docs\encoder.md:1` accordingly.
* Attach a `H:\CHDBattleResults`-style CSV diff showing D2 moving 0/3 → 3/3 and D1 moving 0/43 → 43/43 (or documented as intentional).

---

*Generated from `CHDBattleTest` run `2026-08-26 03:32:52` — corpus `H:\CHDTest` (56 CHDs, V5, chdman 0.289). Harness commit `CHDBattleTest:1127` at `CSharp_BatchConvertToCHD\CHDBattleTest\Discrepancies.md:1`.*
