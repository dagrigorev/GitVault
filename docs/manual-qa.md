# GitVault manual QA

Manual test plan for a release candidate. The automated suite (740 tests, 81 % line coverage in
`GitVault.Core`) already covers parsing, planning and round-tripping. This plan covers what it
cannot: real machines, real key stores, real fonts, real permissions, and whether the thing is
usable.

Read the safety section before touching anything. GitVault reads private keys.

---

## 1. Safety

### 1.1 The profile cannot be sandboxed on Windows

The obvious idea — run GitVault with `USERPROFILE`, `APPDATA` and `HOME` pointed at a scratch
directory — **does not work, and fails silently in the worst direction.** Measured on
Windows 11, .NET 8:

| Call | With the environment overridden | 
|---|---|
| `GetFolderPath(SpecialFolder.UserProfile)` | returns the **real** profile, ignoring `%USERPROFILE%` |
| `GetFolderPath(SpecialFolder.ApplicationData)` | returns an **empty string** |
| `GetFolderPath(SpecialFolder.LocalApplicationData)` | returns an **empty string** |

.NET resolves these through the shell, not the environment. So a tester who "sandboxed" the app
this way would be scanning their own `~/.ssh` while believing otherwise. Do not do it, and do not
add such a step to any future script.

### 1.2 What to do instead

Isolation comes from the artifacts, not from the process:

- **Read-only cases** may run against the tester's own machine. Discovery never writes.
- **Write cases** (section 9, profiles) must target the throwaway repositories and keys created by
  `build/qa-fixtures.ps1`, never a repository the tester cares about.
- For a genuinely clean profile — a first-run experience, a machine with no git at all, a
  permission-denied case — use a **fresh Windows user account or a VM**. Nothing less isolates.

### 1.3 Standing rules

1. Never type a real passphrase, token or password into a build under test. Every fixture secret
   is fake and disposable.
2. Before any case that writes, note the current values so you can verify restoration:
   ```bash
   git config --global --list > before.txt
   ```
3. If a case leaves the machine changed and rollback did not restore it, **stop and file it**.
   That is the most serious class of defect this application can have.
4. Screenshots of the Credentials page with a secret revealed must not leave the test machine.

---

## 2. Setting up

### 2.1 Create the fixtures

```bash
pwsh build/qa-fixtures.ps1 -Workspace D:\gitvault-qa
```

Creates throwaway SSH keys in several formats and health states, two git repositories, a fake
plaintext credential file, and a sample `profiles.json`. It touches nothing outside the workspace.
Read the `README.txt` it writes.

### 2.2 Point GitVault at them

There is no UI yet for scan roots or extra key folders (see section 18), so edit the settings file
by hand and restart. Back it up first.

| Platform | Path |
|---|---|
| Windows | `%APPDATA%\GitVault\settings.json` |
| Linux | `~/.config/gitvault/settings.json` |
| macOS | `~/Library/Application Support/GitVault/settings.json` |

```json
"customKeyDirectories": [ "D:\\gitvault-qa\\keys" ],
"repositoryScanRoots":  [ "D:\\gitvault-qa\\repos" ]
```

For the profile cases, copy `D:\gitvault-qa\profiles.json` next to `settings.json`.

### 2.3 Tear down

```bash
pwsh build/qa-fixtures.ps1 -Workspace D:\gitvault-qa -Remove
```

Then restore `settings.json`, and delete `profiles.json` and `activation-state.json` from the
application data directory. Section 19 is the full checklist.

---

## 3. Environment matrix

A release candidate should clear P1 on every row. P2 and P3 need one platform each unless the case
says otherwise.

| # | OS | Notes |
|---|---|---|
| E1 | Windows 11, x64 | primary; Windows Credential Manager, named-pipe agent, TortoiseGit registry |
| E2 | Windows 10 1809 | oldest supported; verify the window renders and the app starts |
| E3 | macOS 14, arm64 | Keychain via `security`, `ssh-agent` over a unix socket |
| E4 | Ubuntu 22.04, x64 | `secret-tool`, GNOME Keyring, `SSH_AUTH_SOCK` |
| E5 | Ubuntu 22.04 headless-ish | no keyring daemon running — vaults must report unavailable, not crash |
| E6 | Any, no git installed | see ER-01 |

**Priorities.** P1 blocks release. P2 ships only with a written justification. P3 is a polish note.

---

## 4. Startup and shell

#### ST-01 — Cold start paints a window (P1)
1. Ensure no GitVault process is running.
2. Launch the application.

**Expect:** the window appears within 5 s, on the Dashboard, fully painted. No blank window, no
window that never renders. Regression guard for the dispatcher-initialisation defect (RG-01).

#### ST-02 — Scan completes and reports its duration (P1)
On the Dashboard, read the line under the count cards.

**Expect:** "Scan finished in N ms" with a plausible N (tens to a few hundred ms). Counts on the
five cards are non-zero on a developer machine.

#### ST-03 — Rescan (P1)
Click **Rescan**, then press **F5**.

**Expect:** both trigger a rescan; the duration line updates; counts stay stable when nothing on
disk changed.

#### ST-04 — Second instance (P2)
Launch a second GitVault while one is running.

**Expect:** defined behaviour — either a second window or focus on the existing one. Not a crash,
not a corrupted `settings.json`.

#### ST-05 — Window geometry survives a restart (P3)
Resize and move the window, close, reopen.

**Expect:** whatever the product decides, applied consistently. Note the actual behaviour.

---

## 5. Dashboard

#### DB-01 — Counts agree with the pages (P1)
Compare each card to the row count on its page.

**Expect:** identical. The Credentials card counts **all** entries; the Credentials page filters to
Git-related by default, so those two legitimately differ — see CR-01.

#### DB-02 — Health warnings expand (P2)
Click each warning row.

**Expect:** it expands to a detail with the offending path. Severity icon is coloured — red for
high, amber for medium, blue for low — and **visible** in both themes (regression guard, RG-05).

#### DB-03 — Warning severities are right (P2)
With the fixtures loaded:

**Expect:** plaintext credentials are high; passphrase-less private keys are medium; a missing
`.pub` is low/informational.

#### DB-04 — Clean machine (P2)
On a VM with no git, no keys and no clients:

**Expect:** zero counts, an empty-state message per page, no warnings, no exception.

---

## 6. Identities

#### ID-01 — Discovery (P1)
**Expect:** every identity in `~/.gitconfig`, the system config, and any included file appears.
Cross-check against `git config --global --list`.

#### ID-02 — Deduplication (P1)
An identity present in both global config and a GUI client appears **once**, with the Details pane
listing both occurrences.

#### ID-03 — Effective identity table (P1)
Open Details on any identity.

**Expect:** the effective-value table names the winning scope, and it matches what
`git config user.email` prints in that context.

#### ID-04 — Confidence column (P3)
**Expect:** config-file identities are `Certain`; anything inferred from a client's storage is
`Probable` or `Heuristic`. No blanks.

#### ID-05 — Included files (P2)
Add to `~/.gitconfig`:
```ini
[includeIf "gitdir:D:/gitvault-qa/repos/beta/"]
    path = D:/gitvault-qa/extra.gitconfig
```
with a distinct `user.email` in `extra.gitconfig`. Rescan.

**Expect:** the included identity is discovered and attributed to the included file, not to
`~/.gitconfig`. Remove both afterwards.

---

## 7. SSH keys

#### KY-01 — Formats (P1)
With the fixtures loaded, confirm each key is listed with the correct algorithm and bit count:
ed25519, RSA 2048, RSA 4096, ECDSA 256. Add a PuTTY `.ppk` (v2 and v3) if the platform has one.

**Expect:** no key shows `Unknown` for an algorithm the tool actually supports. Regression guard for
the `ssh-ed448` defect (RG-04).

#### KY-02 — Fingerprints match the reference tool (P1)
```bash
ssh-keygen -lf D:\gitvault-qa\keys\qa_ed25519.pub
```

**Expect:** byte-identical to the fingerprint in the grid, `SHA256:` prefix included.

#### KY-03 — Protection column (P1)
**Expect:** `qa_ed25519_locked` reports encrypted/protected; the others report unprotected. An
unprotected key raises a medium warning on the Dashboard.

#### KY-04 — Deduplication across formats (P2)
Convert a fixture key to `.ppk` and place it beside the original.

**Expect:** one row, not two — same public key, same fingerprint. The Format column reflects both.

#### KY-05 — Copy fingerprint and public key (P2)
Use both copy actions, paste into an editor.

**Expect:** exactly the value shown, no trailing junk. These are public values — no auto-clear
applies, and none should be claimed.

#### KY-06 — Reveal in file manager (P2)
**Expect:** Explorer / Finder / the desktop file manager opens with the key selected. On a Linux
box with no `xdg-open`, a graceful message rather than an exception.

#### KY-07 — Orphan and missing halves (P2)
**Expect:** `qa_orphan.pub` (no private half) and `qa_rsa4096` (no `.pub`) each produce their own
health finding, and neither breaks the scan.

#### KY-08 — Non-key files are ignored (P2)
**Expect:** `not-a-key.txt` produces no row and no warning. A junk file is not an error.

#### KY-09 — Permissions (P2, POSIX)
```bash
chmod 0644 ~/gitvault-qa/keys/qa_ed25519
```

**Expect:** a warning about the loose mode, and the Details pane shows the actual mode. On Windows
the permissions field is expected to be blank — ACL reading is a documented gap (section 18).

#### KY-10 — Private key material never displayed (P1)
Open Details on every key.

**Expect:** the public key is shown; the private half never is, encrypted or not. Then grep the
logs — see SC-01.

---

## 8. Agents

#### AG-01 — Detection (P1)
Start an agent and add a key:
```bash
ssh-add D:\gitvault-qa\keys\qa_ed25519
```

**Expect:** the agent appears with its transport (named pipe on Windows, socket path elsewhere) and
lists the loaded key by fingerprint.

#### AG-02 — No agent running (P1)
Stop the agent, rescan.

**Expect:** "no agent" empty state. Not an error dialog, not a hang.

#### AG-03 — Shell snippet (P2)
Copy the shell snippet and paste it into a terminal.

**Expect:** it is valid for that shell and sets the agent variables correctly.

#### AG-04 — Remove all keys (P1, destructive)
With only fixture keys loaded, click **Remove all** and confirm.

**Expect:** a confirmation dialog first; cancelling changes nothing; confirming empties the agent,
verified with `ssh-add -l`. Never run this on an agent holding the tester's own keys.

#### AG-05 — Agent dies mid-session (P2)
Kill the agent process while GitVault is open, then rescan.

**Expect:** clean transition to the empty state. No stale rows, no exception.

---

## 9. Credentials

#### CR-01 — Git filter actually filters (P1)
Look at the row count with the "show all" checkbox **unchecked**, then check it.

**Expect:** unchecked shows only Git-related entries; checked shows everything, a substantially
larger number on a real machine. Regression guard — this filter was once a no-op (RG-02).

#### CR-02 — Vaults enumerated (P1)
**Expect:** Windows Credential Manager / macOS Keychain / libsecret entries appear with host,
protocol, username and vault name. Cross-check one against `cmdkey /list` or Keychain Access.

#### CR-03 — Plaintext store is flagged (P1)
Point at the fixture `credentials/.git-credentials`.

**Expect:** a plaintext badge on the row and a high-severity Dashboard warning. This is the finding
that matters most to a real user.

#### CR-04 — Reveal requires confirmation (P1)
Click **Reveal** on a fixture entry.

**Expect:** a confirmation prompt naming what is about to be shown. Cancelling reveals nothing.

#### CR-05 — Auto-hide after 30 s (P1)
Reveal a fixture secret, start a stopwatch, do not touch the app.

**Expect:** it hides itself at ~30 s, matching the value on the Settings page.

#### CR-06 — Clipboard clears after 60 s (P2)
Copy a fixture secret, wait 65 s, paste.

**Expect:** the clipboard no longer holds it. The UI states plainly that a clipboard manager may
still retain it — verify that note is present and honest.

#### CR-07 — Opaque stores are reported, not cracked (P1)
Find a client whose secret storage GitVault cannot read.

**Expect:** it is listed as opaque, with the store named. No attempt to decrypt, no partial value,
no "unlock" affordance.

#### CR-08 — Hide (P2)
Reveal, then click **Hide** before the timeout.

**Expect:** hidden immediately; revealing again requires confirmation again.

---

## 10. Clients

#### CL-01 — Installed clients detected (P1)
**Expect:** every installed GUI client is found with its install path and config roots. Verify at
least one path by opening it.

#### CL-02 — Accounts and bound keys (P2)
**Expect:** accounts and key bindings shown per client, matching what the client's own UI reports.

#### CL-03 — Not-installed clients (P2)
**Expect:** absent clients are simply not listed. No "not found" rows, no errors.

#### CL-04 — Open config folder (P2)
**Expect:** opens the right directory, and is disabled or messaged when the path does not exist.

#### CL-05 — `core.sshCommand` per client (P3)
**Expect:** where a client pins an SSH command, it is displayed verbatim.

---

## 11. Profiles — the write path

**Every case here writes to disk. Fixtures only.** Capture `git config --global --list` and a copy
of `~/.ssh/config` before starting.

#### PR-01 — Planning writes nothing (P1)
Select the QA Alpha profile, click **Activate**.

**Expect:** a "Planned changes" diff appears with `-` old and `+` new lines. Now verify **nothing
changed on disk**: `git config --global --list` is identical to the pre-capture, and
`~/.ssh/config` is byte-identical (or still absent). **Apply** becomes enabled only now.

#### PR-02 — Scope selector defaults to Global (P1)
Note the scope dropdown when a profile with `"scope": "Repository"` is selected.

**Expect (current behaviour):** it shows *Global (this user)*, and the plan targets the global
config. This is a **known defect** — the profile's stored scope is not honoured by the dropdown.
Confirm it still reproduces, and confirm the dry-run diff makes it visible before any write. If the
dropdown ever silently applies a different scope than the diff showed, that is a P1 escalation.

#### PR-03 — Repository scope (P1)
Set the scope to Repository and the path to `D:\gitvault-qa\repos\alpha`. Preview, then **Apply**.

**Expect:** `repos/alpha/.git/config` gains the identity; `~/.gitconfig` is **unchanged**. Check
explicitly — writing the global identity into a repo config was a real defect in development.

#### PR-04 — Activate then deactivate restores byte-for-byte (P1)
Take a SHA-256 of every file the plan names. Activate, apply, then deactivate and apply.

```bash
git config --global --list > after-deactivate.txt
```

**Expect:** `after-deactivate.txt` matches the pre-capture exactly, and every named file hashes to
its original value. No empty `[section]` headers left behind. No `# >>> GitVault managed` markers
left in `~/.ssh/config`. This is the single most important case in the plan.

#### PR-05 — Deactivation restores a pre-existing value (P1)
Set `user.email` in the target scope to something distinctive first. Activate, then deactivate.

**Expect:** the distinctive value comes back. Deactivation restores; it does not merely delete.

#### PR-06 — Managed block isolation (P1)
Put your own `Host` blocks above and below where GitVault will write, plus comments. Activate, then
deactivate.

**Expect:** everything outside the markers is untouched — including comments, blank lines and
ordering. A file with no trailing newline gains one; that is the only documented side effect.

#### PR-07 — Rollback (P1)
Activate and apply. Click **Roll back**.

**Expect:** the pre-activation state returns. Verify against the hashes from PR-04.

#### PR-08 — Snapshot retention (P2)
Activate and deactivate repeatedly.

**Expect:** snapshots accumulate to a cap of 50, oldest evicted. Nothing in a snapshot contains
private key material — grep one to confirm.

#### PR-09 — Blocked plan applies nothing (P1)
Make a target file read-only, then preview and apply.

**Expect:** the plan reports it cannot proceed, and **no partial write happens**. Not some steps
applied and some failed.

#### PR-10 — System scope without elevation (P1)
Select System scope on a non-elevated session.

**Expect:** "insufficient permissions, run elevated?" — a message, not an exception dialog, and
**no automatic elevation**. Verify the process did not request UAC.

#### PR-11 — Dry-run-by-default setting (P2)
Turn off "Dry run by default" in Settings.

**Expect:** whatever the setting promises, honoured exactly. Apply must still be impossible before
a plan exists.

---

## 12. Repositories

#### RP-01 — Scan (P1)
With `repositoryScanRoots` set, click **Scan for repositories**.

**Expect:** both fixture repositories found, with remote URL and path.

#### RP-02 — Effective identity per repository (P1)
**Expect:** `beta` shows its local identity (`QA Beta <qa-beta@example.invalid>`); `alpha`, which
has none, falls through to the global identity. Confirm with `git -C … config user.email`.

#### RP-03 — Empty state (P2)
Clear the scan roots and rescan.

**Expect:** the empty state appears. Note that its text currently points at Settings, where no such
control exists — see section 18.

#### RP-04 — Nested and bare repositories (P3)
Create a repository inside another, and a `--bare` one.

**Expect:** defined behaviour, documented. Not a duplicate row and not an infinite walk.

---

## 13. Settings and diagnostics

#### SE-01 — Settings persist (P1)
Change language, theme and both checkboxes. Restart.

**Expect:** all four survive, and `settings.json` is valid JSON afterwards.

#### SE-02 — Diagnostics preview before save (P1)
Click **Preview diagnostics bundle**.

**Expect:** the contents are shown *before* anything is written; **Save bundle** is disabled until
previewed. Read the preview: it must contain setting *names* but no config *values*, no key blobs,
no tokens.

#### SE-03 — Saved bundle is clean (P1)
Save a bundle, then search it:

```bash
grep -rniE "BEGIN .*PRIVATE KEY|Private-Lines|ghp_|glpat-|password" bundle-dir
```

**Expect:** no hits. Fingerprints (`SHA256:…`) are expected and fine.

#### SE-04 — Open logs folder (P2)
**Expect:** the right directory opens, and the path shown on the page matches it.

#### SE-05 — Telemetry statement (P1)
**Expect:** the page states GitVault collects nothing and makes no network calls — and SC-02 proves
it true.

#### SE-06 — Corrupt settings file (P2)
Truncate `settings.json` mid-object, launch.

**Expect:** the app starts on defaults and says so. It must not refuse to launch and must not throw.

---

## 14. Logs

#### LG-01 — Live log (P2)
Rescan with the Logs page open.

**Expect:** entries appear live with level and timestamp.

#### LG-02 — Filter (P2)
**Expect:** filtering by text and by level both narrow the list; clearing restores it.

#### LG-03 — Clear (P3)
**Expect:** clears the in-app view. State whether the on-disk log is affected, and verify that the
statement is true.

---

## 15. Localization

#### LC-01 — Every string translates (P1)
Switch to Русский, then 中文（简体）. Visit **all ten pages** each time.

**Expect:** no English left behind — including the nav rail, page titles, column headers, buttons,
empty states, the search watermark, dialogs and warning text. Regression guard: `{loc:Tr}` captions
once failed to retranslate outside the nav rail (RG-03).

#### LC-02 — Switching is immediate (P1)
**Expect:** the UI changes without a restart, and the current page and selection survive it.

#### LC-03 — Russian plurals (P2)
Arrange counts of 1, 2, 5 and 21 keys.

**Expect:** ключ / ключа / ключей / ключ. Getting 21 wrong is the classic failure.

#### LC-04 — CJK glyphs (P1)
In Chinese, check every page for missing-glyph boxes.

**Expect:** none, on all three platforms — the font fallback chain covers Windows, macOS and Linux
families.

#### LC-05 — No layout breakage (P2)
**Expect:** German-length Russian strings do not clip buttons or overlap columns. Note any
truncation without an ellipsis.

#### LC-06 — Language survives a restart (P1)
Set Chinese, restart.

**Expect:** it comes back Chinese. Related: the language must **never** change on its own. If the UI
switches language without the tester touching the dropdown, that is a P1.

---

## 16. Theme, layout and DPI

#### UI-01 — Three theme modes (P1)
Light, Dark, Follow system.

**Expect:** all text legible, all icons visible, severity colours distinct in both. Nothing renders
as an invisible glyph.

#### UI-02 — System theme change while running (P2)
Toggle the OS theme with GitVault open, in Follow-system mode.

**Expect:** the app follows without a restart.

#### UI-03 — DPI (P1)
Test at 100 %, 125 %, 150 % and 200 %.

**Expect:** icons stay sharp, text is not clipped, layout does not overlap. Icons are vector
geometry, so blur at any scale is a defect.

#### UI-04 — Minimum window size (P1)
Resize to 1024 × 720.

**Expect:** every page remains usable — no clipped buttons, no unreachable controls, scrollbars
where needed.

#### UI-05 — Very wide window (P3)
Maximise on an ultrawide display.

**Expect:** content does not stretch into unreadable line lengths.

#### UI-06 — Window icon and taskbar (P2)
**Expect:** the GitVault mark appears in the title bar, the taskbar and the alt-tab switcher; on
macOS in the Dock; on Linux where the desktop shows one. Not a default placeholder.

---

## 17. Keyboard and accessibility

#### AX-01 — Tab order (P1)
Traverse each page with Tab alone.

**Expect:** a sensible order, a visible focus ring at every stop, nothing reachable-but-invisible
and nothing skipped.

#### AX-02 — Ctrl+F (P1)
From anywhere in the window, press Ctrl+F.

**Expect:** focus moves to the search box.

#### AX-03 — F5 (P1)
**Expect:** rescans from any page.

#### AX-04 — Keyboard-only navigation (P2)
Reach every page and activate every primary action without a mouse.

**Expect:** possible throughout. Note anything mouse-only.

#### AX-05 — Escape closes dialogs (P2)
**Expect:** Escape cancels confirmation dialogs, and cancelling never performs the action.

#### AX-06 — Screen reader (P3)
With Narrator or VoiceOver, traverse the Dashboard and one grid.

**Expect:** controls are announced with meaningful names. Record gaps as findings rather than
blockers.

---

## 18. Permissions and error handling

#### ER-01 — git not installed (P1)
On a VM without git:

**Expect:** a clear "git not found" message, and every non-git feature still works. No crash on
launch.

#### ER-02 — Unreadable directory (P1)
Deny read access to a directory in a scan root.

**Expect:** the scan completes, that path is reported as inaccessible, and the message offers to run
elevated rather than throwing. **No automatic elevation.**

#### ER-03 — Locked file (P2)
Hold `~/.gitconfig` open exclusively, then rescan and try a plan.

**Expect:** a readable message naming the file. No partial write.

#### ER-04 — Malformed git config (P1)
Introduce a syntax error into a config file.

**Expect:** the parser reports which file and line, and keeps discovering everything else.

#### ER-05 — Corrupt key file (P2)
Truncate a private key by half.

**Expect:** it is reported as unreadable with a reason, and does not abort the scan.

#### ER-06 — Vault unavailable (P1, E5)
Run on Linux with no keyring daemon.

**Expect:** the vault is reported unavailable. Not an exception, not an empty list implying "no
credentials exist".

#### ER-07 — Read-only application data directory (P2)
Make the app data directory read-only, then change a setting.

**Expect:** a clear failure message; the app keeps running with in-memory settings.

---

## 19. Security assertions

These are the acceptance criteria the whole project rests on. Run every one before release.

#### SC-01 — Nothing secret in the logs (P1)
Exercise the app broadly — scan, reveal a fixture secret, activate and deactivate a profile. Then:

```bash
grep -rniE "BEGIN .*PRIVATE KEY|Private-Lines|Private-MAC|ghp_|glpat-|ATATT|password=|://[^/]*:[^@]*@" "$APPDATA/GitVault/logs"
```

**Expect:** zero hits. A hit is an automatic release blocker.

#### SC-02 — No network traffic (P1)
Run the whole plan with Wireshark, `netstat`, or a host firewall set to block and log.

**Expect:** no outbound connection from GitVault, ever — no telemetry, no update check, no icon
fetch. The only permitted exception is a host-key verification the tester explicitly initiated.

#### SC-03 — No write without a previewed plan (P1)
Across the whole session, confirm every disk write was preceded by a plan the tester saw.

**Expect:** discovery, rescanning, filtering, searching, revealing and copying write nothing outside
logs and settings.

#### SC-04 — No elevation prompt (P1)
**Expect:** GitVault never triggers UAC, `sudo` or an authorisation dialog on its own. It asks the
user to relaunch elevated instead.

#### SC-05 — Snapshots hold no secrets (P1)
Grep the snapshot directory with the SC-01 pattern.

**Expect:** zero hits.

#### SC-06 — Key files are not modified by reading (P1)
Hash every fixture key before and after a full session that includes opening every Details pane.

**Expect:** identical hashes, and unchanged mtimes and modes.

---

## 20. Packaging smoke

Run on a machine that has never had the SDK installed.

#### PK-01 — Windows installer (P1)
**Expect:** installs, launches, appears in Add/Remove Programs with the correct icon and version,
and uninstalls cleanly leaving no service or scheduled task.

#### PK-02 — macOS bundle (P1)
**Expect:** the `.app` launches, shows the right Dock icon, and Gatekeeper behaviour is documented
(signed, or the tester is told what to expect).

#### PK-03 — Linux package (P1)
**Expect:** installs, launches, and the desktop entry shows the icon in the application menu.

#### PK-04 — All three languages are in the build (P1)
```bash
grep -c "Пересканировать" GitVault
```
Or switch languages in the packaged build.

**Expect:** all three cultures work in a single-file publish. Trimming has silently dropped satellite
resources before.

#### PK-05 — Version (P2)
**Expect:** the version in the package metadata matches the tag and what the app reports.

#### PK-06 — First run on a clean machine (P1)
**Expect:** the application data directory is created, defaults are sound, and nothing in the
first-run path assumes a developer machine.

---

## 21. Regression cases

Each of these was a real defect during development. They are cheap to check and expensive to miss.

#### RG-01 — Window paints at all (P1)
Cold start.

**Expect:** the window renders. The cause was touching `Dispatcher.UIThread` before Avalonia
initialised, which produced a window that existed and never painted. Covered by ST-01; listed
separately because it is invisible to unit tests.

#### RG-02 — Credentials filter is not a no-op (P1)
See CR-01. The filter was once seeded from the wrong collection and silently matched everything.

#### RG-03 — Every caption retranslates (P1)
See LC-01. A binding bug once retranslated only the nav rail while page content stayed English.
Check page titles, column headers, buttons and **the search watermark** specifically.

#### RG-04 — Unusual key algorithms are not "Unknown" (P2)
Place an `ssh-ed448` key — one OpenSSH itself declines — in a scan folder.

**Expect:** algorithm identified, not reported as `Unknown`.

#### RG-05 — Severity icons are visible (P2)
See DB-02. Guessed theme brush names once resolved to null, and a null brush paints nothing at all
— the icons were invisible rather than mis-coloured.

#### RG-06 — Deactivation leaves no empty sections (P1)
See PR-04. `git config --unset` leaves the `[section]` header behind, which breaks byte-identical
restore.

#### RG-07 — Repo-scope deactivation does not write the global identity (P1)
See PR-03. Reading the *effective* value instead of the *scoped* value once caused deactivation to
write the user's global identity into a repository's local config.

#### RG-08 — Passwords never reach a command line (P1)
While a credential operation runs, inspect the process tree:

```bash
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'credential' } | Select-Object CommandLine
```

**Expect:** no secret in any argument list. Secrets go over stdin.

---

## 22. Repository configuration and project settings

**Every case here writes to disk. Fixtures only.** Before starting, in the fixture repository run
`git config --local --list > /tmp/qa-local-before.txt` and keep it.

#### RC-01 — The scope shown is the scope written (P1)
Open a repository, go to **Configuration**. Set `user.email` at **Local** scope to
`qa-local@example.invalid`.

**Expect:** the preview's `-` line shows what the *local* file holds, not the effective value
inherited from global. If your global `user.email` appears as the "before" value, that is a P1
defect — it once caused a deactivation to write a global identity into a repository.

#### RC-02 — The preview is not the apply (P1)
Close the preview with the window's X rather than confirming.

**Expect:** `git config --local --list` is byte-identical to the capture.

#### RC-03 — Rollback puts the file back (P1)
Confirm a change, then go to **Snapshots & rollback**, select the newest entry, roll it back.

**Expect:** `git config --local --list` matches the capture again.

#### RC-04 — Project settings live beside the repository (P2)
On **Project settings**, set a note and a key path. Confirm.

**Expect:** `git config --local --get-regexp '^gitvault\.'` lists them. Move the whole repository
directory elsewhere and reopen it: the settings are still there.

#### RC-05 — Clearing a field removes the key (P2)
Clear the note and confirm.

**Expect:** `gitvault.note` is gone from the output rather than present and empty. With every field
cleared, `git config --local --get-regexp '^gitvault\.'` prints nothing.

---

## 23. Remotes, branches and tags

#### RB-01 — A ref backup is taken before a deletion (P1)
Create a throwaway branch with a commit on it that exists nowhere else. Delete it through
**Branches**.

**Expect:** the preview warns that the branch has commits on no other branch. After applying,
`git for-each-ref refs/gitvault/backup` lists an entry, and `git log <that ref>` still shows the
commit.

#### RB-02 — Garbage collection does not eat the backup (P1)
After RB-01, run `git gc --prune=now` in the repository.

**Expect:** the commit is still reachable through the backup ref. This is the whole reason backups
are refs rather than file copies.

#### RB-03 — The checked-out branch cannot be deleted (P2)
Try to delete the branch you are on.

**Expect:** blocked with a reason, and the confirming button never becomes available.

#### RB-04 — A signed tag says what cannot be restored (P2)
If a fixture has a signed tag, delete it.

**Expect:** a warning that the signature cannot be recreated. Confirm, then restore the backup:
the tag points where it did, and is unsigned.

---

## 24. Commit history and editing

**These cases rewrite history. Fixture repository only. Never run them against work you have.**

#### CE-01 — The history page reads without touching anything (P1)
Open **Commits**. Change the filters, select several commits.

**Expect:** `git rev-parse HEAD` is unchanged throughout, and `git status --porcelain` stays empty.

#### CE-02 — An edit is collected, not applied (P1)
Select a commit two or three back, **Edit…**, change the message, confirm the dialog.

**Expect:** the row is marked as edited and a count appears. `git log -1 --format=%s <that commit>`
is unchanged: nothing has been written.

#### CE-03 — Confirming requires typing the branch name (P1)
Click **Apply…**. In the review, try the confirming button without typing anything, then type the
branch name incorrectly, then correctly.

**Expect:** the button is unavailable until the branch name matches exactly. This is the strictest
gate in the application; if it can be passed any other way, that is a P1 defect.

#### CE-04 — The preview says how far the change reaches (P1)
Read the review dialog for CE-03.

**Expect:** it names both counts — commits edited, and commits that get a new identifier only
because they come after one that changed. The second number should be greater than zero when you
edited a commit that is not the tip.

#### CE-05 — The rewrite is one ref away from being undone (P1)
Apply the rewrite. Note the new `git rev-parse HEAD`. Go to **Snapshots & rollback** and restore
the backup the operation recorded.

**Expect:** `git rev-parse HEAD` is the value it had before, and `git log -1 --format=%s` shows the
original message.

#### CE-06 — A dirty working tree blocks a rewrite (P1)
Make an uncommitted change. Try to apply an edit.

**Expect:** blocked with a reason, not attempted.

#### CE-07 — A date without an offset is refused (P2)
In the commit editor, set the author date to `2024-03-01 09:15:00` with no offset.

**Expect:** the confirming button is unavailable. Add `+03:00` and it becomes available. Apply and
check `git log -1 --format=%ai`: the offset you typed is what was written, not this machine's.

#### CE-08 — Editing file content carries forward (P1)
Select a commit that introduced a file no later commit touched. **Edit file…**, change a line,
confirm, apply.

**Expect:** the preview reports commits whose content changes. Afterwards
`git show HEAD:<that file>` shows your change, and `git show <the edited commit>:<file>` does too.

#### CE-09 — A conflict is a question, not a failure (P1)
Edit the same lines of a file that a later commit also changed. Apply.

**Expect:** a dialog appears with git's merged text and conflict markers, *before* any preview.
Close it: `git status --porcelain` is empty and `git rev-parse HEAD` is unchanged — the repository
was never put into a conflicted state.

#### CE-10 — A marker cannot be committed (P1)
Reach the conflict dialog again and try to confirm with the markers still in the text.

**Expect:** the confirming button is unavailable until every `<<<<<<<` and `>>>>>>>` is gone.

#### CE-11 — A binary file is refused (P2)
Select a commit holding a binary file and try **Edit file…** on it.

**Expect:** a message saying it cannot be edited here, and no editor opens.

---

## 25. History tools — purge, move, re-attribute

**These rewrite the whole branch. Fixture repository only.**

#### HT-01 — The purge warning is unmissable (P1)
Open **History tools** and read the removal section before typing anything.

**Expect:** a prominent warning saying that removing a file does not undo the fact that it was
committed, and that a key, token or password has to be revoked. If this is absent or below the
fold, that is a P1 defect: the feature's whole risk lives in that sentence.

#### HT-02 — A purged file is gone from the branch (P1)
Purge a fixture file that several commits held.

**Expect:** `git log --name-only --format= HEAD` no longer mentions it, and every other file is
unchanged.

#### HT-03 — And still reachable through the backup (P1)
After HT-02, run `git log --all --name-only --format= | grep <the file>`.

**Expect:** it is still there, via the backup ref. This is the honest half of the warning: verify
the interface said so before you did it.

#### HT-04 — Commits that only touched it survive (P2)
If a fixture commit changed only the purged file, count commits before and after with
`git rev-list --count HEAD`.

**Expect:** the same count. GitVault keeps them rather than dropping them, and warns that it will.

#### HT-05 — A move keeps the blob (P2)
Move a file to a new path across history. Compare `git rev-parse HEAD:<new path>` with the object
name you noted from `git rev-parse HEAD:<old path>` beforehand.

**Expect:** identical. A move moves the entry rather than rewriting content.

#### HT-06 — A move onto an occupied path is refused (P2)
Try to move a file onto a path another file already occupies.

**Expect:** blocked, with a reason naming the collision.

#### HT-07 — Someone else's authorship is left alone (P1)
In a fixture with commits by two identities, replace one address.

**Expect:** the count of edited commits matches only the commits carrying that address, and
`git log --format='%an %ae'` shows the other person untouched.

---

## 26. Ignore, attributes, mailmap

#### RF-01 — The page says who a change reaches (P2)
Open **Ignore & attributes** and select each of the four files in turn.

**Expect:** `.gitignore`, `.gitattributes` and `.mailmap` say the change reaches everyone once
committed; `.git/info/exclude` says it never leaves this clone.

#### RF-02 — The preview is a difference, not the whole file (P2)
Open a `.gitignore` with twenty or more lines, change one line, save.

**Expect:** the preview shows the changed line with a little context and a marker for the rest —
not the entire file twice over.

#### RF-03 — Line endings survive (P2)
On a fixture file written with CRLF, make an edit and save. Check with
`file` or `git diff --stat`.

**Expect:** the file still uses CRLF and the diff shows one changed line, not every line.

#### RF-04 — Writing is not committing (P2)
After RF-02, run `git status --porcelain`.

**Expect:** the file shows as modified. GitVault wrote it and stopped there, and said so.

---

## 27. Hooks

**A hook is a program git runs. Fixture repository only, and read anything you paste.**

#### HK-01 — The warning is unconditional (P1)
Open **Hooks**.

**Expect:** a prominent warning saying a hook is a program git runs by itself with your
privileges, present every time the page is opened rather than dismissible.

#### HK-02 — The directory shown is the one git uses (P1)
Set `git config core.hooksPath tools/hooks` in the fixture and reopen the page.

**Expect:** the directory shown is `tools/hooks`, with a note that it has been redirected. Write a
hook and confirm it lands there and *not* in `.git/hooks`.

#### HK-03 — An enabled hook is the one git runs (P1)
Write a `pre-commit` containing `#!/bin/sh` and `exit 1`, enabled. Then try to commit anything.

**Expect:** git refuses the commit. This is the only end-to-end proof the feature works.

#### HK-04 — Disabling leaves nothing runnable (P1)
Uncheck "let git run this hook" and save. Then try to commit again.

**Expect:** the commit succeeds, `.git/hooks/pre-commit` is gone, and `pre-commit.sample` is
present. A live copy left behind is a P1 defect.

#### HK-05 — Nothing runs a hook to check it (P1)
Write a hook whose body is `touch /tmp/gitvault-should-not-exist`. Save it. Do not commit.

**Expect:** the file does not exist. GitVault never runs a hook, not even to validate it.

#### HK-06 — A skipped hook is named (P2)
On POSIX, `chmod -x .git/hooks/pre-commit` and refresh.

**Expect:** the state says the hook is enabled but not executable, and a note explains git will
skip it silently.

---

## 28. Working trees, stashes, submodules

#### WT-01 — A dirty working tree is not removed (P1)
Add a working tree, make an uncommitted change inside it, remove it through the page.

**Expect:** the operation fails with git's own message and the directory still exists with your
change in it. `--force` must never be passed.

#### WT-02 — Removing a checkout is not deleting the work (P2)
Remove a clean working tree.

**Expect:** the directory is gone and `git branch --list <its branch>` still lists the branch. The
preview warned that only the checkout goes.

#### WT-03 — A locked working tree is refused (P2)
Lock a working tree with a reason, then try to remove it.

**Expect:** blocked, and the reason you typed is shown back on the page.

#### ST-01 — There is no "pop" (P2)
Look at the stashes page.

**Expect:** separate "put back" and "discard" buttons, and a note explaining why the combined
operation is not offered.

#### ST-02 — Putting back keeps the entry (P1)
Set changes aside, then put them back.

**Expect:** the changes are in the working tree *and* the entry is still listed.

#### ST-03 — Putting back into dirty work is refused (P1)
With an entry in the list, make an unrelated uncommitted change, then try to put the entry back.

**Expect:** blocked with a reason. Merging into work in progress can leave markers in a file you
are editing.

#### ST-04 — A discarded entry is recoverable (P1)
Note `git rev-parse stash@{0}`, then discard it.

**Expect:** the list no longer shows it, and `git cat-file -t <that object>` still says `commit`.
`git show <object>:<file>` shows the work.

#### SM-01 — The page refuses the network out loud (P1)
Open **Submodules** on a fixture with a `.gitmodules`.

**Expect:** a statement, before the buttons, that GitVault will not fetch or check anything out and
that `git submodule update` is yours to run. No button offers to do it.

#### SM-02 — An address correction needs a second step (P2)
Change a submodule's address and confirm.

**Expect:** `.gitmodules` holds the new address, a warning said the local configuration has not
been told, and `git config --get submodule.<name>.url` still holds the old one until you use
**Apply to this clone**.

---

## 29. Known gaps

Confirm these still behave as described; do not file them as new defects.

Three gaps listed here in earlier revisions are closed: scan roots and key folders now have
editors under Options, profiles have a real editor, and the scope selector follows the profile's
stored scope. A tester meeting any of those again is meeting a regression, not a known gap.

| Gap | Behaviour |
|---|---|
| Passphrase prompt is not `char[]`-backed | documented in `docs/security.md` |
| A revealed secret is briefly a `string` | same |
| Windows ACLs are not read | the permissions field is blank on Windows |
| No `bcrypt_pbkdf` | passphrase-protected operations are delegated to `ssh-keygen` |
| Encrypted PPK v2 MAC unverified | v3 is verified |
| Content edits cannot follow a rename | editing a file a later commit renames is refused, not tracked through the rename |
| Non-UTF-8 files cannot be edited | refused rather than re-encoded |
| A purge does not prune the object database | the content stays reachable through the backup ref; see HT-03 |
| Submodules cannot be initialised or updated | both need the network, which this program does not use; see SM-01 |
| Restoring a dropped stash restores the commit, not the list entry | the work is recoverable, the position in the list is not; see ST-04 |
| A hook's content is never checked | GitVault writes whatever it is given; see HK-05 |

---

## 30. Teardown checklist

Run this after every session, and verify each line rather than assuming.

1. `pwsh build/qa-fixtures.ps1 -Workspace <path> -Remove`
2. Restore `settings.json` from your backup.
3. Delete `profiles.json` and `activation-state.json` from the application data directory, unless
   they were yours to begin with.
4. Delete the snapshot directory.
5. `git config --global --list` matches your pre-session capture.
6. `~/.ssh/config` contains no `# >>> GitVault managed` markers.
7. `ssh-add -l` shows what it showed before.
8. Any diagnostics bundle you saved is deleted.
9. Any `includeIf` block added for ID-05 is removed.

---

## 31. Results template

| Case | Env | Result | Build | Notes |
|---|---|---|---|---|
| ST-01 | E1 | pass / fail / blocked | | |

Record a defect with: case ID, environment, build, exact steps, expected versus actual, and the
relevant log excerpt **after** confirming it contains no secret.

A release candidate ships when every P1 passes on every environment in section 3, and every P2 that
fails has a written justification.
