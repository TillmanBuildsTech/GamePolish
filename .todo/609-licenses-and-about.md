---
id: 609
title: Licenses and About
phase: 6
bucket: v1.0
depends_on: [301]
blocks: []
status: todo
area: desktop
---

# 609 — Licenses and About

## Objective

Every shipped dependency's license is inventoried from the real package graph, an in-app scrollable
licenses view renders that inventory (required for distribution), and an About surface shows the version
and offers "Check for updates".

## Context

Authoritative: `../game-polish-notes/docs/views/app-settings.md` (Footer: version, "Check for updates",
"Licenses" — "required for distribution").

**This task extends `301` (the app shell, which owns navigation and the footer).** It depends on the real
package graph, so it runs after the projects exist and their dependencies are pinned (Avalonia `12.1.4`,
`CommunityToolkit.Mvvm`, xUnit). "Check for updates" reuses `605`; do not build a second updater. `610`'s
self-contained publish is why the .NET runtime appears in the inventory at all.

## Scope

- An **inventory** of every dependency's license, generated from the package graph.
- A checked-in, regenerable `THIRD-PARTY-NOTICES.txt` embedded in the app.
- An in-app **Licenses** view: scrollable, listing each dependency with version and license, with the
  license text readable in place.
- An **About** surface: the app version, the "Check for updates" action, and a link/route to Licenses.

## Out of scope

- The auto-updater logic itself (`605`).
- Choosing or changing the app's own license (a product decision; About/notices just state it).
- Auditing licenses for the phase-7 service/SDK until they are built.

## Implementation notes

- **Mechanism (state it, do not hand-wave).** Two parts:
  1. Enumerate the graph with the SDK: `dotnet list GamePolish.Desktop package --include-transitive` and
     the same for `GamePolish.Core` (and any other shipped project). This is authoritative for *what* is
     referenced and its resolved version.
  2. Resolve each package id to a license. NuGet's own license metadata is inconsistent (some packages
     carry an SPDX expression, some a license file, some nothing), so pair the enumeration with a curated
     map `licenses/dependencies.json` (id → SPDX id or a notes field) and use a collector tool for the
     bulk:
     - `dotnet tool install --global dotnet-project-licenses` then
       `dotnet-project-licenses --include-transitive --output licenses.json` (check the exact flags for
       the installed version), or
     - `dotnet tool install --global nuget-license` then `nuget-license -i GamePolish.sln`.
     Either way the tool output is a starting point, not the truth; the curated map overrides it.
- **The gaps you must call out.** Transitive and native dependencies do not always carry license metadata:
  - Avalonia pulls in SkiaSharp, HarfBuzzSharp and their **native** assets; the native artefacts may not
    appear as packages with license fields. Record them from their project pages (Skia: BSD-3-Clause;
    HarfBuzz: MIT) and pin the version shipped.
  - A **self-contained** publish (`610`/`611`) embeds the .NET runtime. Ship the runtime's third-party
    notices: take the `ThirdPartyNotices.txt` that accompanies the runtime pack
    (`packs/Microsoft.NETCore.App.Runtime.*/…`) or the SDK equivalent, and merge it into
    `THIRD-PARTY-NOTICES.txt`. This is the most commonly forgotten item and the reason the inventory is
    "package graph **plus** runtime".
  - Test-only and dev-time packages (xUnit, analyzers) are not shipped; exclude them from the
    distribution notices but keep a separate list so the exclusion is deliberate.
- **Generate at build, embed as a resource.** A build/CI step writes `THIRD-PARTY-NOTICES.txt` (merged
  package licenses + runtime notices) and it is embedded so the Licenses view needs no network. The view
  streams the resource into a `TextBox`/`SelectableTextBlock` inside a `ScrollViewer`.
- **Fail loud on a missing license.** A test asserts that every package returned by
  `dotnet list … --include-transitive` has an entry in the curated map or collector output; an unresolved
  package fails the build. Same discipline as the signing rule (`610`): never ship something whose license
  you did not record.
- **Flag copyleft.** The same test asserts no shipped dependency is GPL/AGPL (or flags it loudly for
  review). The expected stack is permissive — Avalonia MIT, CommunityToolkit.Mvvm MIT, SkiaSharp MIT,
  HarfBuzz MIT, .NET MIT — but the check must exist so a future dependency cannot slip in unnoticed.
- **About.** The version is read from the assembly version (baked from `<Version>` in
  `GamePolish.Desktop.csproj`, the same value `404`'s `check-version` gates on). Show it plus the build id
  (git SHA via SourceLink or an assembly `InformationalVersion`). "Check for updates" calls `605`'s manual
  trigger; the "Licenses" link routes to the Licenses view.

## Acceptance criteria

- `THIRD-PARTY-NOTICES.txt` exists, is embedded, and its package list reconciles with
  `dotnet list GamePolish.Desktop package --include-transitive` and the Core/other shipped projects (a
  test comparing the two sets passes).
- The .NET runtime's third-party notices are present (grep finds the runtime's notice header).
- A build fails with a clear message when a package in the graph has no license entry (add a dummy
  package, watch it fail, remove it).
- The Licenses view renders the notices scrollably with no network access.
- About shows the version equal to the csproj `<Version>`, and "Check for updates" invokes the `605` path.

## Verification

```bash
dotnet list GamePolish.Desktop package --include-transitive   # source list to reconcile against
dotnet-project-licenses --include-transitive --output licenses.json   # (or: nuget-license -i GamePolish.sln)
grep -c "" THIRD-PARTY-NOTICES.txt                            # non-empty; spot-check the runtime header
dotnet test --filter FullyQualifiedName~Licenses
```

Paste the reconciled package count (both sides equal) and the result of opening About and Licenses.

## References

- `../game-polish-notes/docs/views/app-settings.md` — Footer, Licenses link.
- Tasks: `301`, `605`, `610` (self-contained publish → runtime notices), `611`.
