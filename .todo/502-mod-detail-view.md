---
id: 502
title: Mod Detail view
phase: 5
bucket: v0.2
depends_on: [503, 306]
blocks: []
status: todo
area: desktop
---

# 502 — Mod Detail view

## Objective

Build the Mod Detail view, which does not exist in any form today: the MVP bucket excluded "per-mod
changelogs/version history", task 305 names "Card click → Mod Detail and the Mod Detail view itself" as out of
its scope, and task 206 refused to store changelogs or screenshots in the manifest. This task **creates the
view**, **extends task 306** (its Backups panel is the Backups list pre-filtered to one mod) and **consumes
task 503** (the GitHub client supplying releases, tags and changelogs). At the end a user can open any mod from
Library or Discover and see screenshots, a rendered description and changelog, the version list with the
installed version highlighted, a downgrade path, and that mod's backups.

## Context

- Read `../game-polish-notes/docs/views/mod-detail.md` in full. The strings quoted below are transcribed from it;
  where this file and the note disagree, **the note wins**.
- Depends on **503** (releases, tags, ETag caching, the typed rate-limited result this view renders as a notice)
  and **306** (the Backups view and the index at `~/.game-polish/<game_id>/backups/index.json`).
- `blocks: []`. 501 and this task are coupled at runtime — 501's cards navigate here and this view returns to 501
  with scroll and filter state intact — but neither blocks the other in build order.
- Two entry surfaces: Library (installed mods) and Discover (registry mods). Back returns to **whichever surface
  opened it**.
- The registry row (per `CLAUDE.md`) carries no `screenshots` array and no rating: treat both as fields the
  registry must gain (task 402) and defend against their absence.

## Scope

- Screenshot carousel from the registry's `screenshots` array: arrow navigation, dot indicators, full-screen
  lightbox on click, and a styled placeholder carrying the mod name when there are no screenshots (never a
  broken-image glyph).
- Left column: name, author, rating/score **only when the registry provides one**, tag pills, markdown description
  (scrolls when long), changelog collapsed to the last 3 entries with a "Show all" toggle.
- Right column status block covering all four states (not installed; installed and up to date; update available;
  installing/updating).
- Source link — truncated GitHub URL, opened in the system browser; versions list from GitHub releases with
  downgrade via "Install this version".
- Backups panel filtered to this mod: date, file count, `↩` restore with a confirm dialog.
- Back-to-origin navigation preserving the origin's scroll position and filter state.
- The three documented edge cases, each with its exact notice.

## Out of scope

- The full Backups view, its sort/filter controls and its disk-usage footer (task 306).
- Building or changing the GitHub client, its caching or its rate-limit handling (503) — this view consumes
  results and renders failures.
- The update pipeline itself (209); the downgrade action calls it with a pinned ref.
- The Settings Editor, Cloud Backup and Auth (later buckets); writing the registry cache (211).

## Implementation notes

- **Markdown renderer — decision: `Markdig` (parser) + a hand-written `MarkdownToInlinesConverter`.** Avalonia 12
  is new and the markdown-control ecosystem has not caught up: `Markdown.Avalonia`'s only Avalonia-12 build is the
  prerelease `12.0.0-a1` and pulls in AvaloniaEdit, and the maintained fork `ClassIsland.Markdown.Avalonia` 12.0.0
  pins Avalonia 12.0.0. Markdig is a stable, MIT, pure-.NET parser with **no Avalonia dependency**, so the
  converter is unit-testable in `GamePolish.Core.Tests` without a UI head; it renders to `TextBlock.Inlines`
  (`Run`, `Bold`, `Italic`, `Hyperlink`, `LineBreak`). **Spike first (1 hour):** if either candidate resolves
  cleanly with a `net10.0` asset group on Avalonia 12.1.4, prefer it and record the deviation.
- **Never render raw HTML** from registry or release text: disable Markdig's HTML pass-through, strip
  `javascript:`/`data:` link targets, and route links through
  `TopLevel.GetTopLevel(control)!.Launcher.LaunchUriAsync(uri)`. No embedded browser, no script surface.
- **Carousel/lightbox**: `ItemsControl` over `screenshots`, arrow buttons, dots bound to the selected index; the
  lightbox is an overlay `Panel` in the app's main window (full-bleed `Image` + dimmed backdrop) toggled by a
  bool — not a second `Window`, so macOS shows no second window. `Esc` closes it via a shell `KeyBinding`.
- **Changelog**: prefer the registry entry, else release bodies from 503's `/repos/<owner>/<repo>/releases`,
  newest first, collapsed to the last 3 with "Show all"; one malformed entry must not blank the list.
- **Status block, four states, worded as the note words them:**
  - *Not Installed* — a single **Install** button.
  - *Installed, up to date* — **Disable** / **Uninstall**; version tag rendered **green**.
  - *Installed, update available* — **Update** primary, **Disable** / **Uninstall** secondary, version tag
    **amber** with the "→ v4.3.0" hint, taken from 503's latest-known version rather than a fetch at render time.
  - *Installing / Updating* — spinner, **all buttons disabled**, progress message driven by 208/209 progress
    through 307's plumbing.
- **Source link**: truncated URL opened via `ILauncher`. A ZIP-installed mod reads **"User-supplied archive."**
  (the Edge Cases wording); the generic "User-supplied ZIP" from the Source Link section applies to a registry row
  that names no `source`. The manifest's `ModSourceType` (`Github`/`Zip`, 206) distinguishes them.
- **Versions list**: GitHub releases (tags when a repo publishes none), fetched on demand, cached per session by
  503. Mark the installed version and the latest, and offer **"Install this version"** on other rows — the
  documented downgrade path, calling Core's update pipeline (209) with an explicit ref. A SHA-installed mod
  (`VersionKind == "sha"`) matches no row: highlight nothing and say so.
- **Backups panel**: `backups/index.json` (plus `<timestamp>/` trees) filtered to this mod; row = date, file
  count, `↩`. Core creates the pre-restore backup itself; the view only shows the confirm dialog from
  `backups.md`: "This will overwrite the current files with the backed-up versions from [date]. This action will itself create a new backup first. Continue?"
- **Back navigation**: reuse the navigation-context record defined in task 501 (scroll offset + filter snapshot).
- **Edge case — no GitHub source**: version list and changelog are **hidden entirely** (not a notice), and the
  source line reads "User-supplied archive."
- **Edge case — rate limit**: changelog and versions show exactly "Couldn't fetch version info — GitHub rate limit reached. Add a token in App Settings." — a notice in the panel, never an error toast or a red state.
- **Edge case — mod removed from the registry**: an installed mod whose row is gone still renders full detail from
  cached data with the "No longer listed in registry" notice. That data does not exist today — 211's cache is
  rewritten on every fetch and 206 refused per-mod metadata — so add a snapshot store this task owns (e.g.
  `<game_id>/mod-metadata/<mod_id>.json`, written when a registry row is seen for an installed mod) **or** an
  agreed optional field on 206. Raise the finding, pick one, say which.
- **Rating**: show `★★★☆ (registry score)` only when the registry row carries one — never an empty star block.
  Avalonia 12 enables compiled bindings by default, so every new XAML root needs `x:DataType`.

## Acceptance criteria

1. The view opens from a Library row and from a Discover card, and Back returns to that same surface.
2. Description and changelog render as formatted markdown from a fixture with bold, italic, a fenced code block, a
   list and a link; a link click calls the launcher and never opens embedded content (unit test + headless UI).
3. A zero-screenshot mod renders the placeholder carrying the mod name; a 4-screenshot mod shows dots, arrows and
   a lightbox.
4. Each of the four status states renders the documented controls and version-tag colour; while installing, every
   button is disabled.
5. The version list marks the installed version, offers "Install this version" on others, and that action invokes
   Core's update pipeline with the pinned ref.
6. The Backups panel lists only this mod's backups — date, file count, `↩` — with the confirm dialog before any
   write.
7. All three edge cases render their exact strings, and the ZIP case hides versions and changelog.
8. Returning to Discover restores scroll position and filter state.

## Verification

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~MarkdownToInlines"   # renderer unit tests
dotnet test --filter "FullyQualifiedName~ModDetail"           # headless UI (xUnit v3)
dotnet watch run --project GamePolish.Desktop
```

By hand: open `brues-code/pfUI` from Discover (screenshots, versions, changelog), then an addon installed from a
ZIP (versions and changelog gone, source reads "User-supplied archive."). Force the rate-limit path by removing
the PAT and exhausting the 60 req/hour anonymous budget, or with a stub handler returning 403 and
`x-ratelimit-remaining: 0` — the panel must show the notice, not an error. Paste test counts and observed strings.

## References

- `../game-polish-notes/docs/views/mod-detail.md` — Layout, Screenshot Carousel, Left Column, Right Column
  (Status Block, Source Link, Versions List, Backups Panel), Navigation Context, Data Requirements, Edge Cases.
- `../game-polish-notes/docs/views/backups.md` — Restore Flow (confirm dialog text), Context Entry Points item 2.
- `.todo/000-CONVENTIONS.md` — invariants and evidence rules.
- Tasks: **503**, **306**, **501** (origin view + nav context), **209** (update pipeline), **206** (manifest
  `Version`/`VersionKind`/`ModSourceType`), **307** (toasts), **402** (registry `screenshots` + score fields).
