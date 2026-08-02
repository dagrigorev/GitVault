#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rasterises the GitVault mark into the icon formats each platform needs.

.DESCRIPTION
    Produces:
      * a multi-resolution .ico for the Windows executable and window icon
      * PNGs at the sizes Linux desktop entries expect
      * an .iconset directory that `iconutil` turns into a .icns on macOS

    Requires ImageMagick 7 (`magick`). It is not installed by this script: a build script that
    reaches out to install tooling is one nobody can audit.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot 'appicon/gitvault.svg'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../src/GitVault.App/Assets')
)

$ErrorActionPreference = 'Stop'

$magick = (Get-Command magick -ErrorAction SilentlyContinue).Source
if (-not $magick) {
    $magick = Get-ChildItem 'C:\Program Files' -Filter 'ImageMagick*' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'magick.exe' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

if (-not $magick) { throw 'ImageMagick 7 (magick) is required to regenerate the application icon.' }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$iconsetDirectory = Join-Path $PSScriptRoot 'appicon/GitVault.iconset'
New-Item -ItemType Directory -Force -Path $iconsetDirectory | Out-Null

$sizes = 16, 24, 32, 48, 64, 128, 256, 512

$pngs = foreach ($size in $sizes) {
    $png = Join-Path $iconsetDirectory "icon_${size}x${size}.png"

    # -background none keeps the rounded corners transparent instead of matting them to white.
    & $magick -background none -density 384 $Source -resize "${size}x${size}" $png
    if ($LASTEXITCODE -ne 0) { throw "magick failed rendering ${size}px" }

    $png
}

# Windows: one .ico carrying every size, so Explorer and the taskbar each pick what they need.
$ico = Join-Path $OutputDirectory 'gitvault.ico'
& $magick @($pngs | Where-Object { $_ -notmatch '512' }) $ico
if ($LASTEXITCODE -ne 0) { throw 'magick failed writing the .ico' }
Write-Host "wrote $ico"

# The window icon and the Linux desktop entry both take a PNG.
Copy-Item (Join-Path $iconsetDirectory 'icon_256x256.png') (Join-Path $OutputDirectory 'gitvault.png') -Force
Write-Host "wrote $(Join-Path $OutputDirectory 'gitvault.png')"

# macOS wants Retina variants named this way before iconutil will accept the directory.
foreach ($size in 16, 32, 128, 256) {
    $double = $size * 2
    $source = Join-Path $iconsetDirectory "icon_${double}x${double}.png"
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $iconsetDirectory "icon_${size}x${size}@2x.png") -Force
    }
}

Write-Host "wrote $iconsetDirectory (run 'iconutil -c icns' on macOS to produce the .icns)"
