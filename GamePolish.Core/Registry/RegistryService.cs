using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Registry;

/// <summary>
/// Fetches a game's registry JSON, validates it, caches it under the game's data directory, and serves
/// that cache when the network is not there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-flight, and where the state lives.</b> The session state is an instance
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of <see cref="Lazy{T}"/> tasks, one entry per game id:
/// a <see cref="Lazy{T}"/> with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> is the
/// single-flight primitive, so N concurrent callers get one <see cref="Task{TResult}"/> and one HTTP
/// request without a lock. It is an instance field rather than a static one - contrary to
/// <see cref="Manifest.ManifestStore"/>'s per-game gates - because a static entry would be shared by two
/// services over two different data roots in one process (the tests, and the CLI's
/// <c>--data-root</c>), and one root's freshly fetched registry would then be served as the other's. The
/// cost is the documented contract on <see cref="IRegistryService"/>: the app registers one instance per
/// process.
/// </para>
/// <para>
/// <b>A failed attempt never sticks.</b> A faulted entry is removed from the dictionary before the
/// exception reaches the caller, so the Discover banner's Retry button - which calls
/// <see cref="GetAsync"/> with <c>forceRefresh: false</c> - really does try again. An offline *result*,
/// on the other hand, is remembered for the session like any other result: the once-per-session rule
/// means one attempt per game, not one attempt per view open.
/// </para>
/// <para>
/// <b>Nothing here is executed.</b> The payload's <c>source</c> URLs are validated as strings and stored
/// as strings; task 207 is what turns one into a git invocation, and it validates the URL itself. The
/// only filesystem writes are the cache file under the data root (invariant #4), and a write that fails
/// is a warning rather than a failed fetch - the user already has the data.
/// </para>
/// </remarks>
public sealed class RegistryService : IRegistryService
{
    /// <summary>
    /// Largest registry body accepted, 5 MiB. A registry is a list of mods, not a download, and the cap
    /// is enforced twice: on a declared <c>Content-Length</c> before a byte is read, and again while
    /// reading, because a chunked response declares nothing.
    /// </summary>
    public const int MaximumPayloadBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Age at which a cached registry is stale (the Discover view's amber 7-day warning).
    /// </summary>
    public static readonly TimeSpan StalenessThreshold = TimeSpan.FromDays(7);

    /// <summary>
    /// How long one fetch may take before it is treated as offline. Enforced with a token rather than
    /// <see cref="HttpClient.Timeout"/> so that the injected client's own timeout cannot pre-empt it
    /// with a different exception shape.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The process-wide client used by <see cref="RegistryService(GamePolishPaths)"/>. One instance for
    /// the process, per the work order: a client per fetch would exhaust sockets.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new();

    /// <summary>
    /// The shared serializer options plus case-insensitive member matching.
    /// </summary>
    /// <remarks>
    /// The registry file is authored outside this repository, so <c>game_id</c> from the design notes and
    /// <c>gameId</c> from a hand-written file both have to land on <see cref="Registry.GameId"/>.
    /// Everything else - snake_case writing, unknown members ignored - comes from
    /// <see cref="AtomicJson.Options"/>, so the cache file and the registry parse the same way.
    /// </remarks>
    private static readonly JsonSerializerOptions RegistryJsonOptions = new(AtomicJson.Options)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The product token, e.g. <c>GamePolish/1.0.0.0</c>. GitHub answers 403 to a request with no user agent.</summary>
    private static readonly string UserAgentVersion =
        typeof(RegistryService).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly GamePolishPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Action<RegistryWarning>? _onWarning;

    /// <summary>One entry per game: the fetch this session has already started (or finished).</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<RegistryResult>>> _session = new(StringComparer.Ordinal);

    /// <summary>Initializes the service over a data root, with a shared <see cref="HttpClient"/>.</summary>
    /// <param name="paths">The resolved data root: the cache lives under it and nowhere else.</param>
    public RegistryService(GamePolishPaths paths)
        : this(paths, SharedHttpClient)
    {
    }

    /// <summary>Initializes the service over a data root, with an injected client.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="httpClient">The client to fetch with. Tests inject one whose handler is a double.</param>
    public RegistryService(GamePolishPaths paths, HttpClient httpClient)
        : this(paths, httpClient, TimeProvider.System, null)
    {
    }

    /// <summary>Initializes the service with every collaborator injected.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="httpClient">The client to fetch with.</param>
    /// <param name="timeProvider">The clock behind <c>last_fetched</c> and the staleness comparison.</param>
    /// <param name="onWarning">
    /// Sink for warning-level diagnostics: a non-GitHub source host, a cache write that failed, a
    /// payload rejected while a cached copy was served. Null discards them. Must be thread-safe and must
    /// not throw; see <see cref="RegistryWarning"/>.
    /// </param>
    public RegistryService(
        GamePolishPaths paths,
        HttpClient httpClient,
        TimeProvider? timeProvider = null,
        Action<RegistryWarning>? onWarning = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(httpClient);

        _paths = paths;
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onWarning = onWarning;
    }

    /// <inheritdoc/>
    public async Task<RegistryResult> GetAsync(
        string gameId,
        string registryUrl,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        // The id is also the cache directory's name, so GameDirectory (which refuses anything that is
        // not a single path segment) runs before a request or a session entry exists. A traversal-shaped
        // id fails here, not somewhere inside a fetch.
        _ = _paths.GameDirectory(gameId);

        // Throws for a non-https or credential-bearing URL before the once-per-session check, so a
        // configuration mistake surfaces on every call rather than only on the first.
        string url = ValidateRegistryUrl(registryUrl);
        cancellationToken.ThrowIfCancellationRequested();

        if (forceRefresh)
        {
            // Removing the entry and re-adding it through GetOrAdd keeps concurrent refreshes coalesced:
            // whoever wins the add performs the one request and everybody awaits it.
            _session.TryRemove(gameId, out _);
        }
        else if (_session.TryGetValue(gameId, out Lazy<Task<RegistryResult>>? existing))
        {
            return WithCurrentStaleness(await existing.Value.WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        Lazy<Task<RegistryResult>> entry = _session.GetOrAdd(gameId, _ => CreateSessionEntry(gameId, url));

        // WaitAsync, not a token threaded into the fetch: a caller that cancels stops waiting, while the
        // one shared request keeps running for everybody else and still fills the cache.
        return WithCurrentStaleness(await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Creates the single-flight entry that removes itself when its fetch fails.</summary>
    private Lazy<Task<RegistryResult>> CreateSessionEntry(string gameId, string registryUrl)
    {
        Lazy<Task<RegistryResult>>? entry = null;

        entry = new Lazy<Task<RegistryResult>>(
            () => FetchAndForgetOnFailureAsync(gameId, registryUrl, entry!),
            LazyThreadSafetyMode.ExecutionAndPublication);

        return entry;
    }

    /// <summary>Runs the fetch, and forgets the session entry if it throws.</summary>
    private async Task<RegistryResult> FetchAndForgetOnFailureAsync(
        string gameId,
        string registryUrl,
        Lazy<Task<RegistryResult>> entry)
    {
        try
        {
            return await FetchAsync(gameId, registryUrl).ConfigureAwait(false);
        }
        catch
        {
            // The compare-and-remove is by reference: if a forceRefresh already replaced this entry, the
            // new one must not be removed along with it.
            _session.TryRemove(new KeyValuePair<string, Lazy<Task<RegistryResult>>>(gameId, entry));

            throw;
        }
    }

    /// <summary>One fetch: read the cache for its ETag, ask the server, then accept, revalidate or fall back.</summary>
    private async Task<RegistryResult> FetchAsync(string gameId, string registryUrl)
    {
        RegistryCacheFile? cache = ReadCache(gameId);

        using HttpRequestMessage request = BuildRequest(registryUrl, cache);
        using CancellationTokenSource timeout = new(RequestTimeout);

        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return NotModified(gameId, registryUrl, cache);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Offline(
                    gameId,
                    registryUrl,
                    cache,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            }

            (string? body, long sizeBytes) = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);

            return Accept(gameId, registryUrl, cache, body, sizeBytes, response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Offline(
                gameId,
                registryUrl,
                cache,
                $"the request did not finish within {RequestTimeout.TotalSeconds:0} seconds");
        }
        catch (OperationCanceledException)
        {
            // Not this call's timeout: the client or the transport gave up first. Reported as a failed
            // fetch rather than as the caller's cancellation, which cannot reach this point (the fetch
            // runs with its own token, see GetAsync).
            return Offline(gameId, registryUrl, cache, "the HTTP client abandoned the request before a response arrived");
        }
        catch (HttpRequestException exception)
        {
            return Offline(gameId, registryUrl, cache, exception.Message);
        }
        catch (IOException exception)
        {
            // A connection dropped mid-body arrives here, after ResponseHeadersRead has returned.
            return Offline(gameId, registryUrl, cache, exception.Message);
        }
    }

    /// <summary>
    /// Builds the GET. No <c>Authorization</c> header is ever attached and no header is ever logged, so
    /// there is no credential in this request to redact (the GitHub PAT arrives in task 504).
    /// </summary>
    private static HttpRequestMessage BuildRequest(string registryUrl, RegistryCacheFile? cache)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(registryUrl, UriKind.Absolute));

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GamePolish", UserAgentVersion));

        // An entity tag is scoped to the URL that issued it: replaying one at a different URL is how a
        // 304 ends up showing the user a different game's mods.
        bool sameUrl = cache is not null
            && string.Equals(cache.SourceUrl, registryUrl, StringComparison.Ordinal);

        if (sameUrl && !string.IsNullOrWhiteSpace(cache!.ETag))
        {
            // Verbatim, quotes included: the tag is opaque, and re-quoting it stops it matching.
            request.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);
        }

        return request;
    }

    /// <summary>Handles a 304: keep the payload, move <c>last_fetched</c> forward.</summary>
    private RegistryResult NotModified(string gameId, string registryUrl, RegistryCacheFile? cache)
    {
        if (cache is null)
        {
            // Only reachable through an intermediary that answered from a cache this process cannot see.
            // Without a copy to show, a 304 is a failure, not a hit.
            return Offline(gameId, registryUrl, null, "the server answered 304 Not Modified but there is no cached copy");
        }

        // A class, not a record, so the copy is spelled out: an explicit field list is one line per field
        // a future schema version adds, and the compiler makes that line impossible to forget.
        RegistryCacheFile revalidated = new()
        {
            SchemaVersion = cache.SchemaVersion,
            ETag = cache.ETag,
            SourceUrl = cache.SourceUrl,
            LastFetched = _timeProvider.GetUtcNow(),
            Registry = cache.Registry,
        };

        TryWriteCache(gameId, revalidated);

        return FromCached(revalidated, offline: false);
    }

    /// <summary>Decides what to do with a 2xx body: accept it, or keep the cache and go offline.</summary>
    private RegistryResult Accept(
        string gameId,
        string registryUrl,
        RegistryCacheFile? cache,
        string? body,
        long sizeBytes,
        string? etag)
    {
        List<RegistryProblem> problems = [];
        Registry? registry = null;

        if (body is null)
        {
            problems.Add(Problem(
                RegistryProblem.ReasonTooLarge,
                $"the registry body is larger than the {MaximumPayloadBytes} byte limit ({sizeBytes} bytes were sent). "
                + "A registry is a list of mods, not a download."));
        }
        else
        {
            registry = Parse(body, problems);

            if (registry is not null)
            {
                Validate(registry, gameId, problems);
            }
        }

        if (registry is not null && problems.Count == 0)
        {
            DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

            RegistryCacheFile fresh = new()
            {
                ETag = etag,
                SourceUrl = registryUrl,
                LastFetched = fetchedAt,
                Registry = registry,
            };

            TryWriteCache(gameId, fresh);

            return new RegistryResult
            {
                Registry = registry,
                FromCache = false,
                Offline = false,
                LastFetched = fetchedAt,
                IsStale = IsStale(fetchedAt, fetchedAt),
            };
        }

        // Every problem is logged, one line each, so the log names the mods that need editing.
        foreach (RegistryProblem problem in problems)
        {
            Warn(RegistryWarningCode.PayloadRejected, problem.Message, problem.ModId);
        }

        if (cache is not null)
        {
            return FromCached(cache, offline: true);
        }

        throw new RegistryValidationException(gameId, registryUrl, problems);
    }

    /// <summary>Parses the body, or records why it could not be parsed.</summary>
    private static Registry? Parse(string body, List<RegistryProblem> problems)
    {
        // JsonSerializer does not mind a byte-order mark, but the string decode keeps one, and a stray
        // U+FEFF is not JSON whitespace. Cheaper to strip it than to explain it to a registry author.
        ReadOnlySpan<char> text = body.AsSpan();
        if (!text.IsEmpty && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        try
        {
            Registry? registry = JsonSerializer.Deserialize<Registry>(text, RegistryJsonOptions);

            if (registry is null)
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonMalformedJson,
                    "the payload is JSON null. A registry is an object with game_id, updated_at and mods."));
            }

            return registry;
        }
        catch (JsonException exception)
        {
            problems.Add(Problem(
                RegistryProblem.ReasonMalformedJson,
                $"the payload is not valid JSON for a registry: {exception.Message}"));

            return null;
        }
    }

    /// <summary>
    /// Validates the payload against the registry schema, appending every problem found.
    /// </summary>
    /// <remarks>
    /// All problems, never the first: a broken registry usually breaks in several places at once, and a
    /// one-at-a-time report costs a round trip per fix. The messages name the entry (<c>mods[3]</c>) and
    /// the mod id, because that is what the person editing the JSON needs to find it.
    /// </remarks>
    private void Validate(Registry registry, string requestedGameId, List<RegistryProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(registry.GameId))
        {
            problems.Add(Problem(
                RegistryProblem.ReasonMissingGameId,
                $"game_id is missing. Every registry must name the game it lists, e.g. \"game_id\": \"{requestedGameId}\"."));
        }
        else if (!string.Equals(registry.GameId, requestedGameId, StringComparison.Ordinal))
        {
            problems.Add(Problem(
                RegistryProblem.ReasonGameIdMismatch,
                $"game_id is '{registry.GameId}', but this registry was requested for '{requestedGameId}'. "
                + "A registry lists exactly one game's mods, and installing these would put another game's files in this one."));
        }

        if (registry.UpdatedAt == default)
        {
            problems.Add(Problem(
                RegistryProblem.ReasonMissingUpdatedAt,
                "updated_at is missing. It tells the UI how current the registry is; use an ISO 8601 timestamp such as \"2026-10-15T00:00:00Z\"."));
        }

        // Null despite the non-nullable annotation: the payload is external, and "mods": null is a JSON
        // value System.Text.Json will happily assign.
        if (registry.Mods is null)
        {
            problems.Add(Problem(
                RegistryProblem.ReasonMissingMods,
                "mods is missing. Use an empty array when the game has no mods yet."));

            return;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);

        for (int index = 0; index < registry.Mods.Count; index++)
        {
            RegistryMod? mod = registry.Mods[index];

            if (mod is null)
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonMissingField,
                    $"mods[{index}] is null. Remove the entry or give it an id, name, author, description, source and tags."));

                continue;
            }

            string where = Describe(index, mod.Id);

            if (string.IsNullOrWhiteSpace(mod.Id))
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonInvalidModId,
                    $"{where}: id is missing. Use lowercase letters, digits and hyphens, starting with a letter or digit.",
                    mod.Id));
            }
            else if (!IsValidModId(mod.Id))
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonInvalidModId,
                    $"{where}: id '{mod.Id}' is not a valid mod id. Use lowercase letters, digits and hyphens, "
                    + $"starting with a letter or digit (at most {MaximumModIdLength} characters), e.g. 'pfui'.",
                    mod.Id));
            }
            else if (!ids.Add(mod.Id))
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonDuplicateModId,
                    $"{where}: duplicate id - an earlier entry already uses '{mod.Id}'. Ids identify a mod for the "
                    + "manifest and the install badges, so they must be unique across the file.",
                    mod.Id));
            }

            RequireText(where, "name", mod.Name, mod.Id, problems);
            RequireText(where, "author", mod.Author, mod.Id, problems);
            RequireText(where, "description", mod.Description, mod.Id, problems);

            if (string.IsNullOrWhiteSpace(mod.Source))
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonMissingField,
                    $"{where}: source is missing. Use https://github.com/<owner>/<repo>.",
                    mod.Id));
            }
            else
            {
                ValidateSource(where, mod, problems);
            }

            if (mod.Tags is null)
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonMissingTags,
                    $"{where}: tags is missing. Use [] when the mod has no tags.",
                    mod.Id));
            }
            else if (mod.Tags.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add(Problem(
                    RegistryProblem.ReasonMissingField,
                    $"{where}: tags contains an empty entry. Remove it, or use [] when the mod has no tags.",
                    mod.Id));
            }
        }
    }

    /// <summary>Validates a mod's source: absolute https, no credentials, and a warning for a non-GitHub host.</summary>
    private void ValidateSource(string where, RegistryMod mod, List<RegistryProblem> problems)
    {
        if (!Uri.TryCreate(mod.Source, UriKind.Absolute, out Uri? uri))
        {
            problems.Add(Problem(
                RegistryProblem.ReasonInvalidSource,
                $"{where}: source '{mod.Source}' is not a URL. Use https://github.com/<owner>/<repo>.",
                mod.Id));

            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(Problem(
                RegistryProblem.ReasonNonHttpsSource,
                $"{where}: source '{mod.Source}' is a {uri.Scheme}:// URL. Only https:// sources are accepted: "
                + "file:// reaches this machine's disk, and http:// is an unauthenticated download of code the game will load.",
                mod.Id));

            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // The credential itself is never echoed: this message goes to the log, and a secret that
            // reaches a log line has already leaked (task 212's redactor is the second line of defence,
            // not the first).
            string sanitised = $"{uri.Scheme}://<credentials>@{uri.Host}{uri.AbsolutePath}";

            problems.Add(Problem(
                RegistryProblem.ReasonCredentialInSource,
                $"{where}: source '{sanitised}' carries an embedded credential, which would be copied into "
                + "registry-cache.json and the install manifest as plain text. Use the plain https://host/path form.",
                mod.Id));

            return;
        }

        if (!IsGithubHost(uri))
        {
            // A warning, not a problem (octowow.md calls out the incumbent's inability to handle a Gitea
            // mirror). Task 207 clones whatever https URL it is given and validates it itself, so this is
            // a diagnostic: either a legitimate mirror, or a typo in the host.
            Warn(
                RegistryWarningCode.NonGithubSource,
                $"{where}: source '{mod.Source}' is on '{uri.Host}', not github.com. A mirror is installable, "
                + "but a typo in the host would only fail later, at install time.",
                mod.Id);
        }
    }

    /// <summary>Appends <c>missing-field</c> for a required mod string that is empty.</summary>
    private static void RequireText(
        string where,
        string field,
        string? value,
        string? modId,
        List<RegistryProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(Problem(
                RegistryProblem.ReasonMissingField,
                $"{where}: {field} is missing or empty. The Discover card cannot be built without it.",
                modId));
        }
    }

    /// <summary>Reads the cache, or returns <see langword="null"/> with a warning when it cannot be used.</summary>
    private RegistryCacheFile? ReadCache(string gameId)
    {
        string cacheFile = _paths.RegistryCacheFile(gameId);
        RegistryCacheFile? cache;

        try
        {
            cache = AtomicJson.Read<RegistryCacheFile>(cacheFile);
        }
        catch (JsonException exception)
        {
            Warn(RegistryWarningCode.CacheUnreadable, $"the registry cache '{cacheFile}' is not readable and will be replaced: {exception.Message}");

            return null;
        }

        if (cache is null)
        {
            return null;
        }

        string? problem = DescribeCacheProblem(cache, gameId);
        if (problem is not null)
        {
            Warn(RegistryWarningCode.CacheUnreadable, $"the registry cache '{cacheFile}' is not used: {problem}");

            return null;
        }

        return cache;
    }

    /// <summary>
    /// Why a parsed cache file cannot be trusted, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// The file is this app's own, but it is a plain JSON file a user can edit, and a half-edited one must
    /// not reach the UI as a null list. A cache the app refuses is simply overwritten by the next fetch,
    /// which is why the failure is a warning and not a quarantine - unlike the manifest, nothing here is
    /// unrecoverable.
    /// </remarks>
    private static string? DescribeCacheProblem(RegistryCacheFile cache, string gameId)
    {
        if (cache.SchemaVersion != RegistryCacheFile.CurrentSchemaVersion)
        {
            return $"it was written with schema_version {cache.SchemaVersion}, and this build reads {RegistryCacheFile.CurrentSchemaVersion}";
        }

        if (cache.Registry is null || cache.Registry.Mods is null)
        {
            return "it has no registry object with a mods array";
        }

        if (!string.Equals(cache.Registry.GameId, gameId, StringComparison.Ordinal))
        {
            return $"it holds the registry for '{cache.Registry.GameId}', not '{gameId}'";
        }

        if (cache.Registry.Mods.Any(mod => mod is null || mod.Tags is null || string.IsNullOrWhiteSpace(mod.Id)))
        {
            return "a mod entry has no id or no tags";
        }

        return null;
    }

    /// <summary>Writes the cache, turning any failure into a warning: the caller already has the data.</summary>
    private void TryWriteCache(string gameId, RegistryCacheFile cache)
    {
        try
        {
            _paths.EnsureGameDirectory(gameId);
            AtomicJson.Write(_paths.RegistryCacheFile(gameId), cache);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A read-only profile or a full disk must not cost the user the registry they just fetched;
            // the next fetch simply starts cold.
            Warn(
                RegistryWarningCode.CacheWriteFailed,
                $"the registry cache for '{gameId}' could not be written, so the next fetch will not be conditional: {exception.Message}");
        }
    }

    /// <summary>Reports a failed fetch, serving the cache when there is one.</summary>
    private RegistryResult Offline(string gameId, string registryUrl, RegistryCacheFile? cache, string diagnostic)
    {
        Warn(RegistryWarningCode.FetchFailed, $"the registry at {registryUrl} could not be fetched: {diagnostic}");

        if (cache is null)
        {
            throw new RegistryUnavailableException(gameId, registryUrl, diagnostic);
        }

        return FromCached(cache, offline: true);
    }

    /// <summary>Builds a result from the cached copy, with staleness measured now.</summary>
    private RegistryResult FromCached(RegistryCacheFile cache, bool offline) => new()
    {
        Registry = cache.Registry,
        FromCache = true,
        Offline = offline,
        LastFetched = cache.LastFetched,
        IsStale = IsStale(cache.LastFetched, _timeProvider.GetUtcNow()),
    };

    /// <summary>
    /// Re-measures staleness on a session hit: a session that has been open for a week must not keep
    /// reporting the freshness the fetch had on day one.
    /// </summary>
    private RegistryResult WithCurrentStaleness(RegistryResult result) =>
        result with { IsStale = IsStale(result.LastFetched, _timeProvider.GetUtcNow()) };

    /// <summary>True when <paramref name="lastFetched"/> is more than seven days before <paramref name="now"/>.</summary>
    private static bool IsStale(DateTimeOffset lastFetched, DateTimeOffset now) => now - lastFetched > StalenessThreshold;

    /// <summary>
    /// Reads the body, refusing anything over <see cref="MaximumPayloadBytes"/>.
    /// </summary>
    /// <returns>
    /// The decoded text, or <see langword="null"/> when the body is too large, with the byte count in
    /// both cases. Deferred rejection rather than an exception keeps "too large" on the same path as
    /// every other payload problem, so it falls back to the cache like the rest.
    /// </returns>
    private static async Task<(string? Text, long SizeBytes)> ReadBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > MaximumPayloadBytes)
        {
            return (null, declared);
        }

        using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();

        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                // The second half of the cap: a chunked response declares no length, so the limit has to
                // be enforced while reading or not at all.
                if (total > MaximumPayloadBytes)
                {
                    return (null, total);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), total);
    }

    /// <summary>Validates the registry URL the caller passed before anything is requested.</summary>
    /// <remarks>
    /// https only: the registry decides which code the user will install, and an unauthenticated fetch of
    /// that list is a downgrade no feature needs. Embedded credentials are refused because the URL is
    /// written to <c>source_url</c> in the cache, which is plain text (task 504 owns the PAT, and it must
    /// never arrive here).
    /// </remarks>
    private static string ValidateRegistryUrl(string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            throw new ArgumentException("A registry URL is required.", nameof(registryUrl));
        }

        string url = registryUrl.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException($"'{registryUrl}' is not an absolute URL.", nameof(registryUrl));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{registryUrl}' is a {uri.Scheme}:// URL. The registry must be fetched over https://: it decides "
                + "which code the user will install, and a plain-text fetch of that list can be rewritten in transit.",
                nameof(registryUrl));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                $"'{registryUrl}' carries an embedded credential, which would be written to registry-cache.json as "
                + "plain text. Use the plain https://host/path form.",
                nameof(registryUrl));
        }

        return url;
    }

    /// <summary>True for the mod id shape the registry schema fixes.</summary>
    private static bool IsValidModId(string id)
    {
        if (id.Length is 0 or > MaximumModIdLength)
        {
            return false;
        }

        for (int index = 0; index < id.Length; index++)
        {
            char character = id[index];

            bool valid = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
            if (!valid || (index == 0 && character == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True for github.com and its <c>www.</c> spelling; anything else is a warning only.</summary>
    private static bool IsGithubHost(Uri uri) =>
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names a mod entry in a problem message: its index, and its id when it has one.</summary>
    private static string Describe(int index, string? modId) =>
        string.IsNullOrWhiteSpace(modId) ? $"mods[{index}]" : $"mods[{index}] ('{modId}')";

    private static RegistryProblem Problem(string reason, string message, string? modId = null) => new()
    {
        Reason = reason,
        Message = message,
        ModId = modId,
    };

    /// <summary>Longest mod id accepted; the same limit the game id uses, and far beyond any real addon.</summary>
    private const int MaximumModIdLength = 64;

    private void Warn(RegistryWarningCode code, string message, string? modId = null) =>
        _onWarning?.Invoke(new RegistryWarning { Code = code, Message = message, ModId = modId });
}
