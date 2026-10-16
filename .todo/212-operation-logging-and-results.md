---
id: 212
title: Operation logging and results
phase: 2
bucket: mvp
depends_on: [102, 201]
blocks: [208, 209, 210, 213, 307]
status: todo
area: core
---

# 212 — Operation logging and results
## Objective
One shared result type that every Core operation returns, one error-code enum so the UI can branch on *what* went wrong instead of parsing strings, and a redacting
rolling file log under the data root. After this task no Core method throws across its public surface for an expected failure, and no token can reach a log.

## Context
Greenfield repo. Prerequisites: `102` (the test-runner decision, so the tests exist) and `201` (`GamePolishPaths.LogsDirectory`, `AppSettings.LogLevel`, `AtomicJson`
for shape consistency). Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything else leans on; then 206–210; then 211–213.**

**Deliberately front-loaded — read this before sequencing.** The roadmap lists `212` after `208`–`210`, but `208`, `209` and `210` *return* these types and `213` prints
them. This task has no dependency on any of them and needs only `201`, so build it **in parallel with `205`/`206`/`207`, and before `208`**. Do not let `208` invent a
local result type "for now"; that is the duplication this task exists to prevent. Flag the ordering deviation in your report.

Authoritative behaviour: `../game-polish-notes/docs/views/discover.md` § *Install Flow (inline)* ("On failure: error toast with a details link (opens a log drawer)")
and § *Empty / Edge States* (banner text). The view that renders all of this is `../game-polish-notes/docs/views/library.md` + the toast/drawer task `307`.

Restated invariant: **(#4)** — logs live under `~/.game-polish/logs/`, never in a game directory, never in the user profile. And `000-CONVENTIONS.md` § Local state
layout: "Secrets (GitHub PAT, S3 keys, auth tokens) go in the **OS keychain** — Windows Credential Manager, macOS Keychain. Never in a JSON file, never in a log, never
in an export."

## Scope
- `GamePolish.Core/Operations/OperationStatus.cs`, `OperationErrorCode.cs`.
- `GamePolish.Core/Operations/OperationLogEntry.cs`, `OperationResult.cs`, `OperationProgress.cs`.
- `GamePolish.Core/Operations/SecretRedactor.cs`.
- `GamePolish.Core/Operations/IOperationLog.cs` + `FileOperationLog.cs`.
- `GamePolish.Core/Operations/CoreException.cs` — the base for the typed exceptions `202`–`211` throw.
- Tests: the `FromException` mapping table, redaction, log rotation/purge, and the "no secret reaches disk" end-to-end test.

## Out of scope
- The toast and drawer controls (`307`); this task defines the data they render, not the rendering.
- Crash reporting, telemetry, or sending logs anywhere (`708`+).
- Backup event history. The Backups view reads `205`'s `index.json`, not this log.

## Implementation notes
```csharp
public enum OperationStatus { Succeeded = 0, Failed = 1, Cancelled = 2 }
public enum OperationErrorCode
{
    None = 0, Unknown, Cancelled, Timeout, ConcurrentWrite,
    // paths & sources (204, 207)
    PathUnsafe, SourceNotFound, SourceAuthRequired, SourceInvalid,
    GitNotFound, GitFailed, ZipInvalid, ZipTooLarge,
    // local environment (201, 203, 205, 206)
    DiskFull, PermissionDenied, IoError, DataRootUnavailable,
    // game & install state (202, 203)
    DescriptorInvalid, GameNotFound, GameAlreadyTracked, InstallPathInvalid,
    GameInstallPathMissing, GameVerifyFileMissing,
    // mod state (206, 208, 209, 210)
    ModAlreadyInstalled, ModNotInstalled, ModFolderMissing, StagedCopyMissing,
    ConflictsUnresolved, ModAlreadyUpToDate, VersionUnknown,
    // safety net (205)
    BackupFailed, BackupNotFound, RestoreFailed, ManifestCorrupt, ManifestUnreadable,
    // network (211)
    NetworkUnavailable, RegistryUnavailable, RegistryInvalid
}
```
```csharp
public sealed record OperationLogEntry(DateTimeOffset At, string Level, string Message, string? Detail);
// Level: "info" | "warn" | "error"
public sealed record OperationProgress(string Phase, int Completed, int Total, string? CurrentItem = null);
public sealed record OperationResult
{
    public required OperationStatus Status { get; init; }
    public required OperationErrorCode Code { get; init; }        // None on success
    public required string Message { get; init; }                 // one line, user-facing, <= 200 chars
    public string? Detail { get; init; }                          // multi-line technical text
    public required IReadOnlyList<OperationLogEntry> Entries { get; init; }
    public string? GameId { get; init; }
    public string? ModId { get; init; }
    public string? BackupTimestamp { get; init; }                 // lets the toast offer "View backup" (306)
    public static OperationResult Success(string message, /* gameId, modId, backupTimestamp, entries */);
    public static OperationResult Failure(OperationErrorCode code, string message, string? detail,
                                          Exception? cause, /* ids, backupTimestamp, entries */);
    public static OperationResult Cancelled(string message, /* ids, backupTimestamp, entries */);
    public static OperationResult FromException(Exception exception, string? gameId, string? modId,
                                                IReadOnlyList<OperationLogEntry>? entries = null);
}
```
- `FromException` is the **only** mapping from the typed Core exceptions to codes, and its behaviour is the acceptance criterion below. `202`'s
  `GameDescriptorValidationException` ⇒ `DescriptorInvalid`; `203`'s two exceptions ⇒ `GameAlreadyTracked` / `InstallPathInvalid`; `204`'s ⇒ `PathUnsafe` (put the
  `Failures` list in `Detail`); `205`'s ⇒ `BackupFailed` / `BackupNotFound` / `RestoreFailed`; `206`'s ⇒ `ManifestCorrupt`; `207`'s ⇒ `GitNotFound` / `GitFailed` /
  `ZipInvalid` / `SourceNotFound` / `Timeout`; `211`'s ⇒ `RegistryUnavailable` / `RegistryInvalid`; `IOException` with a full disk ⇒ `DiskFull`;
  `UnauthorizedAccessException` ⇒ `PermissionDenied`; `OperationCanceledException` ⇒ `Cancelled`; anything else ⇒ `Unknown`. Every unknown exception's `Detail` includes
  the exception type and stack — `Detail` is technical, `Message` is not; **`Message` must never contain a stack trace, an exception type name, or an absolute path
  other than the game's install directory.**
```csharp
public static class SecretRedactor
{
    public static void RegisterSecret(string? secret);   // in-memory only; ignore null/short (< 6 chars)
    public static string Redact(string? text);           // replace every match with "***"
    public static bool ContainsSecret(string? text);
}
```
- Built-in patterns, all applied by `Redact`: GitHub tokens (`ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, `github_pat_` prefixes followed by ≥ 20 token chars),
  `(?i)bearer\s+\S+`, credentials in a URL (`https?://[^/\s:@]+:[^@\s]+@`), AWS access keys (`AKIA[0-9A-Z]{16}`), and a key-value catch-all
  (`(?i)(token|password|passwd|secret|api[_-]?key|authorization)\s*[=:]\s*\S+`) which keeps the key name and replaces the value.
```csharp
public interface IOperationLog
{
    void Write(OperationLogEntry entry);                     // redacted, appended to today's file
    void Write(string level, string message, string? detail = null);
    string CurrentLogFilePath { get; }                       // <root>/logs/game-polish-yyyyMMdd.log
    void PurgeOlderThan(TimeSpan age);                       // startup; default 14 days
}
```
- Layout: `<data root>/logs/game-polish-<yyyyMMdd>.log`, one file per UTC day, appended. Line format `2026-10-15T21:42:03.123Z INFO  Backup Snapshot created
  20261015T214203123Z (0 files)` — a fixed-width level, a category, then the message; `Detail` follows as indented continuation lines. Machine-parseable enough for `213
  doctor` without a parser library.
- Level gate from `AppSettings.LogLevel` (`201`) with the `GAMEPOLISH_LOG_LEVEL` environment variable winning when set (`debug|information|warn|error`) — the env var is
  how the CLI and tests turn volume up.
- Failure to write a log line must **never** fail the operation: catch `IOException`, drop the entry, and continue. A log that breaks installs is worse than a missing
  log line.

## Acceptance criteria
1. `FromException` maps each typed exception to the code in the table above — one test per mapping, asserting the `Code` **and** that `Status == Failed`.
2. Redaction: each built-in pattern is redacted in a sample string; after `RegisterSecret("ghp_ABC…")` any message containing it comes out as `***`.
3. **End-to-end secret test:** register a fake token, run an operation whose `Detail` includes a URL with the token in it (and an exception whose message embeds it),
  then `grep -rn "ghp_" <data root>/logs` returns nothing, and `result.Message`/`Detail`/`Entries` contain no plaintext token either.
4. `Message` is ≤ 200 chars for a synthetic exception with a 10 000-char message, and `Detail` carries the remainder.
5. File log: after N writes there is exactly one file, named `game-polish-<today>.log`, whose first line starts with an ISO-8601 UTC timestamp.
6. `PurgeOlderThan(14 days)` deletes only log files older than that and leaves today's.
7. A read-only `logs/` directory does not fail an operation: writes are swallowed and the operation still returns its normal result.
8. `OperationResult` round-trips through `AtomicJson` (it is what `213 --json` prints), including the `Entries` list and a null `BackupTimestamp`.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~OperationResult|FullyQualifiedName~SecretRedactor|FullyQualifiedName~OperationLog"
# expect: all pass; criterion 5 is the one that must never be skipped or marked inconclusive
# after 213 lands, prove it on real output:
dotnet run --project GamePolish.Cli -- --json --data-root "$TMPDIR/gp" install --game octowow --source https://github.com/nope/nope
grep -rn "ghp_\|Bearer \|AKIA" "$TMPDIR/gp/logs"   # expect: no output
```
## References
- `../game-polish-notes/docs/views/discover.md` § *Install Flow (inline)*, § *Empty / Edge States*
- `../game-polish-notes/docs/views/app-settings.md` § *Security Notes* ("The GitHub PAT is never logged…")
- `CLAUDE.md` § *Key Design Constraints* (security) and § *Pitfalls*
- `000-CONVENTIONS.md` § Hard invariants (#4), § Local state layout, § Evidence rules
- Related tasks: `201` (`LogsDirectory`, `LogLevel`), `202`–`211` (the exception types this maps and the pipelines that return results), `208`/`209`/`210` (return
  `OperationResult`/`OperationProgress`), `213` (`--json`), `307` (toasts + log drawer — the consumer), `504` (`RegisterSecret`).
