using GamePolish.Core.Games;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Games;

/// <summary>
/// Covers the single composition point for the addon root (.todo/202, acceptance criterion 7) and the
/// property that makes invariant #6 true: the destination comes from the descriptor, so a second game
/// with a different destination needs no C# change.
/// </summary>
[Collection(StorageCollection.Name)]
public class DescriptorPathsTests
{
    private readonly StorageFixture _fixture;

    public DescriptorPathsTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AddonRootIsTheInstallRootPlusTheDescriptorDestination()
    {
        GameDescriptor descriptor = LoadShipped();
        string installRoot = _fixture.NewSubdirectory("octowow-install");

        string expected = Path.GetFullPath(Path.Combine(installRoot, "Interface/AddOns"));

        Assert.Equal(expected, descriptor.AddonRoot(installRoot));
        Assert.StartsWith(Path.GetFullPath(installRoot), descriptor.AddonRoot(installRoot), StringComparison.Ordinal);
    }

    [Fact]
    public void AddonRootFollowsTheDescriptorRatherThanTheGame()
    {
        GameDescriptor descriptor = LoadShipped() with
        {
            ModLayout = new ModLayout { Type = GameDescriptorValidator.AddonsFolderLayout, Destination = "mods" },
        };
        string installRoot = _fixture.NewSubdirectory("second-game-install");

        Assert.Equal(Path.GetFullPath(Path.Combine(installRoot, "mods")), descriptor.AddonRoot(installRoot));
    }

    [Fact]
    public void AnEmptyInstallRootIsRejected()
    {
        GameDescriptor descriptor = LoadShipped();

        Assert.Throws<ArgumentException>(() => descriptor.AddonRoot("   "));
    }

    private static GameDescriptor LoadShipped()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, GameDescriptorLoader.DescriptorDirectoryName);

        return new GameDescriptorLoader([directory]).LoadById("octowow")!;
    }
}
