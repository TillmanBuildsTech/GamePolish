---
id: 209
title: Update pipeline
phase: 2
bucket: mvp
depends_on: [201, 205, 206, 207, 208, 212]
blocks: [210, 213, 503, 504]
status: todo
area: core
---

# 209 — Update pipeline
## Objective
Bring an installed addon up to the current upstream version without losing anything: back up first, stage the new source, swap it in, update the manifest, and report
both the version that was replaced and any local edits that the swap overwrote. Also expose the cheap "is there an update?" probe the Library row consumes.

## Context
Greenfield repo. Prerequisites: `207` (staging — and, critically, **the model it decided**), `208` (the `SwapOperations` swap + rollback and the conflict scan, reused
verbatim), `205` (backup), `206` (manifest), `201` (staging paths), `212` (result model). Phase 2 order: **201/202/203 first; 204 and 205 are the two tasks everything
else leans on; then 206–210; then 211–213.**

**The model is decided in `207`: re-stage a fresh shallow clone and swap. Do not `git fetch --depth=1` followed by `git reset --hard origin/BRANCH`.** Read `207`'s "THE
OPEN DECISION" section before writing code. The design note's operation table (`../game-polish-notes/docs/games/octowow.md` § *Addon Layout*) documents `git fetch
--depth=1 && git reset --hard origin/BRANCH` as the incumbent's shape; the swap model supersedes it for the reasons recorded in `207`, and that supersession must be
noted in your report so the design note can be corrected in the same change (`000-CONVENTIONS.md`: "If the design notes and this directory disagree, the notes win and
this directory should be corrected").

Restated invariant **(#1)**: the backup is created before the first byte is written, on every update. **(#2)**: the update never runs anything from the repo — no hooks,
no build steps, no LFS smudge (all enforced in `207`'s git invocation, which you call rather than duplicate).

Authoritative behaviour: `../game-polish-notes/docs/views/library.md` § *Overflow Menu Actions* ("Check for Update — Fetch latest tag from source repo; show update
badge if newer") and § *Update Indicator* ("If a mod has a newer version available (detected via GitHub releases/tags), the version tag turns amber with an up-arrow.
Clicking it opens an 'Update Available' sheet showing the changelog and a confirm button"); `../game-polish-notes/docs/views/backups.md` § *Backup Entry* shows the
update label as "Mod update — SkyUI v5.1 → v5.2", which is where the `SourceVersion` string format comes from.

## Scope
- `GamePolish.Core/Update/UpdatePipeline.cs`, `IUpdatePipeline.cs`.
- `GamePolish.Core/Update/IVersionProbe.cs` + `GitVersionProbe.cs` — the remote-version probe (HEAD SHA over a shallow fetch into staging; the GitHub API client that
  also gives tags and changelogs is `503`).
- `GamePolish.Core/Update/UpdateAvailability.cs`, `LocalEditReport.cs`.
- Tests: update round trip, backup-before-write ordering, local-edit reporting, version detection, failure rollback, cancellation, and the availability contract.

## Out of scope
- The GitHub releases/changelog API and rate limits (`503`) — this task's probe answers "what is upstream HEAD?", it does not render a changelog.
- Auto-update on session start and the update-check setting (`503`, `504`); the `UpdateCheckOnStart` preference exists in `201` and is read by the UI, not here.
- Merging local user edits back into the new version. Report them; do not merge. (Record as a follow-up rather than building a three-way merge.)
- Uninstall / enable-disable (`210`).

## Implementation notes
```csharp
public sealed record VersionInfo(string Value, string Kind, DateTimeOffset? ObservedAt);
// Kind: "sha" | "tag". For a GitHub source Value is the remote HEAD SHA (40 hex) unless a tag sits on HEAD.
public enum UpdateState { UpToDate, UpdateAvailable, Unknown, CheckFailed }
public sealed record UpdateAvailability(
    string ModId, string InstalledVersion, string? LatestVersion, UpdateState State, string? Reason);
public sealed record LocalEditReport(IReadOnlyList<string> ModifiedPaths, IReadOnlyList<string> AddedPaths);
public interface IVersionProbe
{
    Task<VersionInfo> GetRemoteVersionAsync(string sourceUrl, string? branch, CancellationToken ct);
}
public interface IUpdatePipeline
{
    Task<UpdateAvailability> CheckAsync(string gameId, string modId, CancellationToken ct);
    Task<OperationResult> UpdateAsync(string gameId, string modId,
                                      IProgress<OperationProgress>? progress, CancellationToken ct);
}
```
**`CheckAsync` — the contract the UI consumes.** This is `504`'s (and the Library row's) input, so define it completely here rather than letting the UI guess:
- `UpToDate` — installed `Version` equals the probed remote version. No badge.
- `UpdateAvailable` — they differ. `LatestVersion` is set. Library turns the version tag amber with the up-arrow (`library.md` § *Update Indicator*).
- `Unknown` — the installed version or the remote version cannot be determined (e.g. a ZIP-sourced install has no upstream, or `VersionKind` was never recorded). **The
  UI must render nothing** rather than a false "up to date" — say this explicitly in the XML doc comment.
- `CheckFailed` — network/auth/timeout. `Reason` carries the mapped error code's text; the UI shows the neutral tag with no badge and offers a manual retry.
- `CheckAsync` **never mutates** state: no backup, no manifest write, no unstaged leftovers. It stages into `staging/check-<modId>-<opId>/` and always cleans up,
  including on cancellation.
- Cheapness: for a git source, probe with a shallow fetch into a scratch clone and `git rev-parse` — do not clone the tree. If `207`'s `StageGitAsync` is the only tool
  available, an acceptable MVP is to stage fully and read the version, but then the cleanup path must be exercised by a test (no staging directories left).
- Respect `SourceUrl` for ZIP-sourced mods: `Unknown`, immediately, without network. Add that test.

**`UpdateAsync` sequence** (phases mirror `208`; do not fork the code — call `208`'s `SwapOperations`):
1. Resolve game, descriptor, `addonRoot`, and `IManifestStore.Find(gameId, modId)`. Missing manifest entry ⇒ `ModNotInstalled` (never silently install over an orphan
  folder).
2. Re-stage the source via `207` with the mod's recorded `SourceUrl`/branch. Failure ⇒ no mutation.
3. Validate the staged paths (204) and scan conflicts (`208`'s `DetectConflictsAsync` logic) — an update that would overwrite a *different* mod's files is a failure,
  not a warning.
4. **Local-edit scan, before the backup**, so the report can be included in the backup's `Note`: compare the on-disk files under `<addonRoot>/<FolderName>/` against the
  manifest's recorded `Files` list — a path whose recorded size differs from the current size, or a path on disk that the manifest does not list, is a local edit
  (`LocalEditReport`). Sizes are what MVP records; if you also record a hash in the manifest, use it, but do not change `206`'s shape in this task.
5. **Backup — before the first byte.** `IBackupEngine.Create(gameId, installPath, ModUpdate, sourceName: mod.Name, sourceVersion: $"{oldVersion} -> {newVersion}",
  relativePaths: every destination file that exists)`. Backup failure ⇒ `BackupFailed`, nothing written.
6. Swap (`208`'s `SwapOperations`), overwriting the addon folder wholesale.
7. `IManifestStore.Upsert` with the new `Version`, `VersionKind`, `UpdatedAt = opStart`, the new `Files`, and `Enabled` preserved from the existing entry.
8. Clean up staging. Return `OperationResult.Success` with `Message` like `"Updated pfUI v5.1 -> v5.2"` (that is the string `backups.md` shows as the source name +
  version) and a `Detail` that lists the `LocalEditReport` entries when non-empty, plus the backup timestamp.

Rollback mirrors `208` step-for-step; a failed update must leave the **old** addon folder in place **and** the old manifest entry intact (assert both). Cancellation:
before the backup ⇒ clean staging, `Cancelled`; after ⇒ roll back the folder and the manifest, keep the backup, `Cancelled`.

Do not update `InstalledAt`; only `UpdatedAt`. Do not change `Enabled`; an update of a disabled mod keeps it disabled and (per `210`) the swap targets the disabled
staging copy if that is where the files live — detect that case and return `ModNotInstalled` with a message telling the user to enable it first, rather than guessing.
Record that decision in the code comment.

## Acceptance criteria
1. `CheckAsync` returns `UpdateAvailable` with a `LatestVersion` when the installed SHA differs from the probed SHA; `UpToDate` when identical; `Unknown` for a
  `SourceType.Zip` mod with no network access attempted (assert no `HttpClient`/process call by using a probe double that throws on invocation).
2. `CheckAsync` leaves the data root byte-identical (no backup, no manifest change, no staging leftovers).
3. Update ordering: the recording `IFileOps` (`205`) shows the backup's `CreateDirectory` and copies before any `.game-polish-new-*` write.
4. Update round trip: installed version changes, `UpdatedAt` advances, `InstalledAt` does not, `Enabled` is preserved, and the new addon files are on disk with no
  `.git` directory.
5. Local edits: mutate one installed file and add one new file; `UpdateAsync` returns `Success` with a `Detail` naming both paths, and the pre-update backup contains
  the mutated content.
6. Injected mid-swap failure leaves the old addon folder byte-identical **and** the old manifest entry intact (assert version and `Files` unchanged).
7. `ModNotInstalled` for an id with no manifest entry, and for a disabled mod whose files are in the staging area — in both cases nothing is written and no backup is
  created.
8. Cancellation during the copy leaves the old folder intact and returns `Cancelled` with the backup kept.
9. The update label string in the backup event matches `"<oldVersion> -> <newVersion>"` — asserted in `backups.md`'s display format.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~UpdatePipeline|FullyQualifiedName~VersionProbe"
# expect: all pass; criteria 1, 3, 5 and 6 are the load-bearing ones
# end to end, once 213 lands (real network):
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" install --game octowow --source https://github.com/brues-code/pfUI
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" update --game octowow --mod pfUI
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" backup list --game octowow
# expect: a Mod update event whose source name reads "pfUI <old> -> <new>"
```
## References
- `../game-polish-notes/docs/games/octowow.md` § *Addon Layout* (the documented update shape this task supersedes — report the divergence)
- `../game-polish-notes/docs/views/library.md` § *Overflow Menu Actions*, § *Update Indicator*, § *Data Requirements* (>24 h stale badge)
- `../game-polish-notes/docs/views/backups.md` § *Backup Entry* (the "Mod update — X v5.1 → v5.2" label)
- `000-CONVENTIONS.md` § Hard invariants (#1, #2), § Definition of done
- Related tasks: `205` (backup + `IFileOps`), `206` (manifest), `207` (staging + the decided model), `208` (`SwapOperations`, conflict scan), `212` (result model),
  `213` (`update`), `503` (GitHub API, changelogs, tags), `504` (the UI consumer of `UpdateAvailability`).
