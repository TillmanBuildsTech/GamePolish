using System.Reflection;
using System.Text.RegularExpressions;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Structural guards from .todo/201 acceptance criteria 7 and 8: <see cref="AppSettings"/> never gains a
/// secret-shaped property, and the two places in <c>GamePolish.Core</c> that are allowed to write files -
/// <see cref="AtomicJson"/> for state under the data root, and the <c>IO/FileOps</c> seam (task 205) for
/// game content - stay the only two.
/// </summary>
public class StorageConventionsTests
{
    /// <summary>Names that would put a secret in a plain-text settings file.</summary>
    private static readonly Regex SecretShapedName =
        new("(token|secret|password|key|credential)", RegexOptions.IgnoreCase);

    /// <summary>Calls that create or overwrite a file without going through <see cref="AtomicJson"/>.</summary>
    private static readonly string[] NonAtomicWriteCalls =
        ["File.WriteAllText", "File.CreateText", "StreamWriter("];

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
        IReadOnlyList<string> sources = RepositoryLayout.CoreSourceFiles();

        Assert.NotEmpty(sources);

        // The grep below is only meaningful if the file it excludes is actually reachable: prove the
        // pattern matches something before asserting that nothing else does.
        string writer = Path.Combine(RepositoryLayout.CoreDirectory, AtomicJsonFile);
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
                    offenders.Add($"{RepositoryLayout.Relative(source)}: {call}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void FileContentWritesGoThroughTheFileOpsSeam()
    {
        // Task 205 added the second writer in Core: IO/FileOps.cs is the mutating seam the install pipeline
        // uses for game content, and it is the only place allowed to call File.WriteAllBytes. Both writers
        // are named here, so a third one fails the build.
        IReadOnlyList<string> sources = RepositoryLayout.CoreSourceFiles();
        string seam = Path.Combine(RepositoryLayout.CoreDirectory, "IO", "FileOps.cs");

        Assert.Contains(seam, sources);

        List<string> offenders = [];
        foreach (string source in sources)
        {
            if (Path.GetFullPath(source) == Path.GetFullPath(seam))
            {
                continue;
            }

            if (File.ReadAllText(source).Contains("File.WriteAllBytes", StringComparison.Ordinal))
            {
                offenders.Add($"{RepositoryLayout.Relative(source)}: File.WriteAllBytes");
            }
        }

        Assert.Empty(offenders);
    }
}
