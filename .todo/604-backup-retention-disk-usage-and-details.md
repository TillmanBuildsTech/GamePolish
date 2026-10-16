---
id: 604
title: Backup retention, disk usage, details and diff
phase: 6
bucket: v1.0
depends_on: [306, 205]
blocks: [606]
status: todo
area: core
---

# 604 — Backup retention, disk usage, details and diff

## Objective

The Backups view grows the controls `backups.md` specifies beyond the MVP list: a Configure retention
sheet with all four policies, a disk-usage footer computed from disk, a per-entry Details expander
listing files, filter and sort, delete-with-confirmation, and a backup-location migration that is
atomic with rollback.

## Context

Authoritative: `../game-polish-notes/docs/views/backups.md` (Backup Entry, Details Expander, Restore
Flow, Filter & Sort, Disk Usage Footer) and `app-settings.md` (Backups section, which links to the same
retention sheet).

**This task extends `306` (the Backups view, MVP) and leans on `205` (the backup engine).** `306` built
the list, the entry rows, the restore flow and the empty state against `index.json`. This task adds the
retention policies, the disk-usage footer, the Details expander, sorting/filtering, deletion and
storage migration. Nothing in `306` is superseded; it is extended. The configuration sheet's UI lives
with `306`'s view; the *policy logic* is Core and is what the acceptance tests pin.

## Scope

- **Configure retention sheet**, reachable from the Backups footer and from App Settings, with all four
  documented policies:
  1. Keep all backups (default).
  2. Keep the last *N* backups per mod.
  3. Auto-delete backups older than *X* days.
  4. Manual "Delete all backups" with a confirmation dialog.
- **Disk-usage footer**: total size of every backup under the active backup root, computed by Core
  asynchronously (spinner until ready), with a "Configure retention…" link.
- **Details expander** per entry: a table with columns **File**, **Original path**, **Size**, plus a
  "Show diff" link for text files (the diff itself is `603`).
- **Filter & sort**: text filter (mod name, file name, trigger); sort Newest (default), Oldest, Largest,
  Smallest; a **Mod** dropdown; a **Type** dropdown (All / Mod installs / Settings changes).
- **Delete** a single backup entry with confirmation, removing both the directory and the `index.json`
  entry.
- **Backup-location change**: migrate every existing backup to the new root atomically — copy, verify,
  then delete the old — with rollback on failure.

## Out of scope

- The diff comparator itself (`603`); this task only provides the link.
- Backup *creation* semantics (`205`) and restore semantics (`306`).
- Moving the whole data dir (task `606`) — a superset move that reuses the same primitive.
- Cloud sync of backups (`707`+).

## Implementation notes

- A retention policy is a pure function over the parsed `index.json` plus recorded sizes:
  `IReadOnlyList<BackupEvent> Apply(RetentionPolicy policy, IReadOnlyList<BackupEvent> events)`. Keep it
  pure so it is testable without a filesystem. "Last N per mod" groups by trigger source (mod id) and
  keeps the N newest per group; "older than X days" compares the event timestamp to now.
- When retention runs: after every successful backup and when the Backups view opens. It must never
  delete the newest backup for the current state of a file — a policy that would do so is clamped with a
  warning, because deleting the newest snapshot removes the recovery path.
- Disk usage sums the real file sizes under `backups/<timestamp>/**`, not the sizes recorded in
  `index.json` — the index can drift after a partial failure, and the footer must match `du`. Compute
  off the UI thread; the App Settings and Backups footers share one computation.
- Delete removes the timestamp directory first, then rewrites `index.json` without the entry. If the
  index rewrite fails the orphan directory is harmless — log it. Confirmation text names the date and
  file count so the user knows what is about to go.
- **Storage migration (atomic, with rollback)** reuses a primitive `606` also needs:
  1. Enumerate `backups/**` under the old root.
  2. Copy into a staging directory *on the target volume* (a cross-volume copy cannot be `File.Move`d
     atomically; copying into a sibling of the destination keeps the final step a rename).
  3. Verify file count and per-file length (hash where cheap) against the source.
  4. `Directory.Move(staging, newRoot)` — atomic on one volume.
  5. Only then delete the old root and update the `backup_location` setting.
  If any step before 4 fails, delete the staging copy and leave the old root authoritative; the setting
  is never pointed at a root that does not exist.
- **Absolute paths are the trap.** If `index.json` entries store absolute file paths, a move to a new
  root (or a new drive) breaks every entry. Store backup file locations **relative to the backup root**,
  and record the root once in `app-settings.json`. `606` makes the same decision at the data-dir level;
  the two must agree or `606`'s move will not fix `604`'s entries.
- Concurrency: hold a lock file (`~/.game-polish/<game_id>/backups/.lock`) during retention, deletion
  and migration so a background backup cannot race a delete.

## Acceptance criteria

- `Apply(KeepLast(3), events)` keeps the three newest per mod and marks the rest deleted; a pure unit
  test over a fixture index asserts the exact surviving set.
- "Older than X days" deletes by timestamp and never the newest entry for a still-current file.
- The footer value equals `du -sh <backup root>` (an integration test compares the two).
- The Details table shows File, Original path and Size for every file in the snapshot; the sums match
  the entry's file count and total size.
- The four sorts produce the documented order on a fixture; the Mod and Type dropdowns filter as
  described.
- Delete removes the directory and the `index.json` entry and leaves other entries intact.
- A migration to a second directory succeeds, the old root is empty, and a forced mid-copy failure
  (read-only file, or a full target) leaves the old root intact and `backup_location` unchanged.

## Verification

```bash
dotnet test --filter FullyQualifiedName~Retention
dotnet test --filter FullyQualifiedName~Migration
du -sh ~/.game-polish/<game_id>/backups            # compare to the footer in the running app
ls ~/.game-polish/<game_id>/backups/index.json     # one entry per remaining directory
```

Migration is tested with two temp directories on the same volume and, where a second volume is
available, the cross-volume path. Paste the pass/fail counts and the `du` output.

## References

- `../game-polish-notes/docs/views/backups.md` — Details Expander, Filter & Sort, Disk Usage Footer.
- `../game-polish-notes/docs/views/app-settings.md` — Backups section.
- Tasks: `205`, `306` (extended), `603` (diff), `606` (same atomic-move primitive, data-dir scope).
