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
  adds `LICENSE.txt` and `ReadMe.md`. Do not put both architectures in one zip.
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
- Tests are xUnit; add regression tests next to the existing ones in
  `BatchConvertToCHD.Tests/`. The suite must pass before a release.
  `[Trait("Category", "Integration")]` classes depend on local sample folders
  and are excluded from CI with `--filter "Category!=Integration"`.
