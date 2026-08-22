#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails the build when a project's line coverage falls below its threshold.

.DESCRIPTION
    `dotnet test --collect:"XPlat Code Coverage"` writes a Cobertura report but has no way to
    enforce a minimum, so the gate lives here. Only the projects named below are gated: a
    threshold on UI or platform code would be a number chased for its own sake, whereas
    GitVault.Core is where the parsing and the file mutation live.

    The script collects the coverage itself rather than reading whatever happens to be lying in
    the results directory. It did the latter once, and the consequence was worse than no gate:
    every run reported the same figure from a report weeks old, so the number looked stable while
    it was simply not being measured. A gate that cannot fail is not a gate.

    Pass -SkipTests to measure an existing report deliberately — for a second opinion on a run you
    just did, not as the normal path.
#>
[CmdletBinding()]
param(
    [string] $ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/coverage'),
    [hashtable] $Thresholds = @{ 'GitVault.Core' = 75.0 },
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

if (-not $SkipTests) {
    # Cleared first: the measurement below takes the best figure seen for each package across every
    # report present, so one stale report is enough to hide a real fall.
    if (Test-Path -LiteralPath $ResultsDirectory) {
        Remove-Item -LiteralPath $ResultsDirectory -Recurse -Force
    }

    $solution = Join-Path $PSScriptRoot '../GitVault.sln'

    Write-Host 'Running the tests with coverage collection...'
    & dotnet test $solution --configuration Release --nologo --verbosity quiet `
        --collect:"XPlat Code Coverage" --results-directory $ResultsDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "The test run failed ($LASTEXITCODE); coverage was not measured."
    }
}

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
