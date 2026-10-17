using GamePolish.Core.Mods;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Mods;

/// <summary>
/// Covers the staging layout (.todo/210): where a disabled addon is parked, that the folder name from the
/// manifest is still validated on the way in, and - the load-bearing one - that the orphan sweep in `207` cannot
/// delete a mod the user disabled months ago.
/// </summary>
[Collection(StorageCollection.Name)]
public class StagingAreaTests
{
    private readonly StorageFixture _fixture;

    public StagingAreaTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("../pfUI")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("CON")]
    public void AFolderNameThatIsNotASafeSingleNameIsRefused(string folderName)
    {
        ModScenario scenario = new(_fixture);

        scenario.Paths.EnsureGameDirectory(ModScenario.GameId);

        Assert.Throws<PathValidationException>(
            () => scenario.Staging.DisabledPath(ModScenario.GameId, folderName));
    }

    [Fact]
    public void TheParkedPathIsUnderTheGamesStagingArea()
    {
        ModScenario scenario = new(_fixture);

        // No parked copy exists yet, but the game directory does (anything the app wrote created it), and that is
        // the root the validator is asked about in that case. The path must still be answerable, because the
        // first disable has to be told where it is going to put the files.
        scenario.Paths.EnsureGameDirectory(ModScenario.GameId);

        Assert.Equal(
            Path.Combine(scenario.Paths.StagingDirectory(ModScenario.GameId), StagingArea.DisabledDirectoryName, "pfUI"),
            scenario.Staging.DisabledPath(ModScenario.GameId, "pfUI"));
    }

    [Fact]
    public void ACopyIsReportedOnlyWhenItIsActuallyParked()
    {
        ModScenario scenario = new(_fixture);
        scenario.Paths.EnsureGameDirectory(ModScenario.GameId);

        Assert.False(scenario.Staging.HasDisabledCopy(ModScenario.GameId, "pfUI"));

        Directory.CreateDirectory(scenario.ParkedFolder("pfUI"));

        Assert.True(scenario.Staging.HasDisabledCopy(ModScenario.GameId, "pfUI"));
        Assert.False(scenario.Staging.HasDisabledCopy(ModScenario.GameId, "pfQuest"));
    }

    [Fact]
    public void ParkedCopiesAreListedInOneStableOrder()
    {
        ModScenario scenario = new(_fixture);

        Assert.Empty(scenario.Staging.ListDisabled(ModScenario.GameId));

        Directory.CreateDirectory(scenario.ParkedFolder("pfQuest"));
        Directory.CreateDirectory(scenario.ParkedFolder("AtlasLoot"));
        Directory.CreateDirectory(scenario.ParkedFolder("pfUI"));

        Assert.Equal(["AtlasLoot", "pfQuest", "pfUI"], scenario.Staging.ListDisabled(ModScenario.GameId));
    }

    [Fact]
    public void AnEmptyParkedRootIsRemovedAndANonEmptyOneIsLeftAlone()
    {
        ModScenario scenario = new(_fixture);

        // Missing entirely is "gone", not a failure.
        Assert.True(scenario.Staging.TryRemoveEmptyRoot(ModScenario.GameId));

        Directory.CreateDirectory(scenario.DisabledRoot);
        Assert.True(scenario.Staging.TryRemoveEmptyRoot(ModScenario.GameId));
        Assert.False(Directory.Exists(scenario.DisabledRoot));

        Directory.CreateDirectory(scenario.ParkedFolder("pfUI"));
        Assert.False(scenario.Staging.TryRemoveEmptyRoot(ModScenario.GameId));
        Assert.True(Directory.Exists(scenario.ParkedFolder("pfUI")));
    }

    /// <summary>
    /// A parked addon is a direct child of the staging directory, so `207`'s orphan sweep would otherwise delete
    /// a mod the user disabled months ago: age is not evidence that a directory is nobody's. The sweep must
    /// remove an abandoned operation directory of the same age and leave the parked copy alone.
    /// </summary>
    [Fact]
    public void TheOrphanSweepLeavesParkedAddonsAlone()
    {
        ModScenario scenario = new(_fixture);
        SourceAcquirer acquirer = new(scenario.Paths);

        string operation = Path.Combine(scenario.Paths.StagingDirectory(ModScenario.GameId), "abandoned-operation");

        Directory.CreateDirectory(Path.Combine(operation, "pfUI"));
        File.WriteAllText(Path.Combine(operation, "pfUI", "pfUI.toc"), "-- staged");
        Directory.SetLastWriteTimeUtc(operation, DateTime.UtcNow.AddHours(-30));

        Directory.CreateDirectory(scenario.ParkedFolder("pfUI"));
        File.WriteAllText(Path.Combine(scenario.ParkedFolder("pfUI"), "pfUI.toc"), "-- parked");
        Directory.SetLastWriteTimeUtc(scenario.ParkedFolder("pfUI"), DateTime.UtcNow.AddHours(-30));
        Directory.SetLastWriteTimeUtc(scenario.DisabledRoot, DateTime.UtcNow.AddHours(-30));

        int removed = acquirer.SweepStaleStaging(ModScenario.GameId, TimeSpan.FromHours(24), excludeOperationId: null);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(operation));
        Assert.True(Directory.Exists(scenario.ParkedFolder("pfUI")), "the sweep deleted a disabled mod's only copy.");
        Assert.True(File.Exists(Path.Combine(scenario.ParkedFolder("pfUI"), "pfUI.toc")));
    }
}
