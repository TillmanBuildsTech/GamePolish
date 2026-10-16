---
id: 303
title: Library mod list and row actions
phase: 3
bucket: mvp
depends_on: [203, 301]
blocks: [304, 305]
status: todo
area: desktop
---

# 303 — Library mod list and row actions

## Objective

The Library's right panel: the installed-addon surface the user lives in. When this task is done the
selected game's addons are grouped into Enabled and Disabled, filterable by a client-side search, each
row carrying an enable/disable checkbox, the installed version tag and an overflow menu; the toggle
runs as a locked, spinnable row operation; uninstall goes through a confirmation dialog. `304` opens its
Add Mod modal from this header; `305` is reached from its "no mods" empty state.

## Context

- `301` built the shell, navigation service, DI container and theme resources; `302` built the left
  panel and the `SelectedGame` this panel observes.
- Phase-2 Core tasks providing the data and the operations: **`206`** (`manifest.json` records — addon
  name, version, enabled state, source — which is the row list), **`210`** (uninstall / enable /
  disable and their result types), **`205`** (`IBackupEngine` — the snapshot taken on first enable and
  before uninstall), **`204`** (`IPathValidator` — the gate every mutation passes through; the UI never
  validates a path) and **`203`** (`IGamesRegistry` — the selected game and its install path).
- The design note is Skyrim-shaped ("Unofficial Patch v4.2.9", "SkyUI", "USSEP"). **MVP ships OctoWoW
  only**: real rows are `pfUI`, `pfQuest`, `AtlasLoot`, and the path shown is whatever `games.json`
  holds for `octowow`. OctoWoW addons live in `<install>/Interface/AddOns/<AddonName>/`, installed as a
  `--depth=1` clone, so the version shown is the HEAD commit SHA or the latest git tag
  (`docs/games/octowow.md`).
- Inherited invariants: backup before mutation (enable, disable and uninstall all write into the
  install directory), never execute mod content, write only inside the two allowed roots.

## Scope

- `ViewModels/ModRowViewModel.cs`: name, installed version, enabled flag, per-row `IsBusy`, a
  `CanToggle` guard, and commands for the toggle and each overflow entry.
- `ViewModels/LibraryViewModel.cs` mod-list section: the source collection, search text, the two derived
  groups with live counts, and per-group collapse state.
- `Views/LibraryView.axaml` right column: header (game name + truncated install path with a tooltip),
  `[Search mods...]`, `[+ Add Mod]`, the two collapsible groups, and the row template — checkbox, name,
  version tag, `⋮` `MenuFlyout`, and a spinner occupying the row while a toggle runs.
- Uninstall confirmation dialog, then the Core uninstall call.
- The three empty states, verbatim (see Implementation notes), and `NavigateToBackups(modId)` wiring
  into `306`'s view pre-filtered to that mod.

## Out of scope

- **Open Detail → `502`** and **Check for Update → `503`**: both menu entries are stubs in the MVP
  (Mod Detail and the GitHub API client are v0.2).
- The Add Mod modal itself (`304`) — this task owns only the button that opens it.
- Discover (`305`) — this task only links to it from the "no mods" empty state.
- Version pin/rollback, tag pills on rows, drag reordering, bulk selection/actions, and the amber
  update badge (`library.md` — Update Indicator), which is `503`'s.

## Implementation notes

- **Stub menu entries must be visibly dead, not silently broken.** `Open Detail` and `Check for Update`
  render as disabled `MenuItem`s with a tooltip such as `Available in v0.2` — never as enabled entries
  that swallow a click or throw. `View Backups` (`306`) and `Uninstall` are live; say in the report
  which entries are stubs.
- **Search is client-side** over the in-memory collection, with no Core call per keystroke. The note
  says "by mod name/author/tags", but the manifest carries name, version, enabled state and source:
  search **name and version** for certain, include author and tags only if `206`'s schema carries them,
  and otherwise record the deviation rather than inventing manifest fields.
- **Grouping.** Fixed order, Enabled then Disabled, each header showing its count in the note's shape
  (`ENABLED (12)` / `DISABLED (3)`), each collapsible. Collapsing must not lose the filter or selection.
- **Enable/disable mechanics, restated from `library.md` — Enable/Disable Mechanics.** Enabling copies
  (or links) the addon's files from the Game Polish staging area into the game's install directory as
  defined by `mod_layout` (`Interface/AddOns`); disabling removes them from the game directory. **A
  backup is created on first enable.** The row shows a spinner during the operation and **the checkbox
  is locked until it completes** — `IsBusy` true ⇒ `IsEnabled=False` on the `CheckBox`, row spinner
  visible, all row commands disabled.
- **A failed toggle must not lie.** Keep the pre-operation value, apply the new one only on success, and
  surface failures through `307`'s `IUserFeedback` error path (or a local inline error until `307`
  lands). A checkbox that flips and then silently reverts is the worst outcome.
- **Uninstall.** Confirm first, then Core `210`. The note gives no wording; proposed text, to be marked
  as proposed in the report: `Uninstall <name>? Its files will be removed from Interface/AddOns. Game
  Polish takes a backup first, so this can be undone from Backups.` After success the row disappears
  and, if the list is now empty, the "no mods" empty state shows.
- **Empty states, verbatim from `library.md` — Empty States.** No games added:
  `Add your first game to get started` with a CTA (that condition is `302`'s). No mods installed:
  `No mods yet — browse Discover to find some` with a link to Discover. Search returns nothing:
  `No mods match your search.`
- **Header path display.** Truncate with an ellipsis and put the full value in a tooltip
  (`ToolTip.Tip`): a long Windows path must not push the Add Mod button off screen.
- **Threading and half-states.** Await every Core call and mutate collections on the UI thread or
  replace them wholesale; if the manifest fails to load, show an error state with a retry rather than
  an empty list, because an empty list reads as "you have no addons".

## Acceptance criteria

- With at least one installed addon, Library renders two groups with correct counts and each row shows
  name, version tag and the overflow menu; typing in the search box filters with no Core call, and
  clearing it restores every row.
- Toggling a row runs the operation, shows a row spinner, disables that checkbox for the duration and
  settles into the new state; `~/.game-polish/<game_id>/backups/` gains an entry for a first enable
  (invariant 1 — a mutation with no backup is a bug even if it works).
- `View Backups` navigates to the Backups view pre-filtered to that mod.
- `Open Detail` and `Check for Update` are visibly disabled with a "v0.2" explanation, and clicking
  them throws nothing.
- Uninstall shows the confirmation; cancelling leaves both the files and the manifest untouched.
- The three empty-state strings appear exactly as written above, in their conditions.
- No `System.IO` call and no `~/.game-polish` path literal in `ViewModels/`; `dotnet build` clean; tests
  run with the real counts quoted.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                                      # expect: 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop
# search: type a substring of an installed addon -> list filters; clear -> all rows return
# toggle: uncheck an enabled addon -> row spinner, checkbox disabled, then it moves to DISABLED
ls -1 ~/.game-polish/octowow/backups/             # expect a new timestamp directory from the toggle
# row menu: View Backups -> Backups view filtered; Open Detail / Check for Update disabled
# uninstall: confirm -> cancel -> addon still in Interface/AddOns and manifest.json
cat ~/.game-polish/octowow/manifest.json          # enabled flag matches the UI
grep -rn "System.IO" GamePolish.Desktop/ViewModels/    # expect: no matches
dotnet test                                       # quote the real pass/fail counts
```

## References

- Design notes: `../game-polish-notes/docs/views/library.md` — **Right Panel — Mod List**,
  **Mod Row — Overflow Menu Actions**, **Enable/Disable Mechanics**, **Empty States**, **Data
  Requirements**, **Update Indicator** (v0.2); `../game-polish-notes/docs/games/octowow.md` —
  **Addon Layout** (`Interface/AddOns`, `--depth=1`, version = HEAD SHA or latest tag).
- Repo: `000-CONVENTIONS.md` (invariant 1 backup-before-mutation, invariant 2 never execute, local
  state layout).
- Related tasks: `203`, `204`, `205`, `206`, `210` (Core), `301` (shell), `302` (left panel), `304` (Add
  Mod modal opened here), `305` (Discover, linked from the empty state), `306` (Backups, from the row
  menu), `307` (toasts/progress/log drawer), `502`/`503` (the stubbed menu entries).
