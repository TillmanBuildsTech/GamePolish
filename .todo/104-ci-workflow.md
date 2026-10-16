---
id: 104
title: CI workflow
phase: 1
bucket: mvp
depends_on: [101, 102, 103]
blocks: [404]
status: todo
area: ops
---

# 104 — CI workflow

## Objective

`.github/workflows/ci.yml` implementing exactly the three jobs the design notes specify — `format`,
`build-and-test`, `build-check-desktop` — with every job resolving the SDK from `global.json` and every
action pin carrying its reason, plus the branch-protection note and the recorded `nightly.yml` decision.

## Context

- The spec is `../game-polish-notes/docs/ci-release.md`, section **"Workflow 1 — CI"**: the trigger, the
  three jobs and their steps, the runner/RID matrix, and the status-check requirement. Implement it
  literally; where this file and that note disagree, the note wins and this file is corrected in the same
  change. `.github/` does not exist yet, and the note's "Repository Structure (CI-relevant)" also expects
  `release.yml` and an optional `nightly.yml` — this task creates `ci.yml` only.
- **Every job starts with `actions/setup-dotnet` taking the version from `global.json`.** Without a version
  input the action takes the runner's preinstalled SDK, which drifts; with it, local and CI build the same
  `10.0.401` band, and `rollForward: latestFeature` lets a newer 10.0.x satisfy the pin.
- **No secret is needed**, and macOS Intel is out of scope: `osx-arm64` only, with `macos-latest` being
  arm64, and the note's secrets table being entirely release-time (signing certificates, Apple IDs).
- `build-and-test` step 4 is **VSTest syntax**:
  `dotnet test --no-build --logger "trx;LogFileName=results.trx"`. If task `102` selected
  Microsoft.Testing.Platform, `--logger` is silently ignored (warn-only, MTP0001) and produces no TRX; the
  MTP form is `--report-trx --report-trx-filename results.trx`, with
  `Microsoft.Testing.Extensions.TrxReport` referenced by each test project.

## Scope

- `.github/workflows/ci.yml`: the note's trigger and exactly three jobs, in the note's order.
- `format`: `ubuntu-latest`; checkout, setup-dotnet, `dotnet format --verify-no-changes`.
- `build-and-test`: `ubuntu-latest`; checkout, setup-dotnet, `dotnet restore`,
  `dotnet build --no-restore -c Debug`, the test run with TRX, artifact upload.
- `build-check-desktop`: a two-leg matrix (`windows-latest`/`win-x64`, `macos-latest`/`osx-arm64`) running
  checkout, setup-dotnet, `dotnet restore`, and
  `dotnet build GamePolish.Desktop -c Release -r <RID> --no-self-contained`.
- A comment on every `uses:` line: which action, which major, why.
- The branch-protection / required-status-check note, and the `nightly.yml` decision (recorded, **not**
  created).

## Out of scope

- `release.yml`, all packaging, signing and tagging (`403`, `404`), and `nightly.yml` itself — the note
  marks it optional, so this task records the decision without creating the file.
- Caching (the NuGet restore cache), concurrency control, test-result dashboarding, and any change to
  `.editorconfig` (task `103`) or the runner choice (`102`) made from inside the workflow file.

## Implementation notes

1. **Triggers, verbatim:** push to `main` and pull requests targeting `main`; add no tags or schedules the
   note does not list.
2. **Pins, each with its reason as a YAML comment:**
   - `actions/checkout@v7` — current major.
   - `actions/setup-dotnet@v6` with `global-json-file: global.json`. That input exists in the action's own
     `action.yml` and is what makes CI use the pinned SDK rather than whatever the runner ships; v6 is the
     current major (v5 moved to node24, v6 moved internals to ESM with no input changes). Do not also pass
     `dotnet-version` — two sources of truth is the drift this prevents.
   - `actions/upload-artifact@v7` — one upload only: since v4 an artifact name cannot be written twice, and
     a job is capped at ten artifacts.
3. **`format`.** After setup-dotnet, exactly `dotnet format --verify-no-changes`. It needs the solution from
   `101` and exits non-zero by itself when a file would change — verified here: `0` clean, `2` changes
   needed. No `|| exit 1` wrapper; the step's exit code already is the gate.
4. **`build-and-test`.** The note's five steps, in order, with nothing between them. The details that make
   them work:
   - The VSTest TRX reporter writes into a `TestResults` folder beside each test project, so the upload
     `path:` must glob across projects: `**/TestResults/*.trx`, with `if-no-files-found: error`.
   - Give the upload `if: always()` so a failing test run still publishes its TRX — otherwise the report
     you need most is the one you never get.
   - If `102` chose MTP, swap the test arguments per Context; without
     `Microsoft.Testing.Extensions.TrxReport` in both test projects the TRX option is rejected, not ignored.
5. **`build-check-desktop`.** Matrix `include` with exactly two entries —
   `{os: windows-latest, rid: win-x64}` and `{os: macos-latest, rid: osx-arm64}` — with
   `runs-on: ${{ matrix.os }}`. Keep the note's shape: step 2 is a plain `dotnet restore` and step 3 has
   **no** `--no-restore`, because a RID-specific build needs RID-specific assets and step 3's implicit
   restore supplies them; adding `--no-restore` breaks the job on a clean runner. Set `fail-fast: false`
   so a Windows failure still reports whether macOS compiles. This proves compilation per platform only —
   packaging is `403`.
6. **Status checks.** After the first green run on `main`, make all three jobs required status checks in
   branch protection: `format`, `build-and-test`, and both legs of `build-check-desktop` (a matrix job
   surfaces one check per leg, named with its matrix values — read the exact names off the first run's
   check list). Branch protection is not expressible in YAML, so state the setting in the commit message
   and in the task report.
7. **`nightly.yml` — decide, do not create.** The note marks Workflow 3 optional: a scheduled
   `dotnet publish` on both platform runners, plus an auto-opened issue tagged `nightly-failure` via
   `actions/github-script`. Default position: **defer** — there is no release pipeline to mirror yet
   (`release.yml`, task `403`), and an unsigned nightly publish exercises nothing PR CI does not. Revisit
   when `403` lands or a time-dependent failure appears that PR CI cannot catch; if created later it is its
   own work order, not another job bolted onto `ci.yml`.
8. Commit as `ci(104): CI workflow`. If `102` chose MTP, update `ci-release.md` step 4 and its note in the
   same commit — `000-CONVENTIONS.md`'s definition of done requires that for CI/release config.

## Acceptance criteria

- `.github/workflows/ci.yml` exists and parses as YAML.
- Job names are exactly `format`, `build-and-test`, `build-check-desktop`; the trigger matches the note's
  push/PR-on-`main` pair, and the desktop matrix has exactly two legs with no macOS Intel entry.
- Every job's first step after checkout is `actions/setup-dotnet` resolving the version from `global.json`
  — no hardcoded `dotnet-version` anywhere.
- `build-and-test`'s steps match the note's five, one-for-one and in order, with the TRX argument matching
  the runner chosen in `102`.
- Every `uses:` carries a comment stating the reason for that pin; no `secrets.*` reference appears.
- `.github/workflows/nightly.yml` does not exist, and the decision not to create it is written down.
- If `102` chose MTP, `ci-release.md` is updated in the same commit.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish

# YAML parse. python 3.14.6 is present here but pyyaml is NOT installed; install it once:
python -m pip install --user pyyaml
python -c "import yaml; d=yaml.safe_load(open('.github/workflows/ci.yml', encoding='utf-8')); print(list(d['jobs']))"
# expected: ['format', 'build-and-test', 'build-check-desktop']
# note: pyyaml parses the unquoted on: key as the boolean True — read the trigger as d[True].

# spec coverage, one line per argument the note requires
grep -n 'verify-no-changes\|dotnet restore\|build --no-restore -c Debug\|trx\|--no-self-contained\|matrix.rid' .github/workflows/ci.yml
```

Then the authoritative check: push the branch and let GitHub parse it — a malformed workflow reports
`Invalid workflow file … (Line: N, Col: M)` and registers no run, while `gh workflow list` should show `CI`
(`gh` is 2.95.0 here). Three green jobs on a trivial PR is the end-to-end proof.

## References

- `../game-polish-notes/docs/ci-release.md` — "Workflow 1 — CI" (trigger, `format`, `build-and-test`,
  `build-check-desktop`, "Status Checks"); "SDK & Runtime Pinning"; "Workflow 3 — Nightly".
- `.todo/000-CONVENTIONS.md` — "Definition of done" (CI / release config row); "Evidence rules".
- `CLAUDE.md` — "Pinned Versions"; "Open decision — `dotnet test` runner".
- Follows `101`, `102`, `103`. Related: `403` (packaging) and `404` (tagging), which reuse these pins.
