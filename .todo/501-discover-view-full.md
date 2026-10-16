---
id: 501
title: Discover view, full
phase: 5
bucket: v0.2
depends_on: [305, 211, 504]
blocks: []
status: todo
area: desktop
---

# 501 — Discover view, full

## Objective

Turn the MVP's Discover-lite into the discovery surface the design note specifies. **Task 305** built a
deliberately minimal browse-and-install screen and listed this task as the owner of everything richer —
"Sort options (Popular / Newest / A–Z)", "Tag pills and multi-select AND filtering", "the 'Last updated X
ago' staleness line and the >7-day amber warning", "the game switcher dropdown", "card click → Mod
Detail". This task **supersedes the state, sort, filter and header half of 305** and **consumes task 211**
(registry fetch + cache) and **task 504** (the GitHub PAT). The inline install flow stays where 305 and
208 put it: on the card, through `GamePolish.Core`, backed up before the first byte is written.

## Context

- `../game-polish-notes/docs/views/discover.md` is authoritative. The strings quoted below are transcribed
  from it and must appear character-for-character; if this file and the note disagree, **the note wins**.
- Depends on **305** (view, `DiscoverViewModel`/`DiscoverModCardViewModel`, card, inline install, Installed
  badge, header refresh affordance), **211** (`~/.game-polish/<game_id>/registry-cache.json` with `etag` /
  `last_fetched`, re-fetch at most once per session, network-vs-cache flag), **504** (the PAT — Discover
  never reads the token).
- `blocks: []`: no other phase-5 task waits on this file. Mod Detail (502) is reached *from* these cards at
  runtime; that is a navigation edge, not a build-order edge.
- Repo state: greenfield, docs-only. 305's files are the starting point; no type named here exists yet.
- OctoWoW is the only supported game, so the game switcher is exercised with a two-game fixture.

## Scope

- Sort with exactly three options: **Popular** (registry-defined order — the order rows appear in the
  registry JSON), **Newest** (by `added_at`, descending), **A–Z**.
- Tag pills derived from all unique registry tags; "All" as the default, multi-select with **AND** logic,
  and a `+` expander for overflow tags.
- Staleness line "Last updated X ago" under the tag bar with a manual-refresh icon, and an amber warning
  when the cache is older than 7 days.
- Game switcher dropdown in the header, shown when more than one game is tracked.
- Back arrow to Library **retaining game context**; title "Discover: <game name>".
- Skeleton card grid before the registry is fetched, with a header spinner; fetch-failed banner with
  Retry; the stale-cache notice; verbatim empty states; incremental "Load more" paging.
- Scroll position and filter state preserved across navigation to Mod Detail and back.

## Out of scope

- Mod Detail itself (502) — cards navigate to it, this task does not build it (305 defers it too).
- Registry fetch/caching mechanics and its `etag`/`last_fetched` fields (211); this task calls and renders
  them.
- Storing, validating or displaying the PAT (504); Discover must not read the keychain.
- Backups view (306), toasts/error drawer (307), app shell and navigation host (301).
- Changelogs, version lists (502) and update *detection* (503) — this task only renders the badge 503 feeds.
- Any registry write: the JSON is fetched, never written.

## Implementation notes

- **Keep 305's state machine and extend it**: `Loading` / `Loaded` / `Failed` / `OfflineCached` already map
  onto the note's skeleton, grid, banner and stale-notice cases. Do not invent a second vocabulary.
- **Filtering is client-side** over the cached registry: one `IReadOnlyList<RegistryMod>` from 211 is the
  source of truth; search + tags + sort are a LINQ pipeline producing an
  `ObservableCollection<ModCardViewModel>`. Debounce the search box (~150 ms), not a re-query per keystroke.
- **Search** matches name, author, description and tags, case-insensitive (`StringComparison.OrdinalIgnoreCase`).
- **Tag pills**: `mods.SelectMany(m => m.Tags).Distinct()`, "All" first. Active tags are a
  `HashSet<string>`; a mod passes when `active.All(t => mod.Tags.Contains(t))`. The `+` expander reveals
  overflow pills inline (a second `ItemsControl` in the same `WrapPanel`) so active filters stay visible;
  cap the visible count with a constant, not a number buried in XAML.
- **Grid host**: `ListBox`/`ItemsControl` with a `WrapPanel` `ItemsPanel`. If it stutters past ~200 cards,
  escalate to `ItemsRepeater` + `UniformGridLayout` and record the measurement.
- **Paging**: page size 24 (305's optional "Load more" is required here). "Load more" appends the next slice
  of the current filtered+sorted list, client-side only — no extra network calls. Any change to search,
  tags, sort or game resets to page 1 and scrolls to top.
- **Skeleton**: while 211 reports no cache and a fetch is in flight, render N placeholder cards with a
  shimmer (`Avalonia.Animation.Animation` on `Opacity`) plus the header spinner — same card size, so there
  is no layout jump.
- **Staleness line** formats `last_fetched` as a relative age; the amber warning shows automatically when
  `now - last_fetched > 7 days`. Fold 305's refresh affordance into this line's refresh icon (one control,
  as 305 intended) and disable it while a fetch is in flight.
- **Fetch failure**: banner text "Couldn't load registry. Check your connection." with a **Retry** button.
  With a stale cache the banner shows *and* the stale grid renders underneath with the notice "Offline —
  showing cached results"; with no cache the banner replaces the grid.
- **Filter empty state**: exactly "No mods match your filters" with a "Clear filters" link resetting search
  + tags + sort (not scroll position). **All installed**: normal grid, every card Installed — the note
  explicitly needs no special state.
- **Cards**: thumbnail (`thumbnail` URL, placeholder asset when absent or 404), name, "by <author>", primary
  tag, badge (blank / green "Installed" / amber "Update Available"), button (Install / Installed / Update —
  the Update states come from 503). Clicking anywhere **except the button** opens Mod Detail.
- **Inline install** unchanged from 305/208: spinner replaces the button → Core clones, validates, backs up
  conflicting files, installs → success flips to "Installed" + green badge → failure raises the 307 toast
  with a details link opening the log drawer. The card must not become clickable-to-detail while installing.
- **Navigation state**: capture `ScrollViewer.Offset` on `ScrollViewer.ScrollChanged` plus the
  filter/sort/tag snapshot into the navigation-context record 301's host carries; restore after layout via
  `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)`. Define that record's shape here — 502 restores
  from it.
- **Schema finding**: `added_at` is absent from the registry schema in `CLAUDE.md` and therefore from 305's
  view of the data. Newest sort needs it on every row, so task 402's seed registry must carry it. A row
  without it falls back to registry order for that row — report it, do not crash the comparer.
- Avalonia 12 enables **compiled bindings by default**: every new or edited XAML root needs `x:DataType`.
  Tag pills and the sort control must be Tab-reachable and Space/Enter-operable (full a11y pass is 608; add
  no new keyboard traps).

## Acceptance criteria

1. Sort exposes exactly Popular / Newest / A–Z, each producing the documented order over a fixture registry,
   proven by a unit test on the sorting pipeline in `GamePolish.Core.Tests`.
2. Tag pills derive from the registry (not hard-coded); "All" is the default; two active tags return only
   mods carrying **both**; `+` reveals the overflow.
3. Search matches name, author, description and tags and composes with the tag pills (AND), not either/or.
4. The staleness line reads "Last updated X ago" from `last_fetched`, the refresh icon re-fetches, and a
   >7-day cache shows the amber warning with no user action.
5. The offline path renders stale results with "Offline — showing cached results" plus the banner
   "Couldn't load registry. Check your connection." and a working Retry.
6. The no-match state reads exactly "No mods match your filters" and "Clear filters" restores the list.
7. "Load more" appends the next page without re-rendering existing cards, makes no network calls, and
   resets on filter change.
8. Returning from Mod Detail restores scroll position and full filter state.
9. The game switcher appears only with more than one tracked game and reloads that game's registry and
   installed set.

## Verification

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~Discover"     # sort/filter/paging pipeline
dotnet format --verify-no-changes --include GamePolish.Desktop GamePolish.Core
dotnet watch run --project GamePolish.Desktop
```

By hand: fetch once online, close, disconnect the network, relaunch, open Discover — the stale grid must
render with the offline notice and the Retry banner. Then corrupt the cache file deliberately and confirm
the banner replaces the grid instead of the app throwing. Paste real test counts and observed strings.

## References

- `../game-polish-notes/docs/views/discover.md` — Layout, Header, Search & Filter Bar, Mod Cards, Install
  Flow, Empty / Edge States, Data Requirements, Registry Staleness.
- `../game-polish-notes/docs/views/library.md` — Update Indicator (the amber badge 503 detects).
- `.todo/000-CONVENTIONS.md` — invariants, definition of done, evidence rules.
- Tasks: **305** (superseded view), **211** (registry cache), **504** (PAT), **502** (Mod Detail + nav
  context), **503** (badge data), **307** (toasts), **208** (install), **402** (seed registry `added_at`).
