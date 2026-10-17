namespace GamePolish.Core.Registry;

/// <summary>
/// Obtains a game's mod registry: fetch, validate, cache, and serve the cache when the network is not
/// there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once per session.</b> The first call for a game fetches; every later call without
/// <c>forceRefresh</c> returns the same result without a request. Concurrent calls for the same game
/// share one in-flight fetch, so opening Discover five times does not produce five requests.
/// </para>
/// <para>
/// <b>Register the implementation as a singleton.</b> The once-per-session and single-flight state lives
/// on the <see cref="RegistryService"/> instance, not in a static field, because two services in one
/// process can be pointed at two different data roots (tests, and the CLI's <c>--data-root</c>) and
/// sharing state across them would serve one root's registry from the other's cache. The consequence is
/// a contract rather than a suggestion: the app must build one service per process. A view model that
/// constructs its own service would fetch once per view model, which is exactly what the design note's
/// "re-fetched at most once per session" forbids.
/// </para>
/// <para>
/// <b>Failure is data, not an exception, whenever the cache can answer.</b> A network failure or a
/// payload that fails validation is returned as a <see cref="RegistryResult"/> with
/// <see cref="RegistryResult.Offline"/> set, as long as a cached copy exists. The exceptions are the two
/// cases with nothing to show: <see cref="RegistryUnavailableException"/> (no cache, no network) and
/// <see cref="RegistryValidationException"/> (no cache, invalid payload). That is also the mapping the
/// Discover view needs: throw means banner, no throw means "showing cached results".
/// </para>
/// </remarks>
public interface IRegistryService
{
    /// <summary>
    /// Returns the registry for <paramref name="gameId"/>, fetching it when the session has not already
    /// done so.
    /// </summary>
    /// <param name="gameId">
    /// The game's id, e.g. <c>octowow</c>. Also the cache directory's name, so it is validated as a
    /// single path segment before any file is touched.
    /// </param>
    /// <param name="registryUrl">
    /// The game's <c>registry_url</c> from its descriptor. Must be an absolute <c>https://</c> URL with
    /// no embedded credentials.
    /// </param>
    /// <param name="forceRefresh">
    /// True for the Discover refresh icon: bypass the session's result and re-fetch, still coalescing
    /// with any concurrent refresh. False is the normal "open the view" path.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the registry; it never cancels another
    /// caller's shared fetch.</param>
    /// <returns>The registry, with where it came from and how old it is.</returns>
    /// <exception cref="RegistryUnavailableException">The fetch failed and there is no cached copy.</exception>
    /// <exception cref="RegistryValidationException">The payload failed validation and there is no cached copy.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="gameId"/> is not a single path segment, or <paramref name="registryUrl"/> is not
    /// an absolute https URL without credentials.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<RegistryResult> GetAsync(string gameId, string registryUrl, bool forceRefresh, CancellationToken cancellationToken);
}
