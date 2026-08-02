# Packaging

Every artifact is a **self-contained single file**: GitVault ships its own runtime, so nothing
has to be installed first.

`PublishTrimmed` stays **false**. BouncyCastle and the JSON source generators both use reflection
in ways the trimmer cannot see, and a trimmed build that fails at run time when someone opens a
PPK file is a worse outcome than a larger download. Revisiting it needs explicit trimmer roots
and a pass over every reflective call site.

`InvariantGlobalization` stays **false**. ICU is required for the Russian plural rules and for
correct Chinese collation; without it both languages degrade quietly.

## Windows

```powershell
pwsh build/windows/pack.ps1 -Runtime win-x64 -Version 0.1.0
```

Produces a portable zip always, and an installer when Inno Setup is on the machine. The installer
is **per-user by default** (`PrivilegesRequired=lowest`): GitVault reads and writes the current
user's git and SSH configuration, so a machine-wide install would grant nothing extra while
demanding elevation the application otherwise avoids.

Uninstalling removes the cache but deliberately keeps settings, profiles and **snapshots** — a
snapshot is the user's route back from a change GitVault made, and uninstalling the tool is not a
reason to discard it.

## macOS

```bash
./build/macos/pack.sh osx-arm64 0.1.0
```

Builds `GitVault.app` with a correct `Info.plist` (bundle id `org.gitvault.app`, minimum
macOS 12) and wraps it in a `.dmg`.

Signing and notarisation happen only when the environment provides credentials, so the script runs
unchanged on a machine with no certificates:

```bash
export MACOS_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
xcrun notarytool store-credentials gitvault-notary \
    --apple-id you@example.com --team-id TEAMID --password <app-specific-password>
export MACOS_NOTARY_PROFILE=gitvault-notary
```

Without stapled notarisation, Gatekeeper refuses to open the app on any machine other than the one
that built it. The script says so rather than producing something that silently fails for users.

The bundle requests **no network entitlement**, because GitVault makes no network calls.

## Linux

```bash
./build/linux/pack.sh linux-x64 0.1.0
```

Produces a `.tar.gz` always, a `.deb` when `dpkg-deb` is present, and an AppImage when
`appimagetool` is present. Missing tools are reported, never installed — a packaging script that
reaches out to the network is one nobody can audit.

The `.deb` depends on ICU (any of several versions, since distributions disagree), recommends
`git` and `openssh-client`, and suggests `libsecret-tools` for Secret Service support. The desktop
entry is translated into all three shipped languages.

A Flatpak manifest is **not** provided; it remains the stretch item the specification called it.

## CI

The `publish` job in `.github/workflows/ci.yml` builds all six runtime identifiers. Packaging
scripts are invoked per platform on top of that. Installer signing is not performed in CI: the
credentials do not belong in a repository, and an unsigned artifact that is clearly labelled is
better than a signing step that appears to work and does not.
