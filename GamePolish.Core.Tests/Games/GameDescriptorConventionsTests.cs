using System.Xml.Linq;

using GamePolish.Core.Games;

namespace GamePolish.Core.Tests.Games;

/// <summary>
/// Structural guards from .todo/202 (.todo/202 acceptance criteria 2 and 8): the OctoWoW destination
/// string exists only in its YAML, and <c>GamePolish.Core</c> still carries exactly one package
/// reference.
/// </summary>
public class GameDescriptorConventionsTests
{
    private static readonly string[] ForbiddenLiterals = ["Interface/AddOns", "AddOns"];

    [Fact]
    public void CoreSourceContainsNoHardcodedGameDestination()
    {
        IReadOnlyList<string> sources = RepositoryLayout.CoreSourceFiles();

        Assert.NotEmpty(sources);

        List<string> offenders = [];
        foreach (string source in sources)
        {
            string text = File.ReadAllText(source);

            foreach (string literal in ForbiddenLiterals)
            {
                if (text.Contains(literal, StringComparison.Ordinal))
                {
                    offenders.Add($"{RepositoryLayout.Relative(source)}: {literal}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void YamlDotNetIsTheOnlyPackageCoreReferences()
    {
        XDocument project = XDocument.Load(RepositoryLayout.CoreProjectFile);

        string[] packages = project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty)
            .ToArray();

        string[] versions = project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Version") ?? string.Empty)
            .ToArray();

        Assert.Equal(["YamlDotNet"], packages);
        Assert.Equal(["18.1.0"], versions);
    }

    [Fact]
    public void TheDescriptorTextDefaultsMatchTheShippedFixture()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            GameDescriptorLoader.DescriptorDirectoryName,
            "octowow.yaml");

        Assert.Equal(Normalise(File.ReadAllText(fixture)), Normalise(new DescriptorText().ToYaml()));
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
