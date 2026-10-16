---
id: 210
title: Uninstall and enable/disable
phase: 2
bucket: mvp
depends_on: [201, 202, 203, 205, 206, 208, 212]
blocks: [213, 302]
status: todo
area: core
---

# 210 — Uninstall and enable/disable
## Objective
Three operations that change a game directory without changing what is installed: uninstall (back up, delete the addon folder, forget the manifest entry), disable (park
the addon folder in the Game Polish staging area and keep the manifest entry), and enable (put the parked folder back, backing up first). After this task the Library
row's checkbox, overflow menu and confirmation dialogs have real behaviour behind them.

## Context
Greenfield repo. Prerequisites: `201` (data root — staging lives under it), `202` (`AddonRoot`), `203` (install path), `205` (backup), `206` (manifest), `208`
(`SwapOperations` — reuse it, do not fork the swap), `212` (result model). Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else leans on;
then 206–210; then 211–213.**

Restated invariants: **(#1)** every one of these three operations writes into a game directory, so all three create a timestamped snapshot before the first byte changes
— including a *disable*, which only moves files. **(#4)** exactly two roots: the game's install directory and `~/.game-polish/`; the staging area is the second one.

Authoritative behaviour: `../game-polish-notes/docs/views/library.md` § *Overflow Menu Actions* ("Uninstall | Confirm dialog → remove files → remove from manifest"), §
*Right Panel — Mod List* ("Checkbox toggle (enable/disable — moves files in/out of game directory)"), § *Enable/Disable Mechanics* ("Enabling a mod copies/symlinks its
files from the Game Polish staging area into the game's install directory (as defined by the game YAML `mod_layout`). Disabling removes them. A backup is created on
first enable. The UI shows a spinner on the row during the operation and locks the checkbox until complete."); `../game-polish-notes/docs/games/octowow.md` § *Addon
Layout* ("Uninstall | Delete `Interface/AddOns/<folder_name>/` (after backup)").

## Scope
- `GamePolish.Core/Mods/UninstallPipeline.cs`, `EnableDisablePipeline.cs`, `StagingArea.cs`.
- `GamePolish.Core/Mods/ModStateService.cs` — the small facade the Library view model talks to (`GetRows`, `SetEnabled`, `Uninstall`), so `302` does not sequence Core
  components itself.
- Tests: uninstall backup-ordered deletion, enable-first-enable backup, disable-keeps-the-copy, round-trip enable→disable→enable, and failure/cancellation behaviour.

## Out of scope
- The confirm dialogs, spinners and checkbox locking (`302`, `307`) — this task returns `OperationResult` + `IProgress<OperationProgress>` and nothing else.
- Retention of backups after an uninstall. Uninstall does not delete the backups it made; deleting a mod's backups is the Backups view's job (`306`) and the policy is
  `604`.
- Deleting a game or removing it from `games.json` (`203.Remove`; the UI calls it directly).
- Symlinking. `library.md` says "copies/symlinks"; MVP copies. A symlink-based enable is a `604`-era optimisation, and on Windows it would need Developer Mode. Report
  the divergence from the note.

## Implementation notes
**What the staging area is (define it once, in `StagingArea`, and use those methods everywhere):**
- Location: `~/.game-polish/<game_id>/staging/` — `GamePolishPaths.StagingDirectory(gameId)` from `201`. It is inside the data root, i.e. inside invariant `#4`'s second
  root. Nothing about it is a third root and nothing may be created outside it for temporary work.
- Three kinds of content, and only three:
1. `staging/<opId>/` — a source being staged by `207` for an in-flight install/update. Lifetime: one operation; swept by `207.SweepStaleStaging`.
2. `staging/disabled/<FolderName>/` — the parked copy of a **disabled** addon. Lifetime: until the mod is enabled, uninstalled, or the manifest entry is removed.
3. `staging/check-<modId>-<opId>/` — a version probe's scratch clone (`209`). Lifetime: one call.
- It is **not** a mirror of the game directory and **not** a second canonical copy. For an *enabled* mod the game directory is canonical and
  `staging/disabled/<FolderName>` does not exist. A mod therefore has exactly one copy of its files, in one of two places, and `InstalledMod.Enabled` in the manifest
  says which.
- `StagingArea` exposes: `string DisabledPath(string gameId, string folderName)`, `bool HasDisabledCopy(string gameId, string folderName)`, `IEnumerable<string>
  ListDisabled(gameId)`. Validate `folderName` through `204.ResolveSafeDirectoryName` before building any path.
```csharp
public interface IModStateService
{
    Task<OperationResult> EnableAsync(string gameId, string modId,
                                      IProgress<OperationProgress>? progress, CancellationToken ct);
    Task<OperationResult> DisableAsync(string gameId, string modId,
                                       IProgress<OperationProgress>? progress, CancellationToken ct);
    Task<OperationResult> UninstallAsync(string gameId, string modId,
                                         IProgress<OperationProgress>? progress, CancellationToken ct);
}
```
**Uninstall** (`ModUninstall`):
1. `206.Find(gameId, modId)` — absent ⇒ `ModNotInstalled`.
2. Resolve `addonRoot`; if `Enabled`, the files are at `<addonRoot>/<FolderName>`; if disabled, at `staging/disabled/<FolderName>`.
3. **Backup** (`205.Create(gameId, installPath, BackupTrigger.ModUninstall, sourceName: mod.Name, sourceVersion: mod.Version, relativePaths: mod.Files prefixed with the
  addon-root-relative path)`). A disabled mod's files are inside the data root, so the backup's `destinationRoot` for that case is the **game install root** and the
  relative paths are `Interface/AddOns/<FolderName>/...` — the same shape as an enabled mod's backup, so a restore puts them back where the game expects them. Do not
  invent a second backup layout for the disabled case; state this in a comment because it is the subtle part.
4. Delete the folder (`IFileOps.DeleteDirectory`, recursive) and remove the empty parent if the engine created it — never delete the addon root itself.
5. `206.Remove(gameId, modId)`.
6. Return `Success` with the removed file count and the backup timestamp. If step 4 succeeded and step 5 failed, return `Failure` with a message that says the files are
  gone and the manifest entry remains, and name the backup — do not attempt to restore automatically, and do not swallow it.

**Disable** (`EnableDisable` trigger — `205`'s `BackupTrigger.EnableDisable` covers both directions):
1. `206.Find` ⇒ absent ⇒ `ModNotInstalled`; already `Enabled == false` ⇒ `Success` no-op (idempotent; a double-click on the checkbox must not create a second backup).
2. Verify the in-game folder exists; if it does not, return `Failure` with a message telling the user the addon folder is missing and offering the manifest's file list
  as evidence — do not "fix" it by flipping the flag.
3. **Backup**, then move `addonRoot/<FolderName>` → `staging/disabled/<FolderName>` (`IFileOps.MoveDirectory` when both are on one volume; `205`'s contract allows a
  copy+verify+delete fallback, which the data-root path may need when the game is on `D:` — implement the fallback and cover it with a test).
4. `206.SetEnabled(gameId, modId, false)`. The **parked copy is never deleted**: a disable must be reversible with no network access.
5. Return `Success` with the file count and the backup timestamp.

**Enable**:
1. `206.Find` ⇒ absent ⇒ `ModNotInstalled`; already `Enabled == true` ⇒ `Success` no-op.
2. The parked copy must exist (`StagingArea.HasDisabledCopy`); if not ⇒ `StagedCopyMissing` with a message that says the mod's files are not in the staging area nor in
  the game folder and that a reinstall is needed — plus the newest backup timestamp for that mod as a recovery hint.
3. Resolve `addonRoot`; re-validate every relative path in `mod.Files` through `204` against the addon root (a manifest written before a game moved must not be
  trusted).
4. **Backup — on first enable, before the first byte.** Always call `205.Create` with `BackupTrigger.EnableDisable`, even when the destination has no conflicting files:
  that is what `library.md` § *Enable/Disable Mechanics* means by "A backup is created on first enable", and `205` already guarantees the snapshot directory is created
  even with nothing to copy.
5. Swap the parked folder into place with `208`'s `SwapOperations.ReplaceFolder(addonRoot, folderName, staging/disabled/<folderName>)` — same temp-sibling-then-rename
  pattern, so a failure leaves either the old state or the complete new one.
6. `206.SetEnabled(gameId, modId, true)`; delete the now-empty `staging/disabled/<FolderName>` remnant.
7. Return `Success`.

**Shared rules for all three:** check the cancellation token at each step boundary; on cancellation run the step's rollback (nothing has been deleted irrecoverably at
any point, because every destructive step is preceded by a rename or by the backup), return `Cancelled`, and keep the backup. Every step emits an `OperationLogEntry`
(212) — the Backups view's per-mod filter and the error drawer both depend on the trigger and source name being right, so assert those strings in tests. All three take
`IFileOps` from `205`.

## Acceptance criteria
1. Uninstall ordering: the recording `IFileOps` shows `CreateDirectory("<backups>/<ts>")` and the backup copies before the first `DeleteDirectory` of the addon folder.
2. Uninstall removes the addon folder, removes the manifest entry, and leaves the backup restorable: after `Restore`, the addon folder and its files are back.
3. Disable keeps everything: after `DisableAsync`, the game folder is gone, `staging/disabled/<FolderName>/` contains the identical file set (same relative paths and
  byte counts), and the manifest entry still exists with `Enabled == false`.
4. Enable restores the identical file set into `addonRoot/<FolderName>`, sets `Enabled == true`, deletes the disabled staging remnant, and **creates a backup first**
  (asserted by call order).
5. `DisableAsync` then `EnableAsync` then `DisableAsync` again produces one `EnableDisable` backup event per transition that changed state, and none for the no-op
  calls.
6. Enabling a mod whose parked copy is missing returns `StagedCopyMissing` and changes nothing.
7. No-op idempotence: calling `EnableAsync` twice on an enabled mod returns `Success` twice and creates one backup total.
8. Uninstall of a **disabled** mod deletes the parked copy, backs it up first, and removes the manifest entry.
9. Cancellation during a disable's move (injected `IFileOps` failure) leaves the mod enabled, the game folder intact, and the manifest unchanged.
10. Every one of the three operations writes nothing outside the game install directory and the data root — asserted by a path-prefix test on the recording `IFileOps`.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~Uninstall|FullyQualifiedName~EnableDisable|FullyQualifiedName~StagingArea"
# expect: all pass; criteria 1, 3, 4 and 8 are the load-bearing ones
# end to end, once 213 lands:
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" disable --game octowow --mod pfUI
ls "$TMPDIR/gp/octowow/staging/disabled/pfUI"    # expect: the addon files, parked
ls "$TMPDIR/game/Interface/AddOns"               # expect: no pfUI
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" enable  --game octowow --mod pfUI
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" uninstall --game octowow --mod pfUI
```
## References
- `../game-polish-notes/docs/views/library.md` § *Right Panel — Mod List*, § *Overflow Menu Actions*, § *Enable/Disable Mechanics*
- `../game-polish-notes/docs/games/octowow.md` § *Addon Layout* (uninstall row)
- `../game-polish-notes/docs/views/backups.md` § *Backup Entry* (trigger labels), § *Restore Flow*
- `000-CONVENTIONS.md` § Hard invariants (#1, #4), § Local state layout
- Related tasks: `201` (`StagingDirectory`), `202` (`AddonRoot`), `203` (install path), `205` (`BackupTrigger.{ModUninstall,EnableDisable}`), `206` (`SetEnabled`,
  `Remove`), `208` (`SwapOperations`), `212` (`OperationResult`), `213` (`enable`, `disable`, `uninstall`), `302`, `306`.
