---
id: 712
title: Game saves backup
phase: 7
bucket: post-v1
depends_on: [708]
blocks: []
status: todo
area: desktop
---

# 712 — Game saves backup

## Objective

Game saves can be backed up: save directories are declared in the game YAML, the configuration panel shows an estimated size per game with the warning "⚠ Save files can be large. Check sizes before enabling.", a game whose saves exceed the 500 MB default threshold stays excluded until the user explicitly confirms, changes are picked up with a 30-second debounce, the category is off by default, and the privacy note about save files containing personal data appears in the setup wizard and the privacy policy.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 708 (the saves category, the `saves/<game-id>/` prefix and the trigger table) and extends the game descriptor YAML, which must stay additive: a new game's saves are declared in YAML, never in C# (invariant 6 — if code has to change per game, that is a finding). It interacts with 710 (a save changed on two machines is the case that can never be merged automatically) and 711 (saves are the most personal data in the set, so encryption matters most here).

Verification needs a game with a real save directory. OctoWoW declares none, so use a second supported game, or a synthetic directory declared in a test descriptor and clearly labelled as synthetic in the report.

## Scope

- A `saves:` section in the game YAML: one or more directory paths (the notes' examples use `~/Documents/My Games/Skyrim Special Edition/…`, `%APPDATA%/StardewValley/Saves/`, `~/Library/Application Support/RimWorld/Saves/`), plus optional include/exclude patterns and an optional per-game size hint. Games with no saves (OctoWoW) simply omit the key — nothing about the feature may require every game to have it.
- The configuration panel, as documented: the warning line at the top, one row per game with the resolved path, "Estimated size: 84 MB" and an [☑ Include in backup] toggle, and the "Total selected: 86.1 MB" footer.
- Size estimation before enabling: an asynchronous recursive walk off the UI thread with progress and cancellation, refreshed on demand, plus a re-estimate before a first large upload so the number in the panel is not stale.
- The 500 MB default threshold: a game over it is excluded until the user explicitly confirms — a distinct confirmation step, not merely an enabled checkbox — and the threshold itself is configurable.
- Change detection with a **30-second debounce**, coalescing a burst of writes into one sync, active only while saves are enabled for that game. The notes call this polling; a filesystem watcher with the same debounce satisfies the observable contract, and the report should say which mechanism was used.
- Opt-in default **OFF**: a fresh profile has saves unchecked, the category shows "[Configure →]" in Cloud Backup, and no save data leaves the machine until the user turns a game on.
- The privacy note in three places: the saves configuration panel, the restore/setup wizard's saves step (709), and the website privacy policy (706) — saves can contain personal data such as usernames, playtime and achievements.

## Out of scope

- Platform cloud-save APIs (Steam Cloud, Epic), console save transfer (715), save editing or inspection, and per-save version history beyond the snapshot model.
- Encrypting only saves (711 is all-or-nothing for the sync set), compression, and differential/delta saves.
- Auto-discovering save directories the YAML does not declare, and writing anything into a save directory (backups copy out, never move — invariant 2's spirit).
- A per-save-file restore UI; saves restore as part of the snapshot (709).
- Backing up saves for games Game Polish does not otherwise manage — the category follows the game descriptor, so an unsupported game's saves are simply not a target.
- Any form of save-file parsing, validity checking, or "is this save corrupted" heuristic; the engine copies bytes and compares hashes, nothing more.
- Watching a save directory while the game is running for live sync of in-progress saves — the debounce contract assumes writes settle.

## Implementation notes

- Paths come from the game YAML but resolve to user-machine locations, and saves frequently live under `Documents`/`AppData`/`Application Support`. Validate that each declared path exists, refuse symlinks/junctions that resolve outside the declared root, and never follow a link out of it (invariant 3, applied to a path we did not get from a mod source but still did not create).
- Games write saves while running. Debounce plus a settle check (size and mtime stable across two consecutive observations) before uploading, because a half-written save uploaded as a "backup" is worse than no backup — and 711 makes a corrupted blob unrepairable.
- A missing save directory is a real state (the game has not run yet, or the folder moved, or OneDrive redirected it). Report "saves not found" in the panel and skip the game; never sync an empty directory as though it were a backup, because a later restore would then look successful while restoring nothing.
- Size accounting: the estimate is local and uncompressed; if 711's encryption is on, uploaded bytes differ slightly. Label the estimate as local and approximate.
- Conflict interaction (710): two machines with different saves cannot be merged. The panel should say so up front, so the explicit-choice dialog later is not a surprise, and the losing version lands in the local backup tree.
- Encryption interaction (711): if encryption is enabled, saves are encrypted with everything else. The saves help text should recommend it, since save files are the most personal category.
- Invariant 1 applies to restores of saves as well as the game's own directory — a restore writes into the save directory, which is a mutation path even though it is not the game's install directory; make that explicit in the code, since `~/.game-polish/<game>/backups/` is the only place a losing version can be kept for the "outside the install dir" case too.

## Acceptance criteria

- With saves off (fresh profile), editing a save file produces no sync and no history entry, and the category toggle is unchecked.
- With saves on for a small game, a save change syncs under `game-polish/snapshots/<ts>/saves/<game-id>/` after the debounce; two changes 10 seconds apart produce exactly one sync.
- A synthetic save directory larger than 500 MB shows the warning, stays excluded until the user explicitly confirms, and then uploads.
- The exact user-visible strings appear: "⚠ Save files can be large. Check sizes before enabling.", "Estimated size: …", "Total selected: …".
- Size estimates render without freezing the UI (report the measured time for a ~100 MB directory) and can be cancelled.
- The privacy note is present in the panel, in the wizard's saves step, and on the privacy policy page.
- Restore returns saves byte-identically (`sha256sum`), and a both-machines-changed save produces 710's explicit choice with the losing version kept locally and restorable.
- A game whose declared directory is missing reports "saves not found" instead of uploading nothing and claiming success.
- Adding a third game's saves requires only a YAML edit — if C# had to change, report that loudly as a design failure (invariant 6).
- The category state shown in Cloud Backup reflects per-game choices (one included, one not) rather than a single global truth, and turning a game off stops its watcher and debounce within one sync cycle with no further entries in history.
- Nothing is written into the save directory at any point during backup or restore: the original files are read and copied, never moved (verified by comparing mtimes and hashes of the game's own directory before and after).

## Verification

```bash
aws --endpoint-url https://<endpoint> s3 ls s3://<bucket>/game-polish/snapshots/<ts>/saves/ --recursive
sha256sum ~/Documents/My\ Games/<game>/Saves/<file> "$TMPDIR/gp-b/…/<file>"    # before/after a restore
```

Then paste: the panel's rendered rows with their estimated sizes and the total, the debounce evidence (two edits, one history entry), the >500 MB confirmation flow, the missing-directory message, and the timing of the size walk. State plainly which parts were hand-verified in `dotnet watch`, per the conventions' desktop rule.

## References

- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "What Gets Backed Up" (category table and defaults), "Game Saves Configuration", "Security" (the personal-data note), "Edge Cases" (the 500 MB threshold).
- `../game-polish-notes/docs/games/octowow.md` — a descriptor with no save directories.
- `.todo/000-CONVENTIONS.md` — invariants 1, 3 and 6; secrets in the keychain; definition of done.
- Tasks: 708 (categories, prefix, debounce), 709 (wizard's saves step), 710 (binary conflicts), 711 (encryption), 706 (privacy policy page).
