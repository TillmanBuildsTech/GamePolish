namespace GamePolish.Core.Games;

/// <summary>
/// The one place the addon directory is composed: <c>&lt;install root&gt;/&lt;mod_layout.destination&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else in Core may build this path - a hardcoded destination is how a second supported game
/// would end up needing a C# change, which invariant #6 forbids. Tests in this repository grep for the
/// OctoWoW destination string and fail if it appears anywhere in <c>GamePolish.Core</c>.
/// </para>
/// <para>
/// This method does no sandboxing of its own: <see cref="GameDescriptorValidator"/> already rejected a
/// destination that is rooted, absolute, has a <c>..</c> segment or a trailing separator. Callers hand
/// the result to the mod-path validator (task 204) as the destination root, and it is that validator
/// which judges mod-supplied names.
/// </para>
/// </remarks>
public static class DescriptorPaths
{
    /// <summary>Returns the absolute directory mods are installed into for this game.</summary>
    /// <param name="descriptor">A loaded, validated descriptor.</param>
    /// <param name="installRoot">The game's install directory, e.g. <c>D:\OctoWow</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="installRoot"/> is null, empty or whitespace.</exception>
    public static string AddonRoot(this GameDescriptor descriptor, string installRoot)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return Path.GetFullPath(Path.Combine(installRoot, descriptor.ModLayout.Destination));
    }
}
