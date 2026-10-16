---
id: 301
title: Desktop shell, navigation, theming, first-run
phase: 3
bucket: mvp
depends_on: [101, 102]
blocks: [302, 303, 306, 307, 308]
status: todo
area: desktop
---

# 301 — Desktop shell, navigation, theming, first-run

## Objective

A launchable Avalonia desktop app. When this task is done, `dotnet run --project GamePolish.Desktop`
opens a window with a left navigation rail, a navigation service that swaps the content region between
views, MVVM wiring through the template's `ViewLocator`, a DI container supplying view models,
dark/light theme switching plus a swappable accent resource, and the first-run empty state. Every other
phase-3 task renders into the content region defined here.

## Context

- `101` created the solution, `GamePolish.Core` and `GamePolish.Desktop` (`dotnet new avalonia.mvvm`);
  `102` created the test projects and settled the `dotnet test` runner. Add to what exists.
- Stack: Avalonia `12.1.4`, TFM `net10.0`, `CommunityToolkit.Mvvm`; the template ships `App.axaml`
  (+.cs), `Program.cs`, `ViewLocator.cs`, `ViewModels/`, `Views/`, `Assets/`.
- Only **OctoWoW** ships in the MVP. Every ASCII sketch in the design notes is Skyrim-shaped (Skyrim
  SE / Stardew / RimWorld, "Essential", "Bug Fix") and is **illustrative**: render the same structure
  for OctoWoW and its addons, never the example game names.
- Phase-2 Core state this shell reads: `201` (`GamePolishPaths` — the `~/.game-polish/` data root,
  `paths.GamesFile`, `paths.EnsureCreated()`) and `203` (`IGamesRegistry.List()` / `ListUnresolved()`,
  which decides first-run). `202`'s `IGameDescriptorLoader.LoadById` supplies a game's display name.
- Inherited from `000-CONVENTIONS.md`: Core has no UI reference, so every Core touch point on the
  desktop is an interface a headless test can fake.
- `308` creates the first headless test project and depends on this task; `504` (phase 5) later drives
  theme and accent from `app-settings.json`.

## Scope

- `Program.cs` / `App.axaml.cs`: `AppBuilder` configuration, developer tools under `DEBUG` (the
  template's `.WithDeveloperTools()`), and ownership of the DI container.
- `Views/MainWindow.axaml` (+`.axaml.cs`): two-region root layout — a fixed-width left rail, a content
  region hosting whatever the navigation service supplies, and a reserved overlay region for `307`.
- `Services/INavigationService.cs` + `NavigationService.cs`: navigate to a route, back where a route
  declares a back target, current route exposed as observable state.
- `Services/IThemeService.cs` + `ThemeService.cs`: `Light` / `Dark` / `System`, plus an accent colour
  from a small preset set.
- `Resources/Theme.axaml`: keyed brushes including the accent key every view consumes with
  `DynamicResource`.
- `ViewModels/MainWindowViewModel.cs`, plus a separate shell view model only if the split earns its
  keep — decide once and say which you chose in the report.
- First-run empty state in the content region when no games are tracked.
- `Views/SettingsPlaceholderView.axaml` — the header gear button navigates somewhere real; App Settings
  itself is `504`, so this task ships a clearly-labelled placeholder.

## Out of scope

- View content: Library panels (`302`, `303`), Add Mod (`304`), Discover (`305`), Backups (`306`).
- Toasts, progress and the log drawer (`307`) — reserve the overlay region only.
- The real App Settings screen and persistence of theme/accent to disk (`504`); theme state is
  in-memory here.
- Custom window chrome, tray icon, single-instance handling, and macOS-specific menu work.

## Implementation notes

- **DI choice: `Microsoft.Extensions.DependencyInjection`.** Build a `ServiceCollection` /
  `ServiceProvider` once in `App.OnFrameworkInitializationCompleted`, then
  `new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() }`. The template's
  `new MainWindow()` is replaced, not extended. `Microsoft.Extensions.Hosting` is explicitly rejected
  for the MVP: host lifetime, hosted services and the configuration stack are machinery this app does
  not need, and the container alone is testable headlessly. Register `IGamesRegistry` (`203`),
  `IGameDescriptorLoader` (`202`) and `GamePolishPaths` (`201`) — read those tasks' constructors.
- **Views resolve through the template's `ViewLocator`** (an `IDataTemplate` mapping a view model's
  type name by replacing `ViewModels`/`ViewModel` with `Views`/`View`), so every view model bound into
  the content region needs a matching `Views/<Name>View.axaml`; a missing view renders nothing and
  throws nothing. Also set `AvaloniaUseCompiledBindingsByDefault` and put `x:DataType` on the root of
  every view and `DataTemplate`, so binding mistakes fail the build instead of rendering blank.
- **No filesystem access from a view model.** No `System.IO` call, no `~/.game-polish` path literal,
  no `Directory.Exists` in any VM: a headless test must be able to inject a fake `IGamesRegistry` and
  assert the empty state with no data directory present. Check the diff for this.
- **Navigation.** A route is an enum or a small record; `NavigationService` holds the current route and
  raises `PropertyChanged` for it; the shell binds a `ContentControl` to the current view model. The
  rail is data-driven from a `NavigationItemViewModel` list with the active item highlighted, not
  hand-written buttons. `SplitView` (real Avalonia control: `IsPaneOpen`, `OpenPaneLength`) is an
  acceptable rail; a `Grid` with a fixed first column plus `ToggleButton`s is simpler and is the
  recommended MVP default.
- **Theming.** `Application.Current.RequestedThemeVariant = ThemeVariant.Dark | ThemeVariant.Light |
  ThemeVariant.Default`, where `ThemeVariant.Default` is "follow system". Colours live in
  `Resources/Theme.axaml` as keyed brushes consumed with `DynamicResource` — literal hex in a view will
  not follow a variant switch. Check Light **and** Dark for every surface this task creates.
- **Accent.** Do not assume FluentTheme exposes an overridable accent resource: the key this Avalonia
  version ships, and whether overriding it is supported, must be confirmed against `12.1.4` first. The
  recommended approach is to own the key — define `GamePolish.AccentBrush` (and
  `GamePolish.AccentForegroundBrush`) in `Resources/Theme.axaml`, consume it with `DynamicResource`
  everywhere, and have `ThemeService` swap that entry when the accent changes. That key is the contract
  `504` later drives from app settings.
- **First-run empty state.** With an empty tracked-games list, the content region shows a large centred
  prompt with the exact string **`Add your first game to get started`** and a primary call-to-action.
  The CTA opens the Add Game modal owned by `302`; until `302` lands, wire the command and disable the
  button rather than leaving a dead control. `302`'s panel shows the same string.

## Acceptance criteria

- `dotnet run --project GamePolish.Desktop` opens a window: left rail, header gear button, content
  region, no unhandled exception at startup (read the process stdout, not just the exit code).
- With an empty or absent `~/.game-polish/games.json`, the content region renders exactly
  `Add your first game to get started`.
- Light and Dark both render correctly, `ThemeVariant.Default` follows the OS setting, and changing the
  accent changes button and highlight colours in both variants; no view hard-codes a hex for an
  accent-coloured surface (search the views for `#`). Say which variants you actually looked at.
- No `using System.IO;` and no `~/.game-polish` literal anywhere under `ViewModels/`.
- `dotnet build` from the repo root completes with 0 warnings and 0 errors.
- A test asserting the shell loads exists and passes; the harness is `308` (it depends on this task).
  If `308` has not landed, record the launch check as `dotnet watch`-only verification and let `308`
  add the automated assertion.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                              # expect: Build succeeded, 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop   # window opens; empty-state text visible
grep -rn "System.IO" GamePolish.Desktop/ViewModels/     # expect: no matches
grep -rn '#[0-9A-Fa-f]\{6\}' GamePolish.Desktop/Views/  # review: no accent surfaces hard-coded
dotnet test                               # once 308 lands: shell test green, real count quoted
git status --short
```

## References

- Design notes: `../game-polish-notes/docs/views/library.md` (**Layout**, **Empty States** — the
  no-games string), `../game-polish-notes/docs/views/app-settings.md` (**Appearance** — theme
  Light/Dark/System, accent presets Blue/Purple/Green/Orange, `app-settings.json` persistence).
- Repo: `CLAUDE.md` (Tech Stack, Scaffolding, Pinned Versions), `000-CONVENTIONS.md` (invariants,
  definition of done, pitfalls 4 and 7).
- Related tasks: `101`, `102` (predecessors); `201`, `202`, `203` (Core state this shell reads); `302`
  (Add Game modal behind the CTA); `303`, `306`, `307` (render into this shell); `308` (headless tests);
  `504` (drives theme + accent from app settings).
