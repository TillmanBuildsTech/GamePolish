---
id: 205
title: Backup engine
phase: 2
bucket: mvp
depends_on: [201, 204]
blocks: [208, 209, 210, 213, 306]
status: todo
area: core
---

# 205 — Backup engine
## Objective
A service that snapshots every file an upcoming operation will touch into a timestamped directory under the per-game backups folder, records the event in a
`index.json`, and can restore any snapshot — reversibly, because a restore snapshots the current state first. This is the component the whole product promises; the
README calls it the thing that makes the MVP worth building.

## Context
Greenfield repo. Prerequisites: `201` (data root, `AtomicJson`) and `204` (path safety — the file list this engine copies comes from mod sources, so it is validated
before it is used as a copy list). Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else leans on; then 206–210; then 211–213.**

Restated invariant **(#1)**, verbatim from `000-CONVENTIONS.md` § Hard invariants: *"Any operation that writes into a game directory (install, update, uninstall,
enable, disable, settings write) creates a timestamped snapshot under `~/.game-polish/<game_id>/backups/<timestamp>/` **before the first byte is written**. A mutation
path with no snapshot call is a bug even if the operation succeeds."* And **(#4)**: exactly two roots — the game's install directory and `~/.game-polish/`.

Authoritative behaviour: `../game-polish-notes/docs/views/backups.md` — § *Backup Entry* (timestamp, trigger label, source name + version, file count, total size), §
*Details Expander* (per-file original paths and sizes), § *Restore Flow* ("`GamePolish.Core` creates a new backup of current state, then copies backed-up files back to
their original paths… This means restores are always reversible"), § *Disk Usage Footer*, § *Data Requirements* (index path, snapshot layout).

Trigger labels the UI renders come from `backups.md` § *Backup Entry*: "Mod install", "Mod update", "Mod uninstall", "Settings change" — plus the enable/disable case
from `views/library.md` § *Enable/Disable Mechanics*.

## Scope
- `GamePolish.Core/Backup/BackupTrigger.cs`, `BackupFileEntry.cs`, `BackupEvent.cs`, `BackupIndex.cs`.
- `GamePolish.Core/Backup/IBackupEngine.cs` + `BackupEngine.cs`.
- `GamePolish.Core/IO/IFileOps.cs` + `FileOps.cs` — the mutating-call seam (see Implementation notes).
- Tests: the ordering proof, the restore round trip, index shape, disk usage, collision handling, and a test that a snapshot happens even when there is nothing to copy.

## Out of scope
- Backing up whole game directories. The engine snapshots **the paths it is told**, never "everything".

## Implementation notes
```csharp
public enum BackupTrigger { ModInstall = 0, ModUpdate = 1, ModUninstall = 2, EnableDisable = 3,
                            SettingsChange = 4, Restore = 5 }
public sealed record BackupFileEntry(string RelativePath, long SizeBytes, DateTimeOffset ModifiedAt);
public sealed record BackupEvent
{
    public required string Timestamp { get; init; }             // folder name, also the key
    public required DateTimeOffset RecordedAt { get; init; }    // UTC, real time
    public required BackupTrigger Trigger { get; init; }
    public required string SourceName { get; init; }            // "pfUI" or "Skyrim.ini"
    public string? SourceVersion { get; init; }                 // "v5.1 -> v5.2" style for updates
    public required IReadOnlyList<BackupFileEntry> Files { get; init; }
    public required long TotalSizeBytes { get; init; }
    public string? Note { get; init; }                          // e.g. "pre-restore of 20261015T214203123Z"
}
public sealed class BackupIndex { public int SchemaVersion { get; init; } = 1;
                                  public List<BackupEvent> Events { get; init; } = []; }
public interface IBackupEngine
{
    // Creates the snapshot directory, records the index entry, then copies. Returns the event.
    BackupEvent Create(string gameId, string destinationRoot, BackupTrigger trigger, string sourceName,
                       string? sourceVersion, IEnumerable<string> relativePaths);
    IReadOnlyList<BackupEvent> List(string gameId);                 // newest first
    BackupEvent? Get(string gameId, string timestamp);
    void Restore(string gameId, string destinationRoot, string timestamp);
    void Delete(string gameId, string timestamp);
    long GetDiskUsageBytes(string gameId);                          // sum of all snapshots on disk
}
```
- **`BackupTrigger.Restore` exists on purpose.** `backups.md` § *Restore Flow* step 3 requires that the pre-restore snapshot itself be visible and restorable, and every
  row in the Backups view renders a trigger label. Reusing `ModUpdate` for a restore would lie in the audit trail. The five values named in the plan are all present;
  this is the sixth, and the UI maps it to "Restore (pre-restore state)".
- **The mutating seam.** `IFileOps` is a deliberately small interface covering *only* the calls that change the filesystem, so `208`–`210` tests can inject a failure at
  an exact point:
```csharp
public interface IFileOps
{
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);   // creates the parent dir
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void WriteAllBytes(string path, byte[] contents);
}
```
  `BackupEngine`, and `208`–`210`, take an `IFileOps` (default `FileOps`, which forwards to
  `System.IO.File`/`Directory`). This is the seam that makes "the snapshot directory existed before the
  first write" a *provable* assertion rather than a code-reading exercise: the test substitutes a
  recording implementation and asserts the recorded order of calls starts with
  `CreateDirectory("<backups>/<ts>")`. **Do not** abstract read calls, `File.Exists`, or `Path` — a
  wide `IFileSystem` abstraction is scope creep and the reason this one is narrow.
- **Snapshot layout** (`backups.md` § *Data Requirements*): `~/.game-polish/<game_id>/backups/<timestamp>/<original_relative_path>` — the copied file keeps its path
  relative to the *game install root*, not to the addon folder. For an addon file that means `<ts>/Interface/AddOns/pfUI/pfUI.toc`. That is what lets `Backups.md`'s
  Details expander show the original path, and what lets one restore routine serve both addon files and settings files.
- `destinationRoot` is the **game install root**; `relativePaths` are relative to it and are validated through `204`'s `IPathValidator.ResolveSafe` before use.
- Files that do not exist on disk are recorded in the index with `SizeBytes = 0` and are *not* created in the snapshot. This matters: a fresh install's backup is
  legitimately near-empty, and the index is the record that a backup happened.
- **Timestamp**: `DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")` — lexicographic order equals chronological order, legal as a Windows and macOS folder name, no
  colons. On collision (two operations in the same millisecond, or a clock that went backwards) append `-2`, `-3`, … and use the final value as `Timestamp`.
- **Order of operations inside `Create` — this order is the acceptance criterion, not an implementation detail:**
1. Resolve and `CreateDirectory` the snapshot directory. **This is the first filesystem mutation.**
2. Copy each existing file (via `IFileOps.CopyFile`), accumulating `BackupFileEntry`.
3. Compute `TotalSizeBytes` and `RecordedAt`.
4. Read-modify-write `index.json` (`AtomicJson`, 201) with the new event appended.
- If copying fails midway, the engine deletes the partial snapshot directory, does **not** append to the index, and throws `BackupFailedException(gameId, trigger,
  innerException)`. The caller must treat that as a hard stop: **no mutation proceeds when the backup failed.**
- The index is written *after* the files so an index entry always implies a complete snapshot. A snapshot directory with no index entry (crash between 3 and 4) is
  repaired by `List()`: it re-reads the directory, and either adopts it with a reconstructed entry or deletes it if its file list is incomplete. Choose "delete the
  orphan and log it", and say so in a comment.
- **`Restore(gameId, destinationRoot, timestamp)`**:
1. Read the event; unknown timestamp ⇒ `BackupNotFoundException`.
2. `Create(gameId, destinationRoot, BackupTrigger.Restore, sourceName: "pre-restore " + timestamp, …)` over the *current* files named in the event — so a restore is
  reversible (`backups.md` § *Restore Flow*).
3. Copy each file from `<backups>/<timestamp>/...` back to `Path.Combine(destinationRoot, relativePath)`, after re-validating the relative path through `204`.
4. Files listed in the event that are absent from the snapshot (zero-byte entries) are **deleted** at the destination — restoring "this file did not exist yet" is a
  real state for an install backup. Emit that in the log entries so the user can see deletions happened.
5. Never touch a path outside `destinationRoot` + the backups tree.

## Acceptance criteria
1. **Ordering:** with the recording `IFileOps`, `Create` records `CreateDirectory` of `<backups>/<timestamp>` as the first mutating call, before any `CopyFile`. The
  test asserts on the recorded sequence, and fails if any copy precedes it.
2. A test that runs a `Create` with `relativePaths` naming files that do not exist still leaves `<backups>/<timestamp>/` on disk and appends an index event with zero
  files.
3. Snapshot contents: for `<addon>/Interface/AddOns/pfUI/pfUI.toc`, the snapshot holds `<ts>/Interface/AddOns/pfUI/pfUI.toc` with identical bytes, and the index entry's
  `RelativePath` is `Interface/AddOns/pfUI/pfUI.toc`.
4. `Restore` returns the prior state: write A, `Create`, overwrite A with B, `Restore` ⇒ the file content is A again, **and** a new event with `Trigger == Restore`
  exists whose snapshot holds B.
5. `Restore` of a pre-install snapshot (zero-byte entries) removes the file that was installed.
6. Two `Create` calls in the same millisecond produce two distinct snapshot directories, both listed.
7. An injected `IFileOps.CopyFile` failure mid-snapshot leaves no snapshot directory and no index entry, and throws `BackupFailedException`.
8. `GetDiskUsageBytes` equals the byte total of the snapshot tree (assert against a hand-computed value).
9. Index round trip: `List()` after a process restart (new engine instance, same data root) returns the same events in newest-first order.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~BackupEngine"
# expect: all pass; test 1 is the one that must fail loudly if the order is ever reversed
# after 213 lands, end to end:
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" restore --game octowow --timestamp <ts>
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" backup list --game octowow
# expect: newest-first list, the restore visible as its own event
```
## References
- `../game-polish-notes/docs/views/backups.md` § *Backup Entry*, § *Details Expander*, § *Restore Flow*, § *Disk Usage Footer*, § *Data Requirements*
- `../game-polish-notes/docs/views/library.md` § *Enable/Disable Mechanics* ("A backup is created on first enable")
- `000-CONVENTIONS.md` § Hard invariants (#1, #4), § Definition of done ("Install / update / uninstall")
- Related tasks: `201` (`GamePolishPaths.BackupsDirectory`, `AtomicJson`), `204` (validates the copy list), `208`/`209`/`210` (callers), `306` (Backups view renders the
  index), `604` (retention), `213` (`backup list`, `restore`).
