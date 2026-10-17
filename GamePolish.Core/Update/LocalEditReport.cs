using System.Text;

namespace GamePolish.Core.Update;

/// <summary>
/// What the user changed inside an installed mod's folder since it was installed, which the update is about
/// to replace.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a report, not a merge.</b> An update replaces the addon folder wholesale, so a hand-edited
/// <c>.lua</c> or an added settings file is gone the moment the swap lands - except that the backup taken
/// immediately before it holds the versions that were on disk. Naming them is the difference between "your
/// edit was replaced, here is where it went" and silent loss. A three-way merge is deliberately out of scope
/// (task 209's *Out of scope*): merging untrusted upstream changes into hand-written Lua is a feature with
/// its own failure modes, and shipping it as a side effect of update would be the wrong way to get it.
/// </para>
/// <para>
/// <b>Size is the signal, because size is what the manifest records.</b> Task 206 stores each file's
/// <c>SizeBytes</c> and nothing else (no hash), so "modified" means "the size on disk differs from the size
/// when it was installed". That misses an edit that keeps the byte count, and it never invents one; a content
/// hash is a change to 206's shape, which this task is not allowed to make.
/// </para>
/// <para>
/// Paths are <b>relative to the game's addons folder</b> - the manifest's coordinate system, e.g.
/// <c>pfUI/pfUI.lua</c> - so they can be compared with the file list and shown next to it without
/// translation.
/// </para>
/// </remarks>
public sealed record LocalEditReport
{
    /// <summary>Files the manifest records whose size on disk is no longer the size it recorded.</summary>
    public required IReadOnlyList<string> ModifiedPaths { get; init; }

    /// <summary>Files inside the folder that the manifest does not list: the user's own additions.</summary>
    public required IReadOnlyList<string> AddedPaths { get; init; }

    /// <summary>A report with nothing in it: nothing on disk was changed by hand.</summary>
    public static LocalEditReport None { get; } = new() { ModifiedPaths = [], AddedPaths = [] };

    /// <summary>True when the user changed nothing since the install.</summary>
    public bool IsEmpty => ModifiedPaths.Count == 0 && AddedPaths.Count == 0;

    /// <summary>How many paths the update will replace or remove.</summary>
    public int Count => ModifiedPaths.Count + AddedPaths.Count;

    /// <summary>
    /// The report as the technical half of a result: what changed, grouped by how, and where the previous
    /// content is now.
    /// </summary>
    /// <remarks>
    /// The closing sentence names the backup deliberately. A user who reads this in the log drawer needs the
    /// next step ("restore it from the Backups view") in the same breath as the bad news, because the detail
    /// may be the only thing they see after dismissing a toast.
    /// </remarks>
    public string Describe()
    {
        StringBuilder builder = new();

        builder.Append("Local edits replaced by this update: ").Append(Count).Append(' ').Append(Count == 1 ? "path" : "paths").Append('.').Append('\n');

        Append(builder, "Modified since the install", ModifiedPaths);
        Append(builder, "Added since the install", AddedPaths);
        builder.Append("The backup taken before this update holds the versions that were on disk.").Append('\n');

        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => $"{ModifiedPaths.Count} modified, {AddedPaths.Count} added";

    private static void Append(StringBuilder builder, string title, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        builder.Append(title).Append(':').Append('\n');

        foreach (string path in paths)
        {
            builder.Append("  - ").Append(path).Append('\n');
        }
    }
}
