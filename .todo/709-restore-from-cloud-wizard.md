---
id: 709
title: Restore-from-cloud wizard
phase: 7
bucket: post-v1
depends_on: [708]
blocks: []
status: todo
area: desktop
---

# 709 — Restore-from-cloud wizard

## Objective

The marquee new-PC path works: on a fresh install the app offers "Start Fresh" or "Restore from Cloud", and the wizard's five steps — connect storage, choose a snapshot, choose what to restore, locate each game, then restore and re-install — put a machine back to the previous setup, re-downloading mods from their original sources and reporting any mod whose source is gone.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 708 (the snapshots it reads) and needs 707's credential handling for the credentials step. It is the task that makes the product's promise about not losing data real for the case where the machine itself is gone — the notes describe it as the marquee use case, and it relies on the honest fact that mods are re-downloaded rather than stored.

The oldest snapshot in the bucket is the deliverable's only input; verification needs at least two snapshots and two games to be meaningful. If a suitable test bucket or account does not exist, **ask the user** rather than inventing one.

## Scope

- First-run entry point, with the notes' wording: "Welcome to Game Polish" / "Set up from scratch, or restore from cloud?" and the two actions [ Start Fresh ] and [ Restore from Cloud ]. Also reachable later from App Settings → Cloud Backup → [Restore from Cloud →].
- **Step 1 — Connect storage**: enter credentials (BYO S3 now; Game Polish Cloud login is a future tier) and validate the connection by the same real check 707 uses, or reuse a saved keychain credential when present.
- **Step 2 — Choose snapshot**: list snapshots from `meta.json` newest first, each row showing the notes' shape — "Oct 15, 2026 9:42 PM (latest)", then "3 games · 47 mods · From: DESKTOP-B7X" — with the origin machine read from the snapshot manifest. Radio selection plus [Next →].
- **Step 3 — Choose what to restore**: the three checkboxes with byte sizes — "☑ Game Polish config (mod lists, settings)", "☑ Game settings files", "☑ Game saves (84 MB)" — and the warning, verbatim: "⚠ Game files must be installed first. Game Polish will detect your game installs and re-download mods from their original sources automatically."
- **Step 4 — Locate games**: for each game in the snapshot, ask "Where is Skyrim SE installed on this PC?" — auto-detect first (the game YAML's `install_detection`, e.g. Steam registry heuristics where the YAML allows detection, then `verify_file`), then manual browse, and a "skip for now" option for games not found. Note that OctoWoW is `manual_only: true` with `verify_file: WoW.exe`, so its path always falls through to browsing — a good built-in test case.
- **Step 5 — Restore & re-install**: the progress screen with the documented lines — "Downloading config from S3 ✓", "Restoring game settings ✓", "Re-installing 47 mods… (downloads from GitHub sequentially)", "Restoring game saves ✓" — a real progress bar, and a cancellable run.
- Mods are re-installed from their original sources, **sequentially**, respecting GitHub's rate limits (use the PAT from 504 when present). A mod whose source is gone appears in the post-restore summary as "could not re-install — source no longer available." with the manual ZIP fallback to complete that install.
- Invariant 1 applies to restore as much as to install: writing into a game directory takes a timestamped backup first, so a cancelled or failed restore can be undone.
- Path safety applies to remote data too: snapshot manifests come from another machine and possibly another account, so every path they contain is validated as untrusted input (invariant 3).

## Out of scope

- Producing snapshots (708), conflict resolution between machines (710), decryption setup (711 — but the wizard must accept an encryption passphrase when the chosen snapshot is encrypted, via 711's key entry).
- A partial, per-mod or per-file restore UI beyond the three documented categories.
- Cross-provider migration, restoring a snapshot into a different data root layout, or server-side restore.
- The console equivalent — and note honestly that the manual ZIP fallback cannot exist on console (715).

## Implementation notes

- Restore order matters: config (`games.json`, manifests) first so the app knows what to re-install, then settings files into located installs, then mods, then saves. Saves last means a failure in the expensive mod phase still leaves the user's save data in place.
- Never restore the local `backups/` tree or mod files from the snapshot: they are not in it (708), and the UI must not imply otherwise.
- Game files must be installed first — the wizard's own warning says so. If a located install turns out to be missing its verification file, stop and ask rather than writing into an arbitrary directory (this is how a restore turns into data loss).
- Re-installation reuses the install pipeline from 208 (never a second, parallel implementation), so backups, path validation and manifest writes behave identically. If the pipeline cannot be reused, that is a finding worth reporting, not a shortcut to take.
- Missing source: distinguish "repo deleted/404", "rate-limited" (retry later, not a failure of the snapshot) and "network down" (queue and retry) in the summary. Only the first is genuinely "source no longer available."
- The mod count, byte sizes and origin machine shown in step 2 come from the snapshot manifest, so 708's manifest must carry them; if it does not, add them there rather than recomputing remotely by listing objects.
- Cancellation must state what was left behind: which games got settings, which mods installed, and that a backup exists for every directory it touched.
- Refuse to restore a snapshot whose declared app/schema version is newer than the running app, and explain why (downgrade would corrupt manifests).

## Acceptance criteria

- Round trip: sync on machine A, point machine B at an empty data root, run the wizard, and end with identical `games.json`, identical per-game manifests (same mod ids, versions and enabled state), byte-identical settings files, and (with 712 in play) byte-identical saves — proven with `sha256sum` comparisons, not by eyeballing the UI.
- Step 2's rows show the notes' strings: date and "(latest)", game count, mod count, and "From: DESKTOP-B7X" for the origin machine.
- Step 4 auto-detects a detectable game and falls back to browse for a manual-only one (OctoWoW), and "skip for now" leaves that game out without failing the run.
- A snapshot referencing a deleted repository produces exactly "could not re-install — source no longer available." in the summary, and the manual ZIP fallback completes that mod's install.
- A doctored snapshot manifest containing `../`, an absolute path, or a symlink escape is rejected (test with traversal payloads, as for any mod-sourced path).
- Cancelling mid-restore leaves the pre-existing state intact and enumerates what it did and did not do.
- The restore writes no tokens and no S3 credentials into the data root or the game directories.
- The first-run choice appears exactly once on a fresh install (and not on subsequent launches).

## Verification

```bash
sha256sum ~/.game-polish/games.json ~/.game-polish/<game>/manifest.json ~/.game-polish/<game>/settings/*.ini   # on A
# after restoring on B (same commands, different machine or data root)
sha256sum ~/.game-polish/games.json ~/.game-polish/<game>/manifest.json ~/.game-polish/<game>/settings/*.ini
diff <(aws --endpoint-url https://<endpoint> s3 ls s3://<bucket>/game-polish/snapshots/<ts>/ --recursive) \
     <(echo "expected listing")
```

Paste the hash pairs from both machines, the wizard's five screens (screenshots or the markup text of each), the missing-source summary line, and the traversal test output. State plainly which parts were verified only by hand in `dotnet watch`, per the conventions' desktop rule.

## References

- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Restore Flow (New PC / Recovery)" (steps 1–5 and the strings), "S3 Storage Structure", "Edge Cases".
- `../game-polish-notes/docs/games/octowow.md` — install detection (`manual_only`, `verify_file`).
- `.todo/000-CONVENTIONS.md` — invariants 1 and 3, definition of done (backup-before-write tests).
- Tasks: 208 (install pipeline to reuse), 504 (GitHub PAT), 707, 708, 710, 711, 712, 715.
