# What's New in 3.5.1

Released: 2026-09-02

## Changed

- **chdman is now the primary encoder.** Conversions run on the bundled `chdman`, the reference MAME CHD encoder. If chdman fails on a file, the conversion automatically falls back to `CHDSharp.exe` — the managed encoder whose output is byte-identical to chdman. The fallback reuses the already-prepared cue work directory and ASCII staging, so a cue's work set stays valid and nothing is re-prepared.
- **A batch is no longer blocked when only one encoder is present.** If `chdman.exe` is missing but `CHDSharp.exe` is available, every file is converted with CHDSharp; if CHDSharp is missing, chdman carries the batch alone. The startup notice now shows a critical error only when *both* encoders are missing.
- **Duplicate output targets are skipped, not overwritten.** When two inputs in the same batch would produce the same `.chd` (for example `Game.7z` and `Game.iso` sitting side by side), the redundant input is skipped up front and the resolution is logged — the first non-archive input wins, and the redundant archive is no longer extracted and converted just to be overwritten.

## Fixed

- **PSP `.pbp` extraction** now uses the reference-compatible SharpZipLib inflater for PSAR block decompression, so images that the stricter .NET `DeflateStream` rejected with `InvalidDataException` extract correctly. The bundled `CHDSharp` was updated as part of this fix.
- **A bare `.bin` with no cue and no readable sector header** (for example a console BIOS dropped into the input folder) is again treated as informational only — it logs guidance instead of triggering a bug report.
- **Live write-speed (MB/s) stat card** now updates during CHDSharp-driven conversions (it previously froze at 0.0 MB/s), and the elapsed-time card ticks every second during long single-file conversions.
- chdman `Input/output error` messages now include actionable guidance (failing or disconnected drive, antivirus/cloud-sync file locks, damaged image).

## Improved

- Bug reports are de-duplicated: an identical warning repeated inside a 10-minute window (a failing batch retrying the same input, a loop logging the same warning per file) is sent once.
- Alcohol 120% (`.mds`/`.mdf`) and UltraISO (`.isz`) support moved into standalone, MIT-licensed libraries: [Alcohol120Sharp](https://github.com/PureLogicCode/Alcohol120Sharp) and [UltraIsoSharp](https://github.com/PureLogicCode/UltraIsoSharp). No functional change.

## Internal

- Analyzers upgraded: Meziantou.Analyzer 3.0.200, Roslynator.Analyzers 5.0.0 added, with analyzer fixes applied across the solution.
- Removed the internal `CHDBattleTest` battleground project (its parity results live on in the CHDSharp documentation); updated the bundled 7-Zip binaries.
- Test suite grown to **813 passing tests**, including new coverage for duplicate-output resolution and ISZ header sizing.

## Packaging

- Binaries ship as a single-file, framework-dependent executable — the [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) must be installed. `chdman`, `CHDSharp` and `7za` for your architecture are bundled next to the app exe, together with the readme and license.
