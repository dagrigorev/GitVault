#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads the icon set and generates the Avalonia resource dictionary that exposes it.

.DESCRIPTION
    Icons are Material Symbols (Apache-2.0), fetched through the Iconify API. Apache-2.0 is one
    of the licences this project accepts, and it carries no attribution obligation that every
    downstream clone would inherit.

    Icons8 was considered first. Its SVG endpoint requires a paid API key (HTTP 403); only PNG is
    free, and raster icons cannot stay sharp across the 100-200% DPI range the UI targets.

    Each Material Symbol is a single path on a 24x24 canvas, so the generator extracts the path
    data into a StreamGeometry. That means no SVG rendering dependency, and every icon inherits
    the theme's foreground colour like any other control.

    Run this after editing the icon list, then commit the generated files.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../src/GitVault.App/Assets'),
    [switch] $SkipDownload
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Resource key -> Iconify icon name. Outline variants throughout, so the set stays coherent.
$icons = [ordered]@{
    'Dashboard'    = 'material-symbols:space-dashboard-outline'
    'Identities'   = 'material-symbols:badge-outline'
    'Keys'         = 'material-symbols:key-outline'
    'Agents'       = 'material-symbols:cable'
    'Credentials'  = 'material-symbols:lock-outline'
    'Clients'      = 'material-symbols:apps'
    'Profiles'     = 'material-symbols:switch-account-outline'
    'Repositories' = 'material-symbols:folder-open-outline'
    'Settings'     = 'material-symbols:settings-outline'
    'Logs'         = 'material-symbols:receipt-long-outline'

    'Rescan'       = 'material-symbols:refresh'
    'Search'       = 'material-symbols:search'
    'Copy'         = 'material-symbols:content-copy-outline'
    'Reveal'       = 'material-symbols:visibility-outline'
    'Hide'         = 'material-symbols:visibility-off-outline'
    'FolderOpen'   = 'material-symbols:folder-open-outline'
    'Apply'        = 'material-symbols:check-circle-outline'
    'Rollback'     = 'material-symbols:undo'
    'Preview'      = 'material-symbols:preview'
    'Delete'       = 'material-symbols:delete-outline'
    'Shield'       = 'material-symbols:shield-outline'
    'Hardware'     = 'material-symbols:usb'

    'SeverityHigh'   = 'material-symbols:error-outline'
    'SeverityMedium' = 'material-symbols:warning-outline'
    'SeverityLow'    = 'material-symbols:info-outline'
    'SeverityOk'     = 'material-symbols:check-circle-outline'
}

$svgDirectory = Join-Path $OutputDirectory 'Icons'
New-Item -ItemType Directory -Force -Path $svgDirectory | Out-Null

$paths = [ordered]@{}

foreach ($entry in $icons.GetEnumerator()) {
    $key = $entry.Key
    $name = $entry.Value
    $file = Join-Path $svgDirectory "$key.svg"

    if (-not $SkipDownload -or -not (Test-Path $file)) {
        $url = "https://api.iconify.design/$($name -replace ':', ':').svg"
        try {
            $response = Invoke-WebRequest -Uri $url -TimeoutSec 20 -UseBasicParsing
        }
        catch {
            throw "Could not fetch $name from Iconify: $($_.Exception.Message)"
        }

        # PowerShell 7 hands back a string for text content types; Windows PowerShell bytes.
        $svg = if ($response.Content -is [byte[]]) {
            [System.Text.Encoding]::UTF8.GetString($response.Content)
        }
        else {
            [string]$response.Content
        }

        if ($svg -notmatch '<path') { throw "$name did not return a path-based SVG." }

        [System.IO.File]::WriteAllText($file, $svg, [System.Text.UTF8Encoding]::new($false))
    }

    $svg = Get-Content -LiteralPath $file -Raw

    # Material Symbols are a single path; anything else would need a different representation.
    $matches = [regex]::Matches($svg, '\sd="([^"]+)"')
    if ($matches.Count -ne 1) {
        throw "$name has $($matches.Count) paths; the generator expects exactly one."
    }

    $paths[$key] = $matches[0].Groups[1].Value
    Write-Host ("{0,-16} {1}" -f $key, $name)
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('<!--')
[void]$builder.AppendLine('    GENERATED FILE - do not edit by hand.')
[void]$builder.AppendLine('    Source: build/generate-icons.ps1')
[void]$builder.AppendLine('')
[void]$builder.AppendLine('    Icons are Material Symbols, licensed Apache-2.0, fetched via the Iconify API.')
[void]$builder.AppendLine('    Each is a single path on a 24x24 canvas, so it renders as a StreamGeometry and')
[void]$builder.AppendLine('    inherits the theme foreground like any other control.')
[void]$builder.AppendLine('-->')
[void]$builder.AppendLine('<ResourceDictionary xmlns="https://github.com/avaloniaui"')
[void]$builder.AppendLine('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">')

foreach ($entry in $paths.GetEnumerator()) {
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("  <StreamGeometry x:Key=`"Icon$($entry.Key)`">$($entry.Value)</StreamGeometry>")
}

[void]$builder.AppendLine()
[void]$builder.AppendLine('</ResourceDictionary>')

$target = Join-Path $OutputDirectory 'Icons.axaml'
[System.IO.File]::WriteAllText($target, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "wrote $target ($($paths.Count) icons)"

$license = @'
# Icons

Material Symbols, licensed under the Apache License 2.0, fetched through the Iconify API by
`build/generate-icons.ps1`.

    https://github.com/google/material-design-icons
    Copyright the Material Symbols authors, licensed under Apache-2.0.

Apache-2.0 is one of the licences this project accepts. It permits redistribution in source and
binary form and imposes no attribution link inside the running application.

## Why not Icons8

Icons8 was the first choice. Two things ruled it out:

  * Its SVG endpoint requires a paid API key and answers HTTP 403 without one. Only the PNG
    endpoint is free, and raster icons cannot stay sharp across the 100-200% DPI range this UI
    targets.
  * The Icons8 free licence requires a visible attribution link inside the application and
    restricts redistribution of the icon files, an obligation every clone of this repository
    would inherit. That conflicts with the project's stated licence posture.

The .svg files here are kept for provenance; the application binds to the generated
`Icons.axaml`, not to the SVGs directly.
'@

[System.IO.File]::WriteAllText(
    (Join-Path $svgDirectory 'README.md'), $license, [System.Text.UTF8Encoding]::new($false))
Write-Host 'wrote Assets/Icons/README.md'
