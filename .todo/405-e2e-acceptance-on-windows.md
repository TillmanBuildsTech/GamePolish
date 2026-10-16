---
id: 405
title: E2E acceptance on Windows
phase: 4
bucket: mvp
depends_on: [213, 403]
blocks: [404]
status: todo
area: desktop
---

# 405 — E2E acceptance on Windows

## Objective

A signed-off acceptance record for v0.1.0 on Windows, run against the **real** OctoWoW install — first
through the CLI harness from task 213, then through the Avalonia UI — covering install, backup-before-write,
disable/enable, update, uninstall, restore, four failure drills, and a two-root proof that nothing was
written outside the game install and the Game Polish data root.

## Context

Deliverable: `docs/acceptance/v0.1.0-windows.md` — a table of scenario | steps | expected | observed |
pass/fail | evidence. A row without an observed value fails this task; a row that was not run is `NOT RUN`
with the reason. This record feeds 404's pre-tag checklist (405 blocks 404): never tag a release whose
record has unrun rows.

Environment, verified 2026-10-15:

- Install directory `D:\OctoWoW`; on disk the folder is spelled `D:\Octowow` (NTFS is case-insensitive, same
  folder). `WoW.exe` is in the root, matching `install_detection.verify_file`.
- `Interface/AddOns/` is **not** empty: 16 entries — 13 `Blizzard_*` folders shipped with the client, plus
  `AtlasLoot`, `pfExtend`, `pfQuest`, `pfQuest-turtle`. Installing must add to that and disturb nothing
  else; this is the normal case, not an edge case.
- `AtlasLoot/AtlasLoot.toc` confirms the loader rule: WoW reads `<folder>/<folder>.toc`. A repo whose `.toc`
  sits in a subdirectory will not load — the likeliest real-world failure for the incumbent we replace.
- `C:\Users\Brandon\.game-polish\` does **not** exist yet; task 201 creates it on first run, so its
  appearance is itself an observation.
- Use addons that are **not installed yet** (`magnify`, `flighttracker`, `itemrack`, `succ-bag`); do not
  destroy `pfUI` / `pfQuest` / `AtlasLoot`, which are in active use. Use `Otari98/AtlasLoot` (already on
  disk) once as the deliberate already-installed collision probe.
- Harness verbs come from **task 213** — read it and use its real command names. Do not invent verbs. If 213
  is not done, stop and record `blocked` with the reason.
- The registry from 402 must be live for the Discover-sourced installs; if the raw URL still 404s, mark those
  rows `NOT RUN` and run the ZIP- and URL-sourced ones instead.

## Scope

- Pre-run baseline evidence: hashes, directory listings, git status, registry check.
- Scenarios S1–S7, CLI first, then the same paths through the UI.
- Failure drills S8a–S8e, each with its repro and its observed result.
- The two-root proof (S9) and the whole-machine checks.
- `docs/acceptance/v0.1.0-windows.md`, committed, with raw command output pasted in.

## Out of scope

- Fixing bugs found: record a repro and file a task; if you do fix something here, update the record and say
  so explicitly. A `NOT RUN` row is a valid outcome; a guessed one never is.
- macOS (the osx-arm64 artifact cannot run here), Settings Editor, Discover-full (501), Mod Detail (502),
  auto-update (605).
- Unit and headless tests (each task's own gate; 308 for UI) — this pass is the only evidence for real
  filesystem effects, which headless tests cannot produce.

## Implementation notes

**S0 — baseline.** Run all of it and paste:

```bash
find /d/OctoWoW/Interface/AddOns -type f -exec sha256sum {} + | sort > "$TMPDIR/e2e-before-addons.txt"
ls -la /d/OctoWoW > "$TMPDIR/e2e-before-install-root.txt"
ls -la /c/Users/Brandon/.game-polish 2>&1 | head
ls -la /c/Users/Brandon | grep -v '\.game-polish' > "$TMPDIR/e2e-before-profile.txt"
git -C C:/Users/Brandon/Projects/game-polish status --short > "$TMPDIR/e2e-before-git.txt"
reg query "HKCU\Software\GamePolish" 2>&1        # expect: unable to find the specified registry key
```

**S1 — Add game (manual path).** `games add octowow --path D:\OctoWoW` (verb per 213). Expect
`~/.game-polish/games.json` to gain the path and the Library UI to list OctoWoW. Repeat with
`D:\OctoWoW\Interface` and `D:\OctoWoW\WTF`: both must be rejected with a message naming `WoW.exe` and must
not be recorded. Probes: a plausible-but-wrong path being accepted.

**S2 — Install an addon.** `install octowow magnify` from the registry (or ZIP/URL if 402 is not live).
Expect `Interface/AddOns/magnify/magnify.toc` — the `.toc` **directly inside** the addon folder — and
`Interface/AddOns` at 17 entries with the 13 `Blizzard_*` folders and `AtlasLoot` untouched. Probes: the
`.toc` landing one level too deep (repo root has no `.toc`), and a sibling addon being clobbered.

```bash
ls -la /d/OctoWoW/Interface/AddOns/magnify | head
ls /d/OctoWoW/Interface/AddOns | wc -l        # 16 -> 17
```

**S3 — Backup exists BEFORE the write.** A successful install cannot prove ordering, so force the write to
fail after the snapshot point:

```bash
attrib +R "D:\OctoWoW\Interface\AddOns\magnify\magnify.toc"     # or icacls deny W
# re-run the same install; it MUST fail
ls -la /c/Users/Brandon/.game-polish/octowow/backups/           # a new timestamped dir must exist
attrib -R "D:\OctoWoW\Interface\AddOns\magnify\magnify.toc"
```

On a normal install also record the mtime ordering — backup directory before destination folder — and that
the manifest entry names the backup id. Probes: the snapshot taken *after* the first byte, a data-loss bug
that looks like success.

**S4 — Disable, then enable.** `disable octowow magnify` → the game folder is gone, a staged copy survives
under the data root, the manifest marks it disabled. `enable octowow magnify` → content returns
**byte-identical** (hash both sides and diff the lists). Probes: disable deleting instead of staging, enable
restoring a different revision.

**S5 — Update.** `update octowow magnify` → a new backup exists, the recorded HEAD SHA or tag changed, files
match the new revision, and `git -C <addon dir> status --short` is clean. Probes the incumbent's actual
bug: overwriting without a snapshot.

**S6 — Uninstall.** `uninstall octowow magnify` → folder gone (count back to 16), backup exists, manifest no
longer lists it as installed, registry cache untouched.

**S7 — Restore.** `backups restore octowow <id>` against the S6 backup → prior content byte-identical to the
S0/S2 hashes; then restore an older backup and confirm it wins.

**S8 — Failure drills.** Exact steps, expected observation, failure mode probed:

- **a. Unreachable source.** Install from `https://github.com/game-polish/does-not-exist` and from a mistyped
  host. Expect a typed, readable error, non-zero exit, **no** addon folder, and no manifest row claiming
  success. Probes silent partial installs and unhandled exceptions.
- **b. ZIP with traversal payloads.** Build it with Python (stdlib `zipfile` writes such names happily):
  `../../../../Users/Brandon/pwned.txt`, `..\\..\\escape.txt`, an absolute `C:/Windows/Temp/pwned.txt`, and a
  symlink/junction escaping the destination. Expect rejection **before any write**, nothing created outside
  `Interface/AddOns`, and a message naming the offending entry. Probes invariant 3.
- **c. Read-only game folder.** Extend S3 to a whole read-only addon directory: typed error, backup kept,
  read-only attributes restored, app still usable.
- **d. Addon folder deleted behind the app's back.** With the app running, `rm -rf` a folder it believes is
  installed. Expect a re-scan to report the discrepancy rather than lie, and disable/enable/uninstall to give
  a clear error — never an unhandled exception.
- **e. No execution of mod content.** Install a ZIP containing a shell script and a `.git/hooks/pre-commit`
  payload that would write `pwned.txt`. Expect the files placed and never run: no marker file appears
  anywhere. Probes invariant 2.

**S9 — Two roots only.** Re-run S0 and diff; every unexpected line is a finding against invariant 4, not
noise to explain away:

```bash
find /d/OctoWoW/Interface/AddOns -type f -exec sha256sum {} + | sort > "$TMPDIR/e2e-after-addons.txt"
diff "$TMPDIR/e2e-before-addons.txt" "$TMPDIR/e2e-after-addons.txt" | head -60   # only expected deltas
ls -la /d/OctoWoW | diff "$TMPDIR/e2e-before-install-root.txt" -                 # no new files in root
ls -la /c/Users/Brandon | diff "$TMPDIR/e2e-before-profile.txt" -                # only .game-polish added
git -C C:/Users/Brandon/Projects/game-polish status --short                      # equals the baseline
reg query "HKCU\Software\GamePolish" 2>&1                                        # still: not found
ls -la "$TMPDIR" | grep -i game                                                  # no stale staging left
```

**UI pass.** Repeat S1, S2, S4, S5, S6 and S7 through the Avalonia UI (Library rows and their actions, Add
Game, Add Mod, Backups + restore) using the 403 published `win-x64` build, not `dotnet watch`. The same typed
errors should surface in the error drawer / toasts (307). Record what the UI showed that the CLI could not:
progress states, a row's disabled state, the backup list contents.

## Acceptance criteria

- `docs/acceptance/v0.1.0-windows.md` has one row per scenario above, each with exact steps/commands, expected
  observation, observed result, pass/fail and evidence (pasted output or a file path). No row says only "OK".
- S3 shows a backup directory created by an install that subsequently failed, plus the mtime ordering on a
  successful install.
- Every failure drill has an observed outcome and, where it exposed a bug, a repro and a filed task id or an
  explicit "not filed yet".
- S9 shows the before/after hashes and the four profile / git / registry / temp checks with real output.
- Anything `NOT RUN` says so, with the reason, and 404's checklist reflects it.

## Verification

```bash
git status --short
ls -la docs/acceptance/
grep -v '^|' docs/acceptance/v0.1.0-windows.md | grep -c .        # prose should be minimal
grep -n 'NOT RUN' docs/acceptance/v0.1.0-windows.md || echo "every row was run"
```

The record is the artifact; "it all worked" is not. Re-run any failed row after a fix and update the record
in the same commit.

## References

- `../game-polish-notes/docs/games/octowow.md` — "Install Path", "Addon Layout"
- `.todo/000-CONVENTIONS.md` — invariants 1–4, definition of done, evidence rules
- Tasks: 201, 204, 205, 206, 208–210, 213, 307, 308, 402, 403, 404 — depends on 213 and 403, blocks 404
