using GamePolish.Core.Backup;
using GamePolish.Core.Manifest;
using GamePolish.Core.Mods;
using GamePolish.Core.Operations;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Mods;

/// <summary>
/// Covers the facade the Library view model talks to (.todo/210): it reports the manifest's rows and routes a
/// checkbox click to the right pipeline, and it decides nothing on its own - every invariant belongs to the
/// pipelines, which is why this file is short.
/// </summary>
[Collection(StorageCollection.Name)]
public class ModStateServiceTests
{
    private readonly StorageFixture _fixture;

    public ModStateServiceTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void TheRowsAreTheManifestsEntriesIncludingDisabledOnes()
    {
        ModScenario scenario = new(_fixture);
        ModStateService service = Service(scenario);

        scenario.Seed("pfUI", "pfui");
        scenario.Seed("pfQuest", "pfquest", enabled: false);

        IReadOnlyList<InstalledMod> rows = service.GetRows(ModScenario.GameId);

        Assert.Equal(["pfquest", "pfui"], rows.Select(row => row.Id).Order(StringComparer.Ordinal));
        Assert.False(rows.Single(row => row.Id == "pfquest").Enabled);
    }

    [Fact]
    public void AGameWithNothingInstalledHasNoRows()
    {
        ModScenario scenario = new(_fixture);

        Assert.Empty(Service(scenario).GetRows(ModScenario.GameId));
    }

    [Fact]
    public async Task SettingEnabledFalseDisablesAndTrueEnablesAgain()
    {
        ModScenario scenario = new(_fixture);
        ModStateService service = Service(scenario);

        scenario.Seed("pfUI", "pfui");

        OperationResult disabled = await service.SetEnabledAsync(
            ModScenario.GameId, "pfui", enabled: false, progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, disabled.Status);
        Assert.False(scenario.Manifest.Find(ModScenario.GameId, "pfui")!.Enabled);
        Assert.True(scenario.Staging.HasDisabledCopy(ModScenario.GameId, "pfUI"));

        OperationResult enabled = await service.SetEnabledAsync(
            ModScenario.GameId, "pfui", enabled: true, progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, enabled.Status);
        Assert.True(scenario.Manifest.Find(ModScenario.GameId, "pfui")!.Enabled);
        Assert.False(scenario.Staging.HasDisabledCopy(ModScenario.GameId, "pfUI"));
        Assert.Equal(2, scenario.BackupCount(BackupTrigger.EnableDisable));
    }

    /// <summary>
    /// The checkbox can fire twice - a double click, a stale row - and a second click in the same direction must
    /// not take a second snapshot or move anything.
    /// </summary>
    [Fact]
    public async Task ClickingTheSameStateTwiceIsANoOp()
    {
        ModScenario scenario = new(_fixture);
        ModStateService service = Service(scenario);

        scenario.Seed("pfUI", "pfui");

        OperationResult first = await service.SetEnabledAsync(
            ModScenario.GameId, "pfui", enabled: true, progress: null, TestContext.Current.CancellationToken);
        OperationResult second = await service.SetEnabledAsync(
            ModScenario.GameId, "pfui", enabled: true, progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
        Assert.Equal(0, scenario.BackupCount());
        Assert.True(Directory.Exists(scenario.GameFolder("pfUI")));
    }

    [Fact]
    public async Task UninstallIsRoutedToTheUninstallPipeline()
    {
        ModScenario scenario = new(_fixture);
        ModStateService service = Service(scenario);

        scenario.Seed("pfUI", "pfui");

        OperationResult result = await service.UninstallAsync(
            ModScenario.GameId, "pfui", progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.False(Directory.Exists(scenario.GameFolder("pfUI")));
        Assert.Null(scenario.Manifest.Find(ModScenario.GameId, "pfui"));
        Assert.Equal(1, scenario.BackupCount(BackupTrigger.ModUninstall));
    }

    private static ModStateService Service(ModScenario scenario) =>
        new(scenario.Manifest, scenario.Uninstall, scenario.Toggle);
}
