---
id: 606
title: Data dir move and export
phase: 6
bucket: v1.0
depends_on: [201, 604]
blocks: []
status: todo
area: core
---

# 606 — Data dir move and export

## Objective

App Settings' Advanced section becomes real: the Game Polish data dir can be moved to a new location
atomically with rollback and a restart prompt, all data can be exported to a ZIP and imported back,
caches can be cleared without touching backups or manifests, and "Reset to defaults" resets only
preferences.

## Context

Authoritative: `../game-polish-notes/docs/views/app-settings.md` (Advanced, Security Notes, Data
Requirements) and the local-state layout in `000-CONVENTIONS.md`.

**This task extends `201` (data root) and `604` (backup-location migration).** `201` established
`~/.game-polish/` as the single data root and gave Core a way to resolve it. `604` proved the atomic
copy-then-delete primitive on the backup subtree; this task applies the same primitive to the *whole*
root and adds export/import/clear/reset. The two moves must share one implementation, or the data dir
and the backup location can drift apart.

## Scope

- **Change data dir**: the user picks a new root; the whole tree is migrated atomically (copy, verify,
  delete old) with rollback on failure; the app requires a restart to use the new root.
- **Export all data**: a ZIP containing every `manifest.json`, the game configs (`games.json`,
  `app-settings.json`) and the `backups/` trees — for moving to a new machine.
- **Import**: read such a ZIP back and restore its contents into the data dir.
- **Clear all caches**: delete registry caches only. It must not delete backups or manifests.
- **Reset to defaults**: reset appearance and update preferences only; explicitly exclude game data and
  mod state; require confirmation.

## Out of scope

- Cloud upload of the export (`707`+) — this is a local ZIP.
- Moving individual games' install directories (a Library concern).
- Backup retention semantics (`604`), which owns pruning.
- Encrypting the export; secrets are never in it (below), so it is plain data by design.

## Implementation notes

- **The path problem, and the decision.** Manifests and backup indices record where files live. If they
  record **absolute** paths, moving the data dir breaks the backup entries (they point into the old
  root), moving a game's install breaks the manifest, and the export is useless on another machine where
  the install is `C:\Games\…` instead of `D:\OctoWow`. **Decision: store paths relative, and store the
  roots separately.**
  - Game files are recorded relative to the game **install root**; the install root lives in
    `~/.game-polish/games.json`.
  - Backup files are recorded relative to the **backup root**; the backup root lives in
    `app-settings.json` (see `604`).
  - Resolution happens at read time by joining with the current roots. Both `604` and `606` inherit
    this; the two must agree.
  - Migration rewrites any *legacy* absolute paths it finds once, best-effort, logging every path it
    could not rewrite. Never silently drop an entry whose path does not resolve — surface it.
- **The bootstrap problem and its solution.** `app-settings.json` lives *inside* the data dir, so if the
  data dir moves the app cannot read `app-settings.json` to learn where the data dir is. Keep a tiny
  pointer file at the OS-default location, `~/.game-polish/.data-dir`, containing the active root path.
  That file never moves; everything else does. It is the first thing read on launch.
- **Atomic move.**
  1. Copy the tree into a staging dir on the **target** volume (so the final step is a rename, not a
     cross-volume copy).
  2. Verify: file count and per-file length for every file (hash where cheap).
  3. `Directory.Move(staging, newRoot)`.
  4. Rewrite `~/.game-polish/.data-dir` to the new root, then delete the old tree.
  If any step before 4 fails, delete the staging copy and leave the old root authoritative; the pointer
  is not rewritten. Never point the app at a half-copied root.
- **Restart requirement.** The active data root is read once at startup and cached in services; there is
  no hot-swap. After a successful move, prompt restart; on the next launch, read the pointer file.
  Mutating the pointer is the last write of the move, so a crash before it leaves the app on the old,
  intact root.
- **Export ZIP.** Use `System.IO.Compression.ZipArchive`. Contents: `app-settings.json`, `games.json`,
  every `<game_id>/manifest.json`, and every `<game_id>/backups/**`. Exclude `registry-cache.json`
  (regenerable, potentially large). **No secrets are included** because secrets never live in files —
  the GitHub PAT and S3 keys are in the OS keychain (`000-CONVENTIONS.md`), so the export has nothing to
  strip; still assert the archive contains no `token`/`secret` keys as a guard. Stream large backups
  with progress and allow cancel; do not hold the whole ZIP in memory.
- **Import.** Validate the ZIP structure before writing; refuse an archive without `games.json` /
  manifests. Restore manifests and configs, and copy backups under the backup root; if a target file
  exists, prompt replace-or-merge rather than clobbering. Import writes only into the data dir, never
  into a game directory.
- **Clear all caches.** Delete `<game_id>/registry-cache.json` (and any `*.etag` sidecar) for every
  game. It must be impossible for this action to touch `backups/` or `manifest.json`; make that a test,
  not a promise. The next Discover visit re-fetches.
- **Reset to defaults.** Rewrite only the appearance and update-preference keys of `app-settings.json`
  to their defaults. It must leave `games.json`, every manifest and every backup byte-identical. The
  confirmation dialog names exactly what resets.

## Acceptance criteria

- Moving the data dir to a fresh directory leaves the old root empty, and after restarting the app it
  reads state from the new root (verified by the pointer file's contents and a manifest read).
- A forced failure during the copy (read-only target, disk full) leaves the old root intact, the pointer
  unchanged, and no half-populated new root.
- After a move, every manifest and backup entry still resolves (no absolute-path leftovers): a test loads
  the manifest and opens a backed-up file at the new root.
- Export produces a ZIP whose entry list equals the expected set (configs + manifests + backups, no
  `registry-cache.json`, no secret-looking keys); `unzip -l` shows them.
- Import of that ZIP into a clean root reconstructs the same manifest and backup set.
- "Clear all caches" removes every `registry-cache.json` and leaves the backup file count and every
  `manifest.json` byte-identical.
- "Reset to defaults" leaves `games.json` and all manifests byte-identical and restores only
  appearance/update preferences.

## Verification

```bash
dotnet test --filter FullyQualifiedName~DataDir
dotnet test --filter FullyQualifiedName~Export
unzip -l ~/game-polish-export.zip          # expected entry set, no caches, no secrets
cat ~/.game-polish/.data-dir               # points at the active root after a move
sha256sum ~/.game-polish/games.json        # unchanged across a "Reset to defaults"
```

The move and rollback are integration tests over temp directories; where a second volume is available,
run the cross-volume case too. Paste the command output.

## References

- `../game-polish-notes/docs/views/app-settings.md` — Advanced, Security Notes, Data Requirements.
- `000-CONVENTIONS.md` — local state layout; secrets in the OS keychain, never in a file.
- Tasks: `201` (data root), `604` (same atomic-move primitive, backup scope), `709` (cloud restore,
  which depends on relative paths), `707`.
