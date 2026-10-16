---
id: 602
title: Settings Editor view
phase: 6
bucket: v1.0
depends_on: [601, 308]
blocks: [603, 607]
status: todo
area: desktop
---

# 602 — Settings Editor view

## Objective

The structured Settings Editor described in `settings-editor.md` exists as an Avalonia view: a left
file list with dirty indicators, a right panel of accordions grouped by section with field controls
chosen by schema type, a catch-all Other section that preserves unknown keys, dirty-state accents,
Save/Discard, a header Restore button, the config-not-found banner, and the save-blocked notice shown
while the game is running. The nav entry that opens it is hidden entirely for a game with no declared
settings files.

## Context

Authoritative: `../game-polish-notes/docs/views/settings-editor.md` (Layout, Left Panel, Right Panel,
Dirty State, Save & Discard, Restore Button, Validation, Edge Cases).

**This task extends `601` (Core settings I/O and schema) and builds on `308` (headless UI test
harness).** It is a first implementation, not a rewrite: the MVP bucket explicitly excluded the
settings editor, and `504` built the *App Settings* view, not a game's config editor.

**Verification blocker — state it plainly.** The Settings Editor is hidden for a game that declares no
`settings_files`, and that is exactly OctoWoW (`401`). So until task `607` adds a second game whose
descriptor declares settings files, this view has **no real game to open against**. Development and
tests use a fixture descriptor under the test project; only `607` proves it against a real game.

## Scope

- Left panel: one entry per file in the descriptor's `settings_files`; active file highlighted; a dirty
  dot when the in-memory document differs from disk; switching files with unsaved changes opens a
  save-or-discard prompt and does not switch until answered.
- Right panel: section accordions in file order; sections with `advanced: true` collapsed by default;
  field controls mapped by schema type exactly as the settings-editor.md table:
  `bool` → toggle / On-Off radio; `int` and `float` → number input honouring min/max; `range` → slider
  with numeric readout; `enum` → dropdown; `string` → text input; `path` → text input + Browse (system
  file picker); `color` → colour picker.
- Catch-all **Other** section at the bottom holding keys present in the file but absent from the schema,
  rendered as plain text inputs and written back unchanged.
- Dirty state: changed fields get a left border accent; hovering shows a tooltip with both the current
  and the original value.
- **Save Changes** enabled only when dirty; **Discard Changes** resets to the last-saved state with no
  file write. On save, Core backups first, then writes; success toast "Settings saved — backup created."
- Header **↩ Restore** opens the backup list filtered to this settings file.
- Config-file-not-found banner and red file-list entry, with the note's exact banner text.
- Save blocked while the game is running, with the exact notice `Close the game before saving settings.`

## Out of scope

- Raw edit mode and the diff modal (`603`).
- Backup retention, disk usage and the general Backups view changes (`604`).
- Adding settings files to OctoWoW or any new game — `607`.
- Guessing a control for an unsupported schema type: such keys fall back to the Other section.

## Implementation notes

- View model: `SettingsEditorViewModel` owns one `SettingsFileViewModel` per file; the active file is
  loaded through `601`'s reader on open and on switch. Dirty = document differs from the last-read
  snapshot, computed by the document model, not by per-control events.
- Tooltips: bind `ToolTip.Tip` on the field container to a string built from current + original value,
  populated only while dirty.
- Save is a strict two-step that must not be reordered: `205` snapshot, then `601` write. A failed write
  leaves the snapshot in place — that is the point of the snapshot.
- Restore routes to the Backups view pre-filtered to this file (backups.md "Context Entry Points" #3:
  trigger `Settings change`, source = filename). The physical layout cited by settings-editor.md is
  `~/.game-polish/<game_id>/backups/settings/<filename>/`, and the event index is
  `~/.game-polish/<game_id>/backups/index.json`.
- **Running-game detection.** The descriptor supplies the process name(s) to watch — `601` adds a small
  `running_process_names` field alongside `settings_files`, per platform. Mechanism per OS:
  - **Windows** — enumerate with `Process.GetProcessesByName("<name>")` (or filter `Process.
    GetProcesses()` by name). This is a *read* of process names; it works for the same user without
    elevation and returns names of elevated/other-user processes too (opening a handle would fail, but
    a name comparison succeeds). No admin rights required. Record the caveat: compare names from the
    full enumeration, do not open handles, and do not filter by session.
  - **macOS** — `Process.GetProcessesByName` is unreliable for other applications on macOS. Use the
    running-applications list (`NSWorkspace` via the platform interop) or invoke `/usr/bin/pgrep -x
    <name>` as a child process. That launches a *system* binary, not mod content, so invariant 2 is not
    implicated. Same-user apps are visible without elevation; another user's app may not be.
  - The check runs when the view opens and is re-evaluated on save attempt. It is advisory, not a
    kernel lock (the game may start between check and write); say so in the copy.
- Hidden nav: route registration asks the descriptor whether `settings_files` is empty; if so the entry
  is not created (settings-editor.md "Edge Cases"), so there is no dead link.
- Config-not-found: the entry renders red and the banner shows the note's copy; Save stays disabled
  because nothing is loaded.
- Accessibility hooks (names, focus order) are set here but audited in `608`.

## Acceptance criteria

- With a fixture descriptor declaring a bool, an int with min/max, a range, an enum, a string, a path
  and a colour, each renders the control named in the table (a headless test finds the control type).
- Toggling a control marks the file dirty (dot visible), enables Save and adds the accent; Discard
  clears all three.
- An `advanced: true` group is collapsed on open; a non-advanced group is expanded.
- A key present on disk but absent from the schema appears in the Other section and survives a save.
- Switching files while dirty opens the prompt; Discard reverts and switches; Save writes and switches.
- With the watched process name running, Save is disabled and the notice reads exactly
  `Close the game before saving settings.`
- Save creates a backup before the write (assert the backup dir exists and the file changed).
- For a descriptor with an empty `settings_files`, the Settings Editor nav entry does not exist.

## Verification

```bash
dotnet test --filter FullyQualifiedName~SettingsEditor
dotnet watch run --project GamePolish.Desktop   # visual pass with a fixture game
```

Headless tests cover dirty logic, control mapping and the running-game notice (inject a fake process
probe). The visual pass confirms accordions, tooltips and the banner. macOS behaviour of the process
probe is verified by `607` on a real Mac or CI, not here.

## References

- `../game-polish-notes/docs/views/settings-editor.md` — the whole note.
- `../game-polish-notes/docs/views/backups.md` — Context Entry Points, Restore Flow.
- Tasks: `601`, `308`, `401` (OctoWoW has no settings files), `607` (first game that does), `603`, `604`.
