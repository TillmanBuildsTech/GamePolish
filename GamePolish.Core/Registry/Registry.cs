namespace GamePolish.Core.Registry;

/// <summary>
/// The payload of a game's <c>registry_url</c>: every mod the game's registry offers.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape <c>CLAUDE.md</c> calls the registry JSON, and it is the one object 211 stores in
/// the cache and hands to the Discover view. It is parsed with
/// <see cref="Storage.AtomicJson.Options"/> (plus case-insensitive member matching, because the file is
/// authored outside this repository) so <c>game_id</c> and <c>updated_at</c> are the wire names.
/// Unknown members are ignored: a registry may grow a field before this app knows about it, and refusing
/// the file for that would make the app a gate on its own data.
/// </para>
/// <para>
/// <see cref="Mods"/> is non-nullable with an empty default for the same reason as
/// <see cref="RegistryMod"/>'s members, and an explicit <c>"mods": null</c> is refused by validation.
/// Note the one thing this cannot detect: an absent <c>mods</c> key is indistinguishable from
/// <c>"mods": []</c> after deserialization, and an empty registry is legitimate, so it is accepted.
/// </para>
/// </remarks>
public sealed record Registry
{
    /// <summary>
    /// The game this registry belongs to, e.g. <c>octowow</c>. Validated against the id the caller
    /// asked for: a mismatch is the single most likely authoring bug, and serving Skyrim's mods under
    /// OctoWoW's name would install the wrong files into a game folder.
    /// </summary>
    public string GameId { get; init; } = string.Empty;

    /// <summary>
    /// When the registry's contents were last edited upstream. Shown as provenance; the "Last updated
    /// X ago" line in Discover is <see cref="RegistryResult.LastFetched"/>, not this.
    /// </summary>
    /// <remarks>
    /// A missing value deserializes to <see cref="DateTimeOffset.MinValue"/> and is refused by
    /// validation, which is why "year 1" is not a state any caller has to handle.
    /// </remarks>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Every mod the registry lists, in the order the registry defines (Discover's "Popular").</summary>
    public IReadOnlyList<RegistryMod> Mods { get; init; } = [];
}
