---
id: 207
title: "Source acquisition: git and ZIP"
phase: 2
bucket: mvp
depends_on: [201, 204]
blocks: [208, 209]
status: todo
area: core
---

# 207 — Source acquisition: git and ZIP
## Objective
Materialise a mod source (a GitHub repo URL, or a user ZIP) into a fully populated, validated staging directory **without touching the game directory**, together with
the metadata the pipeline needs: the addon folder name to create, the installed version, and the validated file list. This task also **decides** the open clone-in-place
versus clone-to-temp-then-swap question.

## Context
Greenfield repo. Prerequisites: `201` (staging lives under the data root) and `204` (every path a source supplies is validated first). Phase 2 order: **201/202/203
first; 204 and 205 are the two tasks everything else leans on; then 206–210; then 211–213.**

Authoritative behaviour: `../game-polish-notes/docs/games/octowow.md` § *Addon Layout* — "Each addon is a **shallow git clone** (`--depth=1`) of its GitHub repo
directly into the addon folder" — and its operation table: install = `git clone --depth=1 <repo_url> Interface/AddOns/<folder_name>/`; update = `git fetch --depth=1 &&
git reset --hard origin/<branch>` (**in prose the branch is the placeholder `BRANCH`**); uninstall = delete after backup; version = HEAD SHA or latest tag.

Restated invariants: **(#2)** no shelling out to scripts inside a mod, no running mod-provided git hooks, no build steps, no loading mod assemblies. **(#3)** validate
every path from a mod source. **(#4)** two roots only — staging under the data root is inside the allowed set and is preferred over the OS temp dir here because it
makes the swap same-volume and survives a process death.

The decision is left open by the design notes and recorded in the skill `game-polish-development`: cloning or resetting directly in `Interface/AddOns` leaves a
half-written addon in the user's game folder on failure, and `reset --hard` destroys local edits with no recovery beyond the backup. **Decide it here; the choice
dictates the failure-recovery tests.**

## Scope
- `GamePolish.Core/Sources/`: `StagedSource.cs`, `SourceRequest.cs`, `ISourceAcquirer.cs`, `SourceAcquirer.cs`.
- `GamePolish.Core/Sources/GitRunner.cs` — the only place in Core that starts a process.
- `GamePolish.Core/Sources/ZipExtractor.cs` — hand-rolled, 204-validated extraction.
- `GamePolish.Core/Sources/AddonFolderName.cs` — folder-name derivation from the `.toc`.
- `SourceAcquisitionException` (+ `GitUnavailableException`, `ZipInvalidException`).
- Tests: staged tree shape, version metadata, asserted git argv/environment, ZIP payloads reusing 204's table, and the failure-recovery tests the decision below
  dictates.

## Out of scope
- Moving the staged folder into the game directory — that is `208` (install) and `209` (update).
- Authenticated clones with a PAT (`504`): leave one `SourceRequest.Credential` hook set to `null`, documented as filled in phase 5. Do not implement keychain access
  here.

## Implementation notes
### The open decision — clone-to-temp-then-swap, not clone-in-place
**Decision: stage into the data root, then swap with renames inside the addon root. Never clone or reset in place inside `Interface/AddOns`.**

Failure modes of **clone-in-place** (rejected): (1) a clone interrupted at 90% (network drop, disk full, cancel, git crash) leaves a *partial addon folder inside the
user's game directory*; the loader reads whatever `.toc` exists, so the game can load a half-addon with no error and the fix is "delete this folder by hand". (2) The
leftover is neither the old addon nor the new one, so the pipeline cannot tell failure from success without extra state. (3) `git fetch --depth=1 && git reset --hard
origin/BRANCH` destroys local user edits (users do edit `.toc` and `.lua` files), a partial failure leaves a mixed state no `reset` describes, and recovery exists only
via the backup — so every update must back up the whole addon folder. (4) All of it happens **inside a directory the game reads while running**: the worst place for a
torn state.

Failure modes of **temp-then-swap** (chosen), and how each is handled:
1. Staging consumes disk and can be orphaned on a crash. Handled: staging directories are operation-scoped and swept — any `staging/<opId>` older than 24 h is deleted.
2. The swap is two renames, not one; a crash between them leaves the addon folder *absent*. Handled: the old folder is moved aside (not deleted) to a sibling inside the
  addon root, and a startup sweep restores a stranded `.game-polish-old-*` when the matching `.game-polish-new-*` never landed.
3. The addon root and the data root are usually on different volumes (`D:\OctoWow` vs `C:\Users\...\.game-polish`) and a cross-volume `Directory.Move` is
  copy-plus-delete, not atomic. Handled: the cross-volume copy happens *inside the addon root* as `.game-polish-new-<FolderName>-<ts>/`, and only that directory is
  renamed into place — a failure leaves a leftover, never a partial addon under the real name. Accepted cost: every install copies the tree twice (addons are a few MB;
  do not optimise it away).
4. Naming constraint this creates: an addon folder name must never start with `.game-polish-`. Enforce that in `AddonFolderName`, and sweep stale `.game-polish-*`
  siblings on startup (`213 doctor` reports them).

**Consequence for the failure-recovery tests** (why the decision had to be made here): no test asserts "a failed clone leaves no partial addon" as an *outcome* — the
clone never touches the game folder. Instead: **a failure at any point before the swap leaves the game directory byte-identical** (recursive hash of the addon root
before/after). The swap forces three new tests: (a) a failure while copying into `.game-polish-new-*` leaves the original addon intact plus a leftover; (b) a failure
after the old folder moved aside but before the new one landed leaves it recoverable as `.game-polish-old-*`, and the startup sweep restores it; (c) a successful swap
leaves no `.game-polish-*` sibling. Because `209` re-stages and swaps instead of `git fetch && reset --hard`, local edits are **not** preserved in place: the pre-update
backup captures them and the update result reports which files differ from the manifest's recorded list. Report that divergence from the design note.

```csharp
public sealed record SourceRequest(string GameId, string SourceUrl, ModSourceType SourceType,
    string? Branch, string? ExpectedFolderName, string DataRoot, string OperationId,
    string? Credential = null);                              // Credential filled by 504
public sealed record StagedSource
{
    public required string StagingDirectory { get; init; }   // <data root>/<gameId>/staging/<opId>/<FolderName>
    public required string FolderName { get; init; }         // addon folder name to create
    public required string Version { get; init; }            // HEAD SHA, or the tag sitting on HEAD
    public required string VersionKind { get; init; }        // "sha" | "tag"
    public required ModSourceType SourceType { get; init; }
    public required string SourceUrl { get; init; }
    public required IReadOnlyList<InstalledFileEntry> Files { get; init; } // relative to FolderName
    public required bool HasGitDirectory { get; init; }      // git sources; 208 excludes .git when copying
}
public interface ISourceAcquirer
{
    Task<StagedSource> StageGitAsync(SourceRequest request, CancellationToken ct);
    Task<StagedSource> StageZipAsync(Stream zip, string zipFileName, SourceRequest request, CancellationToken ct);
    void Cleanup(StagedSource staged);
    int SweepStaleStaging(string gameId, TimeSpan olderThan, string? excludeOperationId);
}
```
**Git invocation — the hardening list is the acceptance criterion.** One process per call via `ProcessStartInfo` with `UseShellExecute = false`, stderr redirected, and
`ArgumentList` (never a concatenated command line — no shell, invariant #2). Install: `git -c core.hooksPath=<empty scratch dir> -c core.symlinks=false -c
core.longpaths=true -c protocol.file.allow=never clone --depth=1 --single-branch --no-tags --no-recurse-submodules --quiet -- <url> <stagingDir>`.
- `core.hooksPath` pointed at an empty directory means a hook can never run (#2); a fresh clone only ever gets `*.sample` files anyway. `core.symlinks=false` checks a
  symlink out as a plain file holding its target path, so a repo cannot plant an escaping symlink (#3, belt and braces with 204). `--no-recurse-submodules` plus
  `protocol.file.allow=never` stops a repo pulling a submodule or a local path.
- Environment for every git call: `GIT_TERMINAL_PROMPT=0`, `GIT_ASKPASS=` (empty), `GIT_LFS_SKIP_SMUDGE=1` (never download LFS payloads — no build steps, #2),
  `GIT_CONFIG_NOSYSTEM=1`, and `GIT_CONFIG_GLOBAL` pointed at an empty file created in the staging scratch (on Windows `/dev/null` does not exist — create a real file).
- `git` may be absent: detect once, cache it, throw `GitUnavailableException` ("Git is required to install from GitHub. Install git and retry."). Never fall back to a
  tarball download (invariant #5).
- Timeout: linked `CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds))` (default 120); on expiry or cancel `process.Kill(entireProcessTree: true)` and
  throw a timeout `SourceAcquisitionException`. Always delete the staging contents on failure — the caller gets no `StagedSource`, so nothing else cleans up.
- Branch: `--single-branch` with no `--branch` takes the remote default (that is the `BRANCH` placeholder); when supplied, validate
  `^[A-Za-z0-9][A-Za-z0-9._/-]{0,100}$` and reject a leading `-` before it reaches git.

**ZIP extraction — `System.IO.Compression.ZipArchive` from the BCL; no extra package. Never `ZipFile.ExtractToDirectory`: it does not enforce these rules.** Validate
the whole entry list through `204.ValidateAll` up front (so case collisions are caught); skip directory entries; reject symlink entries (Unix mode in
`(entry.ExternalAttributes >> 16) & 0xF000 == 0xA000`, or a reparse attribute on Windows-made archives). Zip-bomb caps: reject an entry over 256 MiB, a total over 1
GiB, or a ratio over 200:1 (log the ratio). Write each entry to `<dest>/<name>.part-<guid>`, then `File.Move` into place, then set `entry.LastWriteTime`. Layout: if
exactly one top-level directory exists and no top-level file, treat it as the wrapper and stage its contents; otherwise the archive root is the addon.

**Folder name (`AddonFolderName`).** WoW requires the folder name to match the `.toc` name, so prefer the archive: exactly one `*.toc` at the staged root ⇒ its stem
(several `.toc` files means a multi-addon repo — record that as a known MVP limitation, do not build multi-addon install). Otherwise the repo/file name with `.git`
stripped and a trailing `-master`/`-main` trimmed. `SourceRequest.ExpectedFolderName` (registry or user supplied) wins over both once `204.ResolveSafeDirectoryName`
rejects separators, `..`, reserved names, a trailing dot, and the `.game-polish-` prefix.

## Acceptance criteria
1. Staging a git source yields a 40-char hex SHA, `HasGitDirectory == true`, and a game directory whose recursive hash is unchanged.
2. The recorded argv includes `--depth=1`, `--single-branch`, `--no-tags`, `--no-recurse-submodules`, `core.hooksPath=`, `core.symlinks=false`,
  `protocol.file.allow=never`, with `UseShellExecute == false`.
3. The child environment contains `GIT_TERMINAL_PROMPT=0` and a `GIT_CONFIG_GLOBAL` pointing at an empty file.
4. `file:///C:/tmp/x`, `ssh://git@host/x` and `git://host/x` are rejected before any process starts; a branch of `--upload-pack=evil` is rejected while `main` and
  `feature/foo` are accepted.
5. A ZIP containing `../escape.txt`, `C:\abs.txt`, `a.txt:ads`, `CON` and a duplicate-case pair fails with the corresponding 204 reasons and writes nothing outside the
  staging directory.
6. Folder-name derivation: single-`.toc` archive yields its stem; `https://github.com/brues-code/pfUI.git` yields `pfUI`; an `ExpectedFolderName` of `.game-polish-x` or
  `a/b` is rejected.
7. Cancelling 100 ms in leaves no staging directory, throws `SourceAcquisitionException`, and the game directory is untouched.
8. `SweepStaleStaging` removes a 30 h old directory and keeps a 1 h old one.

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~SourceAcquirer|FullyQualifiedName~ZipExtractor|FullyQualifiedName~AddonFolderName"
grep -rn "Process.Start\|ProcessStartInfo" GamePolish.Core/ | grep -v "Sources/GitRunner.cs"   # expect: none
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" install --game octowow --source https://github.com/brues-code/pfUI
ls "$TMPDIR/gp/octowow/staging"     # expect: empty afterwards
```
## References
- `../game-polish-notes/docs/games/octowow.md` § *Addon Layout*, § *Seed Registry* (real URLs to test against)
- `000-CONVENTIONS.md` § Hard invariants (#2, #3, #4, #5); skill `game-polish-development` § *OctoWoW Specifics*
- Related: `201`, `204`, `208` (performs the swap), `209` (re-stages), `504`, `213`.
