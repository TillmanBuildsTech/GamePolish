---
id: 101
title: Scaffold the solution and projects
phase: 1
bucket: mvp
depends_on: []
blocks: [102, 103, 104]
status: todo
area: ops
---

# 101 — Scaffold the solution and projects

## Objective

From a docs-only repository to a solution that builds: `GamePolish.sln` containing `GamePolish.Core`,
`GamePolish.SDK`, `GamePolish.Service` and `GamePolish.Desktop`, each created from its template and
wired with project references, plus the first commit. At the end, `dotnet build` from the repo root
succeeds with 0 warnings and 0 errors on SDK `10.0.401` targeting `net10.0`.

## Context

- State verified 2026-10-15: the repo root holds `CLAUDE.md`, `global.json`, `.gitignore`, `.claude/`
  and this `.todo/` plan. There is no `.sln`, no `.csproj`, no `.github/`. `git log` reports
  "your current branch 'main' does not have any commits yet" — nothing has ever been committed, so
  this task also produces the initial commit.
- `global.json` already pins `10.0.401` with `rollForward: latestFeature`. **Do not regenerate it**:
  no `dotnet new globaljson`, no version bump. It is the only SDK installed on the dev machine and the
  band-level roll-forward is deliberate (see `CLAUDE.md`, "Pinned Versions").
- `CLAUDE.md` at the repo root carries the stack pins and the scaffolding commands.
  `.todo/000-CONVENTIONS.md` carries the invariants. Neither is re-litigated here.
- **Scaffold `GamePolish.Desktop` last.** It is the one project that consumes Core's contracts; doing
  it after the other three means its references point at real projects instead of imagined ones.
- The authoritative specs live outside the repo at `../game-polish-notes/docs/`. They are deliberately
  not version-controlled: **never `git add` them**, and never copy them into this repo.

## Scope

- `dotnet new sln` → `GamePolish.sln`, with all four projects added to it.
- `dotnet new classlib` → `GamePolish.Core` and `GamePolish.SDK`.
- `dotnet new web` → `GamePolish.Service` (ASP.NET Core Empty, the minimal-API host shape).
- `dotnet new avalonia.mvvm` → `GamePolish.Desktop`, scaffolded last.
- Project references: `GamePolish.Core` is the dependency root; Service, SDK and Desktop each
  reference it.
- `.gitignore` committed as-is. It already exists and is already the `dotnet new gitignore` output
  plus the Claude Code entries; do not regenerate it.
- The first commit, containing the root files and this plan, and no design notes.

## Out of scope

- `GamePolish.Xbox` (WinUI 3 / GDK). It is future work — task `715`.
- Test projects — task `102`. `.editorconfig` and the format gate — task `103`.
  `.github/workflows/` — task `104`.
- Packaging, publishing, tagging — tasks `403`, `404`.
- Any real `GamePolish.Core` logic (phase 2, from task `201`). This task creates template projects
  whose only content is the template's own placeholder file.
- Hand-editing `GamePolish.sln` or any `.csproj`. `dotnet sln add` and `dotnet add reference` do it
  correctly; hand-edits are how solution files get corrupted.

## Implementation notes

Run everything from the repo root, in this order.

1. **Solution.** `dotnet new sln -n GamePolish --format sln`
   Verified on 10.0.401: *without* `--format sln` this template emits `GamePolish.slnx`, the new XML
   solution format. `CLAUDE.md`, `ci-release.md` ("Repository Structure") and this plan all name
   `GamePolish.sln`, so pass the flag.
2. **Core and SDK.** `dotnet new classlib -o GamePolish.Core -n GamePolish.Core`, then the same with
   `GamePolish.SDK`. Each emits `<name>.csproj` and a `Class1.cs` placeholder.
3. **Service.** `dotnet new web -o GamePolish.Service -n GamePolish.Service`. `web` is ASP.NET Core
   Empty — the minimal-API host, using `Microsoft.NET.Sdk.Web`. `webapi` scaffolds controllers and is
   the wrong shape for this project. It emits `Program.cs`, `appsettings.json`,
   `appsettings.Development.json` and `Properties/launchSettings.json`.
4. **Add the three to the solution.**
   `dotnet sln add GamePolish.Core GamePolish.SDK GamePolish.Service`
5. **References, Core as the root.**
   `dotnet add GamePolish.Service reference GamePolish.Core`
   `dotnet add GamePolish.SDK reference GamePolish.Core`
6. **Desktop, last.** `dotnet new avalonia.mvvm -o GamePolish.Desktop -n GamePolish.Desktop`, then
   `dotnet add GamePolish.Desktop reference GamePolish.Core`, then `dotnet sln add GamePolish.Desktop`.
   Use `avalonia.mvvm`, **never `avalonia.xplat`** — xplat adds browser/Android/iOS heads this product
   does not ship.
7. **What the Avalonia template generates** (inventory verified against 12.1.4):
   `App.axaml`, `App.axaml.cs`, `Program.cs`, `ViewLocator.cs`, `app.manifest`,
   `Assets/avalonia-logo.ico`, an empty `Models/` folder, `ViewModels/MainViewModel.cs`,
   `ViewModels/ViewModelBase.cs`, `Views/MainWindow.axaml`, `Views/MainWindow.axaml.cs`.
   Its packages: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`
   (all 12.1.4), `CommunityToolkit.Mvvm` 8.4.2, and `AvaloniaUI.DiagnosticsSupport` 2.2.3 with
   `IncludeAssets`/`PrivateAssets` set to `None`/`All` outside Debug.
8. **DevTools.** Avalonia 12 has no `Avalonia.Diagnostics` package — there is no 12.x release of it.
   DevTools in 12 is `AvaloniaUI.DiagnosticsSupport`, which the template already wires as
   `.WithDeveloperTools()` inside `#if DEBUG` in `Program.cs`. Do not "correct" either name.
9. **Commit.** `git add -A`, then
   `git commit -m "chore(101): scaffold solution and projects"`. Confirm `git status --short` is empty
   afterwards and that nothing from `../game-polish-notes/` was staged.

## Acceptance criteria

- `dotnet build` from the repo root reports `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`.
- `dotnet sln list` lists all four projects.
- `GamePolish.Core/GamePolish.Core.csproj` contains no `PackageReference` and no UI reference — Core
  stays pure .NET per `000-CONVENTIONS.md`.
- `dotnet --version` prints `10.0.401`, and `git diff global.json` is empty.
- No `GamePolish.slnx` exists in the repo root.
- The working tree is clean and `git log --oneline -1` shows this task's commit.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet --version                                    # 10.0.401
dotnet build                                        # Build succeeded.  0 Warning(s)  0 Error(s)
dotnet sln list                                     # the four project paths
grep -iE "avalonia|PackageReference" GamePolish.Core/GamePolish.Core.csproj   # no output, exit 1
git status --short                                  # empty
git log --oneline -1                                # chore(101): scaffold solution and projects
```

`grep` exiting 1 with no output *is* the pass condition for the Core check — do not chain it with
`&&`, because that would treat success as failure.

## References

- `CLAUDE.md` — "Tech Stack → Pinned Versions", "Build & Dev Commands → Scaffolding",
  "Architecture → Directory Layout (planned)".
- `../game-polish-notes/docs/ci-release.md` — "Repository Structure (CI-relevant)", which names
  `GamePolish.sln`, `GamePolish.Core/`, `GamePolish.Desktop/`, `GamePolish.Service/`,
  `GamePolish.SDK/`.
- `.todo/000-CONVENTIONS.md` — "Stack, pinned"; "Pitfalls that have already bitten this repo"
  items 1, 2 and 7.
- Unblocks `102`, `103`, `104`. Later consumers: `201` (first Core types), `308` (headless UI tests),
  `714` (the real SDK package).
