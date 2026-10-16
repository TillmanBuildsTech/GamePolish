---
id: 102
title: Decide the test runner and create the test projects
phase: 1
bucket: mvp
depends_on: [101]
blocks: [104, 308]
status: todo
area: ops
---

# 102 — Decide the test runner and create the test projects

## Objective

Settle the `dotnet test` runner question for good, then stand up `GamePolish.Core.Tests` and a
headless-UI-capable `GamePolish.Desktop.Tests`, each with one real passing test and both in
`GamePolish.sln`, so that `dotnet test` from the repo root runs both projects and reports real pass
counts. The decision is recorded in writing, the test projects are wired consistently with it, and the
CI test invocation in the design notes matches it.

## Context

Read this section in full before running anything: the choice made here constrains every test project
the product will ever have.

- The .NET 10 SDK runs tests with either **VSTest** (the default) or **Microsoft.Testing.Platform
  (MTP)**. The runner is selected in `global.json`: `{"test": {"runner": "Microsoft.Testing.Platform"}}`.
  `VSTest` is also a valid value and is the default, so choosing VSTest means *no* `global.json` change
  at all. MTP mode requires MTP 1.7 or later.
- **The switch is all-or-nothing.** In Microsoft's words: "In a solution with MTP enabled via
  global.json, every test project must use Microsoft.Testing.Platform. Having any VSTest projects will
  prevent those projects from being able to run." One v2 project added later blocks the whole run.
- **xUnit v2 cannot run under MTP.** `dotnet new xunit` produces v2 — verified on this machine:
  `xunit` 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4,
  `coverlet.collector` 6.0.4. So choosing MTP forces *both* test projects onto xUnit v3.
- **xUnit v3 is mandatory regardless of the runner** for the Desktop tests: `Avalonia.Headless.XUnit`
  12.x depends on `xunit.v3.extensibility.core` (`CLAUDE.md`, "Test projects — the xUnit version
  trap"). xUnit v3 opts into MTP with `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`.
- The v3 templates are **not installed here**: `dotnet new list xunit` shows only the v2 `xunit`
  template. Get v3 with `dotnet new install xunit.v3.templates`, which provides the short names
  `xunit3` and `xunit3-extension`.
- `xunit.runner.visualstudio` 3.1.4+ runs xUnit v1/v2/v3 under VSTest — which is why using v3 does not
  by itself force MTP.
- **TRX is the coupling point.** `../game-polish-notes/docs/ci-release.md`, "Workflow 1 — CI",
  `build-and-test` step 4 is `dotnet test --no-build --logger "trx;LogFileName=results.trx"` — VSTest
  syntax. Under MTP that argument is silently ignored (MTP0001 warns, it does not fail), so the job
  would produce no TRX at all. The MTP form is `--report-trx --report-trx-filename results.trx`, and
  each test project must reference `Microsoft.Testing.Extensions.TrxReport`.

**This work order does not pick a side.** The agent executing it must decide, write the decision and
its reason down, and wire it consistently. The two options as they stand:

| | VSTest (default) | MTP |
|---|---|---|
| `global.json` | unchanged | `{"test": {"runner": "Microsoft.Testing.Platform"}}` |
| `GamePolish.Core.Tests` | the v2 template is fine | must be xUnit v3 |
| TRX | `--logger "trx;LogFileName=results.trx"`, exactly as the notes already write it | `--report-trx --report-trx-filename results.trx` + `Microsoft.Testing.Extensions.TrxReport` |
| Cost of being wrong later | current default, still supported | one v2 project anywhere blocks the run; the migration is all-or-nothing |

Record the decision in three places: replace the "Open decision — `dotnet test` runner" paragraph in
`CLAUDE.md` with the chosen value and the reason; if MTP is chosen, update `ci-release.md` step 4 and
the note beneath it in the same commit (the definition of done requires it for CI/release config); and
name the choice in the commit message for this task.

## Scope

- `GamePolish.Core.Tests` — xUnit (v2 template, or v3 if MTP is chosen), one passing test, added to the
  solution, referencing `GamePolish.Core`.
- `GamePolish.Desktop.Tests` — xUnit v3 (mandatory), with `Avalonia.Headless.XUnit` referenced so the
  project is headless-capable, one passing test, in the solution, referencing `GamePolish.Desktop`.
- The runner decision and its wiring: the `global.json` edit and/or the
  `UseMicrosoftTestingPlatformRunner` property, applied consistently across both test projects.
- The decision record in `CLAUDE.md`, plus `ci-release.md` if MTP wins.

## Out of scope

- Any real Avalonia UI test harness — that is task `308`. One smoke test here, nothing more.
- Coverage thresholds, coverlet configuration, test-report dashboards.
- Changing the CI job structure: the `format` / `build-and-test` / `build-check-desktop` shape belongs
  to task `104` and to `ci-release.md`.
- Touching production code in `GamePolish.Core` or `GamePolish.Desktop` beyond what the builder needs.

## Implementation notes

1. `dotnet new xunit -o GamePolish.Core.Tests -n GamePolish.Core.Tests` for the v2 path. Under MTP,
   run `dotnet new install xunit.v3.templates` once and then
   `dotnet new xunit3 -o GamePolish.Core.Tests -n GamePolish.Core.Tests`.
2. `dotnet new xunit3 -o GamePolish.Desktop.Tests -n GamePolish.Desktop.Tests` after installing the v3
   templates. If you would rather not install a template package, scaffold with the v2 template and
   swap the package references to the xUnit v3 ones — the requirement is v3, not the template.
3. References:
   `dotnet add GamePolish.Core.Tests reference GamePolish.Core`
   `dotnet add GamePolish.Desktop.Tests reference GamePolish.Desktop`
4. `dotnet add GamePolish.Desktop.Tests package Avalonia.Headless.XUnit`, then confirm the resolved
   version is a 12.x line matching the Avalonia pin. Do not hand-pick an 11.x version.
5. One `[Fact]` per project, asserting something trivially true about the assembly it references.
   Delete the template's generated sample test as you add yours — leaving the sample in place inflates
   the pass count without testing anything, which is exactly the kind of number this plan distrusts.
6. `dotnet sln add GamePolish.Core.Tests GamePolish.Desktop.Tests`
7. If VSTest wins, change nothing else. If MTP wins: add the `test.runner` key to `global.json`, put
   `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` in a root
   `Directory.Build.props` (so a future test project cannot silently drift off the platform), add
   `Microsoft.Testing.Extensions.TrxReport` to both test projects, and update `ci-release.md` step 4.
8. Run `dotnet test` from the repo root. Both projects must execute. A runner misconfiguration usually
   shows up as one project refusing to run rather than as a failing test, so read the output for the
   project count, not just the exit code.

## Acceptance criteria

- `dotnet test` from the repo root executes both test projects, reports real pass counts, and exits 0.
- `dotnet sln list` shows both test projects.
- The runner is stated in `CLAUDE.md` with its reason, and the "Open decision" framing is gone.
- If MTP was chosen, neither test project references `xunit` 2.x, both are xUnit v3, and `global.json`
  carries the `test.runner` key.
- `GamePolish.Desktop.Tests` references `Avalonia.Headless.XUnit` 12.x.
- If MTP was chosen, `ci-release.md` step 4 uses `--report-trx --report-trx-filename results.trx` and
  the note beneath it states the `Microsoft.Testing.Extensions.TrxReport` requirement.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet test ; echo "exit=$?"        # both test projects run, 0 failed, exit=0
dotnet sln list                     # six projects: four source, two test
```

The summary format depends on the runner: VSTest prints `Passed!  - Failed: 0, Passed: N, Skipped: 0,
Total: N`; MTP/xUnit prints a `Test run summary: Passed!` block. Paste whichever you get into the task
report verbatim — `000-CONVENTIONS.md` requires real output, not a claim that tests pass.

## References

- `../game-polish-notes/docs/ci-release.md` — "Workflow 1 — CI", `build-and-test` step 4 and the
  sentence beneath it about Microsoft.Testing.Platform.
- `CLAUDE.md` — "Open decision — `dotnet test` runner"; "Test projects — the xUnit version trap".
- `.todo/000-CONVENTIONS.md` — "Stack, pinned" (the Runner row); "Definition of done" (CI / release
  config); "Pitfalls" item 4.
- Follows `101`. Blocks `104` (CI needs runnable test projects) and `308` (headless UI tests).
