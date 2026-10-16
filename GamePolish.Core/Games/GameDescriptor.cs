namespace GamePolish.Core.Games;

/// <summary>
/// A supported game, as described by <c>games/&lt;id&gt;.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member is <c>required</c> and <c>init</c>: a descriptor is immutable once loaded, and
/// <see cref="GameDescriptorValidator"/> runs before the loader hands one out. Note what
/// <c>required</c> does and does not buy here - it is a compile-time rule about object initializers,
/// so a YAML-mapped instance can still arrive with a null where a value was expected.
/// <see cref="GameDescriptorValidator"/> is what turns that into a named problem; the loader never
/// returns an instance it has not validated.
/// </para>
/// <para>
/// Adding support for a game must never need a C# change (invariant #6): a new game is this file plus
/// a registry entry. A fact that cannot be expressed here is a finding to report, not a reason to
/// special-case an id in code.
/// </para>
/// </remarks>
public sealed record GameDescriptor
{
    /// <summary>Stable identifier, <c>^[a-z0-9][a-z0-9-]*$</c>, at most 64 characters, equal to the file stem.</summary>
    public required string Id { get; init; }

    /// <summary>Display name, e.g. <c>OctoWoW</c>.</summary>
    public required string Name { get; init; }

    /// <summary>How the install folder is found and checked.</summary>
    public required InstallDetection InstallDetection { get; init; }

    /// <summary>Where mods live inside the install folder, and in what shape.</summary>
    public required ModLayout ModLayout { get; init; }

    /// <summary>Absolute HTTPS URL of the game's registry JSON.</summary>
    public required string RegistryUrl { get; init; }

    /// <summary>
    /// Settings files the settings editor may edit. Reserved: parsed and validated here, given
    /// behaviour by task 601. Absent in every descriptor shipped today.
    /// </summary>
    public IReadOnlyList<SettingsFileDescriptor> SettingsFiles { get; init; } = [];

    /// <inheritdoc/>
    public override string ToString() => Id;
}

/// <summary>How Game Polish decides a folder really is this game.</summary>
public sealed record InstallDetection
{
    /// <summary>
    /// <c>true</c> when the user must browse to the install folder themselves. Nullable on purpose:
    /// <see langword="null"/> means the key was absent, which is a load error, while <c>false</c> is a
    /// real answer that a future auto-detecting game may give.
    /// </summary>
    public required bool? ManualOnly { get; init; }

    /// <summary>
    /// File that must exist in the game root for the folder to be accepted, e.g. <c>WoW.exe</c>.
    /// Required when <see cref="ManualOnly"/> is <c>true</c>: it is the only check the user sees.
    /// </summary>
    public string? VerifyFile { get; init; }
}

/// <summary>Where mods live inside the install folder.</summary>
public sealed record ModLayout
{
    /// <summary>Layout kind. <c>addons_folder</c> is the only kind today; see <see cref="GameDescriptorValidator.SupportedModLayoutTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// Relative destination under the install root, using <c>/</c> separators. Composed into an
    /// absolute path by <see cref="DescriptorPaths.AddonRoot"/> and nowhere else.
    /// </summary>
    public required string Destination { get; init; }
}

/// <summary>A game settings file the settings editor may read and write. Reserved: task 601.</summary>
public sealed record SettingsFileDescriptor
{
    /// <summary>Path relative to the install root, e.g. <c>WTF/Config.wtf</c>.</summary>
    public required string Path { get; init; }

    /// <summary>File format: one of <c>ini</c>, <c>toml</c>, <c>json</c>.</summary>
    public required string Format { get; init; }
}
