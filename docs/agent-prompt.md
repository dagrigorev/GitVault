# Agent onboarding prompt

Paste this to an AI agent starting work on GitVault.

---

You are working on **GitVault** (https://github.com/dagrigorev/GitVault), a cross-platform desktop
app that discovers, inspects and manages every Git identity artifact on a machine — author
identities, SSH keys, SSH agents, credential-store entries, and the per-client configuration of
third-party Git GUIs — and activates or deactivates an identity globally, system-wide, or per
repository.

**Stack.** .NET 8 (`net8.0` everywhere, including the Windows platform project so the solution
builds on Linux/macOS), Avalonia 11.2.3, FluentAvaloniaUI, CommunityToolkit.Mvvm source generators,
Microsoft.Extensions.DependencyInjection, Serilog, BouncyCastle, System.Text.Json source-generated
contexts. Tests: xUnit + FluentAssertions 6.12.2 + NSubstitute + Avalonia.Headless.

**Layout.**
```
src/GitVault.Core/           domain, abstractions, orchestration — zero OS dependencies
src/GitVault.Platform.*/     the only OS-specific code, one project per platform
src/GitVault.Clients/        probes for third-party Git GUIs, plus JSON manifests
src/GitVault.Localization/   en-US / ru-RU / zh-Hans, runtime culture switching
src/GitVault.App/            Avalonia UI, view models, composition root
tests/                       unit and headless-UI tests
build/                       generators and packaging scripts
```

**Non-negotiable rules.** This app touches people's private keys. Prefer boring, auditable
implementations over clever ones.

1. Read-only by default. Every write is gated behind an explicit user action and a dry-run preview
   the user has seen. Plan and apply are separate APIs so this is enforced by shape, not discipline.
2. Snapshot before every mutation; keep 50; one-click rollback. Deactivation restores byte-for-byte,
   including removing section headers `git config --unset` leaves behind.
3. Nothing secret is ever logged. `SecretRedactor` runs in front of every sink and again before a
   diagnostics bundle is exported. Secrets go over stdin, never a command line.
4. Never decrypt or brute-force another application's secret store. If it's opaque, report it as
   opaque.
5. No network calls, no telemetry, no auto-update. The only exception is host-key verification the
   user explicitly initiated.
6. Handle `AccessDenied` everywhere: surface "insufficient permissions, run elevated?" — never
   auto-elevate, never throw an exception dialog.
7. On POSIX, refuse to write a private key with a mode looser than `0600`. Preserve permissions.
8. No `#if WINDOWS` in business logic. All OS-specific behaviour sits behind interfaces resolved by
   `src/GitVault.App/Composition/PlatformModule.cs`, the single place that branches on OS.
9. Third-party packages must be MIT / Apache-2.0 / BSD / MPL-2.0. No GPL linkage. (This is why
   FluentAssertions is pinned to 6.12.2 and why Icons8 was rejected in favour of Material Symbols.)
10. No user-visible string in XAML or C#. Add a key to `build/loc/strings.json` and regenerate;
    `NoHardCodedStringsTests` enforces it.

**Conventions.** `TreatWarningsAsErrors=true` and `CA1416` is an error — builds must stay at zero
warnings. Public members carry XML docs. No `NotImplementedException`, no `// TODO` placeholders.
Flag any guess with `// VERIFY:`. Reflection bindings are on
(`AvaloniaUseCompiledBindingsByDefault=false`) because view models are `internal`.

**Generated files — edit the source, not the output.**
```bash
pwsh build/generate-localization.ps1   # strings.json -> .resx + Keys.g.cs   (CI fails if stale)
pwsh build/generate-icons.ps1          # Material Symbols -> Assets/Icons.axaml
pwsh build/generate-ssh-fixtures.ps1   # test keys, via the reference tools
```

**Commands.**
```bash
dotnet build GitVault.sln --configuration Release
dotnet test GitVault.sln --configuration Release
dotnet run --project src/GitVault.App/GitVault.App.csproj
pwsh build/check-coverage.ps1          # gate: 75% line coverage in GitVault.Core
```

**State.** Milestones M1–M8 complete. 480 tests pass; `GitVault.Core` at 79 % line coverage.
`docs/security.md` states plainly which of the ten security requirements are met and which are not.
`docs/manual-qa.md` is the pre-release manual plan; `build/qa-fixtures.ps1` builds throwaway keys,
repos and a sample profile for it. Known gaps are listed in both documents — read them before
assuming something is missing by accident.

**Open work.** No editor UI for scan roots, custom key folders, or profiles — all are hand-edited
JSON in the app data directory. The Profiles scope dropdown ignores the profile's stored scope and
defaults to Global.
