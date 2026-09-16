# AGENTS.md

Working instructions for AI/LLM sessions in this repository. Read this first; the
`docs/` folder is the authoritative project documentation (also published as the
GitHub wiki and GitHub Pages).

## What this is

`Batch Convert to CHD` - a Windows WPF desktop app (`net10.0-windows`) that batch
converts disc images (cue/iso/img/ccd/mds/pbp/cso/isz/ecm/split sets/archives)
to CHD using a bundled `chdman.exe` with a managed CHDSharp fallback. The
solution also contains six library projects and an xUnit test project.

## Toolchain

- .NET SDK 10 - declared in `global.json` (`rollForward: latestMajor`). Never
  lower `TargetFramework` below `net10.0-windows`; the WPF UI depends on it.
- Node.js 24 - used only for the zero-dependency helper scripts in
  `scripts/ci/`. Do not add npm packages or a `package.json`.

## Commands

```powershell
dotnet restore CSharp_BatchConvertToCHD.sln
dotnet build CSharp_BatchConvertToCHD.sln -c Release
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release

# CI runs the unit tests only: the [Trait("Category", "Integration")] classes
# read sample folders that exist on this machine (e.g. D:\Emulators\...) but
# not on GitHub runners. Run the full command above before a release.
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release --filter "Category!=Integration"

# Framework-dependent single-file publish (one per architecture)
dotnet publish BatchConvertToCHD/BatchConvertToCHD.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/win-x64
dotnet publish BatchConvertToCHD/BatchConvertToCHD.csproj -c Release -r win-arm64 --self-contained false -p:PublishSingleFile=true -o publish/win-arm64

# Release zip (same command CI runs)
./scripts/ci/package-release.ps1 -Rid win-x64 -Version 3.7.0 -PublishDir publish/win-x64 -OutputDir dist
```

## Release engineering (do not break)

- **The app is framework-dependent. It must NOT embed the .NET runtime.** Users
  install the .NET 10 Desktop Runtime. Always publish with
  `--self-contained false -p:PublishSingleFile=true`. The output is a single
  `BatchConvertToCHD.exe` (plus the bundled tool exes, which are content files
  and stay outside the bundle).
- **Release zips contain exactly one architecture's tools.** For `win-x64`:
  `7za.exe`, `chdman.exe`, `CHDSharp.exe`. For `win-arm64`: the `*_arm64.exe`
  variants. `scripts/ci/package-release.ps1` removes the other architecture and
  the library `.xml` IntelliSense files (never used at runtime), and adds
  `LICENSE.txt` and `ReadMe.md`. Do not put both architectures in one zip.
- **Zip naming is fixed:** `release_<version>_win-<rid>.zip`, e.g.
  `release_3.7.0_win-x64.zip`. One zip per architecture, both attached to the
  GitHub release.
- **Version lives in two csproj files** (`BatchConvertToCHD` and
  `BatchConvertToCHD.Tests`, `AssemblyVersion`/`FileVersion`) plus a matching
  section in `WhatsNew.md`. A release tag is `release_<version>` and must match
  the csproj version - `scripts/ci/version.mjs` enforces this in CI.
- **Cutting a release:** bump both csproj versions, add the `## <version>`
  section to the top of `WhatsNew.md`, commit to `master`, then
  `git tag release_<version> && git push origin release_<version>`. The
  `Release` workflow tests, publishes both RIDs, zips them, and creates the
  GitHub release (title = version, body = the `WhatsNew.md` section).
  Re-running the workflow uploads assets with `--clobber`.

## Library NuGet packages (manual publish)

`PBPSharp` (<https://www.nuget.org/packages/PBPSharp>), `CSOSharp`
(<https://www.nuget.org/packages/CSOSharp>), `CCDSharp`
(<https://www.nuget.org/packages/CCDSharp>) and `MDSSharp`
(<https://www.nuget.org/packages/MDSSharp>) are published as NuGet packages.
Releases are **manual only**: do not add pack/push steps to the solution CI
workflows, and never commit or echo the API key. It is read from the
`NUGET_API_KEY` user environment variable.

- **Version** lives in the project file (`PBPSharp/PBPSharp.csproj`,
  `CSOSharp/CSOSharp.csproj`, `CCDSharp/CCDSharp.csproj` and
  `MDSSharp/MDSSharp.csproj`): bump
  `<Version>`, `<AssemblyVersion>` and `<FileVersion>` together. A pushed
  version is immutable - to change anything, bump and push again.
- **Target frameworks** are `net8.0;net9.0;net10.0` so both packages serve
  .NET 8, 9 and 10 consumers. `GenerateDocumentationFile` must stay on so each
  TFM ships its `.xml` next to the assembly.
- **Metadata** must keep `PackageProjectUrl` and `RepositoryUrl` pointed at
  <https://github.com/purelogiccode/BatchConvertToCHD>.
- **README**: `<Project>/README.md` is the package readme (packed at the package
  root as `README.md`). Keep it descriptive with usage examples and an API
  reference, and update it whenever the public surface changes.
- **Icon**: `<Project>/icon/icon.png` is packed as `icon.png` through its `None`
  item. It must stay under NuGet's 1 MB icon limit (currently ~280-300 KB at
  600x600). `icon.ico` stays in the repo only; do not pack a generated or
  resized copy.
- **XML docs**: every public member and, per this repo's convention, every
  method including private/internal ones carries documentation. The build must
  stay warning-free.

Release procedure (run from the repo root; substitute the project and package
name for the library being published):

```powershell
# 1. bump the three version fields in <project>.csproj, then:
dotnet build PBPSharp/PBPSharp.csproj -c Release        # or CSOSharp/CSOSharp.csproj, CCDSharp/CCDSharp.csproj

# library tests plus real-file integration tests (needs the local sample folder)
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj -c Release --filter "FullyQualifiedName~Pbp"   # or ~Cso
# (CCDSharp has no dedicated test class yet; run the full suite when it changes)

# 2. pack with package validation enabled
dotnet pack PBPSharp/PBPSharp.csproj -c Release -o <out-dir> -p:EnablePackageValidation=true

# 3. inspect the nupkg: README.md, icon.png, and lib/<tfm>/<name>.dll + .xml
#    for net8.0, net9.0 and net10.0

# 4. push with the user environment key (never hard-code it)
dotnet nuget push <out-dir>/PBPSharp.<version>.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json

# 5. verify indexing (takes a few minutes)
Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/pbpsharp/index.json   # or csosharp, ccdsharp
```

Before pushing, smoke-test the packed `.nupkg` in a throwaway consumer project
targeting `net8.0;net9.0;net10.0` (restore from a local package source and open
a real file on each runtime).

## CI workflows

| Workflow | Trigger | Purpose |
|---|---|---|
| `.github/workflows/ci.yml` | push/PR to `master`, manual | Restore, build, test; uploads trx results |
| `.github/workflows/release.yml` | tag `release_*`, manual with tag | Validate version, test, package x64+arm64 zips, publish GitHub release |
| `.github/workflows/docs.yml` | push to `master`, manual | Build/deploy Jekyll Pages site and sync `docs/` to the GitHub wiki |

All workflows set up Node 24 where scripts run. Use the latest action majors
(`actions/checkout@v7`, `setup-dotnet@v6`, `setup-node@v7`, artifacts v7/v8) so
the runner's Node 24 runtime is used.

## Docs = GitHub Pages + GitHub wiki

- `docs/` is the single source of truth for both. Never edit the wiki in the
  GitHub UI.
- `scripts/ci/sync-wiki.mjs` copies `docs/*.md` to the wiki (`index.md` becomes
  `Home.md`, Jekyll front matter is stripped), adds `WhatsNew.md`, and leaves
  the wiki-only `_Sidebar.md` alone.
- The wiki push needs a repository secret named `WIKI_TOKEN` (classic PAT with
  the `repo` scope, or fine-grained PAT with `Contents: Read and write`) because
  `GITHUB_TOKEN` cannot push to the wiki repository. Without the secret the sync
  step logs a message and skips.
- Pages deployment requires Settings -> Pages -> Source: "GitHub Actions".
- `docs/_config.yml` uses the `just-the-docs` remote theme; keep the front
  matter (`title`, `nav_order`) on docs pages for navigation.

## Conventions

- Match existing code style; `DebugType` is `embedded` and analyzers
  (Meziantou, Roslynator) run on every build. Do not add code comments unless
  asked.
- Bundled binaries (`7za*.exe`, `chdman*.exe`, `CHDSharp*.exe`) are committed
  and copied to the output with `CopyToOutputDirectory=Always`; only replace
  them deliberately.
- `BatchConvertToCHD/bin/Release/` is the local release archive: every
  version's `release_<version>_win-<rid>.zip` lives there. Copy new zips in,
  **never delete files inside that path** (also avoid commands that would
  clean it - it sits beside, not inside, the per-TFM build output).
- Tests are xUnit; add regression tests next to the existing ones in
  `BatchConvertToCHD.Tests/`. The suite must pass before a release.
  `[Trait("Category", "Integration")]` classes depend on local sample folders
  and are excluded from CI with `--filter "Category!=Integration"`.
