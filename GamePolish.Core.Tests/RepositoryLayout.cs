namespace GamePolish.Core.Tests;

/// <summary>
/// Locates the repository on disk so the structural tests (grep and package gates) read the real
/// sources rather than a copy, and so a failure names a repo-relative path.
/// </summary>
internal static class RepositoryLayout
{
    /// <summary>Repository root, found by walking up from the test binary to the solution file.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Absolute path of <c>GamePolish.Core</c>.</summary>
    public static string CoreDirectory { get; } = Path.Combine(Root, "GamePolish.Core");

    /// <summary>Absolute path of <c>GamePolish.Core/GamePolish.Core.csproj</c>.</summary>
    public static string CoreProjectFile { get; } = Path.Combine(CoreDirectory, "GamePolish.Core.csproj");

    /// <summary>Every compiled <c>.cs</c> file under <c>GamePolish.Core</c>, excluding obj and bin.</summary>
    public static IReadOnlyList<string> CoreSourceFiles() =>
        Directory.GetFiles(CoreDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasBuildDirectorySegment(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Path relative to the repository root, for readable failure messages.</summary>
    public static string Relative(string path) => Path.GetRelativePath(Root, path);

    private static bool HasBuildDirectorySegment(string path)
    {
        string[] segments = Path.GetRelativePath(CoreDirectory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Contains("obj") || segments.Contains("bin");
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GamePolish.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"No GamePolish.sln found above '{AppContext.BaseDirectory}'.");
    }
}
