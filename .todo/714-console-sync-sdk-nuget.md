---
id: 714
title: Console sync SDK (NuGet)
phase: 7
bucket: post-v1
depends_on: [701]
blocks: [715]
status: todo
area: sdk
---

# 714 — Console sync SDK NuGet

## Objective

`GamePolish.SDK` ships as a NuGet package that a game developer embeds: the game calls one sync method on launch, the SDK authenticates through the platform identity (Xbox Live, PSN), fetches that user's active mod list from `GamePolish.Service`, and hands the list to the game's own loader. The install-model difference is stated plainly, not buried: on console there is no filesystem access to games, so the SDK is a sync relay, mod loading is the game's job, Game Polish owns all user experience, and developers integrate only a few hooks.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 701 (the service API and its contract generation) and blocks 715 (the Xbox app consumes the SDK). It is the console half of the product's "Two Install Models" story from `CLAUDE.md`: the PC path has direct filesystem access and `GamePolish.Core` installs mods into game directories; the console path has no such access, so `GamePolish.SDK` in the game plus the service as a sync relay is the whole mechanism.

**One honest gap must be raised rather than invented:** the notes' API table has no endpoint for platform-identity authentication, and no definition of how mod files physically reach a console. Both are needed for this task to be more than a shell. Ask the user (and propose a notes change) before writing a protocol into a shipped package — a wrong guess here ships inside other people's games.

## Scope

- `GamePolishClient` (name as `CLAUDE.md` uses it) with a launch-time sync call — `SyncAsync(...)` — that returns or exposes the user's active mod list: mod id, name, version, source, enabled state, and whatever the loader needs to load them. Async, cancellable, with a documented timeout.
- Platform-identity authentication: Xbox Live and PSN identities exchanged with `GamePolish.Service` for a service token. Because the notes define no route for this, the task must first agree the shape with the user and record it as a notes delta — for example a `POST /auth/platform` accepting a platform token, verified by the service against the platform's own servers, mapped to a linked Supabase identity. Console sign-in has no browser, so the fallback is a device code the user enters on `gamepolish.com`; propose it rather than leaving the problem as an exercise.
- Fetching the active mod list from the service, with the failure modes a shipped game will actually meet: no network, signed-out platform identity, expired service token, service 5xx, and a user with no mods (which is a normal, empty result — not an error).
- Handing the list to the game's loader through a small, stable contract: the SDK does not load mods, does not touch game files, and does not decide where content lives.
- The install-model difference stated in the first screen of the README, in the package description, and in the XML docs: **console games have no filesystem access to their own directories in the way desktop games do**, so nothing in the SDK copies files into a game; Game Polish handles selection, purchase and management on its own surfaces, and the game loads what the SDK returns.
- Package metadata and engineering: package id `GamePolish.SDK`, authors, the repo's license (per the licenses view, 609), project/repository URL, embedded README, SourceLink, deterministic build, symbols package, and a versioning policy that respects the tag/version rule in `ci-release.md` (bump `<Version>` before tagging).
- Target frameworks: `netstandard2.0` so Unity (2021/2022, IL2CPP) can consume it, plus `net10.0` for GDK/WinUI 3 titles. State which engine integrations are actually supported and which are not (an Unreal consumer would need a C++ shim — mark it out of scope and say so).
- Versioning and stability: SemVer, breaking changes only in a major, and a **service-side compatibility promise** — games ship long before they update, so the API version they were built against must keep working for a documented period (propose at least 12 months of deprecation notice, and record the answer).
- A sample consumer (a small console app or Unity-agnostic sample) that references the package from a local feed, calls the sync method, and prints the resulting mod list against a development instance of the service — the thing that proves the package is usable.

## Out of scope

- `GamePolish.Xbox`, the console UI, and the GDK app (715).
- Mod file delivery to consoles: platform content/patch delivery, first-party store packaging, or a download pipeline the notes never define. If the SDK must fetch content, that is a design decision to raise with the user, and it must be pluggable so this task does not freeze it.
- Mod installation semantics (`GamePolish.Core` owns install/backup/restore; the SDK must not reference Core's filesystem behaviour).
- Storefront entitlement modelling beyond platform identity, and any billing UI on console (713 owns billing on the web and desktop).
- Localisation, telemetry, and crash reporting.

## Implementation notes

- **Never block the game's boot path.** A console game that hangs on a network call is a broken game: enforce a short timeout, return `null`/empty on failure, raise an event the game can ignore, and log locally (console-appropriate logging, not file writes). A user with no mods gets an empty list, not an exception.
- No filesystem writes to the game: assert this in code review and in the package's dependency graph (no `GamePolish.Core` reference, no install/backup calls). It is also the reason the SDK can ship to a platform that forbids the alternative.
- The SDK must not carry a client secret. Platform identity tokens are obtained from the platform SDKs; the service exchanges and verifies them. A secret in a shipped package is a secret in every copy of every game.
- Token storage inside a game process: memory only, refreshed by the same launch-time sync call. There is no keychain on console; do not invent one, and do not write tokens to the game's storage.
- Versioning: the service must accept the API version a title was built against. Add the version to the request (header or path) and make the service's compatibility window explicit and testable; if the service cannot honour an old version, it must fail with a distinguishable error the SDK can surface as "mods unavailable", not a crash.
- Package verification should be real: inspect the produced `.nupkg` (contents, TFMs, metadata, README, symbols) and restore it from a local feed in the sample consumer. Publishing to nuget.org is an operational decision — ask before pushing, and never publish a version that has been reused.
- Consumers must be able to integrate in minutes: the README's quick start shows the two hooks (call sync on launch, read the returned list), a troubleshooting section, and the exact supported targets.

## Acceptance criteria

- `dotnet pack -c Release` produces `GamePolish.SDK.<version>.nupkg` containing both TFM assets (`netstandard2.0`, `net10.0`), the README, license, SourceLink metadata and symbols — verified by listing the package contents and pasting it.
- The sample consumer restores the package from a local feed, calls the sync method against a development service, and prints the user's active mod list; the pasted output shows a real list and, separately, the empty-list case for a user with no mods.
- Failure modes are proven: with the network disabled the call returns within the documented timeout and the game continues; a signed-out platform identity and a service 401 produce distinguishable, non-fatal results.
- The package contains no install/backup/file-copy behaviour and no reference to `GamePolish.Core` (asserted by inspecting the package and the dependency list).
- No secret, token or credential is present in the package, the sample, or the build scripts (`grep` over the produced artefacts, pasted).
- The README's first section states the console install-model difference and who owns the user experience, in the same terms as `CLAUDE.md`.
- The API-version compatibility promise is written down, with the version sent by the SDK and the window the service supports it for.
- The platform-identity endpoint shape and the mod-delivery question are either settled with the user and recorded as an accepted notes delta, or explicitly reported as unresolved blockers — not silently designed.

## Verification

```bash
dotnet pack GamePolish.SDK -c Release -o ./artifacts
unzip -l ./artifacts/GamePolish.SDK.*.nupkg
dotnet nuget push ./artifacts/GamePolish.SDK.*.nupkg --source ./local-feed --skip-duplicate   # local feed only
dotnet add package GamePolish.SDK --source ./local-feed      # in the sample consumer
dotnet run --project samples/GamePolish.Sdk.Sample
```

Paste the package listing, the sample's printed mod list, the timeout/failure transcripts, and the grep proving no secrets. State plainly which parts cannot be exercised without a console development environment (the real Xbox Live/PSN identity exchange) and what was simulated instead — a mock platform identity is acceptable evidence for the package, but it must be labelled as such.

## References

- `CLAUDE.md` — "Two Install Models", "Game Developer SDK (`GamePolish.SDK`)", "Console UI".
- `../game-polish-notes/docs/auth-accounts.md` — "Domains" (`gamepolish.app` is what the console SDK consumes), "API (gamepolish.app)" (the tables that lack a platform-identity route).
- `../game-polish-notes/docs/ci-release.md` — version/tag rules and artifact naming.
- `.todo/000-CONVENTIONS.md` — invariants 2, 4 and 7; evidence rules; the "loudly report a notes gap" habit.
- Tasks: 701 (service API and contract), 208 (install semantics the SDK must not duplicate), 609 (license for package metadata), 713 (billing), 715 (the Xbox app that consumes this).
