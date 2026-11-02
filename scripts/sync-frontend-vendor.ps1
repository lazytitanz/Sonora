#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Downloads pinned Bootstrap and Bootstrap Icons releases into the Test Player www/vendor folder.
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

$bootstrapVersion = "5.3.8"
$iconsVersion = "1.13.2"
$vendorRoot = Join-Path $RepoRoot "samples\Sonora.TestPlayer\www\vendor"
$tempRoot = Join-Path $RepoRoot "deps\cache\frontend-vendor"
$bootstrapZip = Join-Path $tempRoot "bootstrap-$bootstrapVersion-dist.zip"
$iconsZip = Join-Path $tempRoot "bootstrap-icons-$iconsVersion.zip"

New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
New-Item -ItemType Directory -Force -Path $vendorRoot | Out-Null

$bootstrapUrl = "https://github.com/twbs/bootstrap/releases/download/v$bootstrapVersion/bootstrap-$bootstrapVersion-dist.zip"
$iconsUrl = "https://github.com/twbs/icons/releases/download/v$iconsVersion/bootstrap-icons-$iconsVersion.zip"

if (-not (Test-Path $bootstrapZip)) {
    Write-Host "Downloading Bootstrap $bootstrapVersion ..."
    Invoke-WebRequest -Uri $bootstrapUrl -OutFile $bootstrapZip -UseBasicParsing
}
if (-not (Test-Path $iconsZip)) {
    Write-Host "Downloading Bootstrap Icons $iconsVersion ..."
    Invoke-WebRequest -Uri $iconsUrl -OutFile $iconsZip -UseBasicParsing
}

$bootstrapExtract = Join-Path $tempRoot "bootstrap-extract"
$iconsExtract = Join-Path $tempRoot "icons-extract"
if (Test-Path $bootstrapExtract) { Remove-Item -Recurse -Force $bootstrapExtract }
if (Test-Path $iconsExtract) { Remove-Item -Recurse -Force $iconsExtract }
Expand-Archive -Path $bootstrapZip -DestinationPath $bootstrapExtract -Force
Expand-Archive -Path $iconsZip -DestinationPath $iconsExtract -Force

$bootstrapDest = Join-Path $vendorRoot "bootstrap-$bootstrapVersion"
$iconsDest = Join-Path $vendorRoot "bootstrap-icons-$iconsVersion"
if (Test-Path $bootstrapDest) { Remove-Item -Recurse -Force $bootstrapDest }
if (Test-Path $iconsDest) { Remove-Item -Recurse -Force $iconsDest }

$bootstrapSrc = Get-ChildItem $bootstrapExtract -Directory | Select-Object -First 1
Copy-Item -Recurse $bootstrapSrc.FullName $bootstrapDest

$bsLicense = Join-Path $bootstrapDest "LICENSE"
if (-not (Test-Path $bsLicense)) {
    @"
Bootstrap v$bootstrapVersion
https://getbootstrap.com/
Copyright (c) 2011-2025 The Bootstrap Authors
Licensed under the MIT License: https://github.com/twbs/bootstrap/blob/v$bootstrapVersion/LICENSE
"@ | Set-Content -Path (Join-Path $bootstrapDest "LICENSE.txt") -Encoding utf8
}

# GitHub Icons release layout: CSS + fonts/ at the package root (not under font/).
$iconsSrc = Join-Path $iconsExtract "bootstrap-icons-$iconsVersion"
if (-not (Test-Path $iconsSrc)) {
    $iconsSrc = (Get-ChildItem $iconsExtract -Directory | Select-Object -First 1).FullName
}

New-Item -ItemType Directory -Force -Path (Join-Path $iconsDest "font\fonts") | Out-Null
Copy-Item (Join-Path $iconsSrc "bootstrap-icons.css") (Join-Path $iconsDest "font\")
Copy-Item (Join-Path $iconsSrc "bootstrap-icons.min.css") (Join-Path $iconsDest "font\")
Copy-Item (Join-Path $iconsSrc "fonts\*") (Join-Path $iconsDest "font\fonts\")

$licenseCandidates = @("LICENSE.md", "LICENSE", "license")
foreach ($name in $licenseCandidates) {
    $candidate = Join-Path $iconsSrc $name
    if (Test-Path $candidate) {
        Copy-Item $candidate $iconsDest
        break
    }
}
if (-not (Get-ChildItem $iconsDest -Filter "LICENSE*" -ErrorAction SilentlyContinue)) {
    @"
Bootstrap Icons v$iconsVersion
https://icons.getbootstrap.com/
Copyright (c) 2019-2025 The Bootstrap Authors
Licensed under the MIT License: https://github.com/twbs/icons/blob/v$iconsVersion/LICENSE.md
"@ | Set-Content -Path (Join-Path $iconsDest "LICENSE.txt") -Encoding utf8
}

Write-Host "Vendor assets written to $vendorRoot"
Get-ChildItem $vendorRoot | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "Icons font CSS: $(Test-Path (Join-Path $iconsDest 'font\bootstrap-icons.min.css'))"
Write-Host "Icons woff2:    $(Test-Path (Join-Path $iconsDest 'font\fonts\bootstrap-icons.woff2'))"
