#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes GitVault for Windows and produces a portable zip, plus an installer when Inno
    Setup is available.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$publishDir = Join-Path $repoRoot "artifacts/$Runtime"
$installerDir = Join-Path $repoRoot 'artifacts/installers'

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Write-Host "Publishing $Runtime…"
& dotnet publish (Join-Path $repoRoot 'src/GitVault.App/GitVault.App.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:InvariantGlobalization=false `
    -p:Version=$Version `
    --output $publishDir

if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$zipPath = Join-Path $installerDir "GitVault-$Version-$Runtime-portable.zip"
Remove-Item $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
Write-Host "wrote $zipPath"

$iscc = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
}

if ($iscc) {
    $architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }

    & $iscc (Join-Path $PSScriptRoot 'gitvault.iss') `
        "/DSourceDir=$publishDir" `
        "/DAppVersion=$Version" `
        "/DArchitecture=$architecture"

    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    Write-Host 'installer written to artifacts/installers'
}
else {
    Write-Warning 'Inno Setup (iscc) was not found; only the portable zip was produced.'
}
