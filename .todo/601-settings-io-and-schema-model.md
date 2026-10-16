---
id: 601
title: Settings file I/O and schema model
phase: 6
bucket: v1.0
depends_on: [202, 205]
blocks: [602, 603, 607]
status: todo
area: core
---

# 601 — Settings file I/O and schema model

## Objective

`GamePolish.Core` gains a lossless settings I/O layer for INI, TOML and JSON config files, and the game
descriptor gains the `settings_files` schema that drives it. A caller can parse a config file into a
schema-typed model, mutate one key, and write it back with every comment, unknown key, blank line, section
order and key order preserved — the edited value being the only byte that differs.

## Context

The behaviour is defined by `../game-polish-notes/docs/views/settings-editor.md` (Field Types, Data
Requirements, Validation, Edge Cases). That note is authoritative; where this file disagrees, the note
wins and this file is corrected.

**This task extends `202` (game descriptor model) and `205` (backup engine).** `202` produced the
descriptor type parsed from `games/<id>.yaml`; 601 adds the `settings_files` key to it. `205` produced
the backup engine; 601 is its first settings-domain consumer. The Settings Editor was explicitly
deferred by the MVP bucket and is not part of `504` (which built the App Settings view and the GitHub
PAT, not a game's config editor), so this is first implementation, not a rewrite.

**The core difficulty of this task is round-tripping, not parsing.** The obvious implementation —
parse the file into a dictionary, edit one value, serialise the dictionary back — silently destroys
the user's file: INI comments vanish, key order is lost, blank lines disappear, and any key the schema
does not know about is deleted. That is data loss on a file the user may have hand-edited for years.
The model must therefore be a *document* — an ordered token stream that remembers comments and
whitespace — and edits must be applied surgically to the token that owns the key, never by
re-serialising a normalised structure.

Format realities to design against:

- **INI** has no spec; communities use `;` and `#` comments, duplicated keys, case-insensitive keys,
  and values that may or may not be quoted. Preserve all of it.
- **TOML** is spec'd and round-trip-parsable; `Tomlyn` exposes a syntax tree — use it rather than a
  plain dictionary.
- **JSON** has no comments in the spec. .NET's `System.Text.Json.Nodes.JsonNode` preserves key order and
  unknown keys but not comments; do not claim JSON comment preservation — document the limitation.

## Scope

- `SettingsFileFormat` (ini, toml, json) and a reader/writer per format sharing one interface.
- A document model preserving: comments, blank lines, unknown keys, section order, key order, original
  line endings (CRLF vs LF) and encoding (UTF-8 with/without BOM, UTF-16).
- Schema model `SettingsFileSchema { path, format, sections[] }`: each section has a name and an
  `advanced` flag; each key has `type`, optional `min`/`max`, optional enum `values`, and an `advanced`
  flag. Types are exactly the settings-editor.md table: `bool`, `int`, `float`, `range`, `enum`,
  `string`, `path`, `color`. `range` carries `min`/`max` and a numeric kind.
- Extend the descriptor parser so `settings_files` is read from the game YAML.
- Full-file validation before any write: the edited document must re-parse and every schema key must
  satisfy type/min/max/enum; a failure aborts the write with no file touched.
- External-change detection: record `(mtime, length)` at read; on save compare and raise a typed
  `FileChangedExternallyException` carrying the on-disk content so the UI can offer reload-or-overwrite.
- Every write goes through the backup engine (`205`) **before the first byte is written** — invariant 1.

## Out of scope

- The Settings Editor UI (task `602`); raw mode and diff (task `603`).
- Adding `settings_files` to any real game descriptor — OctoWoW declares none (`401`); the second game
  is `607`.
- Retention or disk usage for settings backups (`604`).
- Refusing to write while the game runs — that guard lives in the view (`602`); Core exposes the write
  and lets the caller refuse.
- Any UI/`Avalonia` reference: `GamePolish.Core` stays UI-free (`000-CONVENTIONS.md`).

## Implementation notes

- Proposed model shape (the contract is the behaviour, not these names): `SettingsDocument` holds an
  ordered `IReadOnlyList<SettingsLine>` where a line is `Comment`, `Blank`, `Section(name)` or
  `KeyValue(section, key, rawValue)`. `SetValue(section, key, newRawValue)` rewrites the value span of
  exactly one line and leaves every other line untouched.
- Add a new key by appending a `KeyValue` line to the owning section (or the catch-all when unplaced), never by
  regenerating the section. A missing section is appended at the end with a blank separator.
- The view's "Other" catch-all section (settings-editor.md) is a view concept; in Core it is simply keys
  present in the document and absent from the schema. Core never drops them.
- Encoding and EOL are detected on read and stored on the document, then restored on write. A CRLF file
  written back with LF is a diff for the user even when the value is identical — treat that as a bug.
- Validation ordering: (1) re-parse the serialised document (must succeed), (2) walk schema keys and
  check type, range and enum, (3) only then snapshot via the backup engine and write. Steps 1–2 are pure
  and unit-testable without a filesystem.
- Write atomically: write `file.tmp` in the same directory, flush, then `File.Move(tmp, dest,
  overwrite: true)`. Atomicity is per-file; the backup already guarantees recovery.
- `FileChangedExternallyException` is the only signal the UI needs; Core does not resolve the conflict itself.
- Descriptor addition (shape only; `607` fills real values):

  ```yaml
  settings_files:
    - path: "Skyrim.ini"
      format: ini
      sections:
        - name: Display
          keys:
            - { key: "bFull Screen", type: bool }
            - { key: "iSize W", type: int, min: 640, max: 7680 }
        - name: Audio
          advanced: false
  ```

## Acceptance criteria

- A fixture INI containing `;` and `#` comments, blank lines, an unknown key and non-alphabetical key
  order round-trips **byte-for-byte** when no value is edited (the test asserts string equality).
- Editing one key changes only that value's bytes; the diff is a single line.
- The same round-trip holds for a TOML fixture (via the Tomlyn syntax tree) and a JSON fixture (key
  order and unknown keys preserved; JSON comments documented as unsupported).
- A descriptor with `settings_files` parses; an unknown `type` value fails descriptor load with a
  message naming the file and key.
- Validation rejects an out-of-range int and a value not in an enum, and the file on disk is unchanged.
- A save against a file whose `(mtime, length)` changed since read throws
  `FileChangedExternallyException` and does not write.
- After a successful save the game's backup directory exists and contains the pre-write bytes.

## Verification

```bash
dotnet test --filter FullyQualifiedName~Settings
dotnet build GamePolish.Core -c Debug -warnaserror    # Core must stay UI-free (no Avalonia reference)
```

The round-trip test is the proof: it must compare the written file to the original with `==`, not
"looks the same". Paste the pass/fail counts into the task report.

## References

- `../game-polish-notes/docs/views/settings-editor.md` — Field Types, Data Requirements, Validation,
  Edge Cases.
- `000-CONVENTIONS.md` — invariant 1 (backup before mutation); Core has no UI reference.
- Tasks: `202` (descriptor), `205` (backup engine), `602`, `603`, `607`.
- `../game-polish-notes/docs/games/octowow.md` — no settings files (why `607` must add one to test this).
