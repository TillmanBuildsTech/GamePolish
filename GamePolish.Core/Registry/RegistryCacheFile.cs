using System.Text.Json.Serialization;

namespace GamePolish.Core.Registry;

/// <summary>
/// What <c>&lt;data root&gt;/&lt;game_id&gt;/registry-cache.json</c> holds: the last registry this app
/// accepted, plus what it needs to revalidate that copy with the server.
/// </summary>
/// <remarks>
/// <para>
/// The cache is a copy of data the app can always re-fetch, so unlike the manifest it is disposable:
/// a corrupt or unreadable file is ignored and overwritten, never quarantined (see
/// <see cref="RegistryService"/>). What it must never be is <i>wrong</i>: it is only written from a
/// payload that passed validation, and a later failed fetch returns it untouched rather than replacing
/// it with whatever the network happened to answer.
/// </para>
/// <para>
/// <see cref="ETag"/> carries an explicit <c>etag</c> name because the shared naming policy would
/// otherwise split the member into <c>e_tag</c>. The design notes and the Discover cache contract both
/// spell it <c>etag</c>, and a file that changes shape between versions is a file the next version has
/// to special-case.
/// </para>
/// </remarks>
public sealed class RegistryCacheFile
{
    /// <summary>Schema version written by this build. A file with any other value is not read.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Layout version of this file. Lets a later build change the shape without guessing what an older
    /// file meant; a version this build does not know is treated as no cache and re-fetched.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// The <c>ETag</c> the server sent with the payload, verbatim (quotes included), or
    /// <see langword="null"/> when it sent none.
    /// </summary>
    /// <remarks>
    /// An entity tag is opaque: it is replayed exactly as received on the next conditional request, so
    /// it is stored exactly as received. Null is omitted from the file by the shared serializer, which
    /// keeps the absence of a tag indistinguishable from a tag that was never issued - the state the
    /// next fetch has to handle either way.
    /// </remarks>
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    /// <summary>
    /// The URL this copy came from. An ETag is scoped to the URL that issued it, so a cache whose URL
    /// no longer matches the game's <c>registry_url</c> is not used for a conditional request.
    /// </summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>
    /// When this copy was last confirmed current: the fetch that wrote it, or the 304 that revalidated
    /// it. This is the timestamp Discover renders as "Last updated X ago" and the one staleness is
    /// measured from, in both cases.
    /// </summary>
    public DateTimeOffset LastFetched { get; init; }

    /// <summary>The registry payload that was accepted, exactly as it will be served offline.</summary>
    /// <remarks>
    /// Initialised to an empty registry rather than <c>default!</c> so that a cache file with a missing
    /// <c>registry</c> member deserializes into something a shape check can reject cleanly instead of a
    /// null that every reader would have to guard.
    /// </remarks>
    public Registry Registry { get; init; } = new();
}
