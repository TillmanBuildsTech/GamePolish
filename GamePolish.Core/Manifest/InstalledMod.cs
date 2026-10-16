namespace GamePolish.Core.Manifest;

/// <summary>A mod Game Polish installed into a game, and everything needed to update or remove it.</summary>
public sealed record InstalledMod
{
    /// <summary>Registry id, or a slug for an ad-hoc source. Matched exactly, like every other id here.</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the Library.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>https://github.com/owner/repo</c>, or the original ZIP file name for a user-supplied archive.
    /// Never carries a credential: see <c>ManifestStore</c>'s guard.
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>Which kind of source <see cref="SourceUrl"/> names.</summary>
    public required ModSourceType SourceType { get; init; }

    /// <summary>The single folder under the game's mod destination that this mod owns.</summary>
    public required string FolderName { get; init; }

    /// <summary>Currently installed version: a HEAD commit SHA or a tag name.</summary>
    public required string Version { get; init; }

    /// <summary>How to read <see cref="Version"/>: <c>sha</c> or <c>tag</c>.</summary>
    public required string VersionKind { get; init; }

    /// <summary>Whether the mod's files are currently in the game directory.</summary>
    public required bool Enabled { get; init; }

    /// <summary>When the mod was first installed.</summary>
    public required DateTimeOffset InstalledAt { get; init; }

    /// <summary>
    /// When the record last changed. Set by the caller, never by the store: task 209 needs to distinguish
    /// "the operation succeeded at T" from "the store happened to write at T".
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Every file this mod owns, relative to the addon root.</summary>
    public required IReadOnlyList<InstalledFileEntry> Files { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Id} ({Name}) {VersionKind} {Version}";
}
