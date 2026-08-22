# Agent onboarding prompt

Paste this to an AI agent starting work on GitVault.

---

You are working on **GitVault** (https://github.com/dagrigorev/GitVault), a cross-platform desktop
app that discovers, inspects and manages every Git identity artifact on a machine — author
identities, SSH keys, SSH agents, credential-store entries, and the per-client configuration of
third-party Git GUIs — and activates or deactivates an identity globally, system-wide, or per
repository.

It also edits the repositories themselves: configuration and a `[gitvault]` section of its own,
remotes, branches and tags, commit metadata and file content through a history rewrite, path-wide
operations (purge a file from all of history, move one, replace an identity everywhere), the
plain-text control files (`.gitignore`, `.gitattributes`, `.mailmap`, `.git/info/exclude`), hook
scripts, working trees, stashes and what the parent records about submodules.

**Stack.** .NET 8 (`net8.0` everywhere, including the Windows platform project so the solution
builds on Linux/macOS), Avalonia 11.2.3 with its own FluentTheme (FluentAvaloniaUI was removed: its
Symbols font cannot load headless, which broke the UI tests), CommunityToolkit.Mvvm source
generators,
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
9. Third-party packages and assets must be MIT / Apache-2.0 / BSD / MPL-2.0 / public domain. No
   GPL linkage. (This is why FluentAssertions is pinned to 6.12.2, and why the icon set is the
   public-domain Tango library.)
10. No user-visible string in XAML or C#. Add a key to `build/loc/strings.json` and regenerate;
    `NoHardCodedStringsTests` enforces it, and `UnusedLocalizationKeysTests` fails on a string
    nothing can reach — a stale wording is worse than a missing one, because the next person wires
    it up by mistake.
11. **Ask git; never deduce what git decided.** The first real defect in this codebase was a
    snapshot and a write addressing different files because the global config path was guessed
    rather than resolved. The same rule now covers the system config path, the hooks directory
    (`core.hooksPath`), and every ref a plan touches. If the answer is knowable by asking git, ask.
12. **Never pass `--force`.** Git refuses to remove a working tree holding uncommitted work, to
    deinitialise a submodule with changes inside it, to delete a branch that would orphan commits.
    Those refusals are the safety net; they reach the user as a failed step with git's own message.
13. **Never leave the repository in a state nobody chose.** No operation stops half-way holding a
    conflicted working tree. Where a merge is needed it is *computed* rather than performed — a
    content edit merges three ways through `git merge-file` on temporary files outside the
    repository, so conflicts are found while planning and a closed preview leaves no trace.

**Conventions.** `TreatWarningsAsErrors=true` and `CA1416` is an error — builds must stay at zero
warnings. Public members carry XML docs. No `NotImplementedException`, no `// TODO` placeholders.
Flag any guess with `// VERIFY:`. Reflection bindings are on
(`AvaloniaUseCompiledBindingsByDefault=false`) because view models are `internal`.

**Generated files — edit the source, not the output.**
```bash
pwsh build/generate-localization.ps1   # strings.json -> .resx + Keys.g.cs   (CI fails if stale)
pwsh build/generate-classic-icons.ps1 # Tango icons -> Assets/ClassicIcons.axaml
pwsh build/generate-ssh-fixtures.ps1   # test keys, via the reference tools
```

**Commands.**
```bash
dotnet build GitVault.sln --configuration Release
dotnet test GitVault.sln --configuration Release
dotnet run --project src/GitVault.App/GitVault.App.csproj
pwsh build/check-coverage.ps1          # gate: 75% line coverage in GitVault.Core
```

**Writes.** Every one of them takes the same route, and adding a new one means joining it rather
than inventing a shorter one: build a plan (which writes nothing), render it as a preview the user
reads, preserve what it will change, then apply. Ordinary files are preserved with a snapshot;
anything ref-shaped is preserved as a ref under `refs/gitvault/backup/`, because that keeps
orphaned commits reachable across `git gc --prune=now` and a file copy does not. A blocker is
something the user *cannot* do; a warning is something they may not *want* to — folding the second
into the first makes the program refuse work that is legitimately theirs. Rewriting history is the
one operation whose confirmation is typing the branch name.

**State.** Milestones M1–M8 and V0–V7 complete. 740 tests pass; `GitVault.Core` at 81 % line
coverage.
`docs/security.md` states plainly which of the ten security requirements are met and which are not.
`docs/manual-qa.md` is the pre-release manual plan; `build/qa-fixtures.ps1` builds throwaway keys,
repos and a sample profile for it. Known gaps are listed in both documents — read them before
assuming something is missing by accident.

**Interface.** A classic Windows desktop utility: menu bar, toolbar, navigation tree with a
subtree of pages under each repository, dense grids,
a shared properties pane, group boxes, modal dialogs and a status bar. Styles live in
`src/GitVault.App/Styles/` and override Avalonia's Fluent templates rather than replacing them.
Two rules the UI enforces rather than documents: a profile opens with the scope it was saved with,
and Apply stays disabled until the user has confirmed a dry-run preview dialog — changing anything
the plan was built from invalidates it again.
