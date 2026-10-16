---
id: 603
title: Raw edit mode and diff
phase: 6
bucket: v1.0
depends_on: [601, 602]
blocks: []
status: todo
area: desktop
---

# 603 — Raw edit mode and diff

## Objective

The Settings Editor gains a Structured/Raw toggle, a raw code editor for the file's literal contents
with a warning banner, a save path from raw mode that still backs up first, and a side-by-side diff
modal comparing a backup against the current file for text formats.

## Context

Authoritative: `settings-editor.md` (Raw Edit Mode) and `backups.md` (Details Expander, the "Show diff"
link for INI/JSON/TOML).

**This task extends `602` (the structured view header) and consumes `601` (reader/writer, encoding and
EOL handling).** Raw mode exists because no schema is ever complete: a user must be able to type a key
the schema does not know, or paste a block from a forum post.

**Editor decision — the tradeoff must be made, not dodged.** settings-editor.md names Monaco or
CodeMirror, both browser components, which means a WebView in an Avalonia app. The alternative is a
native Avalonia text control. They are one-way different:

| | Monaco/CodeMirror via WebView | Native Avalonia text control |
|---|---|---|
| Dependency | a WebView control + a runtime (Windows: WebView2/Edge; macOS: WKWebView) | none beyond Avalonia |
| Windows | WebView2 Runtime must be present — Evergreen on Windows 11, a *download* on older Windows 10 images, which is a system change we must not force | none |
| macOS | WKWebView, a separate code path and debugging story | none |
| What it buys | real syntax highlighting, bracket matching, IntelliSense | plain text, optional line numbers |
| Cost | a second rendering stack, a second set of accessibility gaps, packaging and installer burden | we hand-roll highlighting, or skip it |

**Recommendation: the native route.** For a config file of tens to a few hundred lines, a monospace
`TextBox` with a line-number gutter is the right size. A WebView buys syntax colouring this feature does
not need and costs a WebView2 runtime dependency plus a second accessibility and packaging surface on
two platforms — which contradicts "never harm the user's system" precisely when the runtime is absent.
If the team overrides this, record the WebView dependency and its Windows/macOS installers in the same
change.

## Scope

- A Structured/Raw toggle in the Settings Editor header; Structured is the default.
- Raw mode: a native, monospace, multi-line editor showing the file's literal bytes decoded with the
  encoding `601` detected; a line-number gutter; no auto-formatting.
- The warning banner, exact text: `Editing raw config — invalid syntax may cause game issues.`
- Save from raw mode: `601`'s validation is skipped deliberately (raw is the escape hatch), but the
  `205` backup still happens before the first byte.
- A side-by-side diff modal comparing a chosen backup's file against the current file on disk for text
  formats (INI, TOML, JSON), reachable from the Backups Details expander's "Show diff" link and from the
  header Restore sheet.

## Out of scope

- Syntax highlighting, IntelliSense, or any language server (the native choice).
- Editing binary or unknown-encoding files in raw mode — those show a not-editable notice.
- A three-way or merge diff; the modal is two read-only columns showing additions and removals.
- The structured editor itself (`602`) and retention/disk usage (`604`).

## Implementation notes

- Raw editor: a `TextBox` with `AcceptsReturn=True`, `TextWrapping=NoWrap`, a monospace `FontFamily`
  (Cascadia Mono on Windows, Menlo on macOS — resolve via `FontManager`), inside a `ScrollViewer` with a
  synchronised line-number column. It is a plain text control, so screen readers read it as text; the
  accessible name is added per `608`.
- Switching to Raw with unsaved structured edits must not silently discard them: carry the current
  document's serialised text into the editor so the two views agree while the toggle is active.
- EOL and encoding round-trip are `601`'s job; raw mode must not normalise CRLF to LF on save.
- Diff algorithm: a line-based diff (Myers O(ND) or equivalent) over two normalised line arrays (split
  on `\n` after decoding; the original EOL is kept for display). Render two synchronised columns with a
  shared row map: unchanged, added, removed, and a spacer where one side has no counterpart. A hand-rolled
  implementation is fine at this size and avoids a dependency; if a library is used it must be pure
  managed (no native lib) to keep Core UI-free and the publish self-contained.
- Encoding normalisation for diff only: decode each side with its own detected encoding, compare as
  text. A BOM-only difference is not a content change and must not show as a diff.
- Large files: if either side exceeds a threshold (e.g. 2 MB) or is not decodable as UTF-8/UTF-16, show
  "Diff not available for this file" instead of trying.
- The diff modal is read-only; promoting a backup to the live file is the Restore flow (`604`), not
  here.
- The warning banner is non-dismissible while in Raw — it is the whole point of the mode.

## Acceptance criteria

- The toggle switches the panel; returning to Structured re-reads the file and shows the structured
  fields; dirty state is not silently lost on the switch (a prompt or a carried-over document).
- The warning banner with the exact text is visible in Raw mode and absent in Structured mode.
- Saving invalid INI/TOML/JSON from Raw succeeds (no validation) *and* a backup exists before the write;
  a later Restore returns the prior bytes.
- The diff modal shows a two-column, read-only comparison for an INI fixture where one line changed,
  one was added and one removed; a BOM-only difference shows no hunks.
- A binary or undecodable file shows the not-available notice rather than a broken editor.

## Verification

```bash
dotnet test --filter FullyQualifiedName~RawEdit
dotnet test --filter FullyQualifiedName~Diff
dotnet watch run --project GamePolish.Desktop   # eyeball the editor, banner and modal
```

Unit tests cover the diff on fixtures (hunk counts asserted) and the backup-before-raw-save ordering.
The editor itself is eyeballed in `dotnet watch` and — once a real game exists (`607`) — against
`SkyrimPrefs.ini`; state in the report that the control was visually verified, per the desktop
definition of done.

## References

- `../game-polish-notes/docs/views/settings-editor.md` — Raw Edit Mode.
- `../game-polish-notes/docs/views/backups.md` — Details Expander, "Show diff".
- Tasks: `601`, `602`, `604`.
