---
title: Testing
nav_order: 12
---

# 11. Testing

The solution contains a single test project, `BatchConvertToCHD.Tests` (xUnit, `net10.0-windows`), with **813 tests across 43 test classes** (a handful of PBP integration tests need a local sample folder — see §11.5), plus the shared `FakeHttpMessageHandler` and `IszImageBuilder` helpers.

> **Expected result on a clean machine: 762 passed, 15 failed.** The 15 failures are a fixture problem, not a regression — see [§11.5](#115-the-15-expected-failures). A change that leaves exactly those 15 failing has broken nothing.

## 11.1 Running the Tests

```bash
dotnet test CSharp_BatchConvertToCHD.sln -c Release
# or, faster, without rebuilding:
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj --no-build
```

Requirements: the tests are run on Windows (the app project is `net10.0-windows`). Some tests need the app's output directory to contain `chdman.exe` (it is copied by the build).

## 11.2 How Tests Are Structured

- Plain xUnit `[Fact]` / `[Theory]` + `[InlineData]`.
- Filesystem-dependent tests create a GUID temp directory per test class (`Path.GetTempPath() + $"{ClassName}_{Guid:N}"`) and clean it up in `Dispose`.
- HTTP-dependent tests inject an `HttpClient` backed by `FakeHttpMessageHandler` (the only shared helper): a `Func<HttpRequestMessage, HttpResponseMessage>` or a convenience `(HttpStatusCode, string content, string contentType)` constructor, plus a static `WithAsyncHandler` helper.
- Internals are tested because `BatchConvertToCHD.csproj` grants `InternalsVisibleTo("BatchConvertToCHD.Tests")`.
- **Integration tests** are tagged `[Trait("Category", "Integration")]` and read real sample files from fixed absolute directories (`D:\Emulators\...`). Most **early-return when the samples are absent**, so on machines without the sample folders they are effectively skipped (reported as passed). `PbpFileIntegrationTests` is the exception — see [§11.5](#115-the-15-expected-failures).
- **Committed fixtures** live in `BatchConvertToCHD.Tests/Fixtures/` and are copied to the output directory by the csproj. There is one today, `ecm-sample.ecm`; it exists so the ECM decoder can be verified against the reference implementation's own output without that tool being installed.
- **Format fixtures are built in code** rather than committed where the format allows it: `IszImageBuilder` writes spec-conformant ISZ files, and `MdsTests`/`RawCdImageDetectorTests`/`SplitImageJoinerTests` synthesise their descriptors and sector data. This keeps the repository free of disc-sized binaries.

## 11.3 Coverage by File

### Application-level tests

| File | Focus |
|------|-------|
| `AppConfigTests.cs` | Arm64 detection, chdman/7za exe names, API URLs/keys, app name, interval/timeout constants |
| `AppHttpClientTests.cs` | Singleton behavior, Accept header, TLS 1.2+1.3, dispose semantics, thread safety |
| `ArchiveServiceTests.cs` | ZIP extraction (real ZIPs built in-test), corrupt/unsupported/missing archives, bin-only archives → auto-cue, `ExtractCsoAsync` failure/cancellation, 7za fallback matrix, multi-part RAR / network detection, disk-full errors |
| `BinCueGeneratorTests.cs` | Auto-cue marker, cue content, mode alternation, read/rewrite |
| `BugReportApiSinkTests.cs` | Sink forwards Warning/Error/Fatal, ignores Debug/Info |
| `BugReportServiceTests.cs` | Report formatting (inner exceptions, depth), HTTP method/header/body, success/failure mapping, the full exclusion-pattern list (incl. case-insensitivity), no-HTTP-call for excluded messages |
| `CancellationHandlingTests.cs` | `IsCancellationException`, `IsDiskSpaceException`, `IsCorruptionException`, `IsCrcErrorException` and their mutual exclusivity |
| `CueNormalizerTests.cs` | Encoding detection (CP949/CP1251/CP932/UTF-8/UTF-32LE BOM), canonicalization, zero-padding resolution, unresolved names, MP3 transform hook, canonical write format |
| `CueWorkDirectoryTests.cs` | Work-dir creation rules, in-place BOM fast path, MP3→WAV decoding (fake + real NAudio decoders), **end-to-end tests running real `chdman.exe`** (BOM regression, cue/bin/mp3, cue/iso/mp3; skipped when chdman is absent) |
| `FileExtensionsTests.cs` | All extension constants and sets via reflection, cross-consistency, no duplicates |
| `FileItemTests.cs` | INotifyPropertyChanged, `DisplaySize` formatting (0 B … 1.5 TB) |
| `FileWatcherServiceTests.cs` | Start/Stop/Dispose, `GetContextForMissingFile` diagnostics with a real `FileSystemWatcher`, history eviction at 1000 entries, buffer-overflow clearing |
| `GameFileParserTests.cs` | cue/gdi/toc referenced-file extraction (quoted/unquoted/spaces/multi-file), encoding detection |
| `GitHubReleaseTests.cs` | Model defaults, JSON (de)serialization |
| `IsoSectorValidatorTests.cs` | Sector-size alignment warnings; descriptors/empty/missing not validated |
| `MainWindowHelperTests.cs` | `StripUtf8BomIfPresentAsync`, `SelectChdmanErrorLine` (skips progress lines, picks last real error) |
| `PathUtilsTests.cs` | `SanitizeFileName`, `GetSafeTempFileName`, path validation, relative paths, best-temp-directory selection, and `CreateTempDirectoryOnSameVolume` — including the property that actually matters: a cue written in the returned directory can reach the image by a **non-rooted** relative path, checked for every ready fixed volume |
| `PbpExtractionResultTests.cs` | Result-model defaults/setters |
| `RetryingFileOperationsTests.cs` | `TryDeleteAsync`/`TryMoveAsync` with real file locks (`FileShare.None`), read-only attribute clearing, retry-then-give-up, success-after-lock-release, missing-source/missing-destination semantics |
| `StatsServiceTests.cs` | POST method/URL/Bearer header/body, no-throw on 429/401/400/500/network errors |
| `UpdateServiceTests.cs` | Version parsing/normalization theories, new/older/minor/major comparisons, draft/prerelease skip, rate-limit and 5xx handling (no bug report), bug-report paths, invalid tags |

### Format detection and recovery tests

| File | Focus |
|------|-------|
| `DiscImageSignatureTests.cs` | Magic-byte identification of every `DiscImageKind`, `IsArchive` grouping, `Describe` phrasing, unknown/short/missing files |
| `RawCdImageDetectorTests.cs` | Sync-mark and mode-byte sniffing (MODE1/MODE2), rejection of cooked 2048-byte images and non-sector-aligned files, candidate extensions, generated cue content, and the cross-volume refusal that returns `null` |
| `InputFileFilterTests.cs` | A raw image is dropped when a sibling descriptor covers it (by base name and by cue text), kept when nothing covers it, matching is directory-scoped and case-insensitive — and `ResolveOutputCollisions` keeps the first non-archive input of each colliding output group, order-independently, including three-way collisions and all-archive groups |
| `SplitImageJoinerTests.cs` | `.001`/`.002` and `.i00`/`.i01` set discovery and ordering, gaps, single-file non-sets, byte totals, and join output equality |
| `TrackBinCueBuilderTests.cs` | `(Track N)` set recognition and ordering, multi-FILE cue content, data track mode vs. AUDIO tracks, non-track-set rejection |
| `MdsTests.cs` | `.mds` header/session/track parsing, mode-to-cue-track mapping, sector-size classification (2352 / 2448 / 2368 / 2048), implausible session counts, `.mdf` lookup, subchannel stripping, MSF formatting, and the three `MdsInputPreparer` shapes |
| `IszHeaderTests.cs` | **Every header field read at its documented offset** (the test that catches an offset mistake), 64-bit image-size arithmetic for dual-layer sizes, signature and short-input rejection, and each refusal in `GetUnusableReason` including all four encryption modes |
| `IszDecoderTests.cs` | Chunk-entry bit-packing for 2/3/4-byte pointers, segment naming, round trips for zlib / bzip2 / stored / all-zero and mixed chunk types, trailing partial chunks, two-segment images with a chunk straddling the boundary, and the refusals: not-an-ISZ, encrypted, truncated file, truncated chunk table, corrupt compressed data, missing segment, and a segment from a different image |
| `CdSectorEccEdcTests.cs` | Sync/mode layout, EDC accumulation equivalence whole vs. in pieces, and the parity distinction that matters: **Mode 1 parity covers the address, Mode 2 Form 1 parity does not** and restores it afterwards; Form 2 gets an EDC and no parity |
| `EcmImageDecoderTests.cs` | Decoding the committed reference fixture to an expected SHA1, a guard asserting the fixture still contains all four block kinds, repeatability, output naming, and the refusals: no signature, truncated, wrong trailing checksum, corrupt data, missing file |
| `CueNormalizerFallbackTests.cs` | `FILE`-line fallbacks: bare name beside the cue, extension swap, single-FILE match by elimination — and that audio tracks and multi-FILE cues are deliberately **not** guessed |

> **How the ECM decoder is verified.** `EcmImageDecoderTests.BuildReferenceImage()` rebuilds, in code, the 12-sector image (4 Mode 1, 4 Mode 2 Form 1, 4 Mode 2 Form 2) that `Fixtures/ecm-sample.ecm` was encoded from by Neill Corlett's own encoder. Decoding the fixture and comparing proves both the block parsing and the regenerated EDC/parity match the reference implementation. The fixture was produced in a run that also confirmed the reverse direction: the real encoder reported stripping the parity from all twelve sectors — which it only does for sectors whose parity it agrees with — and the real decoder produced the identical SHA1. Regenerating the fixture requires that tool and is documented in the repository's working notes; the guard test exists so a regeneration from a simpler image cannot quietly stop exercising the Mode 2 branches.

### Library tests (Alcohol120Sharp / CSOSharp / PBPSharp / UltraIsoSharp)

| File | Focus |
|------|-------|
| `CsoFileTests.cs` | Open-error mapping, v1/v2 open, dispose behavior, block reads |
| `CsoStreamTests.cs` | Full stream contract: seek/read/zero-length, cross-block reads, throw semantics |
| `CsoHeaderTests.cs` | Header constants, v1/v2 validity, total blocks, index offset shift |
| `CsoFileIntegrationTests.cs` | Real `.cso` files: **byte-for-byte block comparison vs. paired `.iso`**, full extraction equality, stream parity |
| `PbpFileTests.cs` | Open errors, header/SFO/disc parsing, **PbpError enum ordinal assertions**, synthetic PBP+SFO builders |
| `PbpHeaderTests.cs` | Magic, size (0x28), defaults, validity |
| `SfoDataTests.cs` / `SfoEntryTests.cs` / `TocEntryTests.cs` | SFO lookups (incl. type mismatch), entry formats, TOC/track types |
| `CueSheetWriterTests.cs` | Generated CUE content: data/audio tracks, INDEX 00 with 150-frame lead-in, zero-clamp, padding |
| `PbpFileIntegrationTests.cs` | Real `.pbp` files: header/SFO/TOC, `ExtractToBinCue` byte-equality vs. original BIN, normalized CUE equality |

> **Gap**: there are currently **no CCDSharp unit tests** — the test project does not reference CCDSharp (`BatchConvertToCHD.Tests.csproj:36–38`); the only touch-point is the `"CCDSharp: Conversion error"` exclusion pattern. CCDSharp behavior is exercised indirectly only if a real `.ccd` file flows through the app.

## 11.4 Writing New Tests — Quick Conventions

1. File-scoped namespace `BatchConvertToCHD.Tests`; `using Xunit` is global.
2. For filesystem tests, mirror the GUID-temp-dir + `IDisposable` pattern.
3. For HTTP tests, use `FakeHttpMessageHandler` and pass the `HttpClient` to the internal constructor overloads (`StatsService`, `BugReportService`, `UpdateService`, `AppHttpClient`).
4. For chdman-dependent tests, early-return when `chdman.exe` is absent from `AppContext.BaseDirectory`.
5. Prefer building binary fixtures in code (see `IszImageBuilder`) over committing them. Commit one only when the format cannot be generated trustworthily in-repo, as with `ecm-sample.ecm`.
6. When a fixture asserts agreement with an outside implementation, add a **guard test** that the fixture still covers the cases it is meant to. A fixture can be regenerated more simply and silently stop testing anything.
7. Run the full suite before pushing. On a machine with the PBP integration samples present a good run is **813 passed / 0 failed**; without them, the PBP integration group fails as described in §11.5.

### Analyzer constraints worth knowing

The test project runs `Meziantou.Analyzer` too, and a few rules bite:

- One top-level type per file (nested types are fine).
- An `internal` type cannot appear in a public xUnit `[Theory]` signature (CS0051) — use a `[Fact]`.
- `StringBuilder.AppendLine($"...")` trips MA0011; pass an `IFormatProvider` or build the string first.
- `Assert.SkipUnless` is xUnit v3; this project is on 2.9.3, so conditional tests early-return instead.

---

## 11.5 The PBP Integration Sample Folder

`PbpFileIntegrationTests` reads real `.pbp` files from a local sample folder (`D:\Emulators\...\PsxPackager` on the maintainer's machine). When that folder is absent the group fails rather than skips — the tests assert on the discovered-sample collection before checking whether it is empty, so an absent sample surfaces as `Assert.NotEmpty() Failure: Collection was empty` instead of an early return. These failures do not indicate a defect in the application; adopting the CSO tests' early-return pattern would fix the ergonomics.

---

## 11.6 CHDBattleTest Battleground (historical)

The `CHDBattleTest` console harness (`chdbattle`) that pitted **`chdman` (MAME)** against **`CHDSharp.exe`** on a corpus of real CHDs was removed from the solution in 3.5.1 — its purpose was fulfilled. Per file it ran timed decode battles (`extractraw`, `extractcd`, `extractdvd`, `extracthd`), encode battles (`copy`, `createcd`, `createdvd`, `createhd` with a chosen codec), SHA-256 product-parity checks, and 224+ cross-verifications. Results landed as `results.csv`, `report.md`, `battle.log`, `console.log` in the output root, resume-safe.

**Final status (CHDSharp 1.4.3, corpus of 56 discs — 43 CD + 3 GD-ROM, 10 DVD, 3 HDD): zero mismatches.** Every parity battle passed byte-identically and all cross-verifications agreed, i.e. `CHDSharp.exe` produces byte-identical CHDs to `chdman` 0.289 on this corpus. History: 1.4.1 had 46 parity fails (`createdvd` 10, `copy:zstd` 18, `createcd:cdzl` 18), 1.4.2 fixed the DVD group (36 remaining), and 1.4.3's stale work-buffer + LZMA match-finder fix closed the rest.
