---
id: 611
title: Installers — MSIX, WiX or NSIS, and DMG
phase: 6
bucket: v1.0
depends_on: [610, 403]
blocks: []
status: todo
area: ops
---

# 611 — Installers — MSIX, WiX or NSIS, and DMG

## Objective

The release produces real installers with the exact names `ci-release.md` fixes — a Windows `.msix` and a standalone
`.exe`, a macOS `.dmg` and `.app.tar.gz`, all signed (`610`), with stable filenames the website's download links resolve to.

## Context

Authoritative: `../game-polish-notes/docs/ci-release.md` (Build Matrix; Release Artifact Names; the
`publish` job; Website Download Links; Branch & Tag Strategy). **This task extends `610` (signing) and
`403` (packaging).** `403` produced a plain `dotnet publish` output; this task wraps it in real installers.

**One inconsistency to fix, not reproduce.** `Release Artifact Names` uses *versioned* names
(`GamePolish_1.2.0_x64.msix`) while `Website Download Links` uses *unversioned* names. A versioned upload will
**not** satisfy the unversioned website link, so the `publish` job must upload the versioned artifacts **and**
unversioned aliases (`GamePolish_x64-setup.exe`, `GamePolish_aarch64.dmg`). Otherwise it is a silent 404 in production.

## Scope

- Windows `.msix` (primary) and a standalone `.exe` built with **WiX or NSIS** (see the recommendation).
- macOS `.dmg` (via `create-dmg`) and `.app.tar.gz` (for the updater).
- Exact artifact filenames (versioned) plus the unversioned aliases the website needs.
- Install / uninstall / upgrade behaviour stated and tested.
- MSIX identity and package-family decision, and its consequence for `gamepolish://` (`704`).
- Per-user vs per-machine install choice.

## Out of scope

- Signing/notarization mechanics (`610`).
- The auto-updater (`605`), which consumes `.exe` / `.app.tar.gz`.
- Linux packages (not shipped).
- Implementing `gamepolish://` registration — only the identity decision `704` will depend on.

## Implementation notes

- **Exact names (versioned = built and signed; unversioned = aliases the site links to):**
  - `GamePolish_<version>_x64.msix`  (+ alias `GamePolish_x64.msix`)
  - `GamePolish_<version>_x64-setup.exe`  (+ alias `GamePolish_x64-setup.exe`)
  - `GamePolish_<version>_aarch64.dmg`  (+ alias `GamePolish_aarch64.dmg`)
  - `GamePolish_<version>_aarch64.app.tar.gz`
  - `checksums.txt` (SHA-256 of every artifact)

  `<version>` is the tag's version without the `v` (e.g. `1.0.0`) and must equal the csproj `<Version>`
  (`404`'s `check-version` gates that). **These names must stay stable forever** because `gamepolish.com`
  links to `/releases/latest/download/<name>`; renaming an artifact breaks every download link on the site
  at once. The alias step is what keeps the versionless links working.
- **Windows `.exe`: choose WiX (Burn bundle), and here is why.** WiX produces a real MSI-based bundle with a proper
  Add/Remove Programs entry, upgrade detection via a fixed `UpgradeCode`, and both per-user and per-machine installs,
  driven from XML you check in. NSIS is smaller but pushes upgrade/uninstall bookkeeping onto hand-written script.
  **Recommend WiX v5** (`dotnet tool install --global wix`, `wix build`); NSIS is the fallback. The filename is fixed above.
- **MSIX build.** Author an `AppxManifest.xml` (or the Windows Application Packaging project) with
  `Identity Name="GamePolish"` (fixed and frozen), `Publisher="CN=<cert subject>"` — the Publisher **must**
  match the signing certificate's subject (`610`) or install fails — `PublisherDisplayName`, and
  `Version="x.y.z.0"` (MSIX requires a 4-part version; the fourth part is the revision, normally 0). The
  **Package Family Name** is `<Name>_<hash of publisher>`; it is derived, not chosen, and changes if
  either `Name` or `Publisher` changes.
- **Why the identity decision is load-bearing for `704`.** The `gamepolish://` URI scheme is registered in
  the MSIX manifest under `<uap:Extension Category="windows.protocol"><uap:Protocol Name="gamepolish"/>` —
  and protocol handlers are bound to the package identity. Changing `Name` or `Publisher` later changes the
  PFN and drops the association (and existing installs' registrations), so **decide and freeze the identity
  in this task**; `704` must not have to revisit it. Note the tension with invariant 4 (no registry writes
  at runtime): the standalone `.exe` registers the scheme under `HKCU\Software\Classes\gamepolish` **as
  part of installation** — an installer action, not app runtime, and the one place a registry write is
  legitimate. Say so here so `704` does not violate the invariant later.
- **Per-user vs per-machine.** MSIX is per-user by default (no elevation; installs into the user profile).
  The WiX `.exe` should offer per-user by default (HKCU, no UAC) with an optional per-machine mode
  (Program Files, HKCU→HKLM, UAC) for shared machines. Record the consequence: a per-user install means
  each user installs their own copy; a per-machine install means one copy and one `gamepolish://` handler
  for all users. Pick **per-user default** to avoid UAC friction on the common single-user PC and to keep
  the MSIX and `.exe` paths consistent.
- **Upgrade / uninstall behaviour (state it, test it).**
  - MSIX: an in-place upgrade requires the same identity, same publisher, higher version; downgrades are
    refused by the OS. Uninstall removes the package; **user data under `~/.game-polish/` is never
    deleted** by uninstall (records, mods, backups survive a reinstall).
  - WiX: fixed `UpgradeCode` so installing a newer version removes the older one; the ARP entry shows
    `Game Polish <version>`; uninstall removes program files and the shortcut, never `~/.game-polish/`.
- **macOS `.dmg`** with `create-dmg` (the tool ci-release.md names):
  `create-dmg --volname "Game Polish" --window-size 600 400 --icon "GamePolish.app" 140 200 --app-drop-link 460 200 GamePolish_<version>_aarch64.dmg GamePolish.app`.
  Sign the `.dmg` (`610`). The drag-to-Applications layout is the whole install UX; uninstall is "trash
  the app" (state it — users ask).
- **macOS `.app.tar.gz`** for the updater. A signed `.app` carries part of its signature in extended attributes;
  a naive `tar` can drop them and break the signature. Create it with `tar --preserve-permissions -czf
  GamePolish_<version>_aarch64.app.tar.gz GamePolish.app` on the runner and **verify on extraction** that
  `codesign --verify --deep --strict` still passes; if the archive strips the xattrs, switch to a format that
  preserves them (`ditto -c -k --sequesterRsrc --keepParent` makes a `.zip` — but that changes the name contract,
  which is why verify-after-extract is mandatory before choosing). A real gotcha, not a theoretical one.
- `checksums.txt` is generated by the `publish` job over the final artifacts (`605` consumes the
  per-platform `sha256`).

## Acceptance criteria

- A release produces exactly: `GamePolish_<version>_x64.msix`, `GamePolish_<version>_x64-setup.exe`,
  `GamePolish_<version>_aarch64.dmg`, `GamePolish_<version>_aarch64.app.tar.gz`, `checksums.txt`, plus the
  unversioned aliases `GamePolish_x64-setup.exe` and `GamePolish_aarch64.dmg`.
  `gh release view <tag> --json assets -q '.assets[].name'` lists them.
- The two website URLs resolve:
  `curl -sIL https://github.com/game-polish/game-polish/releases/latest/download/GamePolish_x64-setup.exe`
  returns a 200/302 to the artifact.
- MSIX `Identity Name` and `Publisher` are fixed and documented; a later change is flagged as breaking the
  PFN and the `gamepolish://` registration.
- Install, then upgrade to a higher version (in place for MSIX; ARP replace for WiX), then uninstall — and
  `~/.game-polish/` is untouched by the uninstall.
- The `.dmg` mounts, shows the app + Applications link, drag-installs, and the app launches past Gatekeeper
  (signed + stapled, `610`).
- The `.app.tar.gz` extracts to a bundle whose `codesign --verify` still passes.

## Verification

```bash
gh release view <tag> --json assets -q '.assets[].name'          # exact names + aliases present
curl -sIL https://github.com/game-polish/game-polish/releases/latest/download/GamePolish_x64-setup.exe
# Windows:
powershell -c "Get-AppxPackage *GamePolish*"                     # after MSIX install: PFN + version
# macOS (CI / real Mac):
hdiutil attach GamePolish_<v>_aarch64.dmg && spctl -a -vvv -t install "/Volumes/Game Polish/GamePolish.app"
```

The Windows MSIX/`.exe` install-uninstall cycle is observable on this host (self-signed for the test); the
`.dmg` mount/install is proven on a real Mac or CI (`macos-latest`) — say so, do not claim a local macOS
verification.

## References

- `../game-polish-notes/docs/ci-release.md` — Build Matrix, Release Artifact Names, publish job, Website
  Download Links, Branch & Tag Strategy.
- Tasks: `403`, `404`, `610` (signing), `704` (gamepolish:// protocol, depends on the identity chosen
  here), `605` (consumes `.exe` / `.app.tar.gz`).
