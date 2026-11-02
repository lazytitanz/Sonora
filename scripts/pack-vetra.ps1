#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Clones the pinned Vetra commit and packs NuGet packages into deps/nuget (gitignored).

.DESCRIPTION
  Vetra is not published to NuGet.org. This script produces Vetra.Core and Vetra.WinForms
  packages into a repo-relative local feed so Sonora.TestPlayer can restore reproducibly.

  Pin is defined in scripts/vetra-pin.txt (commit SHA).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
}

$pinFile = Join-Path $scriptDir "vetra-pin.txt"
if (-not (Test-Path $pinFile)) {
    throw "Missing pin file: $pinFile"
}

$pinLines = Get-Content $pinFile | Where-Object { $_ -and ($_ -notmatch '^\s*#') }
$commit = ($pinLines | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($commit)) {
    throw "vetra-pin.txt does not contain a commit SHA."
}

$cacheRoot = Join-Path $RepoRoot "deps\cache\vetra"
$nugetOut = Join-Path $RepoRoot "deps\nuget"
$repoUrl = "https://github.com/lazytitanz/Vetra.git"

Write-Host "Vetra pin: $commit"
Write-Host "Cache:     $cacheRoot"
Write-Host "NuGet out: $nugetOut"

New-Item -ItemType Directory -Force -Path $nugetOut | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $cacheRoot -Parent) | Out-Null

$needClone = $true
if (Test-Path (Join-Path $cacheRoot ".git")) {
    Push-Location $cacheRoot
    try {
        $head = (git rev-parse HEAD).Trim()
        if ($head -eq $commit) {
            $needClone = $false
            Write-Host "Cache already at pinned commit."
        }
    }
    finally {
        Pop-Location
    }
}

if ($needClone) {
    if (Test-Path $cacheRoot) {
        Remove-Item -Recurse -Force $cacheRoot
    }
    Write-Host "Cloning Vetra @ $commit ..."
    git clone --filter=blob:none --no-checkout $repoUrl $cacheRoot
    Push-Location $cacheRoot
    try {
        git fetch --depth 1 origin $commit
        git checkout $commit
    }
    finally {
        Pop-Location
    }
}

# Vetra's own nuget.config expects ./artifacts/nuget for local restores while packing WinForms.
$vetraLocalFeed = Join-Path $cacheRoot "artifacts\nuget"
New-Item -ItemType Directory -Force -Path $vetraLocalFeed | Out-Null

Write-Host "Packing Vetra.Core ..."
dotnet pack (Join-Path $cacheRoot "src\Vetra.Core\Vetra.Core.csproj") -c Release -o $nugetOut --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet pack Vetra.Core failed ($LASTEXITCODE)" }

# Stage Core into Vetra's local feed so WinForms restore can resolve the sibling package.
Copy-Item (Join-Path $nugetOut "Vetra.Core.*.nupkg") $vetraLocalFeed -Force

Write-Host "Packing Vetra.WinForms ..."
dotnet pack (Join-Path $cacheRoot "src\Vetra.WinForms\Vetra.WinForms.csproj") -c Release -o $nugetOut --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet pack Vetra.WinForms failed ($LASTEXITCODE)" }

Write-Host "Vetra packages ready in $nugetOut"
Get-ChildItem $nugetOut -Filter "Vetra.*.nupkg" | ForEach-Object { Write-Host "  $($_.Name)" }
