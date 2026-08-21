# Security review

Each of the ten non-negotiable requirements, with what was actually built and where it is
enforced. Where something is **not** fully met, it says so rather than being quietly reworded.

---

## 1. Read-only by default; every write gated behind an explicit action and a dry-run preview

**Met.** Discovery never writes: `IProbe` implementations only read, and the one exception that
would prove the rule — loading a key into an agent — is a separate user action, not part of a
scan.

Profile activation cannot write without a preview, because planning and applying are separate
calls. `PlanActivationAsync` returns an `ActivationPlan` and touches nothing; `ApplyAsync` takes a
plan. The UI's apply button binds to `CanApply`, which is false until a plan exists. The rule is
enforced by the shape of the API rather than by a flag someone must remember to check.

Verified by `ActivationRoundTripTests.Planning_writes_absolutely_nothing`, which compares the
config file's bytes before and after planning.

## 2. Secret material in pinned buffers, zeroed in `finally`, never in a string or an observable property

**Partially met, and the gap is deliberate.**

What holds: vault reads return `byte[]` and every caller zeroes it with
`CryptographicOperations.ZeroMemory` in a `finally`. `WindowsCredentialManagerVault.WriteAsync`
zeroes both the managed array and the unmanaged block handed to Windows. Private key material is
never retained at all — the OpenSSH, PEM and PuTTY parsers read the public half and the KDF
metadata and discard the private section unread.

What does not: a revealed secret becomes a `string` for the moment it is displayed, because
Avalonia renders strings. It is dropped when the auto-hide timer fires. .NET strings cannot be
zeroed reliably, so that value lives until the GC reclaims it. Shortening that window would mean
a custom text control rendering from a `char[]`; that is the right fix and it is not built.

The passphrase dialog the spec describes is **not implemented**. GitVault currently avoids
needing one: adding a key to an agent runs `ssh-add`, and changing a passphrase runs
`ssh-keygen`, so the passphrase goes from the user to the OpenSSH tool without passing through
this process. Where that is not possible — loading a protected key from a windowed session — the
UI says so and points at the terminal instead of inventing a workaround.

## 3. Nothing secret is ever logged

**Met.** `SecretRedactingEnricher` sits in front of every Serilog sink and rewrites every
string-valued property, including nested sequences, dictionaries and destructured objects.

Message templates in this codebase are compile-time literals, so a secret can only reach a log
event as a *property* value; redacting properties therefore covers every path. The redactor is
deliberately over-eager — a false positive costs a log line's readability, a false negative leaks
a credential.

Tested by `SecretRedactorTests` (nine cases including PEM blocks, PuTTY `Private-Lines`, URL
passwords and vendor token formats) and `SecretRedactingEnricherTests` (five cases covering the
nesting).

`SHA256:` fingerprints are 43 base64 characters, deliberately below the long-base64 threshold, so
they stay readable in logs on purpose.

## 4. Passphrase prompts use a `char[]`-backed control, cleared on close

**Not implemented.** See item 2: GitVault currently has no passphrase prompt because it delegates
every passphrase-requiring operation to `ssh-add` and `ssh-keygen`. That is a stronger position
than a well-written dialog would be, but it is not the same thing, and it means one workflow
(loading a protected key into an agent from the GUI) is unavailable rather than merely awkward.

## 5. Revealed secrets auto-hide after 30 s; clipboard cleared after 60 s

**Met.** `CredentialsViewModel` starts a countdown on reveal, shows the remaining seconds, and
clears on expiry, on an explicit hide, and when the selected row changes.
`ClipboardService.CopySecretAsync` schedules a clear and **only clears if the clipboard still
holds what GitVault put there**, so a later copy by the user is not destroyed. Both intervals come
from `SecretRevealPolicy`.

The UI states that a clipboard manager may keep its own copy, because it may.

## 6. No network calls, no telemetry, no auto-update

**Met.** There is no HTTP client, no socket opened to a non-loopback address, and no telemetry of
any kind in the solution. The only sockets are `AF_UNIX` for SSH agents and a loopback TCP
connection to gpg-agent's Windows emulated socket, which is a local IPC mechanism rather than a
network call. The Settings page says so.

## 7. Snapshot before every mutation; keep 50; one-click rollback

**Met.** `ProfileActivator.ApplyAsync` captures a snapshot before the first write, always.
`SnapshotService` retains `RetainedSnapshots` (50) and prunes the rest. A snapshot records files
that did **not** exist, so restoring one deletes a file GitVault created rather than leaving it
behind. Rollback is a single call and is wired to a button.

## 8. Respect and preserve file permissions; refuse to write a private key looser than 0600

**Met for keys, partially met for reading on Windows.** `PosixFilePermissionService` reads and
sets mode bits, and `SshKeyGenerator` hardens every key it writes. On Windows the equivalent is an
ACL: hardening shells out to `icacls` to break inheritance and grant the owner only.

The gap: *reading* Windows ACLs to decide whether a key is over-exposed needs a Windows-specific
target framework, so `WindowsFilePermissionService.Read` reports the owner and leaves the
readability flags conservative rather than guessing. It is marked `// VERIFY:` in the source. Win32
OpenSSH performs its own check and will refuse a bad key regardless, so the practical risk is a
missing warning rather than a key being used unsafely.

## 9. Never attempt to decrypt another application's proprietary store

**Met.** GitKraken's `secBox`, Sourcetree's `passwd` and GitHub Desktop's `safeStorage` token are
reported as *present* and never opened. GCM's DPAPI store is read through `ProtectedData` — the
documented API for data protected to the current user — and its plaintext store is listed and
flagged without being parsed for structure.

Where a format is guessed at, the source carries a `// VERIFY:` comment naming what should be
checked against a real installation.

## 10. Handle `AccessDenied` gracefully; never auto-elevate

**Met.** `ProbeResult<T>` carries `ProbeStatus.AccessDenied` as a value, so a locked keychain or
an unreadable registry key is a row in the status matrix rather than an exception dialog. The
credential probe continues past a vault that refuses and only reports failure when *every* vault
refused. Nothing in the codebase requests elevation; `IPlatformInfo.IsElevated` is read for
display only.

---

## Additional properties worth recording

**Deactivation restores byte-for-byte.** Tested against a real `git` in a throwaway repository,
for both the repository config and `~/.ssh/config` — including a block the user wrote themselves.

**Previous values are read at the target scope, not the effective configuration.** Reading the
effective value would make deactivating a repository-scoped profile write the user's *global*
identity into the repository's local config, inventing a local override that never existed. This
was a real bug found by the round-trip test.

**Nothing outside the managed markers in `~/.ssh/config` is ever modified**, and an opening
marker with no closing one is left strictly alone rather than guessed at.

**Profile exports carry references only** — key paths, helper names, host aliases — never a key,
a passphrase or a token, with a header saying so.

**The diagnostics bundle is previewed before it is written.** Its configuration inventory lists
key names and origins but never values, because a git config can hold a proxy password or a URL
with an embedded token. Logs are passed through the redactor a second time on the way out, since
a bundle leaves the machine and a log file does not.

## Change of posture: GitVault writes its own section

Since the per-repository settings landed, GitVault writes a `[gitvault]` section into a
repository's own `.git/config`. Previously it only ever wrote to a configuration file as part of a
profile activation the user had previewed.

The choice was deliberate — settings stored beside the repository survive the folder being moved,
which application-data storage keyed by path does not — and the cost is paid the same way as every
other write:

- the change is planned, and the plan is rendered as a diff;
- the plan is shown in the same review dialog as an activation, and closing it writes nothing;
- the file is copied into a snapshot before the first byte is written, so it can be rolled back;
- clearing a field removes its key rather than writing an empty one, so a repository with nothing
  configured ends up with no `[gitvault]` section at all.

Nothing in the section is a secret: a key path, a helper name, a profile name and a note. A
`[gitvault]` section inherited from the user's global configuration is deliberately not treated as
belonging to a repository, because settings that belong to every repository belong to none.

## Ref backups: the safety net for operations that change refs

A file snapshot is the wrong instrument for a ref. Refs live in loose files, in `packed-refs`, or
in both at once, and copying whichever file happens to hold one today preserves an implementation
detail rather than the fact worth preserving — which commit the ref pointed at.

So every operation that deletes or moves a ref records the old position as a ref of its own, under
`refs/gitvault/backup/<id>/`. That has a property the file copy does not: it keeps the orphaned
commits *reachable*, so git's garbage collection will not discard the history the operation just
detached. `RepositoryEditingTests` proves it by running `git gc --prune=now` after a branch
deletion and asserting the commit is still there.

Restoring is one `update-ref` per entry. A ref that did not exist when the backup was taken is
recorded as absent, so restoring deletes whatever the operation created.

### Blockers and warnings are different things

A blocker is something the user cannot do: deleting the checked-out branch, editing refs while a
rebase is half-finished, a ref name git would reject. A warning is something they may not want to:
deleting a branch whose commits exist nowhere else, deleting a signed tag, renaming a remote that
moves its tracking refs.

Folding the second group into the first would make GitVault refuse work that is legitimately
someone's to do. The ref backup is what turns those into decisions rather than losses — except for
a signature, which is the one thing a backup cannot recreate, and which the warning says plainly.

### What is still not done here

GitVault does not push, fetch or contact a remote in any way. Editing a remote changes what it
points at; moving those changes to a server stays the user's own action, taken with their own
tools. `GIT_TERMINAL_PROMPT=0` is set on every invocation so a misconfigured credential helper
fails immediately rather than blocking on an invisible prompt.

## Rewriting commits: the one operation that asks the user to type

Editing a commit's metadata is the most consequential thing GitVault does. It is also the one
place where the usual review dialog is not enough on its own, so the gate in front of it is
different in three ways.

**The rewrite is planned from the edits, not performed as they are made.** Each edit is collected
on the history page and nothing is written; the rewrite happens once, when the user applies them.
That is not only caution. Rewriting a branch twice in a row would rebuild the same commits twice
and hand them a second set of identifiers for no reason, so collecting the edits first is also the
correct way to do the work.

**The preview says how far the change reaches, not only what was edited.** Changing one commit in
the middle of a branch gives every commit after it a new identifier, because a commit's name is a
hash of its contents and its parents. The plan counts both groups separately — edited, and rebuilt
because something earlier changed — since the second number is the one people are surprised by.

**Confirming requires typing the branch name.** Every other write in GitVault is confirmed by
acknowledging a plan. This one asks the user to name the branch they are rewriting, in the
tradition of dialogs that make you spell out what you are about to change, because a button is too
easy to press by habit and the consequence here reaches every clone of the repository.

The mechanism underneath is `git commit-tree`, walked from the oldest affected commit to the tip.
Each commit is rebuilt against its already-rebuilt parents with the tree it always had, which is
why the operation cannot produce a conflict and why the file content is provably unchanged —
`HistoryRewriteTests` asserts the tree of every rebuilt commit against the original. Identities
and dates are passed through `GIT_AUTHOR_*` and `GIT_COMMITTER_*`, and the message arrives on
stdin, so no part of it is ever handed to a shell to re-parse.

Before the first write, `refs/heads/<branch>` is recorded as a ref backup, which is what makes the
whole rewrite reversible in one `update-ref` — restoring it is tested. The branch is then moved
with a compare-and-swap against the tip the plan was built from, so a rewrite that raced with
another process fails instead of overwriting whatever happened in between.

Three things it deliberately does not do. It does not sign: GitVault holds no key, so a signed
commit in the range loses its signature, and the plan says so rather than the documentation. It
does not move tags or other branches that point into the rewritten range — they are listed as
stranded, because moving someone else's ref is a decision to make deliberately. And it does not
push: the rewrite changes this clone only, and anyone else holding those commits keeps the old
ones until they fetch and reset themselves.

Dates are the one input the dialog refuses rather than interprets. A date typed without an offset
would be read in the machine's timezone and that guess written into history, so the field stays
invalid until an offset is present.

### Editing file content, without ever conflicting the repository

Changing what a file contained at an old commit is the part of a rewrite that can genuinely
disagree with itself, because the commits after it may have changed the same file. The usual tool
for that is `git rebase`, and it was deliberately not used here.

A rebase resolves conflicts by stopping. It checks things out, leaves the working tree in a
conflicted state, and hands the repository back to the user mid-operation — which is precisely
what this application promises never to do. So the merge is computed instead of performed.

For every commit after the edited one, the file is merged three ways: the base is what the file
contained at that commit's parent, "ours" is what that commit made of it, and "theirs" is the
content carried down from the edit. A commit that did not touch the file has ours equal to base,
so the carried content applies exactly and no merge is needed at all — the ordinary case stays
deterministic. A commit that did touch it gets a real three-way merge from `git merge-file`.

None of that writes anything. The inputs are read with `cat-file`, the merge runs on temporary
files outside the repository, and conflicts are therefore discovered *while planning*, before the
preview appears. A conflict is shown as a question with git's own merged text in an editable box;
confirming is refused while a conflict marker is still present, because a marker committed into
history is a broken file and this is the one moment where catching it is free. Closing any of it
leaves the repository byte-for-byte as it was, which `ContentRewriteTests` asserts by counting
loose objects and checking the index's timestamp across a full plan.

Only when the user applies does anything get written. Blobs are written with `hash-object -w
--stdin` — deliberately without `--path`, so the repository's clean filters do not re-filter
content that came out of a blob already in its stored form — and trees are built through a
temporary `GIT_INDEX_FILE`, so the repository's own index is never opened. File modes are carried
across, so editing a script does not quietly cost it its executable bit.

Four things are refused rather than guessed at, each because carrying on would mean silently
changing something the user did not ask to change:

- a file a later commit deletes or renames, since there is nothing left to carry the edit into;
- a path that is a symbolic link or a submodule, since neither is text;
- a file over 2 MB, or one whose bytes do not survive a round trip through UTF-8 — every read is
  checked against the size git reports for the blob, which also catches a byte-order mark;
- a content edit on a merge commit, where the change has no single side to belong to.

### Purging a file: what it does, and what it cannot do

Taking a file out of every commit that ever held it is the operation people reach for after
committing a key by accident, so it is the one where an over-confident interface does real harm.

The mechanism is the same rewrite as everything else: each affected commit is rebuilt with the
path dropped from its tree, through a temporary index, with the branch moved once at the end
behind a ref backup. It works on anything — a binary, a file in an unknown encoding, a whole
folder — because removing an entry never reads its content. A folder is expanded per commit rather
than once, since what it contained changed over time.

What it cannot do is unmake the fact that the file was committed, and the interface says so at the
point of use rather than in a footnote:

- the old objects stay in this repository until git prunes them;
- they stay reachable through the very backup ref that makes the purge reversible — asserted by a
  test, because it is the part most likely to be assumed away;
- they stay in every other clone until its owner rewrites too;
- they stay on any server the branch was pushed to, and in whatever that server keeps.

So if the file held a key, a token or a password, the remedy is to revoke it. GitVault says this
in the operation's own warning, because a green result that implies otherwise would be worse than
no feature at all.

One deliberate difference from `filter-repo`: a commit whose only change was to the purged path is
kept, holding the same tree as its parent, rather than dropped. Its message and authorship are
history too, and removing it would be a second change nobody asked for. The plan counts those
commits and warns instead, so the user can remove them deliberately if that is what they want.

Moving a path through history works the same way and moves the tree entry rather than rewriting
content, so a moved file keeps the very same blob. A move that would land on a path some commit
already holds is refused rather than silently replacing it, and a typed path that is absolute,
climbs out of the working tree, names `.git`, or starts with a dash is refused before any commit
is touched — a rename git rejects half-way would leave a plan that succeeded on some commits and
not others.

Replacing an identity across history changes only the commits carrying that address, and on each
one only the sides that carry it. Someone else's authorship is not the user's to reassign, and a
bulk correction is exactly where that would happen by accident.

## Known gaps, collected

| Gap | Consequence | Where |
|---|---|---|
| No `char[]`-backed passphrase dialog | Loading a passphrase-protected key into an agent must be done with `ssh-add` in a terminal | items 2 and 4 |
| Revealed secret is briefly a `string` | The value survives until GC rather than being zeroed | item 2 |
| Windows ACL reading not implemented | An over-exposed key on Windows produces no GitVault warning; OpenSSH still refuses it | item 8 |
| `bcrypt_pbkdf` not implemented | Encrypted OpenSSH containers cannot be decrypted in-process; `ssh-keygen` is used instead | `docs/architecture.md` |
| Encrypted PPK v2 MAC unverified | The v2 encrypted path is untested against a real PuTTY | `PuttyKeyFile` |
| Content edits cannot follow a rename | Editing a file that a later commit renames is refused rather than tracked through the rename | `ContentMerger` |
| Non-UTF-8 files cannot be edited | A file in another encoding is refused, because rewriting it would change its encoding | `BlobReader` |
| A purge does not prune the object database | The removed content stays reachable through the backup ref, and stays in the repository until git prunes it | `HistoryTools` |
| Path operations read every commit's tree | Planning a purge on a very large history is slow, because each commit is listed in turn | `HistoryTools.FindHoldersAsync` |
