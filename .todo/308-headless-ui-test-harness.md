---
id: 308
title: Headless UI test harness
phase: 3
bucket: mvp
depends_on: [102, 301]
blocks: []
status: todo
area: desktop
---

# 308 — Headless UI test harness

## Objective

The first Avalonia headless test project, and the pattern every other phase-3 task copies. At the end
of this task `dotnet test` from the repo root builds and runs a headless test that boots the shell,
runs layout, and asserts on the shell view model's state — proving this machine can verify UI
behaviour without a display. The definition of done for every desktop task in `000-CONVENTIONS.md`
("a headless UI test") becomes satisfiable.

## Context

- Task `101` created the solution and the `GamePolish.Desktop` project; task `102` created the test
  projects and **settled the `dotnet test` runner** (VSTest vs Microsoft.Testing.Platform). Read
  `102`'s decision before adding packages — the runner choice is all-or-nothing across test projects;
  mixing MTP and VSTest makes `dotnet test` error out.
- Task `301` built the app shell: `Program.cs` with an `AppBuilder`, `App.axaml`, `ViewLocator.cs`,
  `Views/MainWindow.axaml`, `ViewModels/MainWindowViewModel.cs`, the navigation service and the DI
  container. This task depends on that.
- The xUnit major is not a preference: `Avalonia.Headless.XUnit` **12.x** depends on
  `xunit.v3.extensibility.core`, so this test project is **xUnit v3**. Avalonia 11's headless package
  is the v2 one; mixing them fails to compile.
- The integration API (the assembly-level test-application attribute, the `[AvaloniaFact]`-style
  attribute, the headless `AppBuilder` extension and its options type) has changed across Avalonia
  versions. **Confirm every name below against the installed 12.1.4 packages before writing code** —
  names in this file are the 11.x/12.x shape, not a guarantee.
- Inherited rules: a headless test must not touch the user's real `~/.game-polish/` or a real game
  install. Core access in a test is a hand-written fake, or Core's data root redirected to a temp
  directory (`201` owns that override).

## Scope

- New project `GamePolish.Desktop.Tests/` (xUnit **v3**), added to `GamePolish.sln` with
  `dotnet sln add`.
- Package references: `Avalonia.Headless.XUnit` 12.1.4, `Avalonia.Headless` 12.1.4, plus whatever
  xUnit v3 packages the scaffold produced.
- `GamePolish.Desktop.Tests/TestAppBuilder.cs`: an `AppBuilder` factory mirroring
  `GamePolish.Desktop/Program.cs` but calling the headless platform extension.
- `GamePolish.Desktop.Tests/AssemblyInfo.cs` (or equivalent): the assembly-level headless test
  application attribute.
- `GamePolish.Desktop.Tests/Fakes/`: hand-written fakes for the Core interfaces the shell reads
  (proposal names from `301`: `IGameLibrary`, `IGameDescriptorCatalog`, `IDataRoot`) — enough for an
  empty library, a one-game library and a throwing library.
- `GamePolish.Desktop.Tests/ShellTests.cs`: the boot test asserting view-model state, plus a visual
  tree assertion.
- `GamePolish.Desktop.Tests/README.md`: the copyable pattern — how to build a view model with fakes,
  how to instantiate the view, how to run layout, what to assert, and how to make the test fail on
  purpose.

## Out of scope

- Rewriting `GamePolish.Core.Tests` (task `102`'s) to xUnit v3 or to the other runner. Serve the
  chosen runner from `102`; do not convert other projects in this task.
- Test infrastructure for the per-view tests of `302`–`306` — this task ships the pattern and the
  shell test; each view task adds its own tests beside it.
- Visual regression / screenshot diffing, `Avalonia.Headless` + Skia screenshot comparison, and any
  pixel assertions.
- Accessibility automation, automation peers and WinAppDriver-style UI automation.
- CI wiring: `104` owns `.github/workflows/ci.yml`. Adding the test project changes what `dotnet test`
  runs, so **check whether `104`'s workflow needs the new project listed and say so in the report**
  rather than editing CI here.

## Implementation notes

- **Scaffold with the right major, not `dotnet new xunit`.** `dotnet new xunit` scaffolds **xUnit v2**
  (`xunit` + `xunit.runner.visualstudio`, VSTest). It will not satisfy `Avalonia.Headless.XUnit` 12.x,
  which needs `xunit.v3.extensibility.core`; the failure is a compile error in the test project, and
  the fix is not a version bump of the v2 packages. Two ways out:
  - `dotnet new install xunit.v3.templates::4.0.2` and then the v3 template
    (`dotnet new list` shows the short name it registers — confirm before using, e.g. `xunit3`), or
  - `dotnet new xunit` and then **swap the package references** to xUnit v3 by hand
    (`dotnet remove package xunit` / `dotnet add package xunit.v3`), which is the messier path because
    the generated `Usings.cs`, `GlobalUsings` and runner references also change.
  Either way, `dotnet list package` in the test project must show an xUnit **v3** package before you
  write a single test.
- **Runner.** `xunit.runner.visualstudio` **3.1.4+** runs v1/v2/v3 under VSTest, so VSTest stays
  viable for a v3 project. If `102` chose Microsoft.Testing.Platform instead, use the MTP entry point
  for this project too — and never run one project under each runner.
- **The headless app builder.** `Avalonia.Headless` supplies an `AppBuilder` extension
  (`.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` in the 11.x/12.x
  shape) — **confirm the extension method and options type name in the installed package**; if the
  options type does not exist, the parameterless overload is fine. `UseHeadlessDrawing = false` avoids
  Skia and is faster when the test never asserts on pixels; leave it on if it turns out to be required
  for layout in this version, and say which you used.
- **The test application attribute is assembly-level** and takes a type exposing a static
  `BuildAvaloniaApp` method (the name `AvaloniaTestApplicationAttribute` is the 11.x/12.x shape —
  confirm). Put it in one file with `[assembly: ...]`, and make sure `Program.BuildAvaloniaApp()` is
  public and static so it can be reused, or copy its body into `TestAppBuilder`.
- **Tests run on the Avalonia UI thread** through the framework's fact attribute (`[AvaloniaFact]`,
  confirm the name). Plain xUnit `[Fact]` will throw "call from invalid thread" the moment it touches
  a control — that is the single most common way this harness gets written wrong.
- **Boot the shell properly.** The test must not assert on a theory of the shell: construct the
  window with a real `MainWindowViewModel` built from fakes, set it as `DataContext`, call
  `window.Show()`, then let the dispatcher drain (`Dispatcher.UIThread.RunJobs()` — confirm the name)
  so layout and bindings actually run. Assert:
  - view-model state — with the empty fake library, the shell VM reports no games and its
    empty-state text is exactly `Add your first game to get started`;
  - the visual tree — the shell renders the view the ViewLocator resolved, and the empty-state text
    is present as a control (look it up by `x:Name`, e.g.
    `window.FindControl<TextBlock>("EmptyStateText")`; if `FindControl<T>` is not the right lookup in
    this version, walk `GetVisualDescendants()` and filter).
  A view-model-only assertion is not a UI test: layout, `DataTemplate`, selector and `ViewLocator`
  mistakes all survive it.
- **Prove the harness can fail.** Before finishing, temporarily assert the wrong empty-state string
  (or delete the `x:Name`) and confirm the test goes red, then revert. A green test that would pass
  with no window at all is not evidence.
- **Keep the user's machine out of it.** No test may read or write `~/.game-polish/`, a real game
  install, or the OS keychain. Where a test needs a directory, create one under the OS temp directory
  and delete it afterwards, and set Core's data root to it (`201`'s override).
- **Document the pattern for the rest of the phase.** `GamePolish.Desktop.Tests/README.md` should be
  short and copyable: the fake-service convention, "build the VM, set `DataContext`, `Show()`,
  `RunJobs()`, assert VM state then a named control", the `x:Name` convention for anything a test
  asserts on, and the one-line command to run a single test. Tasks `302`–`306` will copy this file's
  pattern; if they need something it lacks, extend it here rather than diverging per test class.

## Acceptance criteria

- `GamePolish.Desktop.Tests` targets `net10.0` and references an xUnit **v3** package plus
  `Avalonia.Headless.XUnit` 12.1.4 (`dotnet list package` output proves both).
- `dotnet test` from the repo root runs the headless shell test green and **prints the real total
  test count** — quote the actual line in the report, not a summary of it.
- The shell test fails when it should: the deliberately-broken assertion was observed red, and that
  fact is stated in the report.
- The test performs both a view-model assertion (empty library ⇒ shell reports the
  `Add your first game to get started` state) and a visual-tree assertion on a named control.
- No test touches `~/.game-polish/`, a game install directory, or the OS keychain (verify by reading
  the test sources for those paths).
- `GamePolish.Desktop.Tests/README.md` exists and is enough for a fresh agent to add a test for one
  phase-3 view without reading any other file.
- `dotnet build` from the repo root stays clean (0 warnings); no existing project's package
  references changed other than this new one.
- `dotnet format --verify-no-changes` is clean for the new project (scoped with `--include`; never run
  it repo-wide on a branch).

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish

# scaffold (confirm the template short name from `dotnet new list` after installing the pack)
dotnet new install xunit.v3.templates::4.0.2
dotnet new list | grep -i xunit            # expect a v3 template short name
dotnet new <v3-template> -o GamePolish.Desktop.Tests -n GamePolish.Desktop.Tests
dotnet add GamePolish.Desktop.Tests/GamePolish.Desktop.Tests.csproj package Avalonia.Headless.XUnit --version 12.1.4
dotnet add GamePolish.Desktop.Tests/GamePolish.Desktop.Tests.csproj package Avalonia.Headless      --version 12.1.4
dotnet add GamePolish.Desktop.Tests/GamePolish.Desktop.Tests.csproj reference GamePolish.Desktop/GamePolish.Desktop.csproj
dotnet sln add GamePolish.Desktop.Tests/GamePolish.Desktop.Tests.csproj

dotnet list GamePolish.Desktop.Tests/GamePolish.Desktop.Tests.csproj package
# expect: an xUnit v3 package (NOT xunit 2.x) and Avalonia.Headless.XUnit 12.1.4

dotnet build                                # expect: 0 Warning(s), 0 Error(s)
dotnet test                                 # expect: the headless shell test listed, real count printed
dotnet test --filter "FullyQualifiedName~Shell"   # expect: 1 passed, 0 failed
dotnet format --verify-no-changes --include GamePolish.Desktop.Tests   # expect: no changes

grep -rn "game-polish\|\.game-polish\|Directory.Exists" GamePolish.Desktop.Tests/  # expect: no test paths
git status --short
```

## References

- Design notes: `../game-polish-notes/docs/views/library.md` — **Empty States** (the verbatim string
  the boot test asserts). `../game-polish-notes/docs/ci-release.md` — the CI `build-and-test` step
  whose `dotnet test` invocation this project joins (do not edit CI here; `104` owns it).
- Repo: `000-CONVENTIONS.md` — the definition of done ("Desktop UI: a headless UI test, or an explicit
  written statement that it was only verified in `dotnet watch`"), pitfall 4 (wrong xUnit major),
  and the evidence rules. `CLAUDE.md` — the "Test projects — the xUnit version trap" section.
- Related tasks: `102` (test projects + the runner decision this project must follow), `301` (the
  shell under test), `302`–`306` (each adds its own tests using this pattern), `104` (CI, which may
  need the new project acknowledged).
