#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds PuTTY .ppk fixtures for the parser tests.

.DESCRIPTION
    PuTTY's Windows puttygen.exe is a GUI application and cannot convert keys from a script, so
    the .ppk fixtures are assembled here instead. Everything is derived with .NET's own RSA and
    HMAC primitives from the committed PEM key: nothing in this script shares code with the
    parser under test, which is the property that makes the fixtures worth having.

    VERIFY: the PPK v3 MAC key for an unencrypted key is taken to be zero-length, and the v2 MAC
    is taken to cover the plaintext private blob. Both should be confirmed against a real PuTTY
    before relying on the encrypted paths.
#>
[CmdletBinding()]
param(
    [string] $FixtureDirectory = (Join-Path $PSScriptRoot '../tests/fixtures/ssh'),
    [string] $Comment = 'rsa2048@example.com'
)

$ErrorActionPreference = 'Stop'

function ConvertTo-SshString {
    param([byte[]] $Bytes)
    $length = [BitConverter]::GetBytes([int]$Bytes.Length)
    [Array]::Reverse($length)
    return $length + $Bytes
}

function ConvertTo-SshMpint {
    param([byte[]] $Value)
    $trimmed = [System.Collections.Generic.List[byte]]::new()
    $seenNonZero = $false
    foreach ($b in $Value) {
        if (-not $seenNonZero -and $b -eq 0) { continue }
        $seenNonZero = $true
        $trimmed.Add($b)
    }
    if ($trimmed.Count -eq 0) { return ConvertTo-SshString -Bytes @() }
    if ($trimmed[0] -ge 0x80) { $trimmed.Insert(0, 0) }
    return ConvertTo-SshString -Bytes $trimmed.ToArray()
}

function Format-Base64Lines {
    param([byte[]] $Bytes)
    $text = [Convert]::ToBase64String($Bytes)
    $lines = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $text.Length; $i += 64) {
        $lines.Add($text.Substring($i, [Math]::Min(64, $text.Length - $i)))
    }
    return $lines
}

$pemPath = Join-Path $FixtureDirectory 'rsa2048_pem'
if (-not (Test-Path $pemPath)) { throw "Missing $pemPath; run generate-ssh-fixtures.ps1 first." }

$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem((Get-Content -LiteralPath $pemPath -Raw))
$p = $rsa.ExportParameters($true)

$algorithm = [System.Text.Encoding]::ASCII.GetBytes('ssh-rsa')
$publicBlob = (ConvertTo-SshString -Bytes $algorithm) +
              (ConvertTo-SshMpint -Value $p.Exponent) +
              (ConvertTo-SshMpint -Value $p.Modulus)

# PuTTY stores the RSA private half as d, p, q, iqmp; .NET's InverseQ is q^-1 mod p, which is
# exactly what PuTTY calls iqmp.
$privateBlob = (ConvertTo-SshMpint -Value $p.D) +
               (ConvertTo-SshMpint -Value $p.P) +
               (ConvertTo-SshMpint -Value $p.Q) +
               (ConvertTo-SshMpint -Value $p.InverseQ)

function New-PpkFile {
    param(
        [int] $Version,
        [string] $Path,
        [string] $Encryption = 'none'
    )

    $macData = (ConvertTo-SshString -Bytes ([System.Text.Encoding]::ASCII.GetBytes('ssh-rsa'))) +
               (ConvertTo-SshString -Bytes ([System.Text.Encoding]::ASCII.GetBytes($Encryption))) +
               (ConvertTo-SshString -Bytes ([System.Text.Encoding]::UTF8.GetBytes($Comment))) +
               (ConvertTo-SshString -Bytes $publicBlob) +
               (ConvertTo-SshString -Bytes $privateBlob)

    if ($Version -eq 2) {
        $sha1 = [System.Security.Cryptography.SHA1]::Create()
        $macKey = $sha1.ComputeHash([System.Text.Encoding]::ASCII.GetBytes('putty-private-key-file-mac-key'))
        $hmac = [System.Security.Cryptography.HMACSHA1]::new($macKey)
    }
    else {
        # v3, unencrypted: zero-length MAC key.
        $hmac = [System.Security.Cryptography.HMACSHA256]::new([byte[]]@())
    }

    $mac = ($hmac.ComputeHash($macData) | ForEach-Object { $_.ToString('x2') }) -join ''

    $publicLines = Format-Base64Lines -Bytes $publicBlob
    $privateLines = Format-Base64Lines -Bytes $privateBlob

    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add("PuTTY-User-Key-File-$($Version): ssh-rsa")
    $out.Add("Encryption: $Encryption")
    $out.Add("Comment: $Comment")
    $out.Add("Public-Lines: $($publicLines.Count)")
    $publicLines | ForEach-Object { $out.Add($_) }
    if ($Version -eq 3) {
        $out.Add('Key-Derivation: Argon2id')
        $out.Add('Argon2-Memory: 8192')
        $out.Add('Argon2-Passes: 13')
        $out.Add('Argon2-Parallelism: 1')
        $out.Add('Argon2-Salt: 0102030405060708090a0b0c0d0e0f10')
    }
    $out.Add("Private-Lines: $($privateLines.Count)")
    $privateLines | ForEach-Object { $out.Add($_) }
    $out.Add("Private-MAC: $mac")

    [System.IO.File]::WriteAllLines($Path, $out, [System.Text.UTF8Encoding]::new($false))
    Write-Host "wrote $Path"
}

New-PpkFile -Version 2 -Path (Join-Path $FixtureDirectory 'rsa2048_v2.ppk')
New-PpkFile -Version 3 -Path (Join-Path $FixtureDirectory 'rsa2048_v3.ppk')

# A v2 file whose MAC does not match, to prove the parser notices.
$tampered = Get-Content -LiteralPath (Join-Path $FixtureDirectory 'rsa2048_v2.ppk')
$tampered[-1] = 'Private-MAC: 0000000000000000000000000000000000000000'
[System.IO.File]::WriteAllLines(
    (Join-Path $FixtureDirectory 'rsa2048_v2_badmac.ppk'), $tampered, [System.Text.UTF8Encoding]::new($false))
Write-Host 'wrote rsa2048_v2_badmac.ppk'

# Record the fingerprint the key must produce, taken from ssh-keygen on the source key.
$keygen = (Get-Command ssh-keygen).Source
$line = (& $keygen -l -f (Join-Path $FixtureDirectory 'rsa2048_pem.pub')) -split '\s+'
Add-Content -LiteralPath (Join-Path $FixtureDirectory 'expected-fingerprints.tsv') -Encoding utf8 `
    -Value (('rsa2048_v2.ppk', $line[0], $line[1], '-', 'RSA') -join "`t")
Add-Content -LiteralPath (Join-Path $FixtureDirectory 'expected-fingerprints.tsv') -Encoding utf8 `
    -Value (('rsa2048_v3.ppk', $line[0], $line[1], '-', 'RSA') -join "`t")
Write-Host 'appended ppk fingerprints'
