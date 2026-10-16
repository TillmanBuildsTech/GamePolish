---
id: 715
title: Xbox app (WinUI 3 and GDK)
phase: 7
bucket: post-v1
depends_on: [714, 701]
blocks: []
status: todo
area: sdk
---

# 715 — Xbox app (WinUI 3 and GDK)

## Objective

`GamePolish.Xbox` exists as a WinUI 3 application built against the Microsoft GDK for Xbox Series and Xbox One: it signs the player in with platform identity, shows the console navigation surface, and uses `GamePolish.SDK` (714) plus `GamePolish.Service` (701) to read and manage the player's active mod list for a title. It is the last task in the plan and the only one that cannot be built or verified without a GDK/Xbox development environment — a fact this work order states up front rather than discovering halfway through.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning, and confirm 701 and 714 are done as well: this task depends on both and cannot be faked in their absence.

This is the last task in the plan. The desktop app's console story only becomes real here, and the honest scope of it is small: the console installs nothing into a game, because there is no filesystem access to games on console — the SDK is a sync relay in the title, and Game Polish manages selection and identity.

**Environment prerequisite, stated plainly:** building and running this requires an Xbox development environment — the Microsoft GDK installed, a dev-mode console or Xbox dev kit (or an approved dev-mode retail console), and a licensed developer account with access to the GDK. **It cannot be built or verified on a standard Windows development box without it.** The first step of this task is to check for that environment; if it is absent, the correct outcome is a written blocker plus what *can* be done (project scaffolding behind a conditional build, shared-logic tests, the navigation spec), not a stubbed app that claims to work.

## Scope

- A `GamePolish.Xbox` project created from the GDK's own templates, targeting Xbox Series and Xbox One, and added to the solution in a way that does not break the desktop build or CI on machines without the GDK (conditional project inclusion or a separate solution filter — the CI consequence must be reported).
- Verify early which UI stack the GDK environment actually supports for a console XAML app (the GDK's XAML app model) versus plain WinUI 3 with the Windows App SDK, and record the finding. `CLAUDE.md` names "WinUI 3 via Microsoft GDK" as the intended stack; if the environment's supported XAML host differs, that is a documentation correction to raise, not something to paper over.
- Platform identity and entitlement handling: sign-in through Xbox Live, mapping to the Game Polish account exactly as 714 defines; entitlement checks through the platform's own services when the managed tier is ever sold on console, with the caveat that selling a subscription through a console store brings platform commerce rules into play — raise that with the user before designing it.
- The console navigation surface, designed for a controller and a TV: title list, the player's active mod list for a title with enable/disable-style management, discovery/search over the registry, sync status, and an account/settings area with the device-code sign-in from 714. No mouse, no file picker, no drag-and-drop, generous focus states, and no small text.
- Reading and writing the active mod list through the SDK: what the player enables on console is what the title loads next launch.
- Screens and copy that respect the platform's certification expectations (no misleading claims, no unlicensed branding, clear error states when a mod source is unavailable).

## Out of scope

- Any reuse-by-porting of the Avalonia desktop UI (see implementation notes) and any change to the desktop app to accommodate the console.
- Installing files into a game on console (impossible — the SDK is a relay), and the manual ZIP fallback that 709 offers on PC: on console there is no file picker, so a mod whose source is gone simply cannot be supplied by the player. The UI must say that plainly instead of offering a dead end.
- Store submission, certification paperwork, age ratings, and the console storefront listing.
- A PlayStation (`GamePolish.PlayStation`) or Switch equivalent: the notes name PSN only in the SDK's identity list; a second console app is a new task.
- Billing UI on console (713 owns billing on web and desktop).

## Implementation notes

- **What cannot be reused, and why.** The Avalonia desktop UI does not run here: Avalonia renders through Skia with its own windowing platform abstraction and has no GDK/GameCore app-model target, so `GamePolish.Desktop`'s views, view models, controls and theming are not portable — the console is a rebuild, not a port. Widgets that look trivial (navigation, focus handling, list virtualisation) have different platform behaviour on a controller-driven XAML host. `GamePolish.Core` is pure .NET and is reusable *in principle*, but its filesystem semantics are meaningless on console: only its pure parts (descriptor parsing, manifest models, registry models, path validation primitives) are candidates, and the install/backup pipeline must not be called at all. Say this in the task report so nobody plans a "small port".
- The SDK (714) is the only channel to the service; the console app must not re-implement identity exchange, token handling or API calls, or the two will drift.
- Build hygiene: nothing in this project may break `dotnet build`/`dotnet test` on a machine without the GDK. If the GDK project cannot participate in the normal solution build, exclude it explicitly and document the separate build command — a red CI on the dev box is a worse outcome than an excluded project.
- Verification limits are part of the deliverable: which criteria were exercised on a dev kit, which on a retail dev-mode console, and which remain unverified. Never present a desktop-compiled binary as evidence of an Xbox build.
- Accessibility for a ten-foot UI overlaps task 608's work but is not the same work: contrast, focus order and text size for a TV, verified by looking at it on a screen or with the platform's accessibility tools.
- Identity and entitlement are platform-owned: the console app never invents an account picker, never stores credentials of its own, and never shows a password field. If the platform has no signed-in user, the app shows the platform's own sign-in prompt or the device code from 714 — nothing else.
- The mod list shown on console is the same list the desktop app writes (that is the whole point of the sync relay), so any divergence is a bug in one of the two surfaces; test by toggling a mod on console and seeing the change on desktop and vice versa.
- Degrade honestly: with the service unreachable, the app shows the last known list as stale with a visible timestamp, never an empty list that looks like the player has no mods.
- Do not ship a settings-editor equivalent on console — the notes hide it for OctoWoW and it has no console story; reading INI files that are not on disk would be a fiction.

## Acceptance criteria

- The environment check is done first and its result is reported: GDK present/absent, dev kit or dev-mode console present/absent, licensed account present/absent. If absent, the task stops with a written blocker and an inventory of what was and was not attempted — that is a successful outcome for this task, not a failure.
- With the environment: the app builds from the GDK template for the console target and deploys to the dev kit; paste the build command and its real output, plus the deploy command.
- The app launches on the console, signs in with platform identity, and displays the player's active mod list fetched through the SDK from a development service.
- Enabling or disabling a mod on console changes what the title loads on its next launch (verified with a test title or the sample from 714).
- Every screen is navigable with a controller only: no action requires a mouse, no control traps focus, and the back path always returns to the previous screen.
- The console app performs no filesystem writes to a game install directory and no file-picker flows (there is nothing to pick): asserted in code review and by an inventory of the APIs used.
- The desktop solution still builds and its tests still pass on a machine without the GDK, and CI is either unaffected or explicitly documented as excluding the console project.
- The report names exactly which claims were verified on hardware and which were not, and repeats the caveat that a standard Windows dev box cannot build or verify this task.
- Sign-in uses the platform identity path only: no Game Polish password is ever entered on the console, and no credential store is created in the app.
- After a change made on either side, the console's active mod list and the desktop Library agree — the sync-relay promise, tested in both directions.
- With the service unreachable, the console shows the last known list marked stale rather than an empty list, and recovers without a restart.

## Verification

```bash
# 1. environment check, before anything else
dotnet --info | head -20
ls "$PROGRAMFILES/Microsoft GDK" 2>/dev/null || echo "GDK not installed"
# GDK build/deploy command from the environment's own documentation, e.g. the template's build script or MSBuild target
# 2. build and deploy to the dev kit, then run on it and capture the console output/screens
```

Paste: the environment check output, the build command with its real output, the deploy result, the on-console screenshots (mod list, sign-in, sync status), and the desktop `dotnet build`/`dotnet test` counts to show the console project did not break them. If the environment is absent, paste the check output and stop — do not substitute a desktop build for console evidence.

## References

- `CLAUDE.md` — "Two Install Models", "Console UI: WinUI 3 via Microsoft GDK (Xbox Series / One — future)", "Directory Layout" (`GamePolish.Xbox`).
- `../game-polish-notes/docs/auth-accounts.md` — "Domains" (the service the console talks to) and the identity options the SDK covers.
- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Restore Flow" and "Edge Cases" for the mod-source-gone case, which behaves differently on console.
- `.todo/000-CONVENTIONS.md` — invariants 2 and 4; evidence rules (never substitute plausible output for a result you could not produce).
- Tasks: 701 (service API), 714 (the SDK this app consumes), 608 (accessibility work it overlaps), 709 (the PC restore flow whose ZIP fallback does not exist here), 713 (billing on other surfaces).
