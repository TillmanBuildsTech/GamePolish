# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

**Where the design notes live:** the detailed specs (per-view behaviour, auth, CI/release, the game YAML) are *not* in this repository. They sit beside it at `../game-polish-notes/docs/` and are excluded from version control on purpose. Read them when implementing a feature; never commit them here.

## Project Overview

**Game Polish** is a cross-platform mod/addon manager desktop app. It lets users discover, install, and manage mods for supported games — using GitHub repos or user-supplied ZIPs as the source of truth, never proprietary download services. It also lets users safely tweak game settings (INI files, etc.).

## Tech Stack

- **Language**: C# / .NET 10 (LTS, TFM `net10.0`)
- **Desktop UI**: [Avalonia UI](https://avaloniaui.net/) 12.1.4 — cross-platform, native rendering on Windows and macOS (not Mac Catalyst)
- **Console UI**: WinUI 3 via Microsoft GDK (Xbox Series / One — future)
- **Core library**: `GamePolish.Core` — pure .NET 10 class library, no UI dependencies
- **Cloud backend**: `GamePolish.Service` — ASP.NET Core minimal API (hosted). Handles auth proxy + device registration. Premium/paid tier is planned but deferred.
- **Game developer SDK**: `GamePolish.SDK` — NuGet package embedded by game devs for console mod sync
- **Targets**: Windows 10/11, macOS Apple Silicon (arm64), Xbox (future)
- **Package manager**: `dotnet` CLI

### Pinned Versions (decided 2026-10-15)

| Thing | Value | Why |
|---|---|---|
| SDK | `10.0.401`, pinned in `global.json` with `rollForward: latestFeature` | Only SDK installed on the dev machine. The band-level roll-forward means CI can use a newer 10.0.x (e.g. 10.0.402) without breaking local builds |
| Target framework | `net10.0` | .NET 10 is the current LTS (supported to 2028-11-14). .NET 8's support ended 2026-11-10, and .NET 11 was only at RC.2 when this was decided |
| Avalonia | `12.1.4` | The only Avalonia line shipping a native `net10.0` asset group. 11.3.22 ships net6.0/net8.0/netstandard2.0 only |
| Test framework | xUnit v3, via `Avalonia.Headless.XUnit` 12.x | Avalonia 12's headless integration depends on `xunit.v3.extensibility.core`; Avalonia 11's depends on xUnit v2 |

Do not downgrade the TFM without also revisiting the Avalonia pin: an 11.x Avalonia on `net10.0` resolves its `net8.0` assets, which is unsupported-by-omission rather than tested.

**Test runner — decided: Microsoft.Testing.Platform.** `global.json` carries `{"test":{"runner":"Microsoft.Testing.Platform"}}`. This is not a preference, it is forced: the SDK 10 VSTest target hard-errors for any MTP application ("Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later", `microsoft.testing.platform.msbuild`'s `_MTPBeforeVSTest` target), and every xUnit v3 project *is* an MTP application. Because `Avalonia.Headless.XUnit` 12.x forces v3 on the desktop tests, **all** test projects are xUnit v3 — the switch is all-or-nothing, so one xUnit v2 project anywhere breaks the whole `dotnet test` run. TRX reporting is MTP's own: `dotnet test --report-trx` (one uniquely named file per test project), never `--logger "trx;..."`.

## Build & Dev Commands

```bash
# Restore all packages
dotnet restore

# Run desktop app with hot reload
dotnet watch run --project GamePolish.Desktop

# Build production desktop app
dotnet publish GamePolish.Desktop -c Release

# Run all tests (Microsoft.Testing.Platform; xUnit v3)
dotnet test

# Guard against a silently-empty run — exits 9 when fewer than N tests run
dotnet test --minimum-expected-tests 4

# Run a single test by name — scope it to one project: an unscoped filter exits 8
# (MTP's "zero tests ran") because the other project matched nothing
dotnet test GamePolish.Core.Tests/GamePolish.Core.Tests.csproj --filter-method <Namespace.Class.Method>

# Test results as TRX — one uniquely named file per test project
dotnet test --report-trx

# Format / lint
dotnet format

# Run the cloud service locally
dotnet run --project GamePolish.Service
```

### Scaffolding (one-time — start from the templates, don't hand-write project files)

The Avalonia template defaults already match the pins above, so name the template and let it choose:

```bash
# Desktop app — avalonia.mvvm defaults are net10.0 + Avalonia 12.1.4 + CommunityToolkit.Mvvm
dotnet new avalonia.mvvm -o GamePolish.Desktop -n GamePolish.Desktop

# Non-UI projects use the built-in SDK templates
dotnet new classlib -o GamePolish.Core -n GamePolish.Core
dotnet new sln -n GamePolish
dotnet sln add GamePolish.Core GamePolish.Desktop
```

- Use **`avalonia.mvvm`**, never `avalonia.xplat` — xplat adds browser/mobile/Android/iOS heads this product does not ship.
- Item templates available for later: `avalonia.window`, `avalonia.usercontrol`, `avalonia.templatedcontrol`.
- Scaffold `GamePolish.Desktop` **last** so it references real `GamePolish.Core` contracts instead of imagined ones.
- `avalonia.mvvm` publishes Avalonia 12.1.4 and *not* `Avalonia.Diagnostics`: that package has no 12.x release. DevTools in 12 is `AvaloniaUI.DiagnosticsSupport`, which the template already references and wires as `.WithDeveloperTools()` under DEBUG.

**Test projects — the xUnit version trap.** Every test project here is **xUnit v3**, scaffolded with `dotnet new xunit3` (from the `xunit.v3.templates` package, installed on this machine). That produces an `OutputType=Exe` project on `xunit.v3.mtp-v2` with `TestingPlatformDotnetTestSupport=true` — and it rewrites `global.json` to opt the repo into MTP, which is now the intended state. Its two stale defaults need fixing: TFM `net8.0` → `net10.0`. `dotnet new xunit` scaffolds **xUnit v2** and must not be used here: under MTP it cannot run at all, and the failure appears as a refused project rather than a failing test. v3 is not optional for the desktop tests because `Avalonia.Headless.XUnit` 12.x depends on `xunit.v3.extensibility.core`; confirm any resolved Avalonia headless package is a 12.x line.

Verified 2026-10-15 on this machine: the generated desktop project (App.axaml (+.cs), Program.cs, ViewLocator.cs, ViewModels/, Views/, Assets/, app.manifest) builds clean — 0 warnings, 0 errors — against SDK 10.0.401 on `net10.0`.

## Architecture

### Solution Structure

All projects share one `.sln`. `GamePolish.Core` is a direct project reference — no IPC boundary, no local API. `GamePolish.Service` is the cloud backend that every client (desktop, console, website) authenticates against.

### Core Engine Concepts

**Game Config (YAML)** — Each supported game ships a YAML descriptor (in `games/`) that tells the engine:
- Where the game is installed (detection heuristics or user override)
- How mods are structured (what folders/files to place where)
- What settings files exist and their schema (for the settings editor)
- Registry/discovery endpoint (URL to the game's `registry.json`)

**Mod Registry (JSON)** — Per-game JSON file (hosted in a GitHub repo) listing available mods: name, author, description, source repo URL, tags, screenshots. The app fetches and caches this; it is the discovery layer.

**Installer Engine (`GamePolish.Core`)** — Given a mod source (GitHub repo URL or local ZIP), the engine:
1. Clones/downloads to a temp location
2. Reads the game YAML to determine destination paths
3. Creates a timestamped backup of any files it will overwrite
4. Moves/copies files to the correct game directories
5. Records the install in a local manifest (`~/.game-polish/<game>/manifest.json`)

**Backup System** — Before any file mutation (mod install, settings change), the engine snapshots affected files into `~/.game-polish/<game>/backups/<timestamp>/`. Restores are always possible from the UI.

**Settings Editor** — Reads INI/TOML/JSON game config files, presents a structured UI (driven by the game YAML schema), and writes changes back — always after creating a backup.

**Two Install Models** — PC (Desktop) has direct filesystem access; `GamePolish.Core` installs mods into game directories. Console (Xbox, future PS) has no filesystem access to games; `GamePolish.SDK` embedded in the game handles mod loading, with `GamePolish.Service` as the sync relay.

**Game Developer SDK (`GamePolish.SDK`)** — A NuGet package game devs ship with their title. On console, the game calls `GamePolishClient.SyncAsync()` on launch; the SDK authenticates via platform identity (Xbox Live / PSN), fetches the user's active mod list from `GamePolish.Service`, and the game loads those mods. Game Polish handles all UX; developers integrate only a few hooks.

### Directory Layout (planned)

```
game-polish/
├── global.json                # SDK pin — 10.0.401, rollForward latestFeature
├── GamePolish.Core/           # .NET 10 class library — pure logic, no UI
│   ├── Registry/              # Mod registry fetch/cache
│   ├── Manifest/              # Install tracking
│   ├── Backup/                # Snapshot/restore engine
│   ├── Settings/              # INI/TOML/JSON config read-write
│   └── Contracts/             # Shared interfaces, DTOs, game YAML schema
├── GamePolish.SDK/            # NuGet package for game developers
│   ├── ModSyncClient/         # Fetches user's mod list from Service
│   └── Contracts/             # Shared types between SDK and Core
├── GamePolish.Desktop/        # Avalonia UI — Windows 10/11 + macOS arm64
│   ├── Views/
│   ├── ViewModels/
│   └── Controls/
├── GamePolish.Xbox/           # WinUI 3 / GDK — Xbox Series/One (future)
├── GamePolish.Service/        # ASP.NET Core minimal API — cloud backend
│   ├── Auth/                  # Supabase auth proxy, token exchange
│   ├── Sync/                  # Cloud backup endpoints
│   └── Console/               # Console SDK relay endpoints
├── games/                     # Game descriptor YAMLs (one per supported game)
│   └── octowow.yaml           # Current supported game; see ../game-polish-notes/docs/games/octowow.md
├── GamePolish.sln
└── CLAUDE.md
```

## Key Design Constraints

- **Never harm the user's system.** Every destructive operation requires a prior backup. No operation should touch files outside the game's install directory and `~/.game-polish/`.
- **Trust model**: mods come from GitHub repos or user-supplied ZIPs only. No proprietary CDNs. The app never executes downloaded code — it only moves files.
- **Security**: No shell execution of mod contents. Validate all paths in `GamePolish.Core` to prevent directory traversal. `GamePolish.Service` enforces checks server-side — never trust the client.
- **Game support is additive**: adding a new game means adding a YAML + a registry JSON entry — no code changes required for a well-behaved game.

## Game Config YAML Schema (draft)

The current supported game is **OctoWoW** (`games/octowow.yaml`). See `../game-polish-notes/docs/games/octowow.md` for the authoritative YAML. Skyrim SE and others are aspirational future games.

```yaml
id: octowow
name: "OctoWoW"
install_detection:
  manual_only: true
  verify_file: "WoW.exe"
mod_layout:
  type: addons_folder
  destination: "Interface/AddOns"
registry_url: "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json"
```

## Registry JSON Schema (draft)

```json
{
  "game_id": "octowow",
  "updated_at": "2026-10-15T00:00:00Z",
  "mods": [
    {
      "id": "pfui",
      "name": "pfUI",
      "author": "brues-code",
      "description": "A complete UI replacement for vanilla WoW.",
      "source": "https://github.com/brues-code/pfUI",
      "tags": ["ui", "essential"],
      "thumbnail": "https://..."
    }
  ]
}
```

## App Screens

Documented in `../game-polish-notes/docs/views/`. Current scope:

- **Library** — installed games, per-game mod list with enable/disable/uninstall
- **Discover** — browse the registry for a selected game, filter by tags, install
- **Mod Detail** — description, changelog, version history
- **Settings Editor** — structured INI/config editor (hidden for OctoWoW; no settings files defined)
- **Backups** — list of backups with restore action
- **App Settings** — theme, backup retention, GitHub PAT, cloud backup (BYO S3)
- **Cloud Backup & Sync** — BYO S3 setup, sync history, restore wizard
- **Auth / Accounts** — account creation, login, device management (documented in `../game-polish-notes/docs/auth-accounts.md`)
