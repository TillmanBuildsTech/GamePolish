using System.Text;

namespace GamePolish.Core.Install;

/// <summary>
/// What an install would replace, sorted into the three answers that mean different things to the user.
/// </summary>
/// <remarks>
/// <para>
/// The split is the whole point. A file this mod already owns is normal - that is what a reinstall or an
/// update looks like. A file another mod owns is a bug in the user's mod list that they have to decide
/// about. A file nobody owns is the user's own work, which is the one case where replacing it is
/// unrecoverable if they did not mean it. Collapsing the three into one "these files exist" list would make
/// the dialog unable to tell "safe" from "somebody's hand-edited <c>.lua</c>".
/// </para>
/// <para>
/// Paths are <b>relative to the game's addons folder</b> - <c>pfUI/pfUI.toc</c>, not an absolute path and
/// not a path relative to the addon folder itself. That is the coordinate system the manifest stores and
/// the one the dialog can show, and it is the same shape as <c>StagedSource.Files</c>.
/// </para>
/// <para>
/// A folder entry - <c>pfUI/</c> with the trailing slash - appears when the <i>folder</i> is what conflicts,
/// not any single file. It is a real case: another mod can own a folder whose files do not overlap the new
/// tree at all, and moving that folder aside would still take its files with it.
/// </para>
/// </remarks>
public sealed record ConflictReport
{
    /// <summary>Files and folders this mod already owns. Reinstalling over them is normal, never a blocker.</summary>
    public required IReadOnlyList<string> OwnedByThisMod { get; init; }

    /// <summary>Files and folders another installed mod owns. A blocker unless the caller accepted overwrites.</summary>
    public required IReadOnlyList<string> OwnedByOtherMods { get; init; }

    /// <summary>
    /// Files and folders that exist on disk and no mod claims: the user's own files. A blocker for the same
    /// reason as <see cref="OwnedByOtherMods"/>, and the reason the report is shown before the backup.
    /// </summary>
    public required IReadOnlyList<string> UnownedExisting { get; init; }

    /// <summary>A report with nothing in it: the install would create everything it needs.</summary>
    public static ConflictReport None { get; } = new()
    {
        OwnedByThisMod = [],
        OwnedByOtherMods = [],
        UnownedExisting = [],
    };

    /// <summary>True when installing anyway would replace something this mod does not own.</summary>
    public bool IsBlocked => OwnedByOtherMods.Count > 0 || UnownedExisting.Count > 0;

    /// <summary>How many paths installing anyway would take from something else.</summary>
    public int BlockingCount => OwnedByOtherMods.Count + UnownedExisting.Count;

    /// <summary>
    /// The report as the technical half of an operation result: one section per bucket, every path listed.
    /// </summary>
    /// <remarks>
    /// Empty buckets are stated as empty rather than omitted. "No other mod owns anything here" is a
    /// finding a user reporting a bug needs, and an omitted section reads as "the check did not run".
    /// </remarks>
    public string Describe()
    {
        StringBuilder builder = new();

        Append(builder, "Owned by this mod (replaced normally)", OwnedByThisMod);
        Append(builder, "Owned by other mods (blocker)", OwnedByOtherMods);
        Append(builder, "Present but unowned (blocker)", UnownedExisting);

        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{OwnedByThisMod.Count} owned, {OwnedByOtherMods.Count} other mod's, {UnownedExisting.Count} unowned";

    private static void Append(StringBuilder builder, string title, IReadOnlyList<string> paths)
    {
        builder.Append(title).Append(": ").Append(paths.Count).Append('\n');

        foreach (string path in paths)
        {
            builder.Append("  - ").Append(path).Append('\n');
        }
    }
}
