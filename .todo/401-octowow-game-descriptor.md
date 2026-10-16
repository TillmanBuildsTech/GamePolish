---
id: 401
title: OctoWoW game descriptor
phase: 4
bucket: mvp
depends_on: [202]
blocks: [403]
status: todo
area: core
---

# 401 — OctoWoW game descriptor

## Objective

`games/octowow.yaml` exists and is copied beside the desktop binary on every build and publish, so
`GamePolish.Core`'s loader (202) parses it with zero validation errors — OctoWoW supported by data alone,
with no C# naming the game, its addon folder or its registry URL.

## Context

Game Polish is a cross-platform mod/addon manager whose MVP ships exactly one game — OctoWoW, a vanilla
WoW 1.12.1 private server — on Windows and macOS. An OctoWoW "addon" is a folder inside
`<install>/Interface/AddOns/`; the loader reads `<folder>/<folder>.toc` and ignores everything else in
that folder (`.git`, READMEs, build scripts).

The consumer is task **202** (YAML → typed descriptor + validation). Read it first: it defines the
required key set, the error type, and whether unknown keys are tolerated. Satisfy *that* contract, not an
imagined one. If 202 requires a key the design note lacks, add it with an honest value and say so in the
report.

The design note is authoritative — `../game-polish-notes/docs/games/octowow.md`, section "Game YAML
(draft)" — and its block is reproduced verbatim below. If note and this file disagree, the note wins and
this file is corrected in the same commit.

Repo state: greenfield (docs-only), `games/` does not exist; run `git status --short` before claiming
anything. Invariant 6 — game support is additive: a YAML plus a registry entry, no C#. The only
code-adjacent edits permitted here are MSBuild items and tests; needing a C# code path is a finding to
report, not a licence to add one.

## Scope

- `games/octowow.yaml` with exactly the keys and values of the draft block below.
- The MSBuild item that puts `games/*.yaml` in the build output **and** the publish output beside the
  binary, plus the same include for the CLI harness (213) if it is a separate project — one shared
  `.props`, or the same `ItemGroup` in both csproj files.
- The runtime lookup location documented where it is consumed; no second lookup mechanism if 202 has one.
- The two tests named in Acceptance criteria.

## Out of scope

- The loader's validation rules and error types (202); registry content and the repo (402).
- Publish profiles, RIDs, self-contained settings, packaging (403); the tag (404); acceptance (405).
- Any `settings_files` block or settings schema (601/602) — OctoWoW declares none.
- A second game, install-path auto-discovery (`manual_only` is `true`), or editing
  `../game-polish-notes/docs/games/octowow.md` to match the code. The note is the source.

## Implementation notes

The file, verbatim from the design note:

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

- Keep the note's quoting and key order. `destination` is a **relative** path, forward slashes, no
  leading or trailing slash, no `<install>` prefix; absolute values are a validation error by design
  (task 204). The descriptor never holds an install path — `D:\OctoWoW` lives in
  `~/.game-polish/games.json` only (203).
- MSBuild item, verified on this machine against SDK 10.0.401:

  ```xml
  <ItemGroup>
    <None Include="$(MSBuildThisFileDirectory)../games/*.yaml" Link="games/%(Filename)%(Extension)"
          CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
  ```

  `Link` flattens `../games/octowow.yaml` to `games/octowow.yaml` in the output. Verified landing spots:
  `GamePolish.Desktop/bin/Release/net10.0/games/octowow.yaml` and the same path under
  `.../win-x64/publish/games/octowow.yaml`.
- Runtime location: `Path.Combine(AppContext.BaseDirectory, "games")` — identical under `dotnet run`,
  `dotnet watch run` and a published app; tests point the loader's games directory at a temp fixture.
- A non-empty `AddOns` folder is the normal case: the real install at `D:\OctoWoW` has 16 entries there,
  13 of them `Blizzard_*` folders shipped with the client. Installing writes only
  `<install>/Interface/AddOns/<folder>/`; it never clears `AddOns`, never deletes a sibling addon, and
  never requires an empty destination. Backups (205) scope to the destination subtree, never all of
  `AddOns`. What happens when `<folder>` already exists is 208's decision, not the descriptor's.
- Omit `settings_files` entirely rather than writing an empty list, so 602 hides the Settings Editor on
  *absence*. For OctoWoW it stays hidden: this client has no INI/TOML/JSON config schema.

## Acceptance criteria

- `games/octowow.yaml` loads through the 202 loader with zero validation errors, asserted by a test.
- Field assertions hold: `id == "octowow"`, `name == "OctoWoW"`, `install_detection.manual_only == true`,
  `install_detection.verify_file == "WoW.exe"`, `mod_layout.type == "addons_folder"`,
  `mod_layout.destination == "Interface/AddOns"`, `registry_url ==` the raw
  `https://raw.githubusercontent.com/game-polish/registries/main/octowow.json`.
- A test asserts the destination resolves under the OctoWoW install path: combining `D:\OctoWoW` with
  `mod_layout.destination` yields a path ending `Interface\AddOns` that passes 204's containment check.
- The published output contains `games/octowow.yaml` next to the executable.
- `git diff --stat` shows only `games/octowow.yaml`, csproj/props edits, and the new tests.

## Verification

```bash
git status --short
dotnet build -c Release
ls GamePolish.Desktop/bin/Release/net10.0/games/                    # expect octowow.yaml
dotnet publish GamePolish.Desktop -c Release -r win-x64 --self-contained true
ls GamePolish.Desktop/bin/Release/net10.0/win-x64/publish/games/    # expect octowow.yaml
dotnet test --filter "FullyQualifiedName~Descriptor"
dotnet format --verify-no-changes && git diff --stat
```

Paste the real output of every command, including the `dotnet test` pass/fail counts.

## References

- `../game-polish-notes/docs/games/octowow.md` — "Install Path", "Addon Layout", "Game YAML (draft)";
  `../game-polish-notes/docs/ci-release.md` — "SDK & Runtime Pinning"
- `CLAUDE.md` — "Game Config YAML Schema (draft)"; `.todo/000-CONVENTIONS.md` — invariants 3, 6; tasks 202, 203, 204, 205, 208, 213, 402, 403, 602 (depends on 202, blocks 403)
