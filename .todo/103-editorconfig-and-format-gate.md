---
id: 103
title: .editorconfig and the format gate
phase: 1
bucket: mvp
depends_on: [101]
blocks: [104]
status: todo
area: ops
---

# 103 — .editorconfig and the format gate

## Objective

An enforced, non-drifting code style committed before any feature code exists: a tuned `.editorconfig`
plus the nullable / warnings-as-errors posture, such that `dotnet format --verify-no-changes` exits 0
on the clean tree and non-zero the moment a file drifts. Task `104` wires that command into CI as a
required check; this task is what makes it passable.

## Context

- The only C# in the repo today is template output from task `101`. Style is free to fix now and
  expensive to retrofit: once phase 2 lands, a formatting pass rewrites files nobody touched and buries
  the real change inside an unrelated diff.
- `dotnet format --verify-no-changes` is a required CI gate — job `format` in
  `../game-polish-notes/docs/ci-release.md`, "Workflow 1 — CI". It exits `0` when clean and `2` when
  any file would be rewritten (verified on this machine with SDK 10.0.401), so it needs no extra flag
  to fail a build.
- `dotnet format` reads `.editorconfig`. MSBuild concerns — nullable, warnings-as-errors — are not
  `.editorconfig` keys and need `Directory.Build.props` instead.
- The `.editorconfig` is generated on Windows here while CI's `format` job runs on `ubuntu-latest`.
  Resolve the line-ending question explicitly (step 5) rather than leaving it accidental.
- `dotnet format` at the repo root resolves the solution created in `101`; without one it has nothing
  to format.

## Scope

- `.editorconfig` at the repo root, generated with `dotnet new editorconfig` and then tuned.
- `Directory.Build.props` at the repo root carrying the nullable, warnings-as-errors and
  code-style-in-build posture for every project.
- One normalisation pass so the existing tree satisfies the file it must now obey.
- The escalated severities this repo will actually honour: file-scoped namespaces, underscore-prefixed
  private fields, and a small deliberate analyzer set.
- A documented `--include` workflow so day-to-day formatting never touches unrelated files.

## Out of scope

- Any change to `ci-release.md` or to any workflow file — the gate's wiring is task `104`.
- Adding analyzer packages (StyleCop, Roslynator, Meziantou, and so on). None are pinned, and each one
  is its own decision with its own noise budget.
- Bringing generated or vendored content into the gate; exclude it instead if any ever appears.
- Per-project test conventions beyond what falls out of the shared props file.

## Implementation notes

1. `dotnet new editorconfig` from the repo root. Verified on 10.0.401: it writes a 389-line file
   beginning with `root = true`, with CRLF line endings because it was generated on Windows.
2. **Do not re-add what the template already sets** — read the file before editing it:
   `indent_style = space` / `indent_size = 4` for `[*.cs]`;
   `csharp_style_namespace_declarations = file_scoped:suggestion`; a full naming-rules block including
   `dotnet_naming_rule.private_fields_should_be__camelcase` bound to the `_camelcase` style, plus
   `s_camelcase` for private static fields; and a large set of `dotnet_style_*` / `csharp_style_*`
   preferences. The generated file contains **no** `dotnet_diagnostic.*` lines at all.
3. **The day-one trap.** `[*.cs]` carries `insert_final_newline = false`, while SDK templates emit files
   ending in a trailing newline. Verified: immediately after `dotnet new editorconfig`,
   `dotnet format --verify-no-changes` exits **2** with `error FINALNEWLINE: Fix final newline` against
   the `Class1.cs` files created in `101`. Run `dotnet format` **once, immediately**, and commit that
   normalisation — otherwise CI's `format` job is red on its first run and every later agent inherits
   the failure. Verified sequence: exit 2 → `dotnet format` → exit 0.
   Decide `insert_final_newline` explicitly (keep `false`, or set `true` if you want POSIX-style
   trailing newlines) and make the tree match what the file says.
4. **Escalate the rules you will actually obey.** Each is an edit to the generated file:
   `csharp_style_namespace_declarations = file_scoped:warning` for file-scoped namespaces;
   `dotnet_naming_rule.private_fields_should_be__camelcase.severity = warning` for underscore-prefixed
   private fields. A `suggestion` never fails anything; `warning` surfaces the rule in `dotnet build`
   and in `dotnet format` once `EnforceCodeStyleInBuild` is on. Add `dotnet_diagnostic.<ID>.severity`
   entries only for a small set you intend to honour — a large error-severity block turns the first real
   commit red for reasons unrelated to it.
5. **Line endings.** The generated file leaves `end_of_line` unset; verified on Windows that CRLF files
   stay CRLF and only the final newline changed. Because the gate runs on Linux, set it explicitly —
   `end_of_line = lf` for `[*]` plus a `.gitattributes` containing `* text=auto eol=lf` — or verify the
   gate against a Linux checkout before trusting it. The goal is identical bytes on both machines.
6. **Nullability and the warnings posture.** Add a root `Directory.Build.props` setting
   `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and
   `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`. The build is at 0 warnings today (task
   `101`), so a warning arriving with new code should fail the PR that brought it. Note the interaction:
   `TreatWarningsAsErrors` together with `EnforceCodeStyleInBuild` turns every rule you escalated in
   step 4 into a build failure as well. That is the point, but escalate deliberately. If an SDK upgrade
   later floods warnings, suppress the specific IDs with `<WarningsNotAsErrors>` rather than dropping
   the posture.
7. **Day-to-day formatting.** A bare `dotnet format` formats the whole tree and will rewrite files your
   change never touched. Scope it: `dotnet format --include GamePolish.Core/Backup/BackupEngine.cs`, or
   widen to a folder with `--include GamePolish.Core`. `--exclude`, `--severity`, `--no-restore` and
   `--verify-no-changes` are the other useful switches (`dotnet format --help` lists them). Once step
   3's normalisation is committed, whole-tree runs are safe again; the `--include` habit is what stops
   a change from carrying unrelated churn.
8. Commit with a message naming the task: `chore(103): editorconfig and format gate`.

## Acceptance criteria

- `dotnet format --verify-no-changes` exits 0 on the clean tree.
- The same command exits non-zero (2) when a deliberately misformatted file is present.
- `.editorconfig` is committed with `root = true` and explicitly states the chosen
  `insert_final_newline` value and the escalated namespace/naming severities.
- `Directory.Build.props` exists and sets the nullable and warnings-as-errors posture; no project sets a
  conflicting value for the same property.
- `git diff --stat` for the change touches `.editorconfig`, `Directory.Build.props`, `.gitattributes` if
  it was added, and the one-time formatting of existing files — nothing unrelated.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish

# 1. the clean tree must pass
dotnet format --verify-no-changes ; echo "exit=$?"          # exit=0, no output

# 2. force a failure with a temporary misformatted file, then remove it
printf 'namespace GamePolish.Core;\npublic class   Probe{public int   X  =1;}\n' > GamePolish.Core/Probe.cs
dotnet format --verify-no-changes ; echo "exit=$?"          # exit=2, error WHITESPACE naming Probe.cs
rm GamePolish.Core/Probe.cs
dotnet format --verify-no-changes ; echo "exit=$?"          # back to exit=0

# 3. the posture is in effect
dotnet build ; echo "exit=$?"                               # 0 Warning(s), 0 Error(s), exit=0
```

Capture `$?` directly after the command: piping into `tail` reports the last pipeline stage's status and
hides the real one. Expected exit codes verified on SDK 10.0.401 — `0` clean, `2` changes needed.

## References

- `../game-polish-notes/docs/ci-release.md` — "Workflow 1 — CI", job `format`.
- `.todo/000-CONVENTIONS.md` — "Definition of done" (formatting must satisfy `dotnet format
  --verify-no-changes`); "Pitfalls that have already bitten this repo" item 3 (repo-wide
  `dotnet format`).
- `.todo/104-ci-workflow.md` — the job that runs this gate.
- Follows `101`; blocks `104`. The test projects from `102` inherit the same gate.
