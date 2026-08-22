#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds a self-contained workspace for screenshots and demonstrations.

.DESCRIPTION
    Everything in the pictures in README.md comes from here: an invented identity, throwaway keys,
    and repositories with enough history for the editing pages to have something to show. Nothing
    is read from the machine it runs on, which is the point — documentation should not carry
    somebody's real address, key paths or project names around with it.

    Run the application against it with:

        dotnet run --project src/GitVault.App -- --data-root <workspace>

    The switch moves the home directory and the application-data directory together, so the
    application discovers this identity and these keys and writes its settings here.

.PARAMETER Workspace
    Directory to build. Deleted and recreated.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Workspace
)

$ErrorActionPreference = 'Stop'

$git = (Get-Command git -ErrorAction SilentlyContinue)?.Source
if (-not $git) { throw 'git is not on PATH.' }

if (Test-Path -LiteralPath $Workspace) {
    Remove-Item -LiteralPath $Workspace -Recurse -Force
}

$home_ = (New-Item -ItemType Directory -Force -Path $Workspace).FullName
$ssh = New-Item -ItemType Directory -Force -Path (Join-Path $home_ '.ssh')
$repos = New-Item -ItemType Directory -Force -Path (Join-Path $home_ 'projects')
$appData = New-Item -ItemType Directory -Force -Path (Join-Path $home_ 'AppData/GitVault')

# --------------------------------------------------------------------------- identity
# An invented person. The address is under .invalid, which is reserved precisely so that examples
# cannot accidentally name a real mailbox.
Set-Content -LiteralPath (Join-Path $home_ '.gitconfig') -Encoding utf8 -Value @'
[user]
	name = Alex Developer
	email = alex.developer@example.invalid
	signingkey = 6C1A9F2E4B8D0357
[core]
	sshCommand = ssh -i ~/.ssh/id_ed25519
	autocrlf = false
[credential]
	helper = manager
[init]
	defaultBranch = main
[includeIf "gitdir:~/projects/work/"]
	path = ~/.gitconfig-work
'@

Set-Content -LiteralPath (Join-Path $home_ '.gitconfig-work') -Encoding utf8 -Value @'
[user]
	name = Alex Developer
	email = alex@work.example.invalid
'@

# --------------------------------------------------------------------------- keys
# Real key files so the parsers have something genuine to read, generated here and thrown away
# with the workspace. One is left without a passphrase on purpose: the health check has to have
# something to report.
& ssh-keygen -t ed25519 -N '' -C 'alex.developer@example.invalid' -f (Join-Path $ssh 'id_ed25519') -q
& ssh-keygen -t rsa -b 4096 -N '' -C 'alex@work.example.invalid' -f (Join-Path $ssh 'id_rsa_work') -q
& ssh-keygen -t ecdsa -b 256 -N '' -C 'deploy@example.invalid' -f (Join-Path $ssh 'deploy_key') -q

# A public key with no private half beside it, which is a state worth showing.
Copy-Item (Join-Path $ssh 'deploy_key.pub') (Join-Path $ssh 'archived_key.pub')
Remove-Item (Join-Path $ssh 'deploy_key')

Set-Content -LiteralPath (Join-Path $ssh 'config') -Encoding utf8 -Value @'
Host github.com
	User git
	IdentityFile ~/.ssh/id_ed25519
	IdentitiesOnly yes

Host git.work.example.invalid
	User git
	IdentityFile ~/.ssh/id_rsa_work
	IdentitiesOnly yes
'@

# A plaintext credential store, so the warning that finds one has something to find.
Set-Content -LiteralPath (Join-Path $home_ '.git-credentials') -Encoding utf8 -Value @'
https://alex-developer:not-a-real-token@github.com
https://alex:not-a-real-token@git.work.example.invalid
'@

# --------------------------------------------------------------------------- repositories
function New-DemoCommit {
    param([string] $Repository, [string] $Path, [string] $Content, [string] $Subject)

    $full = Join-Path $Repository $Path
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $full) | Out-Null
    [System.IO.File]::WriteAllText($full, $Content)

    & $git -C $Repository add -- $Path
    & $git -C $Repository commit --quiet -m $Subject
}

function New-DemoRepository {
    param([string] $Name, [string] $Url, [string] $Group = '')

    $path = Join-Path (Join-Path $repos $Group) $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null

    & $git -C $path init --quiet -b main
    & $git -C $path config --local user.name 'Alex Developer'
    & $git -C $path config --local user.email 'alex.developer@example.invalid'
    & $git -C $path config --local core.autocrlf false
    & $git -C $path remote add origin $Url

    return $path
}

# The one with history: every editing page has something to show here.
$ledger = New-DemoRepository -Name 'ledger-service' -Url 'git@github.com:example-org/ledger-service.git'

New-DemoCommit $ledger 'README.md' "# Ledger service`n`nDouble-entry bookkeeping over HTTP.`n" 'Add the readme'
New-DemoCommit $ledger 'src/accounts.py' "def open_account(name):`n    return {'name': name, 'balance': 0}`n" 'Add account opening'
New-DemoCommit $ledger 'src/accounts.py' "def open_account(name, currency='EUR'):`n    return {'name': name, 'balance': 0, 'currency': currency}`n" 'Give an account a currency'
New-DemoCommit $ledger 'src/postings.py' "def post(account, amount):`n    account['balance'] += amount`n" 'Add postings'
New-DemoCommit $ledger 'config/service.key' "NOT-A-REAL-KEY-0000000000000000`n" 'Add a signing key by accident'
New-DemoCommit $ledger 'src/postings.py' "def post(account, amount):`n    if amount == 0:`n        raise ValueError('nothing to post')`n    account['balance'] += amount`n" 'Refuse an empty posting'

# A commit by somebody else, so the identity replacement has a case to leave alone.
& $git -C $ledger config --local user.name 'Sam Reviewer'
& $git -C $ledger config --local user.email 'sam.reviewer@example.invalid'
New-DemoCommit $ledger 'docs/review.md' "Reviewed the posting rules.`n" 'Record the review'
& $git -C $ledger config --local user.name 'Alex Developer'
& $git -C $ledger config --local user.email 'alex.developer@example.invalid'

[System.IO.File]::WriteAllText((Join-Path $ledger '.gitignore'), "__pycache__/`n*.pyc`n.env`n")
& $git -C $ledger add -- '.gitignore'
& $git -C $ledger commit --quiet -m 'Ignore build output'

[System.IO.File]::WriteAllText(
    (Join-Path $ledger '.gitmodules'),
    "[submodule `"protocol`"]`n`tpath = vendor/protocol`n`turl = https://github.com/example-org/protocol.git`n`tbranch = main`n")
& $git -C $ledger add -- '.gitmodules'
& $git -C $ledger commit --quiet -m 'Record the protocol submodule'

& $git -C $ledger tag -a 'v1.2.0' -m 'Release 1.2.0'
& $git -C $ledger branch 'feature/multi-currency'
& $git -C $ledger remote add upstream 'https://github.com/example-org/ledger-service.git'

# A hook, enabled, so the hooks page shows a real one beside git's samples.
$hooks = Join-Path $ledger '.git/hooks'
[System.IO.File]::WriteAllText((Join-Path $hooks 'pre-commit'), "#!/bin/sh`n# Refuse a commit that leaves a debugger call behind.`ngit diff --cached | grep -q 'breakpoint()' && exit 1`nexit 0`n")

# Something set aside, so the stashes page is not empty.
[System.IO.File]::WriteAllText((Join-Path $ledger 'src/reports.py'), "def monthly(account):`n    pass`n")
& $git -C $ledger add -- 'src/reports.py'
& $git -C $ledger stash push --quiet -m 'monthly report, half finished'

# A second working tree, so that page has a row of its own.
& $git -C $ledger worktree add --quiet -b 'release/1.2' (Join-Path $repos 'ledger-release') 2>$null

# A few more repositories, so the list looks like a working machine rather than a test.
New-DemoRepository -Name 'web-console' -Url 'git@github.com:example-org/web-console.git' | Out-Null
New-DemoRepository -Name 'billing-api' -Url 'https://git.work.example.invalid/platform/billing-api.git' -Group 'work' | Out-Null
New-DemoRepository -Name 'infra-scripts' -Url 'https://git.work.example.invalid/platform/infra-scripts.git' -Group 'work' | Out-Null

foreach ($name in 'web-console', 'work/billing-api', 'work/infra-scripts') {
    $path = Join-Path $repos $name
    New-DemoCommit $path 'README.md' "# $(Split-Path -Leaf $name)`n" 'Add the readme'
}

# --------------------------------------------------------------------------- settings
$rootsJson = ($repos.FullName -replace '\\', '\\')

Set-Content -LiteralPath (Join-Path $appData 'settings.json') -Encoding utf8 -Value @"
{
  "language": "en-US",
  "theme": "Light",
  "customKeyDirectories": [],
  "repositoryScanRoots": [],
  "scanRoots": [
    {
      "path": "$rootsJson",
      "depth": "Recursive",
      "enabled": true
    }
  ],
  "keyFolders": [],
  "dryRunByDefault": true,
  "watchForChanges": true,
  "logLevel": "Information",
  "revealPolicy": {
    "autoHideSeconds": 30,
    "clipboardClearSeconds": 60,
    "requireConfirmation": true
  }
}
"@

Set-Content -LiteralPath (Join-Path $appData 'profiles.json') -Encoding utf8 -Value @"
{
  "version": 1,
  "profiles": [
    {
      "id": "b6f2c1e4-8d3a-4f57-9c21-0e5a7d9b3f10",
      "name": "Open source",
      "identity": {
        "userName": "Alex Developer",
        "email": "alex.developer@example.invalid",
        "signingKeyId": null
      },
      "sshKeyPath": "$(($ssh.FullName + '/id_ed25519') -replace '\\', '\\')",
      "credentialHelper": "manager",
      "scope": "Global",
      "repositoryPath": null,
      "pinKeyViaSshCommand": true
    },
    {
      "id": "4a9d7c30-51b8-42e6-8f0a-2c6b1e84d795",
      "name": "Work",
      "identity": {
        "userName": "Alex Developer",
        "email": "alex@work.example.invalid",
        "signingKeyId": null
      },
      "sshKeyPath": "$(($ssh.FullName + '/id_rsa_work') -replace '\\', '\\')",
      "credentialHelper": "manager",
      "scope": "Repository",
      "repositoryPath": "$(((Join-Path $repos 'work/billing-api')) -replace '\\', '\\')",
      "pinKeyViaSshCommand": true
    }
  ]
}
"@

Write-Host "built $Workspace"
Write-Host '  identity    Alex Developer <alex.developer@example.invalid>'
Write-Host '  keys        3 (one without a private half, none with a passphrase)'
Write-Host '  projects    ledger-service (history, submodule, hook, stash, worktree, tag), 3 more'
Write-Host ''
Write-Host 'Run against it:'
Write-Host "  dotnet run --project src/GitVault.App -- --data-root `"$Workspace`""
