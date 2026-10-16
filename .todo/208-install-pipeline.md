---
id: 208
title: Install pipeline
phase: 2
bucket: mvp
depends_on: [202, 203, 205, 206, 207, 212]
blocks: [209, 210, 213, 304]
status: todo
area: core
---

# 208 — Install pipeline
## Objective
One method that takes "game + source" and leaves a working addon in the game's addon folder, or leaves the game folder *exactly* as it was. It is the first caller that
sequences every other phase-2 component, so it is also where the product's central promise (`#1`, backup before mutation) either holds or does not.

## Context
Greenfield repo. Prerequisites: `202` (`GameDescriptor.AddonRoot`), `203` (the game must be tracked to have an install path), `205` (backup), `206` (manifest), `207`
(staging), and `212` (the result/error model this pipeline returns). Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else leans on; then
206–210; then 211–213.**

**Note on `212`:** the roadmap lists it after this task, but this pipeline *returns* `212`'s types, so `212` must land first. `212` needs only `201`, so it can be done
in parallel with `205`/`206`/`207` — sequence it before this one and say so in your report rather than inventing a local result type. If `212` is not done when you
start, stop and say so.

Restated invariant **(#1)**: any operation that writes into a game directory creates a timestamped snapshot under `~/.game-polish/<game_id>/backups/<timestamp>/
**before the first byte is written**. A mutation path with no snapshot call is a bug even if the operation succeeds. **(#4)**: exactly two roots — and note that the
swap in `207`'s decided model writes `.game-polish-new-*` siblings *inside the addon root*, which is inside the first root, so it is allowed; it is not a third root.

Authoritative behaviour: `../game-polish-notes/docs/views/discover.md` § *Install Flow (inline)* (steps: "`GamePolish.Core` clones/downloads the source, validates,
backs up conflicting files, installs"; failure ⇒ error toast with a details link) and `../game-polish-notes/docs/games/octowow.md` § *Addon Layout* (install = `git
clone --depth=1` into `Interface/AddOns/<folder_name>/`). The Add Mod flow itself (URL or ZIP upload) is task `304`; this task is the engine underneath it.

## Scope
- `GamePolish.Core/Install/InstallRequest.cs`, `InstallPipeline.cs`, `IInstallPipeline.cs`.
- `GamePolish.Core/Install/SwapOperations.cs` — the staged→addon-root swap and its rollback, shared with `209`.
- `GamePolish.Core/Install/ConflictReport.cs` — what an install would overwrite and who owns it.
- Tests: the ordering proof, the rollback proof, conflict handling, cancellation at every phase, and the "game directory byte-identical after a failure" property.

## Out of scope
- Choosing the source in the UI, ZIP upload plumbing, progress bars (`304`, `307` — you only raise `IProgress<OperationProgress>`).
- Update and uninstall (`209`, `210`).
- Addon dependency resolution (an explicit post-MVP gap the design notes call out as OctoUpdater's bug; do not start it here).
- Editing the descriptor or `games.json`. An untracked game is an error, not an auto-add.

## Implementation notes
```csharp
public sealed record InstallRequest(
    string GameId, ModSourceType SourceType, string SourceUrl,
    string? Branch, Stream? ZipStream, string? ZipFileName,
    string? DisplayName, string? ModId, bool AcceptOverwrites);
public sealed record ConflictReport(
    IReadOnlyList<string> OwnedByThisMod,      // reinstalling over our own files: normal
    IReadOnlyList<string> OwnedByOtherMods,    // blocker unless AcceptOverwrites
    IReadOnlyList<string> UnownedExisting);    // user's own files: blocker unless AcceptOverwrites
public interface IInstallPipeline
{
    Task<ConflictReport> DetectConflictsAsync(InstallRequest request, CancellationToken ct);
    Task<OperationResult> InstallAsync(InstallRequest request,
                                       IProgress<OperationProgress>? progress, CancellationToken ct);
}
```
`OperationProgress` and `OperationResult` come from `212`; do not redefine them.

**Sequence — the phase order is normative. Each phase emits at least one `OperationLogEntry`:**
1. **Resolve.** `IGamesRegistry.Get(gameId)` ⇒ `GameInstallPathMissing` if absent; `IGameDescriptorLoader.LoadById(gameId)` ⇒ `GameNotFound` if absent. Compute
  `addonRoot = descriptor.AddonRoot(tracked.InstallPath)`. Confirm `Directory.Exists(addonRoot)`.
2. **Stage.** `ISourceAcquirer.Stage*Async` (207). The game directory is untouched up to and including this point. On failure: clean up staging, return `Failure` with
  the mapped code (`GitNotFound`, `GitFailed`, `ZipInvalid`, `SourceNotFound`, `Timeout`). **Nothing to roll back.**
3. **Validate.** `IPathValidator.ValidateAll(addonRoot, staged.Files.Select(f => f.RelativePath))` (204). Any failure ⇒ `PathUnsafe` with the failures in `Detail`;
  clean up staging; no mutation.
4. **Conflict detection.** Build the destination paths as `<addonRoot>/<FolderName>/<relative>` and ask `206`: `FindFolderOwners(gameId, FolderName)` and
  `FindOwnersOfPath` for each. Three buckets (see `ConflictReport`). `ModAlreadyInstalled` when the same mod id is already tracked *and* the caller asked to install
  (not update) — return that as a failure naming `209` in the message so the UI can offer Update. Unowned existing files or another mod's files ⇒ stop before the backup
  with `ConflictsUnresolved`, and return the report in `Detail` unless `AcceptOverwrites`.
5. **Backup — before the first byte.** `IBackupEngine.Create(gameId, tracked.InstallPath, ModInstall, sourceName: displayName ?? staged.FolderName, sourceVersion:
  staged.Version, relativePaths: the game-root-relative paths of every destination file that already exists plus the folder itself)`. A backup failure propagates as
  `BackupFailed` and **nothing is written**. Record `result.BackupTimestamp`.
6. **Swap.** Per `207`'s decision: copy the staged tree into `<addonRoot>/.game-polish-new-<FolderName>-<ts>/` (excluding `.git` when `HasGitDirectory` — the game
  ignores it but the user's disk should not carry it, and `206`'s file list should match what ships), then `MoveDirectory(<addonRoot>/<FolderName> →
  .game-polish-old-<FolderName>-<ts>)` when it exists, then `MoveDirectory(.game-polish-new-* → <addonRoot>/<FolderName>)`, then delete the `-old-` folder. Progress is
  reported per copied file.
7. **Record.** `IManifestStore.Upsert(gameId, InstalledMod{ … Files = staged.Files, Enabled = true, InstalledAt = opStart, UpdatedAt = opStart, VersionKind =
  staged.VersionKind })`.
8. **Clean up + report.** `ISourceAcquirer.Cleanup(staged)`; return `OperationResult.Success` naming the mod, folder and version, with `BackupTimestamp` set.

**Rollback:** the only external state that can be half-changed is the swap. Maintain the swap's own progress so rollback is possible at every micro-step:
- fail during the copy into `.game-polish-new-*` ⇒ delete `.game-polish-new-*`; the old folder is intact; **manifest untouched**; return `Failure` (code from the
  underlying exception), `BackupTimestamp` still set.
- fail after the old folder was moved aside ⇒ move `.game-polish-old-*` back to `<FolderName>`; if *that* fails, return a `Failure` whose `Message` says the addon is at
  `.game-polish-old-<FolderName>-<ts>` and whose `Detail` names the backup timestamp. Never claim success and never swallow this.
- fail after the new folder landed but before the manifest write ⇒ roll the folder back (delete the new one, restore the old) and return `Failure`. The manifest is the
  source of truth for the UI; a folder on disk that the manifest does not know about is an orphan the user cannot uninstall.
- Rollback failures are logged as `OperationLogEntry` with level `error` and surfaced in `Detail` — the result stays `Failed`.

**Cancellation:** check the token at every phase boundary *and* inside the copy loop. Cancelling before the backup ⇒ clean staging, return `Cancelled`, nothing written.
Cancelling after the backup ⇒ run rollback, return `Cancelled`, **keep the backup** (it is harmless and the invariant says the snapshot precedes the write). A cancelled
install must never leave a `.game-polish-new-*`/`-old-*` sibling behind; if the delete fails, say so in `Detail`.

**Injected-failure seam for tests:** take `IFileOps` from `205` (default `FileOps`). `205`'s `FailAt` test double lets a test fail at the Nth mutating call, which is
how the ordering and rollback criteria below are proved.

## Acceptance criteria
1. **Ordering:** with the recording `IFileOps` from `205`, a successful install records `CreateDirectory("<backups>/<ts>")` and the backup `CopyFile` calls **before**
  any `.game-polish-new-*` write. The test fails if any game-directory write precedes the snapshot.
2. **Rollback on injected failure:** failing the file-copy that writes file #3 of 10 into `.game-polish-new-*` leaves the addon root byte-identical to before (recursive
  hash), leaves `manifest.json` unchanged (bytes), and returns `Status == Failed` with a non-`None` `Code` and a `BackupTimestamp` that resolves to a real snapshot
  directory.
3. Failing *after* the old folder was moved aside restores the old folder to `<addonRoot>/<FolderName>`.
4. First install of a mod into an empty addon root: the addon folder exists with the staged files, the manifest has one entry with `Enabled == true` and the staged
  `Files` list, and no `.game-polish-*` sibling remains.
5. A failed install (any injected failure) never writes the manifest: assert the file is absent for a first-time install.
6. Path validation: a staged source containing `../evil.txt` (fabricated `StagedSource`) returns `PathUnsafe`, writes nothing, and no backup is created — validation
  precedes backup.
7. Conflict: installing `pfUI` while a *different* mod id already owns the folder `pfUI` returns `ConflictsUnresolved` and changes nothing; with `AcceptOverwrites:
  true` it proceeds, and the result records the overwritten file list.
8. Untracked game ⇒ `GameInstallPathMissing`; unknown descriptor ⇒ `GameNotFound`; both before any write.
9. Cancellation after the backup but during the copy leaves the addon root byte-identical and returns `Cancelled`, and the backup directory from that run still exists.
10. `DetectConflictsAsync` is side-effect free: it creates no backup, writes no manifest, and leaves the addon root unchanged.
11. The install path is the descriptor's: a test with a second fake descriptor whose `mod_layout.destination` is `Mods/` installs into `<install>/Mods/`, proving no
  hardcoded `Interface/AddOns`.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~InstallPipeline"
# expect: all pass; criteria 1, 2, 3 and 9 are the ones that guard the product promise
# end to end, once 213 lands:
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" add-game octowow --path "$TMPDIR/game"
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" install --game octowow --source https://github.com/brues-code/pfUI
ls "$TMPDIR/game/Interface/AddOns/pfUI"          # expect: pfUI.toc and the addon files, no .git
ls "$TMPDIR/gp/octowow/backups"                  # expect: one timestamp, index.json
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" list --game octowow  # expect: pfUI + version
```
## References
- `../game-polish-notes/docs/views/discover.md` § *Install Flow (inline)*, § *Empty / Edge States*
- `../game-polish-notes/docs/games/octowow.md` § *Addon Layout* (operation table)
- `../game-polish-notes/docs/views/backups.md` § *Backup Entry* (trigger label "Mod install")
- `000-CONVENTIONS.md` § Hard invariants (#1, #3, #4), § Definition of done ("Install / update / uninstall")
- Related tasks: `202` (`AddonRoot`), `203` (install path), `204`, `205` (`IFileOps`, backup), `206` (manifest + conflicts), `207` (staging + the swap model), `209`
  (shares `SwapOperations`), `212` (result model — must land first), `213` (`install`), `304` (Add Mod UI).
