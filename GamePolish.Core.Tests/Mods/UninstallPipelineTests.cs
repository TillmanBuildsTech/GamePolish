using GamePolish.Core.Backup;
using GamePolish.Core.Manifest;
using GamePolish.Core.Mods;
using GamePolish.Core.Operations;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Mods;

/// <summary>
/// Covers uninstall (.todo/210, criteria 1, 2, 8 and 10): the snapshot comes first, the folder and the record
/// both go, and a backup of what was there is still restorable afterwards.
/// </summary>
[Collection(StorageCollection.Name)]
public class UninstallPipelineTests
{
    private readonly StorageFixture _fixture;

    public UninstallPipelineTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Criterion 1: the recording seam shows the snapshot directory and the backup's copies before the first
    /// delete of the addon folder.
    /// </summary>
    [Fact]
    public async Task Uninstall_TakesTheSnapshotBeforeTheFirstDelete()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        string folder = scenario.GameFolder("pfUI");
        string toc = Path.Combine(folder, "pfUI.toc");

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(OperationErrorCode.None, result.Code);
        Assert.NotNull(result.BackupTimestamp);
        Assert.Contains("3 file(s)", result.Message, StringComparison.Ordinal);

        string snapshot = Path.Combine(scenario.BackupsRoot, result.BackupTimestamp!);
        int snapshotDirectory = scenario.CallIndex($"CreateDirectory({snapshot})");
        int copy = scenario.CallIndex($"CopyFile({toc} => {Path.Combine(snapshot, "Interface", "AddOns", "pfUI", "pfUI.toc")})");
        int delete = scenario.CallIndex($"DeleteDirectory({folder}, recursive)");

        Assert.True(snapshotDirectory >= 0, "the snapshot directory was never created");
        Assert.True(copy > snapshotDirectory, "the mod's files were copied before the snapshot directory existed");
        Assert.True(delete > copy, "the addon folder was deleted before its files were copied into the snapshot");

        Assert.False(Directory.Exists(folder));
        Assert.Null(scenario.Manifest.Find(ModScenario.GameId, "pfui"));
    }

    /// <summary>
    /// Criterion 2: after a restore, the addon folder and its files are back, byte for byte.
    /// </summary>
    [Fact]
    public async Task Uninstall_LeavesABackupThatRestoresTheFolder()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        IReadOnlyList<string> before = ModScenario.FileSet(scenario.GameFolder("pfUI"));

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);

        // The record is gone, so the restore is the only route back to the files - which is exactly the promise
        // the work order makes ("leaves the backup restorable").
        scenario.Backups.Restore(ModScenario.GameId, scenario.InstallRoot, result.BackupTimestamp!);

        Assert.Equal(before, ModScenario.FileSet(scenario.GameFolder("pfUI")));
    }

    /// <summary>
    /// Criterion 8: a disabled mod is uninstalled from the data root, its parked copy is deleted only after a
    /// snapshot, and the snapshot's paths are game-root-relative even though the files were not in the game.
    /// </summary>
    [Fact]
    public async Task Uninstall_OfADisabledMod_BacksUpThenDeletesTheParkedCopy()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        string parked = scenario.ParkedFolder("pfUI");

        OperationResult disabled = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, disabled.Status);
        Assert.True(Directory.Exists(parked));

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);

        string snapshot = Path.Combine(scenario.BackupsRoot, result.BackupTimestamp!);
        int snapshotDirectory = scenario.CallIndex($"CreateDirectory({snapshot})");
        int delete = scenario.CallIndex($"DeleteDirectory({parked}, recursive)");

        Assert.True(snapshotDirectory >= 0 && delete > snapshotDirectory);

        Assert.False(Directory.Exists(parked));

        // The engine created the parked-copies directory, so it goes once nothing is parked in it.
        Assert.False(Directory.Exists(scenario.DisabledRoot));

        Assert.Null(scenario.Manifest.Find(ModScenario.GameId, "pfui"));

        BackupEvent entry = scenario.Backups.Get(ModScenario.GameId, result.BackupTimestamp!)!;

        Assert.Equal(BackupTrigger.ModUninstall, entry.Trigger);
        Assert.Equal("pfUI", entry.SourceName);

        // Game-root-relative, composed from the descriptor's destination - the shape a restore needs. The bytes
        // themselves are not in this snapshot: the files were already under the data root.
        Assert.Contains(entry.Files, file => file.RelativePath == "Interface/AddOns/pfUI/pfUI.toc");
        Assert.All(entry.Files, file => Assert.False(file.RelativePath.StartsWith("..", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Uninstall_OfAModThatIsNotInstalled_IsModNotInstalled()
    {
        ModScenario scenario = new(_fixture);

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "nothing-here",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.ModNotInstalled, result.Code);
        Assert.Empty(scenario.Calls);
    }

    [Fact]
    public async Task Uninstall_CancelledBeforeTheDelete_ChangesNothing()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
        Assert.True(Directory.Exists(scenario.GameFolder("pfUI")));
        Assert.NotNull(scenario.Manifest.Find(ModScenario.GameId, "pfui"));
        Assert.Equal(0, scenario.BackupCount());
    }

    /// <summary>
    /// The failure the work order singles out: the files went, the record did not, and the result says so and
    /// names the backup instead of restoring it behind the user's back.
    /// </summary>
    [Fact]
    public async Task Uninstall_ReportsThatTheFilesAreGoneWhenTheRecordCannotBeUpdated()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        FailingManifestStore manifest = new(scenario.RealManifest)
        {
            FailRemove = (_, _) => new IOException("injected manifest failure"),
        };

        UninstallPipeline uninstall = new(
            scenario.Paths,
            scenario.Games,
            scenario.Descriptors,
            scenario.Backups,
            manifest,
            scenario.Validator,
            scenario.Seam,
            log: null,
            scenario.Clock);

        OperationResult result = await uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.IoError, result.Code);
        Assert.NotNull(result.BackupTimestamp);
        Assert.Contains("record", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BackupTimestamp!, result.Message, StringComparison.Ordinal);

        Assert.False(Directory.Exists(scenario.GameFolder("pfUI")));
        Assert.NotNull(scenario.RealManifest.Find(ModScenario.GameId, "pfui"));
    }

    /// <summary>
    /// A record whose folder the user deleted by hand can still be cleared: the postcondition is "no files and
    /// no record", and refusing would leave a Library row nothing can remove.
    /// </summary>
    [Fact]
    public async Task Uninstall_OfAModWhoseFolderIsAlreadyGone_RemovesOnlyTheRecord()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        Directory.Delete(scenario.GameFolder("pfUI"), recursive: true);

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Contains("no files left", result.Message, StringComparison.Ordinal);
        Assert.Null(scenario.Manifest.Find(ModScenario.GameId, "pfui"));

        // There is nothing to delete, so the delete is never attempted - a recursive delete of a missing folder
        // would throw and turn a cleanup into a failure.
        Assert.DoesNotContain(scenario.Calls, call => call.StartsWith("DeleteDirectory(", StringComparison.Ordinal));
    }

    /// <summary>
    /// Criterion 10: every path the operation mutates is inside the game's install directory or the data root.
    /// </summary>
    [Fact]
    public async Task Uninstall_TouchesOnlyTheGameFolderAndTheDataRoot()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        OperationResult result = await scenario.Uninstall.UninstallAsync(
            ModScenario.GameId,
            "pfui",
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        AssertEveryPathIsInsideTheTwoRoots(scenario);
    }

    /// <summary>Shared by the two pipelines' criterion-10 tests: no third root, ever.</summary>
    internal static void AssertEveryPathIsInsideTheTwoRoots(ModScenario scenario)
    {
        List<string> touched = [.. scenario.PathsTouched()];

        Assert.NotEmpty(touched);
        Assert.All(
            touched,
            path => Assert.True(
                IsSameOrInside(path, scenario.InstallRoot) || IsSameOrInside(path, scenario.Paths.Root),
                $"'{path}' is outside the game's install directory and the data root."));
    }

    private static bool IsSameOrInside(string path, string root) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
