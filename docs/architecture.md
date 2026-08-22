# Architecture

## Layering

```
GitVault.App            Avalonia views, view models, composition root
   ↓ depends on
GitVault.Localization   resources, Localizer, pluralizer
GitVault.Platform.*     Windows / macOS / Linux implementations
   ↓ depends on
GitVault.Core           models, abstractions, orchestration
```

`GitVault.Core` references nothing OS-specific and nothing UI-specific. It can be exercised
entirely from unit tests on any platform.

## The one place that branches on the OS

`src/GitVault.App/Composition/PlatformModule.cs` picks the implementations for the current OS and
registers them against the `GitVault.Core.Abstractions` interfaces. There is deliberately no
`#if WINDOWS` anywhere else. Platform projects mark their types with `SupportedOSPlatform`, so the
CA1416 analyzer (raised to `error` in `.editorconfig`) catches a call made from the wrong place at
compile time rather than at run time.

Cross-platform behaviour that is *almost* the same everywhere lives in an abstract base in
`GitVault.Core.Platform` (`PlatformPathsBase`, `PlatformInfoBase`, `ShellLauncherBase`), with only
the genuinely different parts left abstract. That keeps the three platform projects small enough
to audit.

## Discovery

A probe is a read-only unit implementing `IProbe`. It returns a `ProbeResult<ProbePayload>` that
carries a `ProbeStatus` (`Ok`, `NotInstalled`, `AccessDenied`, `Timeout`, `ParseError`,
`NotApplicable`, `Failed`) plus redacted diagnostics. **A probe failure is a value, never an
exception**, so one broken client cannot abort a scan.

Probes must not write anything. Discovery is read-only by construction.

## Git configuration

`GitConfigService` prefers the real `git` binary for reads:
`git config --list --show-origin --show-scope -z`. Git is the authority on its own format, and
shelling out gets conditional includes, platform path quirks and precedence right for free. The
`-z` record layout is `scope NUL origin NUL key LF value NUL`; that assumption is verified against
a real `git` on all three CI platforms by `GitBinaryIntegrationTests`.

`GitConfigParser` is the fallback for machines without git. It implements the grammar directly:
section subnames in both the quoted and dotted spellings, quoting and `\n` / `\t` / `\b` / `\\` /
`\"` escapes, inline comments, valueless booleans, line continuation, multi-valued keys, BOM,
CRLF, and `include` / `includeIf` with cycle detection and git's depth limit of 10. Conditional
includes support `gitdir:`, `gitdir/i:` and `onbranch:`; `hasconfig:remote.*.url:` is recognised
but never fires, because evaluating it needs the configuration we are still assembling.

Writes go through `git config --replace-all` / `--unset-all` when git is available. Without it,
`GitConfigWriter` performs a *surgical* text edit: it locates the section and the variable line
and rewrites only that line, preserving comments, ordering, indentation, the byte-order mark and
the file's dominant line ending. Rewriting the whole file would be much simpler, and is exactly
what we refuse to do — people keep hand-written comments in these files.

`EffectiveIdentityResolver` answers "which identity is active here?" by walking the full listing
and keeping the last match per key, which is also how git resolves precedence. It reports both
the winning scope and the scopes that were overridden.

## SSH keys

Everything GitVault shows about a key is read from the parts of the file that are **not**
encrypted: the algorithm, the public blob, the fingerprints, the comment where the format stores
it in the clear, the KDF name and its work factor. A passphrase is never requested to inventory a
key, and no container is ever decrypted during a scan.

Fingerprints are taken over the public key blob in wire format, which is why the same key
fingerprints identically whether it came from a `.pub`, an OpenSSH container, a `.ppk` or an
agent. That is also how deduplication works: by fingerprint, never by path.

Container support:

| Format | Read | Notes |
|---|---|---|
| OpenSSH v1 | public half, cipher, KDF, bcrypt rounds, comment when unencrypted | the private half is decoded only when the cipher is `none` |
| PEM (RSA/DSA/EC) | encryption state from the headers, public half when unencrypted | `Proc-Type`/`DEK-Info` detect encryption without the passphrase |
| PKCS#8 | as above | `BEGIN ENCRYPTED PRIVATE KEY` is self-describing |
| PuTTY v2 / v3 | everything except the private half, plus MAC verification | Argon2 parameters are surfaced as the v3 work factor |

**GitVault does not implement `bcrypt_pbkdf`.** Creating keys, deriving a public half from a
protected private key, and changing a passphrase are performed by `ssh-keygen`, the reference
implementation. Reimplementing OpenSSH's key derivation would add unaudited cryptography to an
application whose entire job is handling other people's private keys, and it is the only route to
`sk-*` keys anyway. When `ssh-keygen` is absent those operations report that plainly rather than
falling back to something weaker.

`ssh_config` parsing understands `Host` and `Match` blocks, `Include` with globbing and cycle
detection, quoted values, and the `%d` / `%u` / `%h` / `%r` / `%p` / `%%` tokens, so keys
referenced only from an `IdentityFile` line are found.

Private key permissions are POSIX mode bits on Unix and an ACL on Windows. Hardening on Windows
shells out to `icacls`, because the managed ACL API needs a Windows-specific target framework and
this solution builds on all three platforms from one `net8.0` target.

## SSH agents

The agent protocol (`draft-miller-ssh-agent`) is implemented directly: a four-byte big-endian
length, then the message. `SshAgentClient` is transport-agnostic and opens a fresh connection per
exchange, because agents drop idle connections and several pages may query the same agent.

| Agent | Transport | Notes |
|---|---|---|
| OpenSSH (Unix/macOS) | `AF_UNIX` socket from `SSH_AUTH_SOCK` | `/tmp/ssh-*/agent.*` is also swept |
| OpenSSH (Windows) | named pipe `openssh-ssh-agent` | verified against the real Win32 service |
| Pageant | named pipe when present, else `WM_COPYDATA` + shared memory | the pipe namespace is enumerated rather than guessing PuTTY's per-user hash |
| gpg-agent | socket, or the Windows emulated socket (port line + 16-byte nonce) | |
| 1Password | socket or pipe, marked read-only up front | it refuses additions by design |
| KeeAgent / WSL relays | whatever `SSH_AUTH_SOCK` points at | classified by path shape |

An endpoint that is simply not there is not an error and is not shown. A reachable agent that
refuses a request produces `false`, never an exception.

**Loading a key into an agent runs `ssh-add`.** GitVault could send `SSH_AGENTC_ADD_IDENTITY`
itself, but that would mean reading the private key into this process, decrypting it, and handing
it over. Delegating keeps private key material out of GitVault entirely: the bytes go from the
file to `ssh-add` to the agent, and GitVault sees only the exit code. The key parsers deliberately
discard private key material for the same reason. The cost is that a passphrase-protected key
needs an interactive prompt a windowed process cannot give, which is reported rather than
worked around.

Removal and lock/unlock do go over the wire, because they carry no key material.

## Credential vaults

A scan reads **metadata only**. Secrets are fetched one entry at a time by an explicit reveal,
which is what makes it safe to scan automatically at start-up.

| Platform | Store | How |
|---|---|---|
| Windows | Credential Manager | `CredEnumerateW` / `CredReadW` / `CredWriteW` / `CredDeleteW` |
| Windows | GCM DPAPI store | files under `%USERPROFILE%\.gcm\dpapi_store`, unprotected with `ProtectedData` |
| macOS | Keychain | the `security` tool |
| Linux | Secret Service | libsecret's `secret-tool` |
| All | `~/.git-credentials` | parsed directly, flagged as plaintext |
| All | GCM plaintext store | listed and flagged as plaintext |
| All | any other helper | `git credential fill/approve/reject` |

**macOS and Linux use the platform's own command line tool rather than the native API.** Reading
a Keychain item through `Security.framework` means marshalling CoreFoundation dictionaries by
hand, and a mistake there is a memory-safety bug in a process that handles passwords. Secret
Service is a D-Bus interface, and speaking it directly would mean implementing the bus handshake
and the session-encryption dance first. `security` and `secret-tool` are the same code paths the
platform's supported clients use — `secret-tool` is what git's own libsecret helper links
against — and they trigger the per-item authorisation prompts users expect.

The `git credential` client matters more than any individual backend: it makes GitVault work with
helpers it has never heard of, including corporate ones. Its request block can carry a password,
so it goes over **stdin** — never in process arguments, which any local process can list, and
never through a temporary file.

Writes zero their buffers in `finally`, including the unmanaged block handed to Windows. The two
plaintext stores refuse writes outright: GitVault will not add a credential to a store that keeps
it in the clear.

### Filtering

A credential store holds everything a user ever saved. Showing all of it is both noise and a
privacy problem, so the default view keeps entries that look git-related — helper target
prefixes, the well-known forges, hosts seen in the user's own remotes — behind an explicit
"show all". On a real machine this cut 175 entries to 11.

## Client probes

Every probe reads through `IClientEnvironment` — home, app data, program files, and simple file
operations — rather than touching `System.IO` directly. That single indirection is what makes the
tests possible: the same probe runs against the real machine or against a committed fixture tree
under `tests/fixtures/clients/<client>/<platform>/`, so **the client tests need none of these
applications installed**.

Three rules hold for all of them:

- **Present but unreadable is still present.** A client whose config cannot be parsed is reported
  with `IsOpaque`, never hidden. Version drift in other people's applications is expected.
- **Opaque stores stay opaque.** GitKraken's `secBox` and Sourcetree's `passwd` are reported as
  existing and never decrypted. GitHub Desktop's token lives in the OS vault, so the account is
  read here and the token is left to the vault probes, where the user's own reveal confirmation
  applies.
- **Third-party stores are `Probable`, not `Certain`.** They are authoritative about what that
  application will do, but they are not git's own configuration.

Code-backed probes exist where there is real parsing to do: GitKraken, Sourcetree, GitHub
Desktop, `gh`, `glab`, TortoiseGit and WSL. Everything that is purely "does this directory
exist, and is there an identity in this file" is a **JSON manifest** under
`src/GitVault.Clients/Manifests/`, with `{home}` / `{appdata}` / `{appsupport}` tokens. Adding
such a client is a data change: no code, no recompile, nothing new to review.

Probes register by assembly scan, so a new probe becomes active by existing.

TortoiseGit is the one that earns its own code. It keeps everything in
`HKCU\Software\TortoiseGit`, including which `.ppk` each remote is bound to — something a
TortoiseGit user cannot easily see for themselves, and a common cause of "push suddenly stopped
working". Only the registry read is annotated `[SupportedOSPlatform("windows")]`; the binding
parser is portable and therefore tested on every platform.

## Profiles

Two properties hold, and both are tested directly against a real `git` in a throwaway repository:

1. **Planning writes nothing.** `PlanActivationAsync` returns an `ActivationPlan` and touches no
   file. The UI can only apply a plan it already has, so the dry-run rule is enforced by the shape
   of the API rather than by remembering to check a flag.
2. **Deactivating restores the touched files byte-for-byte** — the repository config *and*
   `~/.ssh/config`, including a pre-existing block the user wrote themselves.

Keeping those true needs three things that are easy to get wrong:

**`state.json` records the previous value of every key.** Without it, "undo the profile" would
mean guessing which keys a profile owns and unsetting values the user may have set. With it,
GitVault restores exactly what it replaced and removes exactly what it added.

**The previous value is read at the target scope, not from the effective configuration.** This is
subtle and it bit during development: reading the effective value meant that deactivating a
repository-scoped profile would write the user's *global* identity into the repository's local
config — inventing a local override that never existed and silently shadowing any later change to
their global identity. "Unset at this scope" has to stay "unset at this scope".

**Emptied sections are removed.** `git config --unset` removes the variable but leaves the
`[section]` header behind, so a file that gained a section during activation could not be restored
exactly. A section is only removed once nothing but blank lines remains, so a comment the user
wrote inside it keeps the header alive.

`ManagedBlockEditor` owns the `# >>> GitVault managed: <name> >>>` blocks in `~/.ssh/config`.
Nothing outside the markers is ever modified, and add-then-remove is the identity. It has exactly
one documented side effect: a file that did not end with a newline gains one. Adding a blank
separator line instead would be indistinguishable from one the user wrote, and removal could then
not tell whether to take it back. An opening marker with no closing one is left strictly alone —
someone edited by hand, and guessing where the block ends could delete their content.

A snapshot precedes every write, `RetainedSnapshots` of them are kept, and restoring one deletes
files that did not exist when it was taken.

Profiles hold *references* — a key path, a helper name, a host alias — never a private key, a
passphrase or a token. That is what makes export safe to share; the export carries a header
saying so, and importing assigns fresh identifiers so it can never silently replace a profile.

## Editing repositories

Roughly half the code answers a different question from discovery: not "what is on this machine"
but "change this repository, and be able to put it back". Everything in that half is arranged
around one route, and joining it is cheaper than inventing a shorter one.

**Plan, preview, preserve, apply.** A planning call returns a plan and writes nothing. The plan is
rendered as the preview the user reads. Applying takes the plan object the user was shown — not a
freshly computed one — so what runs is what was approved. There are two plan types, distinguished
by what they preserve rather than by what they do: `GitOperationPlan` names files and is preserved
with a snapshot, `RepositoryPlan` names refs and is preserved as refs.

**Refs are backed up as refs.** A file copy of `.git/refs/heads/main` preserves an implementation
detail — a ref may live in a loose file, in `packed-refs`, or in both — while a ref under
`refs/gitvault/backup/<id>/` preserves the fact worth preserving *and* keeps the orphaned commits
reachable, so `git gc --prune=now` cannot discard them. `RepositoryEditingTests` runs exactly that
command after a branch deletion and asserts the commit survives.

**One applier.** `RepositoryPlanApplier` is the only place a `RepositoryPlan` turns into git
commands: preserve the refs it names, run its commands in order, stop at the first failure. Six
editors share it rather than each keeping a copy that could quietly lose the backup.

**Blockers and warnings are different things.** A blocker is something the user cannot do —
deleting the checked-out branch, rewriting with a dirty working tree, a name git would reject. A
warning is something they may not want to — deleting a branch whose commits exist nowhere else,
rewriting a signed commit, purging a file that has already been pushed. Folding the second group
into the first would make GitVault refuse work that is legitimately someone's to do; the ref backup
is what turns those into decisions rather than losses.

### Rewriting history

`HistoryRewriter` rebuilds the commit chain with `git commit-tree`, walked from the oldest affected
commit to the tip, each commit rebuilt against its already-rebuilt parents. For a metadata edit the
tree is the one the commit always had, so the operation cannot conflict and a commit whose inputs
did not change rebuilds to the same object name. Identities and dates go through `GIT_AUTHOR_*` and
`GIT_COMMITTER_*`; the message goes over stdin. Nothing is ever handed to a shell.

Everything after the earliest edit gets a new identifier whether or not the user meant to touch it.
That is inherent to git rather than a choice, so the plan counts those commits separately and the
preview says so — it is the part people are surprised by.

The branch is moved once, at the end, with a compare-and-swap against the tip the plan was built
from, so a rewrite that raced with another process fails instead of overwriting. Confirming
requires typing the branch name: every other write is confirmed by acknowledging a plan, and this
one reaches every clone of the repository.

### Changing file content without conflicting the repository

Editing what a file contained at an old commit is the part that can genuinely disagree with itself,
because later commits may have changed the same file. The obvious tool is `git rebase`, and it is
deliberately not used: a rebase resolves conflicts by *stopping*, leaving the working tree
conflicted and the repository in the user's hands mid-operation.

`ContentMerger` computes the merge instead of performing it. For each commit after the edited one
the file is merged three ways — base is what the parent held, "ours" is what that commit made of
it, "theirs" is the content carried down. A commit that did not touch the file has ours equal to
base, so the carried content applies exactly and no merge is needed; that is the ordinary case and
it stays deterministic. A commit that did touch it gets a real three-way merge from
`git merge-file`, run on temporary files *outside* the repository. Nothing is written, so conflicts
are found while planning and a preview the user closes leaves no trace — asserted by counting loose
objects and checking the index timestamp across a full plan.

Applying writes blobs with `hash-object -w --stdin`, deliberately without `--path` so the
repository's clean filters do not re-filter content that came out of a blob already in stored form,
and builds trees through a temporary `GIT_INDEX_FILE` so the repository's own index is never
opened.

### What is refused rather than guessed

Text has to survive a round trip: a file whose decoded form re-encodes to a different length is not
UTF-8, and rewriting it would change its encoding as a side effect of editing one line. Working-tree
files keep the line ending they already use. A path a later commit deletes or renames, a symlink, a
submodule pointer, a file over the size limit, and a content edit on a merge commit are each
refused with a reason rather than handled approximately.

## Localization

`ILocalizationService` owns the current culture and resolves keys. `Localizer` is the bindable
façade: XAML reaches it through the `{loc:Tr Key}` markup extension, which returns a *binding* to
`Localizer["Key"]` rather than a string. When the culture changes, `Localizer` raises
`PropertyChanged("Item[]")`, and every caption in the running window re-reads itself. No restart,
no view recreation.

View models expose localized captions as computed properties and inherit a blanket
`PropertyChanged(string.Empty)` from `ViewModelBase` on culture change. Objects that are not view
models but appear in item templates (`ThemeOption`, `SummaryCard`) get an explicit
`RefreshCaptions()` call, because a blanket notification on the parent does not reach them.

Plural forms go through `IPluralizer`, which implements the CLDR cardinal rules. Russian needs
four forms (`one` / `few` / `many` / `other`); Chinese needs one. `"{0} key(s)"` is not acceptable.

## Logging and secrets

Serilog writes to a daily rolling file and to a bounded in-memory sink that backs the in-app log
viewer. `SecretRedactingEnricher` sits in front of both sinks and rewrites every string-valued
property — including nested sequences, dictionaries and destructured structures — through
`ISecretRedactor`.

Message templates in this codebase are compile-time literals, so a secret can only reach a log
event as a *property* value. Redacting properties therefore covers every path. The redactor is
deliberately over-eager: a false positive costs a log line's readability, a false negative leaks a
credential. Canonical `SHA256:` fingerprints are 43 base64 characters, below the long-base64
threshold, so they stay readable in the logs on purpose.

## Error handling

`Program.InstallGlobalExceptionHandlers` covers `AppDomain.UnhandledException`,
`TaskScheduler.UnobservedTaskException` and `Dispatcher.UIThread.UnhandledException`. The
dispatcher handler marks the exception handled: a failed page must not take the window down.
