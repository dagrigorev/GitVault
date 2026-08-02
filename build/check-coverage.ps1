#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails the build when a project's line coverage falls below its threshold.

.DESCRIPTION
    `dotnet test --collect:"XPlat Code Coverage"` writes a Cobertura report but has no way to
    enforce a minimum, so the gate lives here. Only the projects named below are gated: a
    threshold on UI or platform code would be a number chased for its own sake, whereas
    GitVault.Core is where the parsing and the file mutation live.
#>
[CmdletBinding()]
param(
    [string] $ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/coverage'),
    [hashtable] $Thresholds = @{ 'GitVault.Core' = 75.0 }
)

$ErrorActionPreference = 'Stop'

$reports = Get-ChildItem -Path $ResultsDirectory -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue
if (-not $reports) {
    throw "No coverage report found under $ResultsDirectory. Run dotnet test with --collect:`"XPlat Code Coverage`" first."
}

# Several test projects each emit a report; take the best coverage seen for each package, since
# a package may be exercised from more than one test assembly.
$measured = @{}

foreach ($report in $reports) {
    [xml]$document = Get-Content -LiteralPath $report.FullName

    foreach ($package in $document.coverage.packages.package) {
        $rate = [double]$package.'line-rate' * 100.0
        if (-not $measured.ContainsKey($package.name) -or $measured[$package.name] -lt $rate) {
            $measured[$package.name] = $rate
        }
    }
}

$failed = $false

foreach ($name in ($measured.Keys | Sort-Object)) {
    $rate = $measured[$name]

    if ($Thresholds.ContainsKey($name)) {
        $threshold = $Thresholds[$name]
        if ($rate -lt $threshold) {
            Write-Host ("FAIL {0,-28} {1,6:N2}% (needs {2:N2}%)" -f $name, $rate, $threshold)
            $failed = $true
        }
        else {
            Write-Host ("PASS {0,-28} {1,6:N2}% (needs {2:N2}%)" -f $name, $rate, $threshold)
        }
    }
    else {
        Write-Host ("     {0,-28} {1,6:N2}%" -f $name, $rate)
    }
}

foreach ($name in $Thresholds.Keys) {
    if (-not $measured.ContainsKey($name)) {
        Write-Host ("FAIL {0,-28} not present in any coverage report" -f $name)
        $failed = $true
    }
}

if ($failed) {
    throw 'Coverage is below the required threshold.'
}

Write-Host 'Coverage thresholds met.'
