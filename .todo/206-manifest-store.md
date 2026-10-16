---
id: 206
title: Manifest store
phase: 2
bucket: mvp
depends_on: [201]
blocks: [208, 209, 210, 213, 302]
status: done
area: core
---

# 206 — Manifest store
## Objective
`~/.game-polish/<game_id>/manifest.json` becomes the durable record of what Game Polish installed into a game: for each mod its source, the addon folder it owns, the
installed version, its enabled state, and the exact list of files it put on disk. Every write is atomic, two concurrent writers cannot lose an entry, and a corrupted
file is quarantined rather than silently reset.

## Context
Greenfield repo. Prerequisite: `201` (`GamePolishPaths.ManifestFile`, `AtomicJson`). Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else
leans on; then 206–210; then 211–213.** This task needs no descriptor or path validator: it stores relative paths, it does not resolve them.

The manifest is consumed by both halves of the product, which is why its shape is fixed here and not by whoever writes first:
- `../game-polish-notes/docs/views/library.md` § *Right Panel — Mod List* (enabled/disabled grouping, installed version tag) and § *Data Requirements* ("Mod list from
  `~/.game-polish/<game_id>/manifest.json`").
- `../game-polish-notes/docs/views/discover.md` § *Data Requirements* ("Installed mod IDs come from `~/.game-polish/<game_id>/manifest.json` to render install state
  badges").
- `../game-polish-notes/docs/views/backups.md` § *Filter & Sort* (scope backups to one mod ⇒ needs the mod name recorded per backup, which the installer takes from
  here).

Restated invariants: **(#1)** the manifest is *not* a mutation of the game directory, so writing it does not require a backup — but the *installed file list* is what
`208`–`210` hand to the backup engine, so it must be complete and exact. **(#4)** this file lives under the data root. **(#2)** nothing in a manifest is ever executed
or treated as a script.

## Scope
- `GamePolish.Core/Manifest/InstalledMod.cs`, `InstalledFileEntry.cs`, `ModSourceType.cs`, `ManifestFile.cs`.
- `GamePolish.Core/Manifest/IManifestStore.cs` + `ManifestStore.cs`.
- `GamePolish.Core/Manifest/ManifestCorruptException.cs`.
- Tests: CRUD round trip, corruption recovery, concurrent writers, the `FindOwnersOfPath` conflict query, and the "queries the UI needs" set below.

## Out of scope
- Resolving or validating the stored relative paths against the filesystem (callers re-validate through `204` at use time; a manifest written by an older version must
  not be trusted blindly).
- Storing per-mod changelogs, screenshots or update metadata — those belong to the registry cache (`211`) and to phase 5 (`502`, `503`).
- Version history. The manifest holds the currently installed version only; the `backups.md` audit trail in `205` carries history.
- Dependencies between mods (the OctoUpdater problem the design notes mention) — explicitly post-MVP.

## Implementation notes
```csharp
public enum ModSourceType { Github = 0, Zip = 1 }
public sealed record InstalledFileEntry(string RelativePath, long SizeBytes);
// RelativePath is relative to the addon root, e.g. "pfUI/pfUI.toc" — not to the game root.
// The backup engine wants game-root-relative paths; 208 joins the addon root prefix when calling it.
public sealed record InstalledMod
{
    public required string Id { get; init; }            // registry id, or a slug for an ad-hoc source
    public required string Name { get; init; }          // display name shown in Library
    public required string SourceUrl { get; init; }     // https://github.com/... or the original zip file name
    public required ModSourceType SourceType { get; init; }
    public required string FolderName { get; init; }    // one folder under mod_layout.destination
    public required string Version { get; init; }       // HEAD SHA (40 hex) or tag name
    public required string VersionKind { get; init; }   // "sha" | "tag"
    public required bool Enabled { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required IReadOnlyList<InstalledFileEntry> Files { get; init; }
}
public sealed class ManifestFile { public int SchemaVersion { get; init; } = 1;
                                   public List<InstalledMod> Mods { get; init; } = []; }
public interface IManifestStore
{
    IReadOnlyList<InstalledMod> List(string gameId);
    InstalledMod? Find(string gameId, string modId);
    bool IsInstalled(string gameId, string modId);
    string? GetInstalledVersion(string gameId, string modId);
    void Upsert(string gameId, InstalledMod mod);                  // add or replace by Id
    bool Remove(string gameId, string modId);
    void SetEnabled(string gameId, string modId, bool enabled);
    IReadOnlyList<InstalledFileEntry> GetInstalledFiles(string gameId, string modId);
    IReadOnlyList<string> FindOwnersOfPath(string gameId, string relativePath); // mod ids claiming a path
    IReadOnlyList<string> FindFolderOwners(string gameId, string folderName);   // folder-name conflicts
}
```
- **The UI-facing query set** (this is the contract; `302`, `304` and `305` call exactly these): *is this mod installed* (`IsInstalled`), *which version*
  (`GetInstalledVersion`), *what files does it own* (`GetInstalledFiles`), *toggle enabled* (`SetEnabled`), *the full row list* (`List`), *would this install clobber
  something* (`FindOwnersOfPath`, `FindFolderOwners`). Anything else the UI wants is a filter over `List` and belongs in the view model.
- Persistence: `AtomicJson.Read<ManifestFile>` / `Write` against `paths.ManifestFile(gameId)`. Missing file ⇒ empty manifest, created on first `Upsert`.
  `paths.EnsureGameDirectory(gameId)` before the first write.
- **Corruption recovery** (decide and implement exactly this): on `JsonException` or a deserialized object that fails a shape check (e.g. a mod with a null `Files`),
  rename the file to `manifest.json.corrupt-<yyyyMMddTHHmmssfffZ>` **in the same directory**, then continue with an empty manifest and surface
  `ManifestCorruptException` through the operation result so the user sees it. Never delete it: the user's record of installed mods is the only thing standing between
  them and an orphaned addon folder. A later task may add recovery from the newest backup's `manifest.json`; do not build it now.
- **Concurrency, two layers:**
1. In-process: a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by game id, so `Upsert`/`Remove`/ `SetEnabled` are read-modify-write under one lock per game. The
  dictionary must be static or the store must be a singleton — two store instances must not each hold their own lock. Say which you chose in the XML doc comment.
2. Cross-process (optional for MVP, cheap to add): an advisory lock file `<manifest>.lock` opened with `FileShare.None`, retried for ~2 s with a backoff, then
  `ConcurrentWriteException`. If you add it, the lock file is created and deleted inside the lock and never left behind on the throw path.
- Store paths exactly as the source produced them, after `204` validation and separator normalization to `/`. Do not store absolute paths: a manifest with absolute
  paths breaks the moment the user moves the game (which `203.SetInstallPath` explicitly supports).
- `Upsert` must set `UpdatedAt` from its caller, not from `DateTimeOffset.UtcNow` internally — `209` needs to distinguish "the operation succeeded at T" from "we
  happened to write at T".
- Never store secrets: `SourceUrl` for a private repo must be the plain `https://github.com/owner/repo` form with no embedded token. Add a guard that rejects a
  `SourceUrl` containing `@` before the host or a `token=`/`key=` query parameter, and note it as defence-in-depth for `504`.

## Acceptance criteria
1. `Upsert` then `List` round-trips every field, including `VersionKind` and the ordered `Files` list.
2. `IsInstalled` / `GetInstalledVersion` / `GetInstalledFiles` / `Find` return the right values for a mod that exists and `false`/`null`/empty for one that does not.
3. `SetEnabled("octowow", "pfUI", false)` flips only that mod's flag; `List` reflects it after a new store instance is constructed against the same data root
  (durability, not cache).
4. `Remove` returns `true` then `false`, and deletes only the named entry.
5. Corruption: a hand-written malformed `manifest.json` throws `ManifestCorruptException`, the original bytes survive as `manifest.json.corrupt-*`, and the next
  `Upsert` produces a valid file.
6. Concurrency: 50 parallel `Upsert` calls of 50 distinct mod ids on one game produce 50 entries and zero `*.tmp` leftovers.
7. A `SourceUrl` containing an embedded credential (`https://x-access-token:abc@github.com/o/r`) is rejected with a message naming the offending component.
8. `FindOwnersOfPath` identifies the owning mod for `pfUI/pfUI.toc`, returns empty for a path nobody owns, and is `OrdinalIgnoreCase` on Windows/macOS (see `204`'s
  `PathComparer`) so a case-differing path still matches.
9. `grep -n "UtcNow" GamePolish.Core/Manifest/ManifestStore.cs` shows no timestamp assignment other than the corruption-quarantine file name — `InstalledAt`/`UpdatedAt`
  come from the caller.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~ManifestStore"
# expect: all pass, including the 50-way concurrency test (run it 3x; it must be deterministic)
# after macOS/Windows both exist, sanity-check the real file once via the CLI:
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" install --game octowow --source https://github.com/brues-code/pfUI
ls "$TMPDIR/gp/octowow/manifest.json" && cat "$TMPDIR/gp/octowow/manifest.json"
```
## References
- `../game-polish-notes/docs/views/library.md` § *Right Panel — Mod List*, § *Overflow Menu Actions*, § *Data Requirements*
- `../game-polish-notes/docs/views/discover.md` § *Data Requirements* (install-state badges)
- `../game-polish-notes/docs/views/backups.md` § *Filter & Sort* (per-mod backup scoping)
- `000-CONVENTIONS.md` § Local state layout, § Hard invariants (#1, #2, #4)
- Related tasks: `201` (`GamePolishPaths`, `AtomicJson`), `204` (`PathComparer` for the conflict query), `205` (backup reconcile target), `208` (writes the file list
  after a successful install), `209` (updates `Version`/`UpdatedAt`), `210` (removes entries, flips `Enabled`), `302` (the UI queries), `213` (`list`).
