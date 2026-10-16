---
id: 306
title: Backups view and restore
phase: 3
bucket: mvp
depends_on: [205, 301]
blocks: []
status: todo
area: desktop
---

# 306 — Backups view and restore

## Objective

The audit trail that makes the rest of the app trustworthy: every backup Game Polish has created for the
selected game, each showing what caused it, what it contains, and a Restore button that is itself
reversible. When this task is done a user can see a timeline of snapshots, expand one to the exact files
it holds with their original paths, restore it after a confirmation, and delete one with a confirmation.

## Context

- `301` built the shell, navigation, DI and theme resources; its nav rail is the MVP's main way in, and
  `303`'s row menu (`View Backups`) is the other.
- **`205` owns the data and every mutation here.** `IBackupEngine` exposes `List(gameId)` (newest first),
  `Get(gameId, timestamp)`, `Restore(gameId, destinationRoot, timestamp)`, `Delete(gameId, timestamp)`
  and `GetDiskUsageBytes(gameId)`. `BackupEvent` carries `Trigger` (`BackupTrigger`), `SourceName`,
  `SourceVersion` (e.g. `v5.1 -> v5.2`), `Files`, `TotalSizeBytes` and an optional `Note`.
  `BackupTrigger` covers mod install / update / uninstall / settings change **and Restore** — the
  pre-restore snapshot is a real, restorable event the UI must render, not hide. `204` validates the
  paths a restore writes to; `206` maps a backup's source to a mod for the mod filter.
- Data layout (`000-CONVENTIONS.md`): the index at `~/.game-polish/<game_id>/backups/index.json`, files
  at `~/.game-polish/<game_id>/backups/<timestamp>/<original_relative_path>`. The design note's rows are
  Skyrim-shaped and illustrative: **MVP ships OctoWoW only**, so real rows read `Mod install — pfUI
  v<sha>` or `Mod uninstall — AtlasLoot`. A restore is itself a mutation, so it is preceded by a backup.

## Scope

- `ViewModels/BackupsViewModel.cs`: the entry list from `IBackupEngine.List`, filter text, sort mode, mod
  filter, per-entry expanded state, and the restore/delete commands.
- `ViewModels/BackupEntryViewModel.cs`: humanised timestamp, trigger label, source name, file count,
  total size, the lazily loaded file list, and per-entry `IsBusy`.
- `Views/BackupsView.axaml`: header with the game name, filter input, sort selector, the entry list with
  the Details expander, and the disk-usage footer.
- A restore confirmation and a delete confirmation.
- The empty state, verbatim.
- Pre-filtering: opening with a mod id scopes the list to that mod and shows a clearable notice naming
  it, so a short list never looks like missing data.

## Out of scope

- **The text diff modal ("Show diff" on INI/JSON/TOML files) — `603`/`604`.** Render the link disabled
  with a "v1.0" tooltip or omit it entirely; either way it must not appear functional. Record the choice.
- **Retention configuration (`604`)** — the footer's `Configure retention…` link is a disabled stub with
  a "v1.0" tooltip. No automatic deletion of any kind: the MVP retains everything.
- **Settings-change backups and the Settings Editor** — OctoWoW defines no settings files and the editor
  is out of MVP, so the Type filter's "Settings changes" option is disabled or omitted with the deviation
  recorded; `All` and `Mods` work.
- Backup location change / data-dir move (`606`), exporting a backup, cloud backup, per-backup notes, and
  the Mod Detail Backups panel (`502`).

## Implementation notes

- **Disk usage comes from Core** (`205`'s `GetDiskUsageBytes`) — no directory summing in a view model. It
  can be slow, so load it asynchronously with a spinner until it resolves.
- **Timestamps.** Recent entries read relative (`2 hours ago`), older ones absolute in the note's form
  (`Oct 14 2026, 9:42 PM`); compute the relative form from the event timestamp, never from file mtimes.
- **Trigger labels.** Map `BackupTrigger` to `Mod install`, `Mod update`, `Mod uninstall` and
  `Restore (pre-restore state)`, rendered with the source appended in the note's shape
  (`Mod install — pfUI v4.2.9`), using `SourceVersion` for the version part when present.
- **Details expander.** Collapsed by default (`▸ Details`), expanding to a table of the snapshotted files
  with **file name**, **original path** and **size**; if the index does not carry the file list inline,
  load it on expand with a small spinner rather than loading every list up front.
- **Restore confirmation — verbatim from `backups.md` — Restore Flow step 2:** `This will overwrite the
  current files with the backed-up versions from [date]. This action will itself create a new backup
  first. Continue?` Substitute the humanised date; do not soften or reword it, because the promise it
  makes is the product promise.
- **Restore success toast — verbatim from step 4:** `Restored — a backup of the pre-restore state was
  also saved.` Afterwards refresh the list (a new entry appeared), the disk-usage footer, and `303`'s
  rows — a restored disabled addon must not still read as enabled.
- **Delete confirmation.** The note says "with confirmation" but gives no wording; proposed text, to be
  marked as proposed in the report: `Delete this backup from <date>? The files it holds can no longer be
  restored.` Deleting removes the snapshot directory and the index entry, and never touches the game's
  install directory.
- **Filter and sort, from `backups.md` — Filter & Sort.** The filter input searches mod name, file name
  or trigger type; sort offers `Newest` (default), `Oldest`, `Largest`, `Smallest`; a **Mod filter**
  dropdown scopes to one mod — all client-side over the index, no Core call per keystroke. When the mod
  filter and `303`'s pre-filter disagree, the explicit control wins and the notice updates.
- **Empty state, verbatim from `backups.md` — Empty State:** `No backups yet — Game Polish creates
  backups automatically before any mod install or settings change.`
- **Busy and failure.** Disable an entry's buttons and show a per-entry spinner while its restore or
  delete runs, so a second click cannot queue a duplicate. A failed restore is the worst case in the app:
  show the error with a details link into `307`'s drawer, state plainly whether the pre-restore backup was
  created, and say so if Core reports a partial restore. Await Core; marshal collection changes.

## Acceptance criteria

- With at least one backup, the view lists entries newest-first, each showing the humanised timestamp,
  the `Mod install — <name> <version>`-shaped trigger and source, and the file count and size; expanding
  an entry lists its files with their original paths and sizes, and collapsed entries cost nothing.
- Restore shows the confirmation with the exact wording above, and cancelling changes nothing on disk.
- Confirming a restore creates the pre-restore backup first (a new entry at the top of the list), then
  restores the files, then shows the exact success toast; `backups/index.json` holds both entries, and
  the restored mod's row in Library shows the pre-restore state.
- Delete confirms first, then removes both the snapshot directory and the index entry, leaving the game's
  install directory untouched (verify by listing it and checking mtimes).
- Filter, all four sort modes and the mod filter narrow/reorder the list client-side; opening from a mod
  row's `View Backups` scopes the list to that mod with a clearable notice; the disk-usage footer shows a
  real total from Core, and `Configure retention…` / any `Show diff` affordance is a visibly disabled stub.
- `dotnet build` clean; tests run with the real counts quoted.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                                    # expect: 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop
ls -1 ~/.game-polish/octowow/backups/ ; cat ~/.game-polish/octowow/backups/index.json  # before state
# nav rail -> Backups
# 1) entries newest-first with counts and sizes; expand one -> file table with original paths
# 2) Restore -> confirm wording matches the spec exactly -> Cancel -> nothing changed on disk
# 3) Restore -> Continue -> success toast -> a NEW entry at the top of the list
ls -1t ~/.game-polish/octowow/backups/ | head -2
# 4) sort Newest/Oldest/Largest/Smallest; filter with no matches; scope via a row's View Backups
# 5) Delete -> confirm -> the entry and its directory are gone; the install dir is untouched
ls -1 ~/.game-polish/octowow/backups/ ; ls -d "D:/OctoWoW/Interface/AddOns"/*
dotnet test                                     # quote the real pass/fail counts
```

## References

- Design notes: `../game-polish-notes/docs/views/backups.md` — **Layout**, **Backup Entry**, **Details
  Expander**, **Restore Flow** (exact dialog and toast wording), **Filter & Sort**, **Disk Usage Footer**,
  **Empty State**, **Data Requirements**, **Context Entry Points**;
  `../game-polish-notes/docs/views/mod-detail.md` — **Backups Panel** (that surface is `502`);
  `../game-polish-notes/docs/views/app-settings.md` — **Backups** (retention, implemented in `604`).
- Repo: `000-CONVENTIONS.md` (invariant 1, local state layout), `CLAUDE.md` (Backup System).
- Related tasks: `204`, `205`, `206` (Core), `301` (shell), `303` (the `View Backups` row action), `307`
  (toasts + error details link), `603` (raw/diff), `604` (retention), `502` (Mod Detail's panel).
