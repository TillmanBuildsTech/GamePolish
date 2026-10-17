namespace GamePolish.Core.Registry;

/// <summary>
/// What a caller gets from <see cref="IRegistryService.GetAsync"/>: the mod list, where it came from,
/// how old it is, and whether the user is looking at it because the network failed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Offline"/> and <see cref="FromCache"/> answer different questions and are not
/// complements. A conditional request that gets a 304 is a <b>cache hit with a working network</b>
/// (<c>FromCache = true, Offline = false</c>); a failed request served from the cache is
/// <c>FromCache = true, Offline = true</c> and is what the Discover banner's "Offline - showing cached
/// results" notice keys off; a payload fetched this session is <c>FromCache = false, Offline = false</c>.
/// </para>
/// <para>
/// <see cref="IsStale"/> is a boolean and nothing more: the UI owns the string ("Last updated 3 days
/// ago") and the amber 7-day warning, this task owns the comparison. <see cref="LastFetched"/> is
/// exposed verbatim, so the UI never has to reconstruct it from an age that already rounded.
/// </para>
/// </remarks>
public sealed record RegistryResult
{
    /// <summary>The validated registry. Never a partially validated one.</summary>
    public required Registry Registry { get; init; }

    /// <summary>True when the payload came from the cache file rather than off the wire.</summary>
    /// <remarks>
    /// A second call in the same session returns the session's result without touching the network, and
    /// reports <c>FromCache = false</c>: that data did come off the wire, in this session. The flag
    /// describes the payload's origin, not the number of HTTP calls behind it.
    /// </remarks>
    public required bool FromCache { get; init; }

    /// <summary>True when the network attempt failed and a cached copy is being shown instead.</summary>
    public required bool Offline { get; init; }

    /// <summary>
    /// When the copy in hand was last confirmed current. For an offline result this is the old value,
    /// not "now" - the point of the banner is that the data is old.
    /// </summary>
    public required DateTimeOffset LastFetched { get; init; }

    /// <summary>
    /// True when <see cref="LastFetched"/> is more than
    /// <see cref="RegistryService.StalenessThreshold"/> before now.
    /// </summary>
    public required bool IsStale { get; init; }
}
