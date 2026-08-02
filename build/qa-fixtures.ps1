#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates (or removes) a throwaway workspace for manual QA.

.DESCRIPTION
    GitVault reads private keys and credential stores. Manual testing must therefore never use
    the tester's own material, and the destructive cases must never point at a real repository.

    Redirecting the whole profile was tried and does not work on Windows: .NET resolves
    SpecialFolder.UserProfile through the shell, not through %USERPROFILE%, and overriding
    %APPDATA% makes GetFolderPath return an empty string. So this script does the honest thing
    instead — it creates clearly-named throwaway artifacts in one directory that the tester points
    GitVault at, and leaves the real profile alone.

    What it creates under -Workspace:
      repos/alpha, repos/beta   two git repositories, safe to activate profiles against
      keys/                     throwaway SSH keys in several formats, one deliberately unhealthy
      credentials/              a fake git-credentials file, NOT the real one
      README.txt                what everything is, and how to remove it

.EXAMPLE
    pwsh build/qa-fixtures.ps1 -Workspace D:\gitvault-qa
    pwsh build/qa-fixtures.ps1 -Workspace D:\gitvault-qa -Remove
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Workspace,
    [switch] $Remove,
    [string] $Passphrase = 'gitvault-qa'
)

$ErrorActionPreference = 'Stop'

if ($Remove) {
    if (Test-Path $Workspace) {
        # git marks objects read-only on Windows; clear that before deleting.
        Get-ChildItem $Workspace -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Attributes = 'Normal' }

        Remove-Item $Workspace -Recurse -Force
        Write-Host "removed $Workspace"
    }
    else {
        Write-Host "$Workspace does not exist; nothing to remove."
    }

    return
}

if (Test-Path $Workspace) {
    throw "$Workspace already exists. Remove it first with -Remove, so a stale fixture cannot be mistaken for a fresh one."
}

$keygen = (Get-Command ssh-keygen -ErrorAction SilentlyContinue).Source
if (-not $keygen) { throw 'ssh-keygen is required to build the QA fixtures.' }

$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) { throw 'git is required to build the QA fixtures.' }

$keys = Join-Path $Workspace 'keys'
$repos = Join-Path $Workspace 'repos'
$credentials = Join-Path $Workspace 'credentials'

foreach ($directory in @($keys, $repos, $credentials)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

# --------------------------------------------------------------------------- keys
# A spread wide enough to exercise the parser, the health checks and the badges.
& $keygen -t ed25519 -f (Join-Path $keys 'qa_ed25519') -N '' -C 'qa-ed25519@example.invalid' -q
& $keygen -t ed25519 -f (Join-Path $keys 'qa_ed25519_locked') -N $Passphrase -C 'qa-locked@example.invalid' -q
& $keygen -t rsa -b 2048 -f (Join-Path $keys 'qa_rsa2048_short') -N '' -C 'qa-rsa2048@example.invalid' -q
& $keygen -t rsa -b 4096 -f (Join-Path $keys 'qa_rsa4096') -N '' -C 'qa-rsa4096@example.invalid' -q
& $keygen -t ecdsa -b 256 -f (Join-Path $keys 'qa_ecdsa256') -N '' -C 'qa-ecdsa@example.invalid' -q

# An orphaned public key, and a private key with no .pub, so both findings appear.
Copy-Item (Join-Path $keys 'qa_ecdsa256.pub') (Join-Path $keys 'qa_orphan.pub')
Remove-Item (Join-Path $keys 'qa_rsa4096.pub')

# A file that is not a key at all: the scan must skip it without complaint.
Set-Content -LiteralPath (Join-Path $keys 'not-a-key.txt') -Value 'plain text, not a key' -Encoding utf8

# --------------------------------------------------------------------------- repositories
foreach ($name in 'alpha', 'beta') {
    $path = Join-Path $repos $name
    New-Item -ItemType Directory -Force -Path $path | Out-Null

    & $git -C $path init --quiet
    & $git -C $path remote add origin "https://github.com/example/$name.git"
}

# beta carries a local identity, so "which identity is active here" has something to resolve.
& $git -C (Join-Path $repos 'beta') config --local user.name 'QA Beta'
& $git -C (Join-Path $repos 'beta') config --local user.email 'qa-beta@example.invalid'

# --------------------------------------------------------------------------- credentials
# A plaintext store in the shape git's `store` helper writes. It is NOT ~/.git-credentials:
# the point of the exercise is a file the tester can safely delete afterwards.
Set-Content -LiteralPath (Join-Path $credentials '.git-credentials') -Encoding utf8 -Value @'
https://qa-user:qa-not-a-real-password@git.example.invalid
https://qa-other@git2.example.invalid
'@

# --------------------------------------------------------------------------- profile
# GitVault has no profile editor yet, so the activation cases need a hand-authored profiles.json.
# This one targets repos/alpha and the throwaway ed25519 key, which keeps activation confined to
# a repository the tester can delete.
$profileJson = @"
{
  "`$comment": "GitVault manual QA fixture. References only; no secret is stored in this file.",
  "profiles": [
    {
      "id": "$([Guid]::NewGuid())",
      "name": "QA Alpha",
      "identity": {
        "id": "$([Guid]::NewGuid())",
        "displayName": "QA Alpha <qa-alpha@example.invalid>",
        "userName": "QA Alpha",
        "email": "qa-alpha@example.invalid",
        "signingKeyId": null,
        "source": "GitGlobalConfig",
        "sourcePath": "",
        "hosts": [],
        "confidence": "Certain",
        "occurrences": []
      },
      "sshKeyId": null,
      "preferredAgent": null,
      "credentialHelper": null,
      "scope": "Repository",
      "repositoryPath": "$((Join-Path $repos 'alpha') -replace '\\', '\\')",
      "hostAliases": [
        {
          "alias": "qa-alpha",
          "hostName": "git.example.invalid",
          "user": "git",
          "identityFile": "$((Join-Path $keys 'qa_ed25519') -replace '\\', '\\')",
          "identitiesOnly": true,
          "extraOptions": {}
        }
      ],
      "sshKeyPath": "$((Join-Path $keys 'qa_ed25519') -replace '\\', '\\')",
      "credentialUserNames": {},
      "writeCoreSshCommand": true
    }
  ]
}
"@

Set-Content -LiteralPath (Join-Path $Workspace 'profiles.json') -Value $profileJson -Encoding utf8

# --------------------------------------------------------------------------- notes
Set-Content -LiteralPath (Join-Path $Workspace 'README.txt') -Encoding utf8 -Value @"
GitVault manual QA workspace
============================

Everything here is throwaway. No key, password or repository in this directory is real, and
nothing outside this directory was touched.

The passphrase on qa_ed25519_locked is: $Passphrase

Contents
  keys/         SSH keys in several formats. qa_rsa2048_short is deliberately below the
                recommended size, qa_orphan.pub has no private half, and qa_rsa4096 has no
                .pub file. Each of those should raise its own health finding.
  repos/alpha   a repository with no local identity, and the target of the sample profile
  repos/beta    a repository with a local identity, for the effective-identity checks
  credentials/  a plaintext credential file in git's `store` format
  profiles.json one profile, "QA Alpha", scoped to repos/alpha. GitVault has no profile editor,
                so copy this next to settings.json to make the activation cases runnable.

Pointing GitVault at it
  GitVault has no editor yet for scan roots and extra key folders, so add them to
  settings.json by hand and restart the application:

      %APPDATA%\GitVault\settings.json          (Windows)
      ~/.config/gitvault/settings.json          (Linux)
      ~/Library/Application Support/GitVault/settings.json   (macOS)

      "customKeyDirectories": [ "$($keys -replace '\\', '\\\\')" ],
      "repositoryScanRoots":  [ "$($repos -replace '\\', '\\\\')" ]

  Back the file up first; the QA plan has a step for restoring it.

Removing it
      pwsh build/qa-fixtures.ps1 -Workspace "$Workspace" -Remove
"@

Write-Host "created $Workspace"
Write-Host "  keys        $((Get-ChildItem $keys -File).Count) files"
Write-Host "  repos       alpha, beta"
Write-Host "  credentials plaintext store (fake)"
Write-Host ''
Write-Host 'Read README.txt in that directory before starting: it says how to point GitVault at it.'
