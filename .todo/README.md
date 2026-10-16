# Game Polish — Delivery Roadmap

This directory is the build plan for Game Polish. Every file here is a **self-contained work order**
for one agent. A work order must be completable without reading any other file in `.todo/` — if you
find yourself needing context that isn't in your work order, that is a bug in the work order: fix the
work order first, then do the work.

Read `000-CONVENTIONS.md` before starting any task. It carries the invariants, the definition of done,
and the evidence rules that every task inherits.

---

## The three release buckets

### 1. MVP — `v0.1.0`: "an OctoWoW addon manager that cannot lose your data"

One game, one platform story, one job: install and manage OctoWoW addons on Windows and macOS, and
never destroy something the user can't get back.

**MVP is in scope:**
- Solution, test projects, CI (format + build + test), `.editorconfig`
- `GamePolish.Core`: game descriptor YAML, `games.json`, manifest, path validation, backup engine,
  git/ZIP source acquisition, install / update / uninstall / enable-disable
- Registry fetch + cache, and a **Discover-lite** list to install from (we cannot ship a mod manager
  where every install starts with pasting a URL)
- Desktop (Avalonia): Library (games + mod list + row actions), Add Game, Add Mod, Backups + restore,
  toasts / progress / error drawer
- The `octowow.yaml` descriptor and the `game-polish/registries` repo with the seed registry
- Unsigned `dotnet publish` output for `win-x64` and `osx-arm64` — runnable, not installable

**MVP is explicitly out of scope:** accounts, any cloud feature, the settings editor, per-mod
changelogs/version history, in-app auto-update, signed installers, Xbox, any second game.

**Why this is the MVP and not something smaller:** the incumbent (`OctoUpdater`) already installs
addons; it just does it unsafely (no backup before update) and only on Windows. A version of Game
Polish that installs addons without the backup engine is strictly a rewrite of the thing we are
replacing. The backup engine is MVP, and it comes first.

### 2. Release after MVP — `v0.2` → `v1.0`: "the product"

The gap between "works for me on OctoWoW" and "a stranger can download it and succeed".

- **`v0.2` — Discovery and identity.** Full Discover (tags, sort, staleness, offline states), Mod
  Detail (changelog, version list, per-mod backups), GitHub API client + rate limits, update checks on
  session start, the App Settings view, GitHub PAT in the OS keychain.
- **`v1.0` — Breadth and shippability.** Structured Settings Editor (+ raw mode + diff), backup
  retention and disk usage, in-app auto-updater, data dir move / export, **a second game to prove the
  "game support is additive" claim**, accessibility pass, licenses view, code signing + notarization,
  real installers (`.msix` / WiX `.exe` / `.dmg`).

`v1.0` is the "main release": tagged, signed, installable, on two platforms, with the website's
`/releases/latest/download/...` links resolving to it.

### 3. After v1 — `v2+`: "the platform"

Everything that requires a server, an account, or another device.

- `GamePolish.Service` (ASP.NET Core minimal API at `gamepolish.app`) + Supabase project, schema, RLS
- Auth: email/password, Google OAuth via PKCE + `gamepolish://` custom URI scheme, device registry
- Website + account portal at `gamepolish.com`
- Cloud Backup & Sync: BYO S3 credentials, incremental snapshots, restore-from-cloud wizard,
  multi-machine conflict resolution, opt-in client-side encryption, game saves
- Managed "Game Polish Cloud" tier + Stripe billing (the thing accounts were built for)
- Console: `GamePolish.SDK` NuGet + `GamePolish.Xbox` (WinUI 3 / GDK)

**The ordering rule for this bucket:** the account system exists to gate a *paid storage tier*, and
that tier is only worth building once the desktop app has users. Do not start this bucket while the
`v1.0` bucket still has open work.

---

## Phase map

| Phase | Files | Bucket | Theme |
|---|---|---|---|
| 1 | `101`–`104` | MVP | Foundations: solution, test projects, format gate, CI |
| 2 | `201`–`213` | MVP | Core engine — the whole safety-and-install spine |
| 3 | `301`–`308` | MVP | Desktop UI |
| 4 | `401`–`405` | MVP | Ship `v0.1.0`: descriptors, registry repo, packaging, acceptance |
| 5 | `501`–`504` | v0.2 | Discovery, detail, updates, settings + PAT |
| 6 | `601`–`611` | v1.0 | Settings editor, breadth, distribution |
| 7 | `701`–`715` | post-v1 | Service, accounts, cloud, consoles |

---

## Dependency order

```
PHASE 1  foundations (101 → 102 → 103 → 104)
   │
   ▼
PHASE 2  core spine
   201 data root ── 202 descriptors ── 203 games.json
        │                 │
        └──── 204 path safety ──┬── 205 backup ── 206 manifest
                                │
                        207 source acquisition
                                │
                    208 install ─ 209 update ─ 210 uninstall/enable-disable
                                │
                     211 registry ─ 212 operation log ─ 213 CLI harness
   │
   ▼
PHASE 3  desktop (301 shell → 302/303 library → 304 add mod → 305 discover
                  → 306 backups → 307 toasts → 308 headless tests)
   │
   ▼
PHASE 4  ship v0.1.0 (401 descriptor → 402 registry → 403 package → 404 tag → 405 accept)
   │
   ▼
PHASE 5  v0.2 (501 discover full · 502 mod detail · 503 github api + updates · 504 settings/PAT)
   │
   ▼
PHASE 6  v1.0 (601 settings io → 602 editor → 603 raw/diff · 604 retention · 605 auto-update
                · 606 data dir/export · 607 second game · 608 accessibility · 609 licenses
                · 610 signing · 611 installers)
   │
   ▼
PHASE 7  post-v1 (701 service → 702 supabase → 703/704 auth → 705 devices → 706 website
                  · 707 s3 creds → 708 sync → 709 restore wizard → 710 conflicts
                  · 711 encryption · 712 saves · 713 managed tier · 714 sdk → 715 xbox)
```

A work order's own frontmatter (`depends_on` / `blocks`) states its edges. Within a phase, tasks that
share no edge can run concurrently — that is the intended use of parallel agents. `204` (path safety)
and `205` (backup) are the two tasks everything else leans on; get them right and tested before
building on them.

---

## How to consume a work order

1. Read `000-CONVENTIONS.md`.
2. Read your task file end to end. It names the design-notes file that defines the behaviour.
3. Read that design-notes file. **The design notes are authoritative.** They live outside this repo at
   `../game-polish-notes/docs/` and are deliberately *not* committed — never `git add` them.
4. Check the frontmatter `depends_on` tasks are actually done (`git log`, the code they should have
   produced). If not, stop and say so instead of guessing at their absence.
5. Do the work. Run the verification commands in the file. Report **command output**, not summaries.
6. When the acceptance criteria in the file are met, set `status: done` in the frontmatter and commit
   with a message naming the task id (`feat(205): backup engine`).

## Ground rules for every task

- **The repo is greenfield via docs.** Verify with `git status --short` and the tree before claiming
  anything exists. No type named in the design notes exists until a task creates it.
- **Two roots only.** A task may write to a game's install directory and to `~/.game-polish/`. Nothing
  else — no registry, no user profile, no other game's folder. Staging under the OS temp dir is fine.
- **Verify in the parent.** When work is delegated, `dotnet build`, `dotnet test`,
  `dotnet format --verify-no-changes` and a read of `git diff` are the gates. A subagent's "done" is a
  claim, not a fact.
- **Line references in this directory** are `path:line` into this repo unless they name a design note.

---

## Open inputs from the user (check before assuming)

These are not task decisions — they are facts about the outside world that the plan *depends on* and
that no agent can settle alone. Verified 2026-10-15 on this machine.

1. **The repo has no git remote.** `git remote -v` is empty. Nothing can be tagged, pushed or released
   until a remote exists, so tasks `404`, `610` and `611` are blocked on it.
2. **The `game-polish` GitHub owner does not exist.** `gh api orgs/game-polish` → 404,
   `gh api orgs/game-polish/repos` → 404, and `gh api repos/game-polish/registries` → 404. The
   authenticated account is `Tillman32` (Brandon; public repos live under `Tillman32/*` and
   `MorphStack/*`). There is **no** `registries` repository anywhere under that account.
3. **Therefore three hardcoded URLs in this plan are placeholders, not facts.** They are the
   descriptor's `registry_url` (task `401`), the registry repo location (task `402`), and the release
   download links the website relies on (tasks `404`, `611`, `706`):

   ```
   https://raw.githubusercontent.com/game-polish/registries/main/octowow.json
   https://github.com/game-polish/game-polish/releases/latest/download/<filename>
   ```

   **Decide the owner before starting task `402`.** Either create the `game-polish` org and the two
   repositories, or pick a real owner (e.g. `Tillman32/game-polish` and `Tillman32/registries`) and
   update `registry_url` in `401` in the same change. The download filenames must stay stable forever
   once published — the owner is cheap to change now and expensive later.

Everything else in this directory is actionable today.

