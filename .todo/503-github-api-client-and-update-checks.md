---
id: 503
title: GitHub API client and update checks
phase: 5
bucket: v0.2
depends_on: [206, 212]
blocks: []
status: todo
area: core
---

# 503 — GitHub API client and update checks

## Objective

Build the one shared GitHub client the whole app talks through, plus the update-check feature on top of it.
Nothing in the repo calls GitHub today, and task 305 lists "Update Available badge/amber state and the Update
button" as this task's. This task **extends task 206** (the manifest gains the last-checked timestamp and
latest-known version) and **routes through task 212** (every check is an operation-log entry); it also **owns
token consumption** — storage and the entry UI belong to task 504, the injection point is here. At the end the
app can answer "is there a newer release of this mod?" for every installed mod, degrade to a notice rather than
an error when the anonymous quota runs out, and drive the library row's amber up-arrow and the "Update
Available" sheet.

## Context

- Authoritative notes: `../game-polish-notes/docs/views/library.md` (Data Requirements — "Version info: cached
  during last update check; stale badge shown if >24h old" — and the **Update Indicator** section),
  `../game-polish-notes/docs/views/mod-detail.md` (Data Requirements; the exact rate-limit notice) and
  `../game-polish-notes/docs/views/app-settings.md` (Updates section, PAT text).
- Depends on **206** (`~/.game-polish/<game_id>/manifest.json`) and **212** (operation log: the check reports into
  it like any other operation).
- **Task 504 owns the PAT** — keychain storage, entry, validation, Clear. This task owns **consumption**: a single
  `IGitHubTokenProvider` injected into the client, never a direct read of `app-settings.json` or the keychain.
- `blocks: []`. Task 502 consumes the client's release/tag results, 501's amber badge consumes the update-check
  results, 504 renders this client's quota value.
- Repo state: greenfield, docs-only; `GamePolish.Core` has no HTTP layer. `Microsoft.Extensions.Http` is fine to
  add to Core; a UI reference is not (Core stays pure .NET).

## Scope

- `IGitHubClient` in `GamePolish.Core/GitHub/`: releases, tags, latest release, token validation and rate-limit
  reads.
- Conditional requests — store the response `ETag` per (owner/repo, resource), send `If-None-Match`, serve the
  cached body on `304`; expose parsed `x-ratelimit-*` quota values to the UI.
- Graceful degradation: quota exhaustion is a typed, expected result, not an exception; every fetched body is
  cached per session.
- `UpdateCheckService`: on session start (per the 504 toggle) ping each installed mod's source for its latest
  release/tag, compare against the manifest's installed version, persist the result.
- Library row amber up-arrow, the 24-hour staleness rule, and the "Update Available" sheet with changelog plus a
  confirm wired to Core's update pipeline (209).

## Out of scope

- Storing, entering, validating-in-UI or clearing the PAT (504). This task defines the injection point only.
- Update pipeline internals — clone, backup, swap (207/209). This task calls it.
- The Discover registry fetch and its cache (211): registry *JSON* is that task, GitHub *repo* content is this one.
- The in-app auto-updater for Game Polish itself (605); only the mod-update path is here.
- Rendering changelogs (502) — this task returns the text, that one formats it.

## Implementation notes

- **Endpoints** (REST v3, version header pinned): `GET /repos/{owner}/{repo}/releases`, `/releases/latest`
  (cheapest answer for the check), `/tags` (fallback for repos with tags but no releases), `/rate_limit` (quota
  read; it does **not** consume quota) and `/user` (token validation for 504). Every request sends
  `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28` and a real `User-Agent` — GitHub
  rejects requests without one — and carries the Bearer authorization header only when a token is configured.
- **Conditional requests — the non-obvious part.** A `304 Not Modified` is exempt from the rate limit only when
  the request was **authenticated**; an anonymous `If-None-Match` request that returns 304 still decrements
  `x-ratelimit-remaining`. ETags are therefore always a correctness/bandwidth win but a quota win only with a
  token. Encode that in a test.
- **Quota parsing**: `x-ratelimit-limit`, `-remaining`, `-used`, `-reset` (Unix epoch **seconds** —
  `DateTimeOffset.FromUnixTimeSeconds`) and `-resource`. Keep the latest values on the client as an observable
  `GitHubRateLimit` so 504 renders "✓ Token valid · Rate limit: 4,823 / 5,000 remaining" without a second call.
  Ceilings: 60/hour unauthenticated per IP, 5,000/hour authenticated; exhaustion arrives as `403` (or `429`) with
  `x-ratelimit-remaining: 0`, and secondary limits carry `retry-after`.
- **Degradation shape**: return a result type — `GitHubResult<T>` with `Success(T)`, `NotModified(cached)`,
  `RateLimited(resetAt, remaining)`, `Failed(reason)` — instead of throwing at call sites. The notice in
  `mod-detail.md` is a *normal* render path: "Couldn't fetch version info — GitHub rate limit reached. Add a
  token in App Settings." A raw exception reaching the UI is a bug.
- **Token injection**: `IGitHubTokenProvider.GetTokenAsync()` → `string?`, implemented in the Desktop composition
  root over 504's `ISecretStore`. Ask once per call; never cache the token in a static field, never let it reach a
  log message (504 defines the redaction rule).
- **Transport**: one `HttpClient` via `IHttpClientFactory` (named client), 15 s timeout, automatic decompression;
  one retry on a transient transport error, never on 403/429.
- **Bounded concurrency**: `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4` (or `SemaphoreSlim(4)`) over
  the installed mods. Unbounded fan-out against 60 req/hour eats the whole quota in ten seconds.
- **Offline skip**: check `NetworkInterface.GetIsNetworkAvailable()` before the sweep and treat a failed request as
  "no update information for this mod" — silent, not a toast or an error state. The check must never block the UI
  or fail the launch.
- **Version comparison**: prefer a semver-ish comparer and strip a leading `v`. Task 206's `VersionKind`
  ("sha" | "tag") tells you which side you hold: a SHA-installed mod has no comparable version, so record
  "unknown" and leave the row unbadged — false "update available" badges train users to ignore them.
- **Persistence (the 206 extension)**: add per-mod `last_checked_at`, `latest_known_version`,
  `latest_known_source_ref`. These must be **optional** in the JSON — 206's `InstalledMod` uses `required` init
  members, so a new required member would make every existing manifest fail to deserialize. Missing = never
  checked; add a test loading a manifest written before these fields existed.
- **Staleness rule**: version info is stale when `now - last_checked_at > 24 hours` (`library.md`), and stale info
  renders de-emphasised, never as a fresh claim.
- **Update-available UI path**: the row's version tag turns amber with an up-arrow ("Update Indicator"); clicking
  opens the "Update Available" sheet showing the changelog (503 supplies the release body, 502's renderer formats
  it) and a confirm button calling Core's update pipeline (209), which creates the backup before the first write.
- **Gate on the setting**: the sweep runs on session start only when 504's "Check for mod updates on session start" is On; with Never, updates appear only from the row menu's manual "Check for Update" (303's overflow menu).

## Acceptance criteria

1. `IGitHubClient` returns releases and tags for a real repo; a unit test covers both payload shapes and the
   tag-only fallback.
2. A second request for the same resource sends `If-None-Match` and, on `304`, serves the cached body without
   re-parsing — asserted with an `HttpMessageHandler` stub that inspects the header.
3. `x-ratelimit-*` headers parse including the epoch-seconds reset; a header-less response degrades without
   throwing, and a 403 with `x-ratelimit-remaining: 0` yields the `RateLimited` result the UI renders as the
   documented notice — never an exception, never a toast.
4. The sweep respects `MaxDegreeOfParallelism = 4`, skips when offline, and writes `last_checked_at` +
   `latest_known_version` for each mod it checked.
5. A newer latest tag shows the amber up-arrow; an equal, unparseable or SHA-installed version does not — and
   version info older than 24 hours renders as stale.
6. A manifest written before the new fields existed still loads and is treated as "never checked".
7. The "Update Available" sheet's confirm button invokes Core's update pipeline, and a backup exists on disk
   before the first byte is written (invariant 1 — test it, do not eyeball it).

## Verification

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~GitHub"          # client, etag, quota, degradation
dotnet test --filter "FullyQualifiedName~UpdateCheck"     # sweep, concurrency, staleness

# real API, unauthenticated (consumes quota — 60/hour, don't loop it)
gh api rate_limit --jq '.resources.core'                  # gh 2.95.0 is on this host
curl -sI https://api.github.com/repos/brues-code/pfUI/releases | grep -i '^x-ratelimit'

# conditional path: capture the etag, replay with If-None-Match, expect 304
ETAG=$(curl -sI https://api.github.com/repos/brues-code/pfUI/tags | grep -i '^etag' | tr -d '\r' | cut -d' ' -f2)
curl -s -o /dev/null -w '%{http_code}\n' -H "If-None-Match: $ETAG" https://api.github.com/repos/brues-code/pfUI/tags
```

Run both acceptance scenarios: (a) with a PAT configured, the quota display shows a 5,000-scale limit and a real
repo's update is detected and badged; (b) with no token and the anonymous budget exhausted, the mod-detail panel
and the library row show the documented notice, not an error. Paste the real output and test counts.

## References

- `../game-polish-notes/docs/views/library.md` — Update Indicator; Data Requirements (24 h staleness).
- `../game-polish-notes/docs/views/mod-detail.md` — releases/tags endpoints, per-session cache, exact rate-limit
  notice; `../game-polish-notes/docs/views/app-settings.md` — PAT purpose and the session-start toggle.
- `.todo/000-CONVENTIONS.md` — invariant 2, invariant 5; secrets never on disk, in a log or in an export.
- Tasks: **206** (`Version`/`VersionKind`, persistence), **212** (operation log), **504** (PAT + quota display),
  **502** (changelog rendering), **209** (update pipeline), **501**/**303** (badge and row-menu call sites).
