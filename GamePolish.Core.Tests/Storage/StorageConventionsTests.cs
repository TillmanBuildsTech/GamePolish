using System.Reflection;
using System.Text.RegularExpressions;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Structural guards from .todo/201 acceptance criteria 7 and 8: <see cref="AppSettings"/> never
/// grows a secret-shaped property, and <see cref="AtomicJson"/> stays the only file writer in
/// <c>GamePolish.Core</c> so no future task can write state unprotected by the atomic replace.
/// </summary>
public class StorageConventionsTests
{
    /// <summary>Names that would put a secret in a plain-text settings file.</summary>
    private static readonly Regex SecretShapedName =
        new("(token|secret|password|key|credential)", RegexOptions.IgnoreCase);

    /// <summary>Calls that create or overwrite a file without going through <see cref="AtomicJson"/>.</summary>
    private static readonly string[] NonAtomicWriteCalls =
        ["File.WriteAllText", "File.CreateText", "StreamWriter("];

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string CoreDirectory = Path.Combine(RepositoryRoot, "GamePolish.Core");
    private static readonly string AtomicJsonFile = Path.Combine("Storage", "AtomicJson.cs");

    [Fact]
    public void NoAppSettingsPropertyIsSecretShaped()
    {
        string[] offenders = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => SecretShapedName.IsMatch(name))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AtomicJsonIsTheOnlyFileWriterInCore()
    {
        string[] sources = Directory.GetFiles(CoreDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(IsCompiledSource)
            .ToArray();

        Assert.NotEmpty(sources);

        // The grep below is only meaningful if the file it excludes is actually reachable: prove the
        // pattern matches something before asserting that nothing else does.
        string writer = Path.Combine(CoreDirectory, AtomicJsonFile);
        Assert.Contains(writer, sources);
        Assert.Contains(
            NonAtomicWriteCalls[0],
            File.ReadAllText(writer),
            StringComparison.Ordinal);

        List<string> offenders = [];
        foreach (string source in sources)
        {
            if (Path.GetFullPath(source) == Path.GetFullPath(writer))
            {
                continue;
            }

            string text = File.ReadAllText(source);
            foreach (string call in NonAtomicWriteCalls)
            {
                if (text.Contains(call, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepositoryRoot, source)}: {call}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsCompiledSource(string path)
    {
        string relative = Path.GetRelativePath(CoreDirectory, path);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !segments.Contains("obj") && !segments.Contains("bin");
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, so the grep tests
    /// read the real sources on disk rather than a copy.
    /// </summary>
    private static string FindRepositoryRoot()
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
