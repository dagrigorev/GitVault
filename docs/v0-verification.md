# V0 — verification of the existing functionality

Done before any new feature work, on the principle that building on top of unverified behaviour
is how a defect becomes load-bearing.

Three layers: integration tests against the real `git` binary, a live pass over the running
application, and this report. Five defects found; four fixed, one left open with a reason.

---

## What was verified

| Area | How | Result |
|---|---|---|
| Identity discovery across scopes | real repository, values written by git at global and local scope | pass |
| `includeIf` conditional include | `includeIf.gitdir:` pointing at a second file | pass — and the resolved value equals what `git config user.email` prints |
| Effective identity resolution | local vs global precedence, compared against git's own answer | pass |
| Configuration round-trip | value containing quotes, a backslash, a hash and spaces | pass — git reads back exactly what GitVault wrote |
| Unset | write then unset at local scope | pass |
| Repository scanning | nested repositories, depth 1 vs depth 8 | pass |
| Scan root that does not exist | non-existent path | pass — skipped, not thrown |
| Remote URL reporting | repository with an `origin` | pass |
| Activation round-trip | activate, apply, deactivate, byte-compare | pass (pre-existing suite) |
| Snapshot metadata and rollback preview | capture, modify, preview, cancel, confirm | pass |
| Options editors | scan roots and key folders, add/edit/remove | pass |
| Profile activation gate | preview, review, apply, invalidation | pass |
| Live application | all pages rendered against the real machine | pass, with one defect below |

Test count after V0: **530 pass**, 0 warnings, `GitVault.Core` line coverage 79 %.

---

## Defects

### V0-1 — The file GitVault snapshots was not always the file git writes · High · fixed

`PlatformPathsBase.GlobalGitConfigPath` returned `~/.gitconfig` unconditionally. Writes went
through `git config --global`, which resolves the per-user file differently: `$GIT_CONFIG_GLOBAL`
when set, otherwise `~/.gitconfig` when it exists, otherwise `$XDG_CONFIG_HOME/git/config` when
*that* exists.

On a machine keeping its configuration in the XDG location — the ordinary Linux arrangement — the
two disagreed, and the consequence ran through the whole safety design:

1. the plan named `~/.gitconfig`
2. the snapshot recorded it as absent
3. git wrote `~/.config/git/config`
4. deactivation or rollback "restored" by deleting a file that had never existed

The change was real and the undo did nothing. This is precisely the failure the snapshot mechanism
exists to prevent, and it would have been invisible on Windows, where the two paths coincide.

Fixed by implementing git's documented order, with `XdgGitConfigPath` exposed alongside.
`GlobalConfigTargetTests` pins every branch of the rule against the real binary.

### V0-2 — Git invocations inherited the ambient environment · High · fixed

`GitConfigService` shelled out to git without controlling its environment, so the file git chose
was whatever the surrounding process implied. Two consequences.

For the product: the fix in V0-1 made GitVault's *prediction* correct, but a prediction is still
two implementations of one rule agreeing by luck. `GIT_CONFIG_GLOBAL` is now pinned to the file
`IPlatformPaths` resolved, so the snapshot target and the write target are the same file **by
construction**. The system file is deliberately not pinned — that would change which machine-wide
configuration the user sees, and the goal is to remove ambiguity, not to hide data.

For the tests: an integration test could not be isolated at all. The first end-to-end run read the
running developer's real `user.email` — proof of the gap, and a reminder that a test suite for this
application has to be as careful as the application.

`IProcessRunner` gained an environment-aware overload for this. It is also what V4 will need to
set `GIT_AUTHOR_*` and `GIT_COMMITTER_*` when rewriting commits.

### V0-3 — Credential grid columns collapsed into unreadable slivers · Medium · fixed

Six columns, four of them fixed-width, in a pane narrowed by the properties pane. The two
proportional columns were left roughly 65 pixels each, so the host read `gh:...` and the account
`dgri...` — every column present and none of them legible. Even the headers truncated: `Hos'`,
`Protoc`, `Use'`.

Fixed with a minimum width per column, so the grid overflows into its own horizontal scrollbar
rather than crushing its contents. Applied to the identities, keys, repositories and snapshots
grids too, which have the same shape and would fail the same way in a narrower window.

### V0-4 — The system-scope target has the same class of mismatch · Low · open

`SystemGitConfigCandidates` is a list of likely paths; `git config --system` writes wherever git
was built to look. On an unusual installation these can differ, and the plan would then name the
wrong file.

Left open deliberately. Severity is low — a system write almost always fails on permissions before
it can do anything, and GitVault refuses to elevate — and the honest fix is to ask git for the
origin rather than guess, which belongs with the configuration editor in V1. Recorded here so it
is not rediscovered as a surprise.

### V0-5 — Language switching not confirmed by hand · Low · open

Synthetic clicks on the language combo box did not register during the live pass, so the switch
was not exercised through the real control. The view-model path is covered by automated tests that
assert against rendered controls in three cultures, including a search box watermark, so the
binding chain is verified — but the pointer path through that particular control is not. Worth one
minute of a human's attention rather than more automation.

---

## What this changes for the milestones ahead

V4 rewrites commit history, and its safety net is a backup ref rather than a file snapshot. V0-1
and V0-2 are the same mistake in the form V4 could repeat: computing where an operation will land
instead of pinning it. The rule to carry forward is that GitVault should tell git what to touch,
not deduce what git decided to touch.
