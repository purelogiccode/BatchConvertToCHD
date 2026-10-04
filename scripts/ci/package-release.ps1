<#
.SYNOPSIS
    Builds a release zip for one runtime identifier.

.DESCRIPTION
    Stages a published CHDStudio output folder, drops the binaries that
    belong to the other architecture, the library .xml IntelliSense files and the
    native .pdb debug symbols, adds LICENSE.txt, ReadMe.md and WhatsNew.md, and
    zips the result as release_<version>_<rid>.zip. The app itself is published
    framework-dependent and single-file, so the .NET runtime is never bundled.

.PARAMETER Rid
    win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 or osx-arm64.

.PARAMETER Version
    Application version, e.g. 3.9.0.

.PARAMETER PublishDir
    Output folder of dotnet publish for the given Rid.

.PARAMETER OutputDir
    Folder that receives release_<version>_<rid>.zip.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publishDir = (Resolve-Path -LiteralPath $PublishDir).Path
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$outputDir = (Resolve-Path -LiteralPath $OutputDir).Path

$isWindowsRid = $Rid.StartsWith('win-')
$isMacRid = $Rid.StartsWith('osx-')

if ($Rid -eq 'win-x64') {
    $toolFiles = @('7za.exe', 'chdman.exe')
    $otherArchFiles = @('7za_arm64.exe', 'chdman_arm64.exe')
}
elseif ($Rid -eq 'win-arm64') {
    $toolFiles = @('7za_arm64.exe', 'chdman_arm64.exe')
    $otherArchFiles = @('7za.exe', 'chdman.exe')
}
else {
    # Linux and macOS use the official 7-Zip console build copied as "7zz".
    $toolFiles = @('7zz', '7-Zip-License.txt')
    $otherArchFiles = @()
}

if ($isWindowsRid) {
    $executable = 'CHDStudio.exe'
    $nativeFiles = @('av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll')
}
elseif ($isMacRid) {
    $executable = 'CHDStudio'
    $nativeFiles = @(
        'libAvaloniaNative.dylib',
        'libHarfBuzzSharp.dylib',
        'libSkiaSharp.dylib'
    )
}
else {
    $executable = 'CHDStudio'
    $nativeFiles = @('libHarfBuzzSharp.so', 'libSkiaSharp.so')
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("chdstudio-stage-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $stage -Recurse -Force

    # The release bundle is flat: any subdirectory in the publish output is either a
    # stale artifact of a reused output folder or an unintended payload, so drop it.
    Get-ChildItem -LiteralPath $stage -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force

    foreach ($name in $otherArchFiles) {
        $path = Join-Path $stage $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    # Library .xml IntelliSense files and native debug symbols (*.pdb) in the publish
    # output are never used at runtime; they do not belong in the release bundle.
    Get-ChildItem -LiteralPath $stage -Filter '*.xml' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    foreach ($extra in @('LICENSE.txt', 'ReadMe.md', 'WhatsNew.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $extra) -Destination $stage -Force
    }

    # The single-file executable still loads the platform's native rendering/text
    # libraries from beside it, so those must survive pruning.
    $expected =
        @($executable, 'LICENSE.txt', 'ReadMe.md', 'WhatsNew.md') + $nativeFiles + $toolFiles
    $missing = @($expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $stage $_)) })
    if ($missing.Count -gt 0) {
        throw "Release stage is missing required file(s): $($missing -join ', ')"
    }

    $zipPath = Join-Path $outputDir "release_${Version}_${Rid}.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

    Write-Host "Created $zipPath"
    Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
        Write-Host ("  {0,-24} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB))
    }
    Write-Host ("SHA256: " + (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash)
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
