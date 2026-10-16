---
id: 211
title: Registry fetch and cache
phase: 2
bucket: mvp
depends_on: [201, 202]
blocks: [213, 305, 501]
status: todo
area: core
---

# 211 — Registry fetch and cache
## Objective
The app can obtain the list of installable mods for a game: fetch the game's `registry_url`, validate the payload against the registry schema, cache it under the
per-game data folder with its ETag and fetch time, and serve the cache — flagged offline — when the network is unavailable. The Discover view gets both the mod list and
an honest staleness timestamp.

## Context
Greenfield repo. Prerequisites: `201` (`GamePolishPaths.RegistryCacheFile`, `AtomicJson`) and `202` (`GameDescriptor.RegistryUrl`). Phase 2 order: **201/202/203 first;
204 and 205 are the two tasks everything else leans on; then 206–210; then 211–213.**

Authoritative behaviour: `../game-polish-notes/docs/views/discover.md` § *Data Requirements* ("Registry JSON fetched from the URL in the game YAML (`registry_url`).
Cached locally at `~/.game-polish/<game_id>/registry-cache.json` with an `etag`/`last_fetched` timestamp. Registry is re-fetched at most once per session (or on manual
refresh). Installed mod IDs come from `…/manifest.json`"), § *Registry Staleness* ("A 'Last updated X ago' line appears below the tag bar… If the cache is >7 days old
an amber warning is shown automatically"), and § *Empty / Edge States* ("Registry fetch failed: Error banner — 'Couldn't load registry. Check your connection.' with a
Retry button. Stale cached data is shown with an 'Offline — showing cached results' notice.").

The registry JSON shape is `CLAUDE.md` § *Registry JSON Schema (draft)*; the seed rows it will contain are `../game-polish-notes/docs/games/octowow.md` § *Seed
Registry*. Authoring the real registry repo is task `402`.

Restated invariants: **(#4)** the cache is written under the data root and nowhere else; the fetch is a read of a public HTTPS URL, and the engine never writes into a
game directory here, so **(#1)** does not apply. **(#2)** nothing from the payload is executed — URLs are stored as strings and only ever handed to `207`, which
validates them itself.

## Scope
- `GamePolish.Core/Registry/RegistryMod.cs`, `Registry.cs`, `RegistryCacheFile.cs`, `RegistryResult.cs`.
- `GamePolish.Core/Registry/IRegistryService.cs` + `RegistryService.cs`.
- `GamePolish.Core/Registry/RegistryValidationException.cs`, `RegistryUnavailableException.cs`.
- Tests: cache round trip, ETag/304 behaviour, once-per-session semantics, staleness at the 7-day boundary, offline-with-cache, offline-without-cache, and schema
  validation failures.

## Out of scope
- The Discover UI, search/sort/tag pills and install badges (`305` for the MVP list, `501` for the full view).
- GitHub API rate limits, tags and changelogs (`503`); the GitHub PAT and the keychain (`504`).
- Writing or hosting the registry repo itself (`402`).
- Thumbnail downloading or image caching. The `thumbnail` URL is passed through as a string.

## Implementation notes
```csharp
public sealed record RegistryMod(
    string Id, string Name, string Author, string Description, string Source,
    IReadOnlyList<string> Tags, string? Thumbnail, DateTimeOffset? AddedAt);
public sealed record Registry(
    string GameId, DateTimeOffset UpdatedAt, IReadOnlyList<RegistryMod> Mods);
public sealed class RegistryCacheFile          // serialized to <data root>/<gameId>/registry-cache.json
{
    public int SchemaVersion { get; init; } = 1;
    public string? ETag { get; init; }
    public string SourceUrl { get; init; } = "";
    public DateTimeOffset LastFetched { get; init; }
    public Registry Registry { get; init; } = default!;
}
public sealed record RegistryResult(
    Registry Registry, bool FromCache, bool Offline, DateTimeOffset LastFetched, bool IsStale);
public interface IRegistryService
{
    // forceRefresh bypasses the once-per-session rule (the Discover refresh icon).
    Task<RegistryResult> GetAsync(string gameId, string registryUrl, bool forceRefresh, CancellationToken ct);
}
```
- JSON: `System.Text.Json` with the same `AtomicJson.Options` as every other store (`201`) — snake_case on the wire, matching `game_id`, `updated_at`, `added_at`. The
  registry file is external, so parse it into `Registry` through the same options and treat unknown properties as ignorable.
- **Validation, with actionable messages** (`RegistryValidationException` carrying all problems): `game_id` present and equal to the requested `gameId` (a mismatch is
  the single most likely authoring bug and must name both values); `updated_at` present; `mods` non-null; per mod: `id` matches `^[a-z0-9][a-z0-9-]*$` and is unique
  across the file, `name`/`author`/`description`/`source` non-empty, `source` an `https://` GitHub URL (a non-GitHub host is a warning-level log entry, not a hard
  failure — `octowow.md` § *Why This Game First* mentions Gitea URLs as a legitimate gap in the incumbent), `tags` non-null (empty allowed). Collect all problems; never
  fail with "invalid registry".
- Response cap: reject a body larger than 5 MiB before parsing (`RegistryValidationException` with reason `too-large`). A registry is a list of mods, not a download.
- `HttpClient`: one static instance (or `IHttpClientFactory` in the app; Core takes an `HttpClient` in the constructor so tests can inject a handler). Timeout 15 s via
  the token, `User-Agent: GamePolish/<version>`, `Accept: application/json`. Send `If-None-Match: <etag>` when a cache exists; on **304** update `LastFetched` only and
  return the cached registry with `FromCache = true, Offline = false`.
- **Once per session:** a per-game `ConcurrentDictionary<string, Lazy<Task<RegistryResult>>>` (or an equivalent single-flight) so N concurrent Discover opens produce
  one HTTP call. Constructing the service per view model must not defeat this — the dictionary is static, or the service is registered as a singleton; state which you
  chose in the XML doc comment. `forceRefresh: true` bypasses the cache lookup *and* refreshes the single-flight entry.
- **Failure behaviour, in this exact order:**
1. Non-2xx, timeout, or network exception ⇒ if a cache exists, return it with `Offline = true`, `FromCache = true` and the original `LastFetched`; log the mapped code
  (`NetworkUnavailable`, `Timeout`, or the HTTP status). If no cache exists ⇒ throw `RegistryUnavailableException` (the Discover banner's input).
2. Payload fails validation ⇒ do **not** overwrite a good cache with a bad one; keep the cached copy, return it with `Offline = true`, and log the validation problems.
  A malformed upstream registry must not destroy the user's ability to install.
- **Staleness:** `IsStale = (DateTimeOffset.UtcNow - LastFetched) > TimeSpan.FromDays(7)`. Expose `LastFetched` verbatim so the Discover header can render "Last updated
  X ago" and the 7-day amber warning; the UI computes the string, this task computes the boolean.
- Cache write: `paths.EnsureGameDirectory(gameId)` then `AtomicJson.Write(paths.RegistryCacheFile(gameId), …)`. A cache write failure is a logged warning, never a
  failed fetch — the user gets the data either way.
- Do not store secrets in the cache: strip any `Authorization` header from logged request detail (the PAT arrives in `504`). `212`'s redactor is the second line of
  defence; do not put the token in `SourceUrl`.

## Acceptance criteria
1. Cold fetch: no cache file ⇒ one HTTP request, `FromCache == false`, `Offline == false`, and `registry-cache.json` exists containing `etag` (when the server sent
  one), `last_fetched` and the parsed registry.
2. Warm, same session: a second `GetAsync` without `forceRefresh` performs **zero** additional HTTP calls (assert on the injected handler's call count).
3. `forceRefresh: true` performs one call and updates `last_fetched`.
4. ETag: a second process/session sends `If-None-Match` with the stored value; a 304 response returns the cached registry and does not re-write the payload (only
  `last_fetched` changes).
5. Offline with cache: the handler throws ⇒ `Offline == true`, `FromCache == true`, and `LastFetched` is the old value (not "now").
6. Offline without cache ⇒ `RegistryUnavailableException`, message usable as the banner text, and **no** cache file is created.
7. Invalid payload with a good cache ⇒ cache preserved byte-for-byte, `Offline == true`, and the validation problems are in the log.
8. `game_id` mismatch ⇒ `RegistryValidationException` naming both the requested and the payload's id.
9. Duplicate mod ids, a missing `source`, and a bad `id` format each produce their own problem entry, and all three appear together when all three are present.
10. Staleness boundary: `LastFetched` of `now - 6d23h` ⇒ `IsStale == false`; `now - 7d1m` ⇒ `IsStale == true`.
11. A 6 MiB body is rejected before parsing; no cache file is written.
12. Nothing is written outside the data root (path-prefix assertion on the injected `IFileOps`/handler log).

## Verification
```bash
dotnet test GamePolish.Core.Tests --filter "FullyQualifiedName~RegistryService"
# expect: all pass; the HTTP double counts calls, so criteria 2, 3 and 4 are real assertions
# against the live registry once 402 lands:
dotnet run --project GamePolish.Cli -- --data-root "$TMPDIR/gp" registry refresh --game octowow
cat "$TMPDIR/gp/octowow/registry-cache.json" | head -40
# expect: game_id octowow, a last_fetched timestamp, and the seed rows from octowow.md
```
## References
- `../game-polish-notes/docs/views/discover.md` § *Data Requirements*, § *Registry Staleness*, § *Empty / Edge States*
- `../game-polish-notes/docs/games/octowow.md` § *Seed Registry*, § *Why This Game First* (Gitea gaps)
- `CLAUDE.md` § *Registry JSON Schema (draft)*
- `000-CONVENTIONS.md` § Local state layout, § Hard invariants (#2, #4, #5)
- Related tasks: `201` (`RegistryCacheFile`, `AtomicJson`), `202` (`RegistryUrl`), `305` (Discover-lite consumes `RegistryResult`), `402` (the registry repo), `501`
  (full Discover), `503`/`504` (GitHub API, PAT), `213` (`registry refresh`).
