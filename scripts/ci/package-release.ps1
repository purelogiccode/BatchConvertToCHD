<#
.SYNOPSIS
    Builds a release zip for one Windows runtime identifier.

.DESCRIPTION
    Stages a published BatchConvertToCHD output folder, drops the binaries that
    belong to the other architecture, adds LICENSE.txt and ReadMe.md, and zips
    the result as release_<version>_<rid>.zip. The app itself is published
    framework-dependent and single-file, so the .NET runtime is never bundled.

.PARAMETER Rid
    win-x64 or win-arm64.

.PARAMETER Version
    Application version, e.g. 3.7.0.

.PARAMETER PublishDir
    Output folder of dotnet publish for the given Rid.

.PARAMETER OutputDir
    Folder that receives release_<version>_<rid>.zip.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
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

if ($Rid -eq 'win-x64') {
    $toolFiles = @('7za.exe', 'chdman.exe', 'CHDSharp.exe')
    $otherArchFiles = @('7za_arm64.exe', 'chdman_arm64.exe', 'CHDSharp_arm64.exe')
}
else {
    $toolFiles = @('7za_arm64.exe', 'chdman_arm64.exe', 'CHDSharp_arm64.exe')
    $otherArchFiles = @('7za.exe', 'chdman.exe', 'CHDSharp.exe')
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("bctchd-stage-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $stage -Recurse -Force

    foreach ($name in $otherArchFiles) {
        $path = Join-Path $stage $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    foreach ($extra in @('LICENSE.txt', 'ReadMe.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $extra) -Destination $stage -Force
    }

    $expected = @('BatchConvertToCHD.exe', 'LICENSE.txt', 'ReadMe.md') + $toolFiles
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
