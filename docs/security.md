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

## Known gaps, collected

| Gap | Consequence | Where |
|---|---|---|
| No `char[]`-backed passphrase dialog | Loading a passphrase-protected key into an agent must be done with `ssh-add` in a terminal | items 2 and 4 |
| Revealed secret is briefly a `string` | The value survives until GC rather than being zeroed | item 2 |
| Windows ACL reading not implemented | An over-exposed key on Windows produces no GitVault warning; OpenSSH still refuses it | item 8 |
| `bcrypt_pbkdf` not implemented | Encrypted OpenSSH containers cannot be decrypted in-process; `ssh-keygen` is used instead | `docs/architecture.md` |
| Encrypted PPK v2 MAC unverified | The v2 encrypted path is untested against a real PuTTY | `PuttyKeyFile` |
