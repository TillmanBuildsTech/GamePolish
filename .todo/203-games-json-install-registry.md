---
id: 203
title: games.json install registry
phase: 2
bucket: mvp
depends_on: [201, 202]
blocks: [208, 210, 213, 302]
status: todo
area: core
---

# 203 — games.json install registry
## Objective
`~/.game-polish/games.json` is the app's record of which games the user tracks and where each one is installed. Adding a game verifies the folder actually is that game,
adding the same game twice is refused with a useful message, and a game whose folder later moves or disappears is reported as unresolved rather than silently dropped.

## Context
Greenfield repo. Prerequisites: `201` (data root + `AtomicJson`) and `202` (descriptor, so the `install_detection.verify_file` value is known without hardcoding). Phase
2 order: **201/202/203 first; 204 and 205 are the two tasks everything else leans on; then 206–210; then 211–213.**

Authoritative behaviour:
- `../game-polish-notes/docs/games/octowow.md` § *Install Path*: "No Steam registry or auto-detection. The user browses to their install folder manually… Verify the
  folder is correct by checking for `WoW.exe` in the root. Path is stored in `~/.game-polish/games.json` and editable from the Library view."
- `../game-polish-notes/docs/views/library.md` § *Left Panel — Game List* and § *Data Requirements*: the game list comes from `games.json`; "Add Game opens a modal:
  either auto-detect from a list of supported games or manually browse to an install folder".

Restated invariants: **(#4)** only two roots may be touched — this file lives under the data root, and adding a game must **never write into the game's install
directory**. **(#6)** the verify-file name comes from the descriptor; do not special-case `WoW.exe` in C#.

## Scope
- `GamePolish.Core/Games/GamesRegistry.cs` — `TrackedGame`, `GamesFile`, `IGamesRegistry`, implementation.
- `GamePolish.Core/Games/InstallPathValidator.cs` — existence + verify-file + root-overlap checks.
- Exceptions: `GameAlreadyTrackedException`, `InstallPathInvalidException` (both carry the game id and the offending path).
- Tests: add/list/remove round trip, duplicate add, missing verify file, moved path, atomic write, and case-insensitive id handling.

## Out of scope
- Auto-detection heuristics — `manual_only: true` for the only supported game, and the descriptor schema has no detection block beyond `verify_file`. Do not invent one.
- Editing an install path from the UI (`302` does the view; it calls `SetInstallPath` here).
- Removing a game's installed mods or backups when the game is untracked. Untracking removes the entry only; deleting manifest/backups is a separate, explicit action
  (do not delete anything here).
- The manifest (`206`), the registry cache (`211`), path-traversal validation of mod contents (`204`).

## Implementation notes
```csharp
public sealed record TrackedGame
{
    public required string Id { get; init; }              // matches GameDescriptor.Id
    public required string Name { get; init; }            // snapshot of descriptor.Name at add time
    public required string InstallPath { get; init; }     // absolute, normalized, no trailing separator
    public required DateTimeOffset AddedAt { get; init; } // UTC
}
public sealed class GamesFile                              // serialized by AtomicJson (201)
{
    public int SchemaVersion { get; init; } = 1;
    public List<TrackedGame> Games { get; init; } = [];
}
public interface IGamesRegistry
{
    IReadOnlyList<TrackedGame> List();
    TrackedGame? Get(string gameId);
    TrackedGame Add(string gameId, string installPath);      // validates, verifies, writes atomically
    TrackedGame SetInstallPath(string gameId, string installPath); // re-validates; for a moved install
    bool Remove(string gameId);
    IReadOnlyList<TrackedGame> ListUnresolved();             // dir or verify_file missing
}
```
- The implementation is constructed with a `GamePolishPaths` (201) and an `IGameDescriptorLoader` (202). It resolves `games.json` as `paths.GamesFile`; it calls
  `paths.EnsureCreated()` before its first write.
- Persistence: `AtomicJson.Read<GamesFile>` / `AtomicJson.Write`. Missing file ⇒ empty `GamesFile`. A malformed file is an error (throw `InvalidDataException` naming
  the path) — **do not** silently reset a file that records where the user's games live.
- **Add validation, in this order, all before any write:**
1. `descriptor = loader.LoadById(gameId)` — unknown id ⇒ `InstallPathInvalidException("unknown game id")`.
2. `installPath` is non-null, non-empty, and `Path.IsPathFullyQualified`; expand `~` if the UI passes one.
3. `Directory.Exists(installPath)` ⇒ else reject. Store `Path.GetFullPath(installPath).TrimEnd(sep)`.
4. The path is **not** the data root and not inside it. A game installed under `~/.game-polish/` would collapse invariant #4's two roots; reject with a message that
  says exactly that.
5. The path is not already tracked under a *different* id.
6. `descriptor.InstallDetection.VerifyFile` — when non-null, `File.Exists(Path.Combine(installPath, verifyFile))` must be true. Message: `"<installPath> does not look
  like OctoWoW: WoW.exe was not found in the game root."` This is the only acceptance test the user sees, so it must name the file it looked for.
7. Duplicate add of the same id ⇒ `GameAlreadyTrackedException` carrying `ExistingPath` and `RequestedPath`, message: `"OctoWoW is already tracked at D:\OctoWow. Use
  'change install path' to move it."`
- Case handling: ids are already lowercase-constrained by `202`, so compare ids with `StringComparison.Ordinal`. Compare paths with `OrdinalIgnoreCase` on Windows and
  macOS and `Ordinal` on Linux, via one private helper — do not sprinkle `StringComparison` arguments.
- `ListUnresolved()` returns entries whose directory no longer exists, or whose verify file vanished. It **does not mutate** the file: the Library view (`302`) renders
  those rows as broken with an editable path, and only an explicit `SetInstallPath`/`Remove` changes state.
- Concurrency: `AtomicJson.Write` plus a private `SemaphoreSlim(1, 1)` around the read-modify-write of `Add`/`SetInstallPath`/`Remove`, so two concurrent adds cannot
  lose one entry. Cross-process safety is out of scope for MVP; state that in a comment.

## Acceptance criteria
1. `Add("octowow", <temp dir containing WoW.exe>)` returns a `TrackedGame` and the file `<data root>/games.json` contains `schema_version: 1` and the entry with
  `added_at` in UTC.
2. `Add` with the verify file absent throws `InstallPathInvalidException` whose message names the verify file and the path; the JSON file is **not** modified (compare
  bytes before/after).
3. `Add` twice for the same id throws `GameAlreadyTrackedException` and leaves exactly one entry.
4. `Add` for a directory inside `GAMEPOLISH_DATA_ROOT` is rejected with a message mentioning the data root.
5. `SetInstallPath` on a moved install updates the entry; `ListUnresolved()` before it returns the entry and after it returns nothing.
6. `Remove("octowow")` returns `true` once and `false` the second time; no other entry is affected.
7. No `tests`/fixtures write outside the temp data root: assert every path the implementation produced starts with the fixture root.
8. Nothing in this task opens a handle for writing inside an install path (a test that runs `Add` against a read-only temp game dir must still succeed).

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~GamesRegistry|FullyQualifiedName~InstallPathValidator"
# expect: all pass; the duplicate-add test asserts the existing path appears in the message
# manual, once the CLI exists (213):
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" add-game octowow --path "D:/OctoWow"
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" list-games
# expect: one row, verify-file check passed; a second add-game exits 1 with the duplicate message
```
## References
- `../game-polish-notes/docs/games/octowow.md` § *Install Path*
- `../game-polish-notes/docs/views/library.md` § *Left Panel — Game List*, § *Data Requirements*
- `000-CONVENTIONS.md` § Local state layout, § Hard invariants (#4, #6)
- Related tasks: `201` (`GamePolishPaths`, `AtomicJson`), `202` (`GameDescriptor.InstallDetection`), `206`/`210` (need the install path to resolve the addon root),
  `213` (`add-game`, `list-games`), `302` (Library game list and the editable install path).
