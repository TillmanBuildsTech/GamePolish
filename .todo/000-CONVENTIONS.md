---
id: 000
title: Conventions and definition of done
phase: 0
bucket: mvp
depends_on: []
blocks: []
status: reference
area: process
---

# 000 — Conventions and definition of done

This is the shared contract that every work order in `.todo/` inherits. It is a reference, not a task:
there is nothing to build here. Read it, then read your own task file.

---

## What Game Polish is

A cross-platform mod/addon manager desktop app for Windows 10/11 and macOS (Apple Silicon). Users
discover, install, and manage game mods, and safely edit game settings. Mod sources are GitHub repos
or user-supplied ZIPs — never proprietary download services. Nothing is ever executed; mod content is
only ever moved into place.

The first supported game is **OctoWoW** (vanilla WoW 1.12.1 private server), because the incumbent
addon manager there is unsafe and Windows-only. On OctoWoW, an "addon" is a folder inside
`<install>/Interface/AddOns/`.

## Where the truth lives

| Source | Authority |
|---|---|
| `../game-polish-notes/docs/**` | **Authoritative.** Per-view behaviour, auth, CI/release, the OctoWoW descriptor. Not committed on purpose — never `git add` it. |
| `CLAUDE.md` (repo root) | Agent-facing summary. Auto-loaded by Claude Code. |
| `.todo/*.md` | This plan. Work orders, not specifications. |
| The skill `game-polish-development` | Workflow and toolchain mechanics. |

If the design notes and this directory disagree, the notes win and this directory should be corrected
in the same change.

## Stack, pinned. Do not re-litigate.

| Thing | Value |
|---|---|
| SDK | `10.0.401`, pinned in `global.json` with `rollForward: latestFeature` |
| Target framework | `net10.0` |
| UI | Avalonia `12.1.4`, MVVM via `CommunityToolkit.Mvvm` |
| Tests | xUnit v3 for anything touching Avalonia headless; xUnit v2 is acceptable for pure-Core tests |
| Runner | **Open decision** — see task `102`. Settle it there, then never mix. |
| Core | `GamePolish.Core` — pure .NET, **no UI references, ever** |

Avalonia 11.x has no `net10.0` asset group, so moving the TFM and the Avalonia pin are one decision,
not two.

## Hard invariants

These are product promises. A task is not done if it breaks one, regardless of whether it "works".

1. **Backup before mutation.** Any operation that writes into a game directory (install, update,
   uninstall, enable, disable, settings write) creates a timestamped snapshot under
   `~/.game-polish/<game_id>/backups/<timestamp>/` **before the first byte is written**. A mutation
   path with no snapshot call is a bug even if the operation succeeds.
2. **Never execute downloaded content.** No shelling out to scripts inside a mod, no running
   mod-provided git hooks, no build steps from a mod repo, no loading mod assemblies.
3. **Validate every path that originates from a mod source.** Reject traversal out of the destination,
   absolute paths, symlinks/junctions that escape, and NTFS alternate data streams. GitHub is not a
   trusted input.
4. **Touch exactly two roots:** the game's install directory and `~/.game-polish/`. No registry writes,
   no user-profile writes, no other game's folders. Temp staging is fine.
5. **Trust model:** GitHub repos and user-supplied ZIPs only.
6. **Game support is additive.** A new game is a YAML file plus a registry entry. If supporting a
   well-behaved new game requires touching C#, the design is wrong — treat that as a finding.
7. **Server-side checks stay server-side.** Client claims are never trusted (applies from phase 7 on).

## Local state layout

```
~/.game-polish/
├── app-settings.json                 # non-sensitive app prefs only
├── games.json                        # tracked games + install paths
└── <game_id>/
    ├── manifest.json                 # installed mods, versions, enabled state
    ├── registry-cache.json           # cached registry + etag/last_fetched
    └── backups/
        ├── index.json                # backup event metadata
        └── <timestamp>/<original_relative_path>
```

Secrets (GitHub PAT, S3 keys, auth tokens) go in the **OS keychain** — Windows Credential Manager,
macOS Keychain. Never in a JSON file, never in a log, never in an export.

## Work order file format

```markdown
---
id: 205
title: Backup engine
phase: 2
bucket: mvp            # mvp | v0.2 | v1.0 | post-v1
depends_on: [204]
blocks: [208, 209]
status: todo           # todo | in-progress | done | blocked
area: core             # core | desktop | service | sdk | ops | docs
---

# 205 — Backup engine

## Objective            One paragraph. What exists at the end that does not exist now.
## Context              Everything the agent needs that is not in this file: what the product is,
                        which design note defines the behaviour, what state the repo is in.
## Scope                Bulleted deliverables.
## Out of scope         The adjacent things this task must NOT do.
## Implementation notes Concrete decisions, signatures, gotchas. Not a tutorial.
## Acceptance criteria  Testable statements. Each one verifiable by a command or a file.
## Verification         Exact commands to run and what output proves success.
## References           Design-note paths with section names; related task ids.
```

## Definition of done

| Change type | Not done until |
|---|---|
| Core logic | A unit test beside it; no Avalonia/UI reference added to Core |
| Anything consuming a path from a mod source | A test with a traversal payload (`../`, absolute, symlink) asserting rejection |
| Install / update / uninstall | A test asserting the backup directory exists **before** the first write, and that restore returns prior state |
| Desktop UI | A headless UI test, or an explicit written statement that it was only verified in `dotnet watch` |
| CI / release config | `../game-polish-notes/docs/ci-release.md` updated in the same commit |
| A new supported game | Only a YAML + registry entry; if C# changed, say so loudly |

**Every task also ends with:** `dotnet build` and `dotnet test` run from the repo root, with the real
pass/fail counts pasted into the report, and `git status --short` checked. Formatting must satisfy
`dotnet format --verify-no-changes` once task `103` lands.

## Evidence rules

- Report **commands and their real output**, not narratives. "Tests pass" with no run is the failure
  mode this plan is written to prevent.
- If a step is blocked, say so and say why. Never substitute plausible-looking fabricated output for
  a result you could not produce.
- A delegated subagent's summary is a claim. The parent re-runs the gates before reporting success.

## Pitfalls that have already bitten this repo

1. **Assuming projects exist.** This repo was docs-only. Check `git status --short` first.
2. **Hand-editing `.sln` / `.csproj`** when `dotnet sln add` / `dotnet add package` does it correctly.
3. **Repo-wide `dotnet format`** on a branch — it rewrites unrelated files. Scope with `--include`.
4. **Wrong xUnit major.** `dotnet new xunit` gives xUnit **v2**; `Avalonia.Headless.XUnit` 12.x needs
   **v3** (`xunit.v3.extensibility.core`).
5. **Delegating without isolation** — two agents editing `main` in the working copy. Use a branch or a
   worktree.
6. **Writing a release tag before bumping `<Version>`** — the `check-version` guard fails after the
   fact, and tags are expensive to move.
7. **`dotnet new avalonia.xplat`** — it adds browser/mobile/Android/iOS heads this product does not
   ship. Use `avalonia.mvvm`.
