---
id: 213
title: Core CLI smoke harness
phase: 2
bucket: mvp
depends_on: [201, 202, 203, 205, 206, 208, 209, 210, 211, 212]
blocks: [405]
status: todo
area: core
---

# 213 — Core CLI smoke harness
## Objective
A dev-only console project, `GamePolish.Cli`, that drives `GamePolish.Core` end to end with no UI: add a game, install, update, uninstall, enable, disable, list
backups, restore. It exists so the engine can be exercised for real before the Avalonia shell exists, and it is the tool task `405` uses for manual acceptance. It never
ships.

## Context
Greenfield repo. Prerequisites: all of `201`–`212` — this project is the composition root, so it wires every piece and therefore depends on all of them. Phase 2 order:
**201/202/203 first; 204 and 205 are the two tasks everything else leans on; then 206–210; then 211–213** — this is the last task of the phase.

Why it exists, in the plan's own words: the MVP is "an OctoWoW addon manager that cannot lose your data", and `000-CONVENTIONS.md` § Evidence rules says a claim without
a run is the failure mode the plan is written to prevent. Until the Desktop UI (phase 3) can install something, this is the only way to produce that run.

Restated invariants this project must not weaken: **(#1)** every mutation creates its backup first — the CLI calls the pipelines and must never reach around them to
`File`/`Directory` calls of its own; **(#4)** the only roots it touches are the data root and the game install directory; **(#2)** it never executes mod content.

Design-note context: `../game-polish-notes/docs/games/octowow.md` § *Install Path* (Brandon's install is `D:\OctoWow`, verify `WoW.exe`), § *Addon Layout*, § *Seed
Registry* (real repo URLs to install from). Task `405` consumes this tool; task `403` must not ship it.

## Scope
- `GamePolish.Cli/GamePolish.Cli.csproj` — `net10.0` console, `ProjectReference` to `GamePolish.Core` only.
- `GamePolish.Cli/Program.cs` — command dispatch, no framework; a `switch` over the parsed command is enough.
- `GamePolish.Cli/Commands/*.cs` — one small class per command group.
- `GamePolish.Cli/Composition.cs` — the one place the real implementations are constructed and passed in (`GamePolishPaths`, `FileOps`, `GamesRegistry`,
  `GameDescriptorLoader`, `PathValidator`, `BackupEngine`, `ManifestStore`, `SourceAcquirer`, `RegistryService`, the three pipelines, `FileOperationLog`).
- Tests: a `dotnet test` project is **not** required for the CLI itself; the acceptance below is a run script, but add one test that builds the command parser and
  asserts the exit-code contract.

## Out of scope
- Any packaging, publishing profile or CI artifact for this project — and no reference to it from `GamePolish.Desktop` (`403` proves that).
- Tab completion, config files, interactive prompts, a TUI. Commands are one-shot with flags; when a required argument is missing, print usage and exit `2`.
- Anything the UI owns: toasts, progress rendering (print the `OperationProgress` phases as plain lines), theming.
- New Core behaviour. If a command needs something Core cannot do, that is a finding for the owning task, not an excuse to put logic in `Program.cs` — the largest
  allowed command body is argument parsing plus one call.

## Implementation notes
```text
GamePolish.Cli [--data-root <path>] [--json] [--log-level <debug|information|warn|error>] <command> [args]
  add-game <gameId> --path <installDir>          # 203
  list-games                                     # 203 (marks unresolved install paths)
  remove-game <gameId>                           # 203
  descriptors                                    # 202: list loadable descriptors, their destination, any errors
  list --game <gameId>                           # 206: installed mods, enabled state, version
  install --game <gameId> --source <repoUrl> [--branch <name>] [--name <display>] [--mod <id>] [--overwrite]
  install --game <gameId> --zip <pathToZip> [--name <display>]
  update --game <gameId> --mod <id>
  check --game <gameId> [--mod <id>]             # 209: prints the UpdateAvailability state
  uninstall --game <gameId> --mod <id>
  enable --game <gameId> --mod <id>
  disable --game <gameId> --mod <id>
  backup list --game <gameId>                    # 205: timestamp, trigger, source+version, files, size
  backup restore --game <gameId> --timestamp <ts>
  registry refresh --game <gameId>               # 211: force refresh, print mod count + last_fetched
  doctor                                         # environment report; see below
```
- `--data-root` sets `GamePolishPaths.Resolve(overrideRoot)` — this is how `405` runs the whole acceptance pass against a temp directory without touching
  `~/.game-polish`. Precedence stays as `201` defined it (argument > `GAMEPOLISH_DATA_ROOT` > default), and the CLI must print the resolved root as its first line of
  output so a transcript proves which root was used.
- Output: human-readable by default (one line per fact, no colours when `NO_COLOR` is set or stdout is redirected). With `--json`, print the `OperationResult` (212) as
  JSON to stdout and nothing else, so `405` can assert on fields. Diagnostics go to stderr, never mixed into `--json` stdout.
- Exit codes: `0` success, `1` the operation failed (`OperationStatus.Failed`), `2` usage error (unknown command, missing/duplicate argument), `3` environment error
  before the operation starts (data root not writable, no git on PATH, the game's install path missing). `Cancelled` from Ctrl-C is `130`. Document the table in
  `--help` and assert it in the parser test.
- `install --zip` must stream the file into `ISourceAcquirer.StageZipAsync` — do not read the whole ZIP into memory to hand it a `byte[]`.
- `doctor` prints, without mutating anything: the resolved data root and whether it is writable; whether `git` is on PATH and its version; each descriptor's id,
  destination and validation status; each tracked game and whether its install path and verify file still resolve; the count of `staging/` leftovers and the newest
  backup timestamp per game; the current log file path. Exit `3` if any of those is broken. This is the first thing to run when a user reports a problem, so it must
  never write.
- Ctrl-C: install a `Console.CancelKeyPress` handler that cancels a linked `CancellationTokenSource` and lets the pipelines run their rollback. Do not
  `Environment.Exit` from the handler.
- **Exclusion from shipped artifacts — state all three mechanisms, and implement the first two:**
1. A separate project that `GamePolish.Desktop` does not reference. An unreferenced project is never in the Desktop's build or publish output; that is the primary
  mechanism.
2. `<IsPackable>false</IsPackable>` in `GamePolish.Cli.csproj`, so it can never be pushed as a NuGet package by a future `dotnet pack` of the solution.
3. The publish profiles/release workflow of `403` publish `GamePolish.Desktop` explicitly (`dotnet publish GamePolish.Desktop -c Release -r win-x64` / `-r osx-arm64`)
  and `403` must assert the produced artifact tree contains no `GamePolish.Cli.dll` / no `GamePolish.Cli` executable. Add that assertion to the `403` work order when
  you touch it, and say so in your report.

## Acceptance criteria
1. The full happy path runs against a temp data root and a temp fake game directory containing `WoW.exe`: `add-game` → `install --source <repo>` → `list` (one mod,
  version printed) → `disable` → `enable` → `backup list` (a `Mod install` event plus `EnableDisable` events) → `uninstall` → `list` (empty).
2. Every one of those steps exits `0`; a deliberate failure (`install --source https://github.com/nope/nope`) exits `1` and prints a `Message` with no stack trace
  unless `--log-level debug`.
3. `--json` output for each command parses as an `OperationResult` and contains `status`, `code`, `message`; the failure case has `code: "git_failed"` (or the mapped
  code) — assert the exact JSON key spelling (snake_case, per `212`/`201`).
4. No command writes outside the data root and the game install directory: after the whole run, `git status --short` in a temp scratch repo is empty and a recursive
  listing of the fake game directory contains only `Interface/AddOns/...` plus whatever the fixture put there.
5. `--data-root` is honoured and printed; running the same sequence with a second temp root starts from an empty library rather than reusing the first.
6. `doctor` exits `0` on a healthy fixture and `3` when the verify file is deleted, and never creates or modifies any file (assert by directory hash before/after).
7. Ctrl-C during a large install (use a repo with many files, or throttle with a fake `IFileOps`) exits `130`, leaves the addon root byte-identical, and leaves one
  backup behind.
8. `GamePolish.Desktop` has no reference to `GamePolish.Cli`: `dotnet list GamePolish.Desktop reference` shows only `GamePolish.Core`, and a grep of
  `GamePolish.Desktop.csproj` for `Cli` finds nothing.
9. `GamePolish.Cli.csproj` sets `<IsPackable>false</IsPackable>` and `dotnet pack` on the solution does not produce `GamePolish.Cli.*.nupkg`.

## Verification
```bash
dotnet build GamePolish.Cli && dotnet test GamePolish.Core.Tests   # from the repo root; paste real counts
TMP="$(mktemp -d)"; GAME="$TMP/OctoWow"; mkdir -p "$GAME" && : > "$GAME/WoW.exe"
CLI="dotnet run --project GamePolish.Cli --"
$CLI --data-root "$TMP/gp" add-game octowow --path "$GAME"
$CLI --data-root "$TMP/gp" install --game octowow --source https://github.com/brues-code/pfUI
$CLI --json --data-root "$TMP/gp" disable --game octowow --mod pfUI     # expect exit 0, JSON on stdout only
$CLI --data-root "$TMP/gp" backup list --game octowow                   # expect the Mod install event
$CLI --data-root "$TMP/gp" uninstall --game octowow --mod pfUI
ls "$GAME/Interface/AddOns"                                             # expect: empty
$CLI --data-root "$TMP/gp" doctor
dotnet list GamePolish.Desktop reference | grep -i cli                  # expect: no output
rm -rf "$TMP"
```
Paste the actual command output (or the reason a step could not run — e.g. no network) into the report. A summary without a transcript does not satisfy
`000-CONVENTIONS.md` § Evidence rules.

## References
- `000-CONVENTIONS.md` § Definition of done, § Evidence rules, § Hard invariants (#1, #2, #4)
- `../game-polish-notes/docs/games/octowow.md` § *Install Path*, § *Addon Layout*, § *Seed Registry*
- `../game-polish-notes/docs/views/backups.md` § *Restore Flow* (`backup restore` mirrors it)
- Related tasks: `201`–`212` (every component it wires; `212` supplies `OperationResult`, which `--json` prints), `403` (must exclude this project from the published
  artifact — add the assertion there), `405` (manual acceptance uses this CLI).
