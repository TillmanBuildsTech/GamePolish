namespace GamePolish.Core.Paths;

/// <summary>
/// The single decision point for filesystem path comparison in this product.
/// </summary>
/// <remarks>
/// Windows and macOS compare paths case-insensitively, Linux does not. Every component that needs the
/// answer asks here instead of passing a <see cref="StringComparison"/> of its own, because two
/// components disagreeing about whether two paths are the same folder is how a mod gets installed
/// twice, or how two archive entries that differ only in case silently become one file.
/// </remarks>
public static class PathComparer
{
    /// <summary>The comparison to use when comparing two filesystem paths.</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True when both paths name the same location. A trailing separator is not a difference.</summary>
    /// <param name="first">A filesystem path.</param>
    /// <param name="second">A filesystem path.</param>
    public static bool AreEqual(string? first, string? second) =>
        string.Equals(TrimTrailingSeparators(first), TrimTrailingSeparators(second), PathComparison);

    /// <summary>True when <paramref name="candidate"/> is a strict descendant of <paramref name="root"/>.</summary>
    /// <param name="candidate">The path to test.</param>
    /// <param name="root">The container path.</param>
    public static bool IsInside(string candidate, string root)
    {
        string relative = Path.GetRelativePath(root, candidate);

        if (relative is "." || relative == ".." || Path.IsPathRooted(relative))
        {
            return false;
        }

        return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or below it.</summary>
    /// <param name="candidate">The path to test.</param>
    /// <param name="root">The container path.</param>
    public static bool IsSameOrInside(string candidate, string root) =>
        AreEqual(candidate, root) || IsInside(candidate, root);

    private static string? TrimTrailingSeparators(string? path) =>
        path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
