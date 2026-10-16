---
id: 302
title: Library games panel and Add Game
phase: 3
bucket: mvp
depends_on: [203, 301]
blocks: []
status: todo
area: desktop
---

# 302 — Library games panel and Add Game

## Objective

The Library's left panel and the Add Game modal. When this task is done the user can see every game
Game Polish tracks, switch the active one, and add a game by browsing to its install folder — the
folder being verified against the game descriptor before anything is persisted. For the MVP the only
game is OctoWoW, whose detection is manual-only and is verified by the presence of `WoW.exe`.

## Context

- `301` built the shell, navigation service, DI container and theme resources; this task fills the
  content region's left column and adds one modal.
- **`203` is where the work happens.** `IGamesRegistry` exposes `List()`, `Add(gameId, installPath)`,
  `SetInstallPath(gameId, path)` and `ListUnresolved()` over `TrackedGame { GameId, InstallPath,
  AddedAt }`. `Add` already performs the whole validation ladder — path fully qualified and existing,
  not the data root, not already tracked, and the descriptor's `verify_file` present — and writes
  `games.json` atomically. Rejections are typed: `InstallPathInvalidException` (message names the
  folder and `WoW.exe`) and `GameAlreadyTrackedException` (carries `ExistingPath` / `RequestedPath`).
  **The UI must not repeat any of that validation.**
- **`202`** supplies `IGameDescriptorLoader.LoadById(gameId)` and `GameDescriptor { Id, Name,
  InstallDetection(ManualOnly, VerifyFile), ModLayout(Type, Destination), RegistryUrl, SettingsFiles }`.
  The descriptor has **no icon field**.
- The design note is Skyrim-shaped: it lists Skyrim SE / Stardew Valley / RimWorld and says Add Game
  offers "auto-detect from a list of supported games or manually browse to an install folder". **MVP
  ships OctoWoW only**, so the auto-detect list has exactly one entry and OctoWoW is
  `manual_only: true`. The example games are illustrative, not a backlog for this task.
- OctoWoW facts (`docs/games/octowow.md`): no Steam registry and no auto-detection; the user browses to
  the install folder; correctness is verified by finding `WoW.exe` in the folder root; the path lives
  in `~/.game-polish/games.json` and is editable from Library. Brandon's install is `D:\OctoWow`.
- Inherited invariants: write only inside the game's install directory and `~/.game-polish/`; never
  execute anything from a mod source (reading a folder for `WoW.exe` is not executing).

## Scope

- `ViewModels/LibraryViewModel.cs` game-list section: an `ObservableCollection<GameListItemViewModel>`
  built from `IGamesRegistry.List()`, plus a `SelectedGame` the right panel (`303`) observes.
- `ViewModels/GameListItemViewModel.cs`: game name (from the descriptor), install path, mod-count
  badge, placeholder icon, unresolved flag.
- `Views/LibraryView.axaml` left column: the game list with the active entry highlighted, a
  `[+ Add Game]` button pinned below it, and the panel empty state.
- `Views/AddGameDialog.axaml` (+ view model): a modal `Window` shown with `ShowDialog(...)`, offering
  **auto-detect list** and **manual browse** as two tabs or a radio choice.
- Manual browse: folder picker, the chosen path echoed for confirmation, verify-on-confirm, inline
  error region, busy state.
- Editing the install path of an already-tracked game via `IGamesRegistry.SetInstallPath`,
  re-verified the same way.

## Out of scope

- The mod list, search, row actions and uninstall (`303`).
- Adding a *supported* game to the descriptor catalog — `202` owns the catalog and `401` owns
  `games/octowow.yaml`; this task only reads it. Supporting a second game is a YAML + registry change.
- Registry browsing (`305`) and the Add Mod flow (`304`).
- Autodetect heuristics that scan drives: OctoWoW is `manual_only`, so a scanner would contradict the
  descriptor.
- Removing a game from the library (delete its files, delete its manifest, or forget it without
  deleting files) — out of MVP; record it as a gap in the report.

## Implementation notes

- **Icon.** `GameDescriptor` has no icon field, so render a deterministic placeholder derived from the
  game name (its first letter in an accent-filled `Border`). Do not invent a YAML key; an icon key
  would be a change to `games/octowow.yaml`, owned by `401`.
- **Modal mechanics.** Show with `Window.ShowDialog(owner)` / `ShowDialog<TResult>(owner)` and
  `WindowStartupLocation = WindowStartupLocation.CenterOwner`; return a result rather than reaching
  into `LibraryViewModel`, so the dialog stays testable headlessly.
- **Verification is Core's job.** "Does this folder contain `WoW.exe`" is answered by
  `IGamesRegistry.Add` through the descriptor's `VerifyFile`, not by `Directory.Exists`/`File.Exists`
  in a view model. Render the outcome; do not rewrite `203`'s messages, which already name `WoW.exe`.
- **Case-insensitive paths.** The note spells the folder `D:\OctoWow`; users type `D:\OctoWoW`. They
  are the same folder, and `203` compares with `OrdinalIgnoreCase` on Windows/macOS — do not
  re-implement or second-guess that comparison, and do not pre-normalise the string yourself.
- **Empty state.** No tracked games: large centred prompt, exact string **`Add your first game to get
  started`**, with the Add Game CTA. `301` renders this at shell level; this panel renders it too when
  shown with no games. Keep the string identical in both places.
- **Error and duplicate states.** Invalid path (folder missing, or `WoW.exe` absent) shows
  `InstallPathInvalidException`'s message; a duplicate shows `GameAlreadyTrackedException`'s message
  naming the existing path and creates **no** second entry. Both are inline panel errors, not toasts —
  toasts are `307`.
- **Loading.** Reading `games.json` and probing a folder is I/O: disable the confirm button and show a
  busy state until it returns — the `IUserFeedback` progress scope of `307` if that has landed,
  otherwise a local `IsBusy` flag.
- **Mod-count badge.** Comes from the per-game manifest (`206`). Until `206` lands, render `0` and say
  so in the report rather than parsing `manifest.json` in a view model.

## Acceptance criteria

- Adding the path `D:\OctoWoW` verifies that `WoW.exe` exists in it, persists the game to
  `~/.game-polish/games.json`, and the game appears in the left panel highlighted as active.
- After restarting the app the added game is still listed and still selected — proof the state came
  from `games.json`, not from memory.
- A folder without `WoW.exe` is rejected with a message naming `WoW.exe`, and `games.json` is
  unchanged.
- Adding the same install path twice, including a case-only difference (`D:\OctoWow` vs `D:\OctoWoW`),
  produces the duplicate message and exactly one entry in `games.json`.
- A tracked game whose folder has been renamed away shows as unresolved rather than vanishing from the
  panel; with no tracked games the panel shows `Add your first game to get started`.
- No `System.IO` call and no `~/.game-polish` path literal in `ViewModels/`; `dotnet build` is clean.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                                # expect: 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop
# [+ Add Game] -> browse to D:\OctoWoW -> confirm -> game appears highlighted

cat ~/.game-polish/games.json               # expect one octowow entry, install_path D:\OctoWoW
# restart the app -> still listed and still selected
# negative: point at C:\ (no WoW.exe)        -> error naming WoW.exe, games.json unchanged
# negative: add D:\OctoWow after D:\OctoWoW  -> duplicate message, still one entry
# negative: rename the install dir            -> the entry shows as unresolved, not absent

grep -rn "System.IO" GamePolish.Desktop/ViewModels/    # expect: no matches
dotnet test                                 # quote the real pass/fail counts
```

## References

- Design notes: `../game-polish-notes/docs/views/library.md` — **Layout**, **Left Panel — Game List**,
  **Empty States**, **Data Requirements**; `../game-polish-notes/docs/games/octowow.md` — **Install
  Path** (manual, `WoW.exe`, `D:\OctoWow`, `games.json`), **Game YAML (draft)**.
- Repo: `CLAUDE.md` (Game Config YAML Schema, Directory Layout), `000-CONVENTIONS.md` (invariants 1
  and 4, local state layout).
- Related tasks: `201`, `202`, `203` (Core), `206` (manifest, for the badge), `301` (shell), `303` (the
  panel beside this one), `307` (toasts/progress), `401` (the shipping `octowow.yaml`).
