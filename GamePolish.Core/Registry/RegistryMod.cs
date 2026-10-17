namespace GamePolish.Core.Registry;

/// <summary>
/// One installable mod, as a game's registry lists it.
/// </summary>
/// <remarks>
/// <para>
/// The members are deliberately non-nullable with empty defaults even though the instance comes from a
/// file nobody in this repository writes. That is not an oversight: a payload that is missing
/// <c>name</c> deserializes to an empty string rather than throwing, and
/// <see cref="RegistryService"/> turns that into a named problem the registry author can act on
/// ("mods[3] ('pfui'): source is missing"). A <c>required</c> member would instead surface as an
/// opaque <c>JsonException</c> that names a byte offset, and the whole file would be rejected with one
/// message that does not say which mod is broken.
/// </para>
/// <para>
/// <see cref="Source"/> is a string and stays one (invariant #2). Nothing here is fetched, cloned or
/// executed: the value is handed to task 207, which validates it again before it becomes git's
/// argument. Storing it is the whole job of this type.
/// </para>
/// </remarks>
public sealed record RegistryMod
{
    /// <summary>Stable id, <c>^[a-z0-9][a-z0-9-]*$</c>, unique across the registry.</summary>
    /// <remarks>
    /// Lowercase and hyphenated because the id is what the library and the manifest keys use, and two
    /// spellings of one mod (<c>pfUI</c> / <c>pfui</c>) would be two installs of the same files.
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name, e.g. <c>pfUI</c>. Not constrained to the id: this is what the card shows.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Author as the registry spells it, e.g. <c>brues-code</c>. Searchable, so it is kept verbatim.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>One-paragraph description shown on the Discover card and the mod detail view.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Repository URL the mod is installed from. An <c>https://</c> URL, and only ever a string here.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Tag pills this mod belongs to, e.g. <c>ui</c>, <c>essential</c>. Empty is a valid answer.
    /// </summary>
    /// <remarks>
    /// Non-nullable with an empty default for the reason given on the type: an explicit
    /// <c>"tags": null</c> is refused during validation, and a registry that passes validation never
    /// hands out a null list, so the Discover view can iterate this without a guard.
    /// </remarks>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Thumbnail image URL, or <see langword="null"/>. Never downloaded here (out of scope for 211);
    /// the UI decides whether to show a placeholder.
    /// </summary>
    public string? Thumbnail { get; init; }

    /// <summary>
    /// When the mod was added to the registry, or <see langword="null"/> when unknown. The Discover
    /// view's "Newest" sort reads it, and an absent value means "sort last", not "today".
    /// </summary>
    public DateTimeOffset? AddedAt { get; init; }
}
