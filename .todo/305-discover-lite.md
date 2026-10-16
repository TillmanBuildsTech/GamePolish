---
id: 305
title: Discover-lite
phase: 3
bucket: mvp
depends_on: [211, 303]
blocks: []
status: todo
area: desktop
---

# 305 — Discover-lite

## Objective

A deliberately minimal browse-and-install screen backed by the game's registry, so a new user can
install an addon without pasting a URL. When this task is done the cached registry renders as a
searchable list of addons, each showing name, author, primary tag, an install-state badge and an Install
button that runs the same Core install as `304`; loading, fetch-failure, offline and no-results states
are all handled. Everything richer is `501` (v0.2) and is explicitly not built here.

## Context

- `301` built the shell, navigation and DI; `303` built the Library mod list, the Add Mod button and the
  "no mods yet — browse Discover" empty state that links here.
- Phase-2 Core tasks providing the data: **`211`** (registry fetch + cache — reads `registry_url` from
  the game descriptor, writes `~/.game-polish/<game_id>/registry-cache.json` with `etag`/`last_fetched`,
  re-fetches at most once per session, exposes a manual refresh, and reports whether the result came
  from the network or the cache), **`206`** (manifest — the installed ids behind the install-state
  badge) and **`208`** (the install operation behind the button). Registry entry shape: `{ id, name,
  author, description, source, tags, thumbnail }` inside `{ game_id, updated_at, mods: [...] }`.
- The design note's grid shows Skyrim SE cards ("Unofficial Patch by Arthmoor", "Bug Fix · ✓"). **MVP
  ships OctoWoW only**: real cards are the seed registry (`pfUI` by `brues-code`, `pfQuest`,
  `AtlasLoot`, …). The Skyrim names are illustrative.
- Inherited invariants: GitHub repos only as sources; mod content is never executed; installs back up
  before writing (invariant 1).

## Scope

- `ViewModels/DiscoverViewModel.cs`: the registry state machine (`Loading` / `Loaded` / `Failed` /
  `OfflineCached`), the mod collection, search text, the filtered view, and the install command.
- `ViewModels/DiscoverModCardViewModel.cs`: name, author, primary tag, install state, per-card
  `IsInstalling`, thumbnail, Install command.
- `Views/DiscoverView.axaml`: header (back arrow to Library, title naming the game), search `TextBox`,
  card list/grid, state banners.
- Install-state badge: green **Installed** badge with a disabled `Installed` button when the card's mod
  id is in the manifest.
- Loading skeleton, fetch-failed banner with Retry, offline notice, no-results message, and a single
  refresh affordance (the cache re-fetches at most once per session) — not the v0.2 staleness UI.

## Out of scope

- **Sort options** (Popular / Newest / A–Z) — `501`.
- **Tag pills and multi-select AND filtering** (`[All] [Bug Fix] [UI] … [+]`) — `501`. In the MVP tag
  text participates in search; there are no filter pills.
- **The "Last updated X ago" staleness line** and the >7-day amber warning — `501`.
- **The game switcher dropdown** — `501`; the MVP has one game, so Discover is scoped to the game
  selected in Library.
- **Card click → Mod Detail** (and Mod Detail itself) — `502`; **the Update Available badge and the
  Update button** — `503`. Thumbnail caching, pagination and infinite scroll: if the note's
  `[ Load more ]` is implemented it must be purely client-side over the cached registry.

## Implementation notes

- **One Core call, no network in the view model** — ask `211` and render what it returns plus its
  provenance flag: no `HttpClient`, no URL building, no reading `registry-cache.json` from `ViewModels/`.
- **The fetch never throws into the UI.** Map `211`'s result to exactly three visible states: loading
  with no data yet renders a skeleton card grid with a header spinner (never an empty grid, which reads
  as "the registry has no mods"); failed with no cache shows the banner `Couldn't load registry. Check
  your connection.` with a **Retry** button; failed with cache shows that banner **plus** the cached
  grid and the notice `Offline — showing cached results`. Both strings are verbatim from the note.
- **Installed-state badge.** The manifest (`206`) is the source of truth: re-read it on view activation
  so an install done in Library (`304`) shows here, and flip card state only after Core reports success.
- **Inline install flow, from `discover.md` — Install Flow.** Click **Install** → the card's button is
  replaced by a spinner → Core clones/downloads, validates, backs up conflicting files, installs → on
  success the button reads **Installed** and the card gets the green badge → on failure an error toast
  with a details link (`307`'s log drawer). The modal of `304` is not used here; the same Core install is.
- **Duplicate/race.** Re-check the manifest before installing, so a mod installed elsewhere while this
  view is open switches the card to Installed instead of installing twice — Core's "already installed"
  refusal, surfaced as a toast rather than a thrown exception.
- **Search** is client-side over name, author, description and tags (all four exist in the registry
  JSON), case-insensitive, with no per-keystroke Core call. No results shows the exact string
  `No mods match your filters` with a `Clear filters` link that clears the search box — in the MVP that
  box is the only filter, so say so in the report.
- **Cards with no thumbnail** render a placeholder derived from the mod name — never a broken image,
  never a network fetch for a missing URL. **Back navigation** returns to Library retaining its
  selected game and scroll/search state. **The card body is inert in the MVP**: no hover pointer, no
  ripple; the Install button is the card's only interactive element.

## Acceptance criteria

- Installing from a card succeeds: clicking **Install** shows the in-button spinner, and on success the
  button reads **Installed** with the green badge.
- After that install `~/.game-polish/<game_id>/manifest.json` contains the mod and
  `<install>/Interface/AddOns/<AddonName>/` exists, preceded by a new backup directory (invariant 1).
- Install state survives navigation: leave Discover, return, and the card still reads **Installed**.
- With `registry-cache.json` deleted and no network, Discover shows `Couldn't load registry. Check your
  connection.` with Retry and no empty grid; with a populated cache and no network it shows the cached
  cards **and** `Offline — showing cached results`.
- A search with no matches shows `No mods match your filters` with a working `Clear filters` link, and
  sort controls, tag pills, the staleness line and the game switcher are absent from the view — their
  absence is an acceptance criterion, not an oversight.
- `dotnet build` clean; tests run with the real counts quoted.

## Verification

```bash
cd C:/Users/Brandon/Projects/game-polish
dotnet build                                        # expect: 0 Warning(s), 0 Error(s)
dotnet run --project GamePolish.Desktop

# Library -> "browse Discover" (or the nav rail) -> install a not-installed card, e.g. pfQuest
ls -d "D:/OctoWoW/Interface/AddOns"/*               # expect the new addon folder
ls -1t ~/.game-polish/octowow/backups/ | head -1    # expect a backup dir created before the write
cat ~/.game-polish/octowow/manifest.json            # expect the mod id/name present
# the card reads Installed; navigate away and back -> still Installed

# offline: move the cache aside, disconnect, open Discover -> banner + Retry, no empty grid;
# restore the cache, disconnect again -> cached cards + "Offline — showing cached results"
mv ~/.game-polish/octowow/registry-cache.json ~/.game-polish/octowow/registry-cache.bak
mv ~/.game-polish/octowow/registry-cache.bak ~/.game-polish/octowow/registry-cache.json
# search: type "zzzz" -> "No mods match your filters" + Clear filters -> clearing restores the list

grep -n -i "sort\|stale\|switcher" GamePolish.Desktop/Views/DiscoverView.axaml  # expect: no matches
dotnet test                                         # quote the real pass/fail counts
```

## References

- Design notes: `../game-polish-notes/docs/views/discover.md` — **Layout**, **Header**, **Search &
  Filter Bar**, **Mod Cards (Grid)**, **Install Flow (inline)**, **Empty / Edge States**, **Data
  Requirements** (**Registry Staleness** is v0.2); `../game-polish-notes/docs/views/library.md` —
  **Empty States** (the link into Discover); `../game-polish-notes/docs/games/octowow.md` — **Seed
  Registry**, **Game YAML (draft)** (`registry_url`).
- Repo: `CLAUDE.md` (Mod Registry (JSON) schema), `000-CONVENTIONS.md` (invariants 1–5).
- Related tasks: `211`, `206`, `208` (Core), `301` (shell), `303` (Library), `304` (the other caller of
  Core install), `307` (error toast + log drawer), `501` (the full Discover), `402` (seed registry repo).
