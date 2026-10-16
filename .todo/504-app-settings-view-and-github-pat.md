---
id: 504
title: App Settings view and the GitHub PAT
phase: 5
bucket: v0.2
depends_on: [201, 308]
blocks: []
status: todo
area: desktop
---

# 504 — App Settings view and the GitHub PAT

## Objective

Build the App Settings screen and the secret-storage abstraction the rest of the app needs. The MVP has no
settings view and nowhere to put a GitHub token; `app-settings.json` and its atomic writer belong to task 201, but
nothing renders or edits it. This task **extends task 201** (data root + settings file) and **extends task 308**
(the headless test harness). At the end a user can change theme and accent without a restart, save a PAT into the OS
keychain and see its remaining rate limit, control update checking, manage backups, clear caches, export data, and
reset preferences — with a guarantee that reset does not touch their games, mods or backups.

## Context

- Authoritative note: `../game-polish-notes/docs/views/app-settings.md` (Sections, Footer, Data Requirements,
  Security Notes). Its layout sketch names the exact control labels — use them.
- Depends on **201** (`~/.game-polish/` data root and `app-settings.json`; 201 owns the file format and the atomic
  write, this task owns the UI and the fields) and **308** (headless UI tests, xUnit v3 via
  `Avalonia.Headless.XUnit` — mind the xUnit major trap in the conventions).
- The secret store built here is what **503** injects as its token provider; 503 defines the consumption side
  (`IGitHubTokenProvider`) and renders this store's validated quota.
- Reachable from the Library header gear icon (task 301's shell owns the navigation host). Repo state: greenfield, docs-only.

## Scope

- **Appearance** — Theme Light / Dark / System, persisted and applied immediately without a restart; accent colour
  presets (Blue, Purple, Green, Orange) driving buttons and highlights app-wide.
- **GitHub** — PAT entry stored **only** in the OS keychain (Windows Credential Manager / macOS Keychain),
  validated against the API after entry with the remaining rate limit shown, a **Clear** button, and a help link
  explaining minimal-scope token creation.
- **Backups** — backup location display, total size read-only with "Manage →", retention placeholder → task 604.
- **Updates** — the mod-update-on-session-start toggle and the app-update toggle.
- **Advanced** — data dir display + Change, Clear all caches, Export all data, Reset to defaults with its explicit
  guarantee. **Footer** — app version, "Check for updates", "Licenses".
- `ISecretStore` with Windows + macOS implementations and a test double; redaction rule; exports never include secrets.

## Out of scope

- Retention policy *behaviour* (604) — this view renders a placeholder that visibly links there, not an inert control.
- The in-app auto-updater (605) and the licenses view (609): the footer triggers exist here, the machinery does not.
- The data-dir and backup-location **move** if task 606 owns them — if it does, call into it; if not, it lives here and
  must be atomic (copy, verify, delete, rollback on failure). The Backups view (306) and GitHub client (503) are not built here.

## Implementation notes

- **`ISecretStore` — the cross-platform decision, spike required, timebox 2 hours.** Interface in
  `GamePolish.Core/Secrets/` (pure, substitutable in tests): `GetAsync(key)`, `SetAsync(key, value)`,
  `DeleteAsync(key)`, `ExistsAsync(key)`, all `Task`-returning with a `CancellationToken`; key `github-pat`,
  service name `GamePolish`. Candidates, in order:
  - **`KeySharp`** (goaaats/KeySharp) — Windows Credential Manager + macOS Keychain + Linux Secret Service behind
    one API, `netstandard2.1`, native libs per RID. Verify: its `osx-arm64` native library needs the consuming app
    to be **code-signed** on Apple Silicon, and v0.2 builds stay unsigned until task 610.
  - Windows-only: P/Invoke to `advapi32.dll` (`CredWriteW`/`CredReadW`/`CredDeleteW`, `CRED_TYPE_GENERIC`,
    `CRED_PERSIST_LOCAL_MACHINE`) or `Meziantou.Framework.Win32.CredentialManager`; macOS-only: `Security.framework`.
  - **The spike outcome is part of the deliverable**: name the option chosen, the command that proved it on both
    RIDs, and confirm that an unavailable OS store reports unavailable instead of crashing — the app stays usable
    with no token. Test doubles: `InMemorySecretStore`, and `NullSecretStore` for keychain-less CI.
- **Redaction rule.** The token lives only inside a `Secret` wrapper whose `ToString()` returns `"***"`, revealed by
  one `Reveal()` in exactly one place (the authorization header). Never interpolated into a log message, exception,
  toast, telemetry payload or crash report; the settings ViewModel logs "token updated" plus the length at most.
- **Theme**: `Application.Current.RequestedThemeVariant = ThemeVariant.Light | ThemeVariant.Dark |
  ThemeVariant.Default` (`Default` follows the OS = "System"), applied live and persisted to `app-settings.json`.
  **Accent presets**: override the accent resource per variant — a `ThemeDictionaries` entry keyed `Light`/`Dark`
  overriding `SystemAccentColor`, or a `PaletteResources` overlay on `FluentTheme` swapped in
  `Application.Current.Resources`/`Styles` at runtime. Confirm the exact type names against Avalonia 12.1.4's
  `Avalonia.Themes.Fluent` (Avalonia 12 removed and renamed things) and paste the resolved API into the report.
- **PAT entry**: a `TextBox` with `PasswordChar` set so the value is masked. On save, write to `ISecretStore`, then
  validate with `GET /user` and read `GET /rate_limit` via 503's client (that call does not consume quota). Display
  exactly `✓ Token valid · Rate limit: 4,823 / 5,000 remaining`, numbers with thousands separators under
  `CultureInfo.InvariantCulture`. An invalid token → inline error and **no** keychain write; **Clear** calls
  `DeleteAsync` and empties the field.
- **Help link**: opens GitHub's fine-grained PAT documentation via
  `TopLevel.GetTopLevel(control)!.Launcher.LaunchUriAsync(uri)`; copy explains read-only, public-repo scope.
- **Backups — a documentation conflict to resolve, do not paper over it.** `app-settings.md` puts the default
  backup location at `~/.game-polish/backups/`, while `000-CONVENTIONS.md`, `backups.md` and task 205 use
  `~/.game-polish/<game_id>/backups/<timestamp>/`. The notes win, but this is almost certainly shorthand rather
  than a second root — 205's engine already writes the per-game path. Decide and record it before implementing
  **Change**, because that action must migrate *every* game's backup tree atomically. Total size is computed by
  Core asynchronously at view open with a spinner until ready; **Manage →** navigates to the Backups view (306).
  Retention renders `[Keep all ▾]` as a placeholder linking to 604's sheet, visibly marked not-yet-active.
- **Updates**: "Check for mod updates" — `On session start` / `Never`; the sweep itself is 503, which reads this setting. "Check for app updates" — `Automatically` / `Never`.
- **Advanced**: data dir display (`~/.game-polish`) with Change (requires app restart — say so in the confirm
  dialog). **Clear all caches** deletes cached registry JSON only — "Does not delete backups or manifests" — and must
  not touch the keychain entry. **Export all data** builds a ZIP of manifests, game configs and backups.
  **Reset to defaults** resets appearance and update preferences only and **does NOT remove games, mods, or backups**: that sentence goes in the confirm dialog verbatim and in a test.
- **Exports exclude secrets by construction**: the export enumerates manifests, game YAML/config and backup trees,
  copies `app-settings.json` with no secret material, and never calls `ISecretStore` — so it could not leak even if
  someone later added a field to the settings file.
- **Footer**: version from the assembly informational version, which must equal `<Version>` in
  `GamePolish.Desktop.csproj` (the release `check-version` guard depends on that equality). "Check for updates" is
  the manual trigger; "Licenses" opens a scrollable in-app view — until task 609 builds the real aggregation,
  render the direct dependency list with a note pointing at 609 rather than an empty screen.
- **Settings model**: an `AppSettings` record serialized with `System.Text.Json` (source-generated context) with an
  explicit `version` field for migrations; unknown fields must round-trip rather than being silently dropped. Reuse
  201's atomic write — do not write the settings file by hand.

## Acceptance criteria

1. Theme changes apply immediately (no restart) and survive a restart; System follows the OS live.
2. Accent presets change button/highlight colours app-wide and persist.
3. A valid PAT is stored in the OS keychain and shows `✓ Token valid · Rate limit: N / M remaining` with the real
   remaining value; an invalid token writes nothing and shows an inline error.
4. **The token survives an app restart** (the masked field is repopulated from the keychain) **and can be cleared**
   — after Clear, `ExistsAsync("github-pat")` is false.
5. A test asserts the token never appears in `app-settings.json` **or** the log file, over real file contents after
   a save + restart cycle, not a code review.
6. Reset to defaults resets appearance + update preferences and leaves the manifest, games list and backup trees
   byte-identical (assert with hashes before/after).
7. Export produces a ZIP containing manifests, game configs and backups, and no secret material.
8. Clear caches deletes registry caches only — backups and manifests still present afterwards.

## Verification

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~SecretStore"     # platform selection + test double
dotnet test --filter "FullyQualifiedName~AppSettings"     # theme persistence, reset, export

# the token must not be on disk anywhere under the data root
rg -n 'ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}' ~/.game-polish/ ; echo "exit=$? (1 = no match = pass)"
rg -n 'github-pat|ghp_|github_pat_' ~/.game-polish/app-settings.json    # also checked: the log file
cmdkey.exe /list | grep -i gamepolish                           # Windows: target name only, never the secret
security find-generic-password -s "GamePolish" 2>&1 | head -5    # macOS: metadata only, never add -w
dotnet watch run --project GamePolish.Desktop                    # restart: masked field is populated
```

Paste both greps' output after saving a token and restarting. A passing P/Invoke test on this Windows host is
**not** evidence for macOS — say plainly that the macOS path is verified by CI (`macos-latest`) or a real Mac.

## References

- `../game-polish-notes/docs/views/app-settings.md` — Layout, Sections (Appearance, GitHub, Backups, Updates,
  Advanced), Footer, Data Requirements, Security Notes.
- `../game-polish-notes/docs/views/backups.md` — Configure retention (604's sheet), Context Entry Points.
- `.todo/000-CONVENTIONS.md` — secrets go in the OS keychain, never in a JSON file, never in a log, never in an
  export; local-state layout.
- Tasks: **201** (data root, `app-settings.json`, atomic write), **308** (headless tests), **503** (token provider
  + quota), **604** (retention), **605** (auto-update), **609** (licenses), **306** (Backups view), **610**
  (signing — affects the macOS keychain spike), **205** (per-game backup paths).
