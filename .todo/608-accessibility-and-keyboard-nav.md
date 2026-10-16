---
id: 608
title: Accessibility and keyboard navigation
phase: 6
bucket: v1.0
depends_on: [301, 303]
blocks: []
status: todo
area: desktop
---

# 608 — Accessibility and keyboard navigation

## Objective

Every primary flow in the desktop app is operable with a keyboard alone and announces itself correctly
to a screen reader, focus is always visible, and both themes meet a stated contrast target, with a
written manual test checklist and an honest note of what Avalonia cannot do.

## Context

**This task extends `301` (app shell / navigation) and `303` (Library).** It is a pass over the whole app,
but the acceptance evidence is anchored on the Library, where the custom row controls (checkbox,
overflow menu) live (`../game-polish-notes/docs/views/library.md`, "Right Panel — Mod List", "Mod Row —
Overflow Menu Actions"), plus the Settings Editor (`602`) sliders/colour picker and App Settings
(`../game-polish-notes/docs/views/app-settings.md`). No product behaviour is added; the deliverable is
that the existing surfaces work without a mouse and are named.

## Scope

- **Tab order / logical focus traversal** across every view: left-to-right, top-to-bottom, with the
  container's `KeyboardNavigation.TabNavigation` set so focus does not escape into chrome.
- **Screen-reader names and roles** on all custom controls: mod rows, the enable/disable checkbox, the
  `⋮` overflow menu and its items, toggles, sliders (with their readout), the colour picker, the
  file-list entries with dirty dots, and the toasts.
- **Focus-visible indication** on every focusable control: a visible ring distinct from the accent used
  for selection, present in both themes.
- **Full keyboard operability** of the primary flows: toggling a mod (the Library checkbox), opening the
  row overflow menu and invoking each item, switching games, installing from Discover, saving in the
  Settings Editor, restoring a backup.
- **Shortcuts** for the primary flows, documented in-app: focus search, App Settings, check for updates,
  and back to Library.
- **Colour contrast** under Light and Dark themes to WCAG AA (4.5:1 body text, 3:1 large text and UI
  component boundaries), including the four accent presets' text-on-accent pairs.
- **High-contrast and OS text scaling** behaviour, with the gaps named.
- A **manual test checklist** completed with a keyboard only, on Windows (Narrator) and macOS (VoiceOver).

## Out of scope

- Changing visual design or spacing beyond what accessibility requires.
- Full WCAG AAA conformance; the target is AA.
- Localisation / RTL (not planned).
- Automated UI-automation harnesses (Appium / WinAppDriver); the evidence is headless assertions plus a
  manual checklist.

## Implementation notes

- **The API to use is Avalonia's attached automation properties.** Set them per control:
  - `AutomationProperties.Name` — the announced label. On the `⋮` button use "Mod actions"; on the
    checkbox use the mod name; on a slider use the label plus unit.
  - `AutomationProperties.HelpText` — extra context read on demand (e.g. the mod's source URL or the
    field's original value).
  - `AutomationProperties.AutomationId` — a stable id for testing and automation peers.
  - `AutomationProperties.LiveSetting` — `Polite`/`Assertive` for toasts (`307`) so save/install results
    are announced without stealing focus.
  - `AutomationProperties.AcceleratorKey` — the shortcut string shown by screen readers.
  In XAML these are `AutomationProperties.Name="…"` attached properties; in code-behind,
  `AutomationProperties.SetName(control, "…")`. Verify the exact member names against Avalonia 12.1.4
  before relying on them — the package is the source of truth, not this list.
- **Custom controls need an automation peer.** A control that draws its own visuals (a hand-templated row)
  exposes nothing unless it derives from a control that already has a peer (`Button`/`CheckBox`/
  `ListBoxItem`) or overrides `OnCreateAutomationPeer`. Prefer composing existing controls over
  custom-drawing; where a custom draw is unavoidable, implement the peer.
- **Focus.** `Focusable="True"`, `IsTabStop="True"`, and `TabIndex` only where DOM order is wrong. For
  focus styling use the `:focus-visible` pseudo-class if present in 12.1.4, else `:focus` combined with
  an `IsKeyboardFocusWithin`-style toggle; a mouse-click focus should not draw a ring unless the control
  is text entry. Record which mechanism the pinned version actually supports.
- **The `⋮` overflow menu** must open from the keyboard (Enter/Space), take focus into the flyout, be
  arrow-navigable, close on Escape, and return focus to the row. Each item needs an accessible name
  (`Open Detail`, `Check for Update`, `View Backups`, `Uninstall`).
- **The Library checkbox** is a real toggle: Space toggles it, its state is announced ("checked"/
  "unchecked"), and while the enable/disable operation runs the row is locked (library.md, "Enable/
  Disable Mechanics") — a locked control must report disabled to assistive tech, not just look grey.
- **Shortcuts.** Register `KeyGesture`s on the window (or `HotKey` on menu items): `Ctrl+F` focus search,
  `Ctrl+,` App Settings, `Ctrl+R` check for updates, `Alt+Left` back to Library. List them in a Help/About
  surface so they are discoverable, and expose each via `AutomationProperties.AcceleratorKey`.
- **Contrast.** Define the theme colour tokens once; produce a table of every foreground/background pair
  actually used (text, muted text, accent-on-background, focus ring) with the computed ratio, and assert
  ratios ≥ 4.5:1 (text) / 3:1 (large text, borders, focus ring) in a test that fails the build if a token
  regresses. Check each of the four accent presets against its button text.
- **High contrast.** Avalonia does not consume Windows High Contrast themes into the Fluent theme
  automatically in every case; expose a "follow system" path if the pinned version supports it and, where
  it does not, document that the user can select the High Contrast theme manually. Test under a Windows
  high-contrast theme and note the result.
- **Text scaling.** Avalonia honours the OS display-scale factor for rendering but does not resize all
  typography to Windows' "Make text bigger" setting; verify at 100/150/200% and at the largest OS text
  setting, and record where layout breaks (the file list and settings rows are the likely cases).
- **What Avalonia cannot do — say so.** Automation peer coverage is partial: some containers and
  virtualised lists expose a coarse tree; there is no built-in table/grid peer narrative for hand-built
  tables (the Backups Details table, the licenses list); macOS bridges to NSAccessibility with known
  gaps. Name these in the checklist rather than claiming uniform support.

## Acceptance criteria

- A keyboard-only run completes: switch game → toggle a mod → open its overflow menu and invoke "Check
  for Update" → open Discover and install → open Settings Editor, change a value, Save → open Backups and
  Restore. No mouse used; recorded in the checklist.
- Every interactive control returns a non-empty `AutomationProperties.Name` (a headless test enumerates
  the tree and asserts it).
- The focus ring is visible on every focusable control in both themes (screenshots in the report).
- The contrast table has no failing pair; a test fails the build if a token drops below AA.
- The manual checklist is executed under Windows Narrator and macOS VoiceOver and attached, with any
  unannounced control listed as a follow-up.
- High-contrast and text-scaling results are recorded with the gap stated explicitly.

## Verification

```bash
dotnet test --filter FullyQualifiedName~Accessibility   # automation-name + contrast assertions
dotnet watch run --project GamePolish.Desktop           # keyboard-only walkthrough + Narrator
```

macOS VoiceOver verification requires a real Mac (or a CI runner with an interactive session); it cannot
be done on this Windows host — say so and name the Mac as the proof. Paste the checklist and the headless
test counts.

## References

- `../game-polish-notes/docs/views/library.md` — mod row, checkbox, overflow menu.
- `../game-polish-notes/docs/views/settings-editor.md`, `../game-polish-notes/docs/views/app-settings.md` — controls to name.
- Tasks: `301`, `303`, `307` (toasts), `602`.
