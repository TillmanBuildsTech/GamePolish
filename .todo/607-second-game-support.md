---
id: 607
title: Second game support proof
phase: 6
bucket: v1.0
depends_on: [601, 602]
blocks: []
status: todo
area: core
---

# 607 — Second game support proof

## Objective

Add a second PC game using **only** `games/<id>.yaml` and a registry JSON entry, drive the whole flow end to
end (descriptor, settings files, registry, install a mod, edit a setting), and write down **every place C#
had to change**. This tests invariant 6 ("game support is additive"); its output is a finding.

## Context

Authoritative: `000-CONVENTIONS.md` invariant 6, `../game-polish-notes/docs/games/octowow.md`,
`../game-polish-notes/docs/views/settings-editor.md`, and the save-path examples in
`../game-polish-notes/docs/views/cloud-backup-sync.md`.

**This task extends `601` (added `settings_files` to the descriptor) and `602` (the editor, which has had
nothing real to open because OctoWoW declares no settings files — see `401`).** It is the load-bearing proof
of the architecture: if a second well-behaved game needs C#, the additive claim is false — a design defect
to escalate, not to paper over.

**Which game, and why it is the hard case.** The notes point at Skyrim SE (`settings-editor.md` is
Skyrim-shaped), Stardew Valley and RimWorld (`cloud-backup-sync.md` save paths). **Choose Skyrim Special
Edition:** the *best* settings-editor case (`Skyrim.ini`/`SkyrimPrefs.ini` are exactly what `602` needs to
become verifiable) and the *hardest* mod-layout case — a shared `Data/` tree with a **plugin load order**
and file-vs-file conflicts, not OctoWoW's `addons_folder`.

**The honest caveat.** OctoWoW's layout is "self-contained addon folders"; Skyrim's is a shared `Data/`
tree with ordering and conflicts. If the current `mod_layout` schema cannot express Skyrim's shape
declaratively, *that is the finding* — and it is legitimate to conclude Skyrim is **not** "well-behaved"
under the current schema rather than bend the schema until it half-fits.

## Scope

- `games/skyrim-se.yaml`: `id`, `name`, install detection (Steam app id `489830`, manual override,
  `verify_file: SkyrimSE.exe`), `mod_layout`, `registry_url`, and **`settings_files`** for the two INIs.
- `skyrim-se.json` in the `game-polish/registries` repo with a few real mods (USSEP, SkyUI, SMIM) in the
  documented registry shape.
- Install one mod end to end (source → placement → manifest entry → visible in Library).
- Edit one setting end to end (open the editor, change `[Display]` in `SkyrimPrefs.ini`, save with a
  backup, confirm both the game file and the backup).
- **The finding**: every file outside `games/*.yaml` and the registry JSON that had to change, with path
  and reason; if non-empty, mark the task blocked and escalate as a design defect.

## Out of scope

- Making Skyrim's load order / conflict resolution work. If it is not data-expressible, that is the
  finding — do not implement a plugin solver here.
- Adding a third game, or Stardew Valley/RimWorld (defer; they may be the *better* second game, and the
  finding should say so if Skyrim proves too hard).
- Steam auto-detection as a general feature beyond what the descriptor can declare.
- Cloud save backup (`712`), even though the save paths are documented.

## Implementation notes

- **Descriptor shape (values to be filled from the real game):**

  ```yaml
  id: skyrim-se
  name: "Skyrim Special Edition"
  install_detection:
    steam_app_id: 489830
    verify_file: "SkyrimSE.exe"
    manual_override: true
  mod_layout:
    type: ???            # the open question — see the finding
    destination: "Data"
  registry_url: "https://raw.githubusercontent.com/game-polish/registries/main/skyrim-se.json"
  settings_files:
    - path: "SkyrimPrefs.ini"
      format: ini
      sections:
        - name: Display
          keys:
            - { key: "bFull Screen", type: bool }
            - { key: "iSize W", type: int, min: 640, max: 7680 }
  ```

- **Record the mechanical test.** "No C#" is falsified by the diff: `git diff --name-only <base>..HEAD -- '*.cs'`
  must be empty. If not, print those paths — that list *is* the finding. A YAML/JSON-only change set passes.
- **Four concrete places the schema is likely to break — check each and say what happened:**
  1. **`mod_layout.type`.** OctoWoW uses `addons_folder`; Skyrim's `Data/` tree with plugins is not that. If
     Core switches on `mod_layout.type` with a closed set of cases, a new type is a C# change → finding. If
     placement is fully data-driven, it may be fine for placement while still failing on **load order**.
  2. **Plugin load order / conflicts.** `plugins.txt`/`loadorder.txt` ordering and file-vs-file conflicts are
     not expressible as the current `{type, destination}` pair. New schema fields *and* installer logic = C#.
  3. **Settings-file location — outside the two allowed roots.** `Skyrim.ini`/`SkyrimPrefs.ini` live under
     `%USERPROFILE%\Documents\My Games\Skyrim Special Edition\` (macOS: `~/Documents/My Games/…`), **not**
     the install dir. Invariant 4 permits only the install dir and `~/.game-polish/`, so editing Skyrim
     settings writes to a third root. Either invariant 4 is amended to permit *declared* settings paths, or
     Skyrim's settings editor cannot be built as specified. **This alone is likely to be the finding.**
  4. **Install detection.** OctoWoW is `manual_only`. If a `steam_app_id`-driven detector does not exist and
     cannot be expressed purely as data, that is a C# change → finding. Tension with invariant 4: Steam
     detection reads the registry / `libraryfolders.vdf`, a read outside the two roots.
- Each item is a yes/no the report answers with evidence (the changed file, or "already data-driven").
- Keep the registry JSON to the documented shape; do not invent fields. If a Skyrim mod needs a field the
  registry shape lacks, that is also part of the finding.

## Acceptance criteria

- `games/skyrim-se.yaml` loads and its `settings_files` drive the Settings Editor (`602`), unhidden for real.
- The registry JSON validates against the documented shape and its mods appear in Discover for `skyrim-se`.
- One mod installs to the correct location with a backup created first, appears in the Library, and is
  tracked in `~/.game-polish/skyrim-se/manifest.json`.
- One `SkyrimPrefs.ini` value is changed and saved with a backup; the change round-trips (rest byte-identical).
- **The finding is written**: the output of `git diff --name-only -- '*.cs'` is quoted; if non-empty, each
  changed `.cs` file is listed with the reason, the task is set to `blocked`, and the design defect is
  escalated in the task report. If empty, the report says so with the command output as proof.
- The report answers the four schema-break questions with evidence and states plainly whether Skyrim's
  `mod_layout` is representable.

## Verification

```bash
git diff --name-only <base>..HEAD -- '*.cs'      # THE test: empty output == additive claim holds
git diff --name-only <base>..HEAD -- '*.yaml' '*.json' | grep -E 'skyrim'   # expected changes only
dotnet test --filter FullyQualifiedName~Descriptor
dotnet watch run --project GamePolish.Desktop    # add Skyrim SE, install a mod, edit a setting
cat ~/.game-polish/skyrim-se/manifest.json
```

A non-empty `*.cs` diff is a **successful falsification**, not a failure: report it as the finding and stop.

## References

- `000-CONVENTIONS.md` — invariant 4 (two roots), invariant 6 (additive game support).
- `../game-polish-notes/docs/games/octowow.md` — the `addons_folder` shape to contrast against.
- `../game-polish-notes/docs/views/settings-editor.md` — the Skyrim-shaped editor spec.
- `../game-polish-notes/docs/views/cloud-backup-sync.md` — Skyrim/Stardew/RimWorld save paths.
- Tasks: `401` (OctoWoW descriptor), `402` (registry repo), `601`, `602`, `208` (install engine).
