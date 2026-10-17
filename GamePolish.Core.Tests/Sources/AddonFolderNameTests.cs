using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// Covers the addon folder name (.todo/207, acceptance criterion 6): the three-way precedence, the
/// multi-<c>.toc</c> case that is deliberately not guessed at, and the names that must be refused -
/// including the <c>.game-polish-</c> prefix, which would otherwise be deleted by the next startup sweep.
/// </summary>
[Collection(StorageCollection.Name)]
public class AddonFolderNameTests
{
    private readonly PathValidator _validator = new();
    private readonly string _root;

    public AddonFolderNameTests(StorageFixture fixture)
    {
        _root = fixture.NewSubdirectory($"addon-name-{Guid.NewGuid():N}");
    }

    [Theory]
    [InlineData("https://github.com/brues-code/pfUI.git", "pfUI")]
    [InlineData("https://github.com/brues-code/pfUI", "pfUI")]
    [InlineData("https://github.com/Otari98/AtlasLoot/", "AtlasLoot")]
    [InlineData("https://octowow.st/git/brandon/pfQuest-octo", "pfQuest-octo")]
    [InlineData("pfUI-v5.1.zip", "pfUI-v5.1")]
    [InlineData("pfUI-main.zip", "pfUI")]
    [InlineData("pfQuest-octo-master.zip", "pfQuest-octo")]
    [InlineData("ShaguTweaks.7z", "ShaguTweaks")]
    public void TheSourceNameImpliesTheFolderName(string source, string expected) =>
        Assert.Equal(expected, AddonFolderName.FromSourceName(source));

    [Fact]
    public void ASingleTocAtTheStagedRootNamesTheFolder()
    {
        File.WriteAllText(Path.Combine(_root, "pfUI.toc"), "## Interface: 11200");

        Assert.Equal("pfUI", AddonFolderName.FromStagedToc(_root));
    }

    [Fact]
    public void ATocWithAnUppercaseExtensionIsStillAToc()
    {
        // Read by enumerating and comparing, not with the "* .toc" search pattern, so macOS and Windows
        // behave the same way when a repository ships PFUI.TOC.
        File.WriteAllText(Path.Combine(_root, "PFUI.TOC"), "## Interface: 11200");

        Assert.Equal("PFUI", AddonFolderName.FromStagedToc(_root));
    }

    [Fact]
    public void AMultiAddonSourceNamesNothing()
    {
        File.WriteAllText(Path.Combine(_root, "pfUI.toc"), "## pfUI");
        File.WriteAllText(Path.Combine(_root, "pfQuest.toc"), "## pfQuest");

        Assert.Null(AddonFolderName.FromStagedToc(_root));
    }

    [Fact]
    public void ATocInASubdirectoryNamesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "pfUI.toc"), "## pfUI");

        Assert.Null(AddonFolderName.FromStagedToc(_root));
    }

    [Fact]
    public void AnExpectedNameWinsOverTheToc()
    {
        File.WriteAllText(Path.Combine(_root, "pfUI.toc"), "## pfUI");

        Assert.Equal(
            "pfUI-classic",
            AddonFolderName.Resolve(_validator, _root, _root, "pfUI-classic", "https://github.com/brues-code/pfUI"));
    }

    [Fact]
    public void TheTocWinsOverTheSourceName()
    {
        File.WriteAllText(Path.Combine(_root, "pfUI.toc"), "## pfUI");

        Assert.Equal("pfUI", AddonFolderName.Resolve(_validator, _root, _root, null, "https://github.com/x/pfui-classic"));
    }

    [Fact]
    public void WithoutATocTheSourceNameIsUsed()
    {
        Assert.Equal("pfUI", AddonFolderName.Resolve(_validator, _root, _root, null, "https://github.com/brues-code/pfUI.git"));
        Assert.Equal("pfUI-v5.1", AddonFolderName.Resolve(_validator, _root, _root, " ", "pfUI-v5.1.zip"));
    }

    [Fact]
    public void AReservedNameIsRefused()
    {
        PathValidationException failure = Assert.Throws<PathValidationException>(
            () => AddonFolderName.Resolve(_validator, _root, _root, ".game-polish-old-pfUI", "pfUI"));

        Assert.Equal(UnsafePathReason.ReservedPrefix, Assert.Single(failure.Failures).Reason);
    }

    [Theory]
    [InlineData("a/b", UnsafePathReason.InvalidCharacters)]
    [InlineData("a\\b", UnsafePathReason.InvalidCharacters)]
    [InlineData("..", UnsafePathReason.TraversalEscape)]
    [InlineData("CON", UnsafePathReason.DeviceName)]
    [InlineData("pfUI.", UnsafePathReason.TrailingDotOrSpace)]
    public void AnUnusableExpectedNameIsRefused(string name, UnsafePathReason expected)
    {
        PathValidationException failure = Assert.Throws<PathValidationException>(
            () => AddonFolderName.Resolve(_validator, _root, _root, name, "https://github.com/brues-code/pfUI"));

        Assert.Equal(expected, Assert.Single(failure.Failures).Reason);
    }

    [Fact]
    public void ASourceNameWithNothingUsableInItIsRefused()
    {
        PathValidationException failure = Assert.Throws<PathValidationException>(
            () => AddonFolderName.Resolve(_validator, _root, _root, null, "https://github.com/x/.git"));

        Assert.Equal(UnsafePathReason.Empty, Assert.Single(failure.Failures).Reason);
    }

    [Theory]
    [InlineData(".game-polish-old-pfUI", true)]
    [InlineData(".game-polish-new-pfUI-20261016T000000000Z", true)]
    [InlineData("pfUI", false)]
    public void TheReservedPrefixIsRecognised(string name, bool expected) =>
        Assert.Equal(expected, AddonFolderName.IsReserved(name));
}
