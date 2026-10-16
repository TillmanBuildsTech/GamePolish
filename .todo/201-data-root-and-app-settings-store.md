---
id: 201
title: Data root and app-settings store
phase: 2
bucket: mvp
depends_on: [101, 102]
blocks: [202, 203, 205, 206, 211, 212, 213]
status: todo
area: core
---

# 201 — Data root and app-settings store
## Objective
One resolution point for the Game Polish data root, plus a typed, atomically-written store for non-sensitive app preferences. After this task, every other phase-2 task
asks one object where its files go instead of re-deriving a path, and `app-settings.json` can never be observed half-written.

## Context
The repo is greenfield: `GamePolish.Core` exists only because phase 1 (`101`, `102`) created the solution, the class library and its test project. This is the first
task of phase 2, so it is also the first task that writes anything to a user's disk. Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else
leans on; then 206–210; then 211–213.**

Restated invariants you are bound by (from `000-CONVENTIONS.md` § Hard invariants):
- **#4:** "Touch exactly two roots: the game's install directory and `~/.game-polish/`. No registry writes, no user-profile writes, no other game's folders. Temp
  staging is fine."
- **#1:** backup before mutation. This task itself never mutates a game directory, so it takes no backup; do not add one.
- **Secrets** (GitHub PAT, S3 keys, auth tokens) live in the **OS keychain**, never in a JSON file, never in a log, never in an export (`000-CONVENTIONS.md` § Local
  state layout; the App Settings view says the same under *GitHub*).

The authoritative behaviour for settings is `../game-polish-notes/docs/views/app-settings.md` — § *Data Requirements* ("Settings persisted to
`~/.game-polish/app-settings.json` (non-sensitive settings only)"; PAT to the OS keychain) and § *Advanced* (the data dir is user-changeable, which is why the root must
be resolved, not hardcoded).

## Scope
- `GamePolish.Core/Storage/GamePolishPaths.cs` — resolves the data root and exposes every path under it.
- `GamePolish.Core/Storage/AtomicJson.cs` — the single JSON reader/writer used by 203, 205, 206 and 211.
- `GamePolish.Core/Storage/AppSettings.cs` — the settings record with a `schema_version` field.
- `GamePolish.Core/Storage/AppSettingsStore.cs` — load/save with defaults and atomic save.
- Tests beside them in the Core test project, plus the two grep-based tests in Acceptance criteria.

## Out of scope
- The OS keychain and anything that stores a secret (that is the phase-5 PAT task `504`).
- Migrating the data root to a new location, and the export/import of the whole data dir (`606`).
- Backup retention and disk usage (`604`); the backup location override stays a reserved field only.
- Applying a theme in the UI (`301`); this task only persists the preference.
- The rolling file log and the operation-result model (`212`) — this task only answers *where* logs go via `GamePolishPaths.LogsDirectory`.
- The contents of `games.json` (`203`), `manifest.json` (`206`), `registry-cache.json` (`211`).

## Implementation notes
Root resolution precedence, highest first:
1. An explicit argument (`new GamePolishPaths(root)`), which is what the CLI's `--data-root` flag (`213`) passes.
2. The environment variable `GAMEPOLISH_DATA_ROOT` — **this is how tests point the engine at a temp directory**. Set it in a fixture, not in each test.
3. Default: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".game-polish")`.
```csharp
public sealed class GamePolishPaths
{
    public GamePolishPaths(string root);                 // stores Path.GetFullPath(root)
    public static GamePolishPaths Resolve(string? overrideRoot = null); // precedence above
    public string Root { get; }
    public string GamesFile { get; }                     // <Root>/games.json
    public string AppSettingsFile { get; }               // <Root>/app-settings.json
    public string LogsDirectory { get; }                 // <Root>/logs
    public string GameDirectory(string gameId);          // <Root>/<game_id>
    public string ManifestFile(string gameId);           // <Root>/<game_id>/manifest.json
    public string RegistryCacheFile(string gameId);      // <Root>/<game_id>/registry-cache.json
    public string BackupsDirectory(string gameId);       // <Root>/<game_id>/backups
    public string StagingDirectory(string gameId);       // <Root>/<game_id>/staging
    public void EnsureCreated();                         // idempotent; creates Root only
    public string EnsureGameDirectory(string gameId);     // creates <Root>/<game_id>, returns it
}
```
- `EnsureCreated()` must never be called from a read-only operation. A `list` that silently creates directories is a surprise; have callers create on write.
- Reject a root that is not fully qualified, is a file, or is a drive root (`C:\`). Reject a root equal to or inside a tracked game's install path (that would collapse
  the two roots into one).
```csharp
public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options; // WriteIndented; SnakeCaseLower; nulls ignored
    public static T? Read<T>(string path);                // default(T) when the file is absent
    public static void Write<T>(string path, T value);    // temp sibling + File.Move(overwrite: true)
}
```
- Temp file name: `<file>.<guid:N>.tmp` **in the same directory** as the target (a rename across volumes is not atomic). `File.Move(temp, path, overwrite: true)`. Wrap
  in try/finally that deletes the temp file on failure.
- On Windows, `File.Move(..., overwrite: true)` maps to `MoveFileEx` with replace — acceptable here.
- `Read<T>` must let `JsonException` propagate. Corrupt-file recovery is the caller's decision (see 206).
```csharp
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;              // "schema_version" in JSON
    public string Theme { get; init; } = "system";            // system | light | dark
    public string Accent { get; init; } = "blue";             // blue | purple | green | orange
    public string UpdateCheckOnStart { get; init; } = "session"; // session | never
    public string LogLevel { get; init; } = "information";    // consumed by task 212
    public string? BackupLocationOverride { get; init; }      // reserved; wired in 606
}
public interface IAppSettingsStore
{
    AppSettings Load();                 // defaults when the file is missing; never throws on absence
    void Save(AppSettings settings);    // atomic; bumps nothing; caller owns schema_version
}
```
- `schema_version` is the *only* migration signal. When a later task adds a field, it must bump this and add a migration; unknown fields must survive a load/save round
  trip or be explicitly dropped — pick "dropped, and logged" and say so in the XML doc comment.
- **No member of `AppSettings` may hold a secret.** That is structural, not stylistic: the Acceptance criteria include a test that fails if a property name matches
  `token|secret|password|key|credential`.
- Bump nothing else. Do not add DI containers, logging abstractions or configuration providers.

## Acceptance criteria
1. With `GAMEPOLISH_DATA_ROOT` set to a temp dir, every path written by `Save`/`EnsureCreated` is inside that temp dir; the real `~/.game-polish` is untouched (assert
  on the temp dir's contents, not the profile).
2. Default resolution with no env var and no argument equals `<user profile>/.game-polish`, and `EnsureCreated()` is idempotent (called twice, no throw, directory
  exists).
3. `AppSettingsStore.Save` called twice leaves exactly one `app-settings.json` and **zero** `*.tmp` files beside it.
4. Round trip: `Save(x); Load() == x` including `schema_version == 1`.
5. `Load()` on a missing file returns the defaults and does not create the file.
6. `Load()` on malformed JSON throws `JsonException` and does not overwrite the file.
7. A reflection test asserts no `AppSettings` property name matches the secret regex.
8. A repo grep asserts no `File.WriteAllText`/`File.CreateText` in `GamePolish.Core` outside `Storage/AtomicJson.cs` — the atomic writer is the only writer.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~GamePolishPaths|FullyQualifiedName~AppSettingsStore|FullyQualifiedName~AtomicJson"
# expect: all pass; the failure messages name the offending path
grep -rn "File.WriteAllText\|File.CreateText\|StreamWriter(" GamePolish.Core/ | grep -v "Storage/AtomicJson.cs"
# expect: no output
grep -rniE "(token|secret|password|api[_-]?key|credential)" GamePolish.Core/Storage/AppSettings.cs
# expect: no output
grep -rn "Interface/AddOns" GamePolish.Core/    # sanity: still no hardcoded game paths in Core
# expect: no output
```
## References
- `../game-polish-notes/docs/views/app-settings.md` § *Data Requirements*, § *Advanced*, § *Security Notes*
- `../game-polish-notes/docs/views/backups.md` § *Data Requirements* (backup index and snapshot paths)
- `000-CONVENTIONS.md` § Hard invariants (#1, #4), § Local state layout, § Definition of done
- `README.md` § Phase map, § Dependency order
- Related tasks: `101`, `102` (prerequisites); `203`, `206`, `211` (reuse `AtomicJson` and `GamePolishPaths`); `207` (staging lives under `StagingDirectory`); `212`
  (consumes `LogsDirectory` and `LogLevel`); `213` (wires `--data-root` into `Resolve`).
