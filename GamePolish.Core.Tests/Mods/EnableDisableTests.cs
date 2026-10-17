using GamePolish.Core.Backup;
using GamePolish.Core.Manifest;
using GamePolish.Core.Mods;
using GamePolish.Core.Operations;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Mods;

/// <summary>
/// Covers the checkbox (.todo/210, criteria 3-7, 9 and 10): disable parks the addon under the data root and keeps
/// the record, enable puts the identical file set back after snapshotting the game folder, both are idempotent, and
/// a failure at any point leaves exactly one copy of the mod - in the place the manifest says it is.
/// </summary>
/// <remarks>
/// The load-bearing assertions are the file-set comparisons: a disable that loses a byte, or an enable that puts
/// back a different tree, is the failure mode this task exists to prevent. They compare relative path, length and
/// SHA-256 per file, so "identical" means identical rather than "the same number of files".
/// </remarks>
[Collection(StorageCollection.Name)]
public class EnableDisableTests
{
    private const string ModId = "pfui";

    private readonly StorageFixture _fixture;

    public EnableDisableTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Criterion 3: after a disable the game folder is gone, the parked copy holds the same relative paths and the
    /// same bytes, and the manifest still lists the mod with <c>Enabled == false</c>.
    /// </summary>
    [Fact]
    public async Task Disable_ParksTheIdenticalFileSetAndKeepsTheRecord()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        string folder = scenario.GameFolder("pfUI");
        IReadOnlyList<string> before = ModScenario.FileSet(folder);

        Assert.Equal(3, before.Count);

        OperationResult result = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(OperationErrorCode.None, result.Code);
        Assert.NotNull(result.BackupTimestamp);
        Assert.Contains("3 file(s)", result.Message, StringComparison.Ordinal);

        // The game directory no longer has the addon...
        Assert.False(Directory.Exists(folder));

        // ...and the parked copy is the same tree, not a re-creation of it.
        Assert.Equal(before, ModScenario.FileSet(scenario.ParkedFolder("pfUI")));

        // The record stays: a disabled mod is still installed, which is what the Library row renders.
        InstalledMod? mod = scenario.Manifest.Find(ModScenario.GameId, ModId);

        Assert.NotNull(mod);
        Assert.False(mod!.Enabled);
        Assert.Equal(3, mod.Files.Count);

        // Assert those strings, because the Backups view filters by them.
        BackupEvent entry = scenario.Backups.Get(ModScenario.GameId, result.BackupTimestamp!)!;

        Assert.Equal(BackupTrigger.EnableDisable, entry.Trigger);
        Assert.Equal("pfUI", entry.SourceName);
        Assert.Equal(mod.Version, entry.SourceVersion);
        Assert.Contains(entry.Files, file => file.RelativePath == "Interface/AddOns/pfUI/pfUI.toc" && file.SizeBytes > 0);
    }

    /// <summary>
    /// Criterion 4: enable puts the identical file set back, sets the flag, deletes the parked remnant, and the
    /// snapshot - directory and copies - is on the call log before the first write inside the game's addons folder.
    /// </summary>
    [Fact]
    public async Task Enable_RestoresTheFileSetAfterBackingUpAndDeletesTheParkedCopy()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        IReadOnlyList<string> before = ModScenario.FileSet(scenario.GameFolder("pfUI"));

        OperationResult disabled = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, disabled.Status);

        // A folder the user created by hand while the mod was disabled: the enable replaces it, so its bytes are
        // exactly what the pre-enable snapshot has to hold. Without it this assertion would be vacuous, because a
        // clean enable legitimately has nothing to copy.
        scenario.WriteGameFile("pfUI/stray.txt", "hand-edited");

        OperationResult result = await scenario.Toggle.EnableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.BackupTimestamp);

        // Identical file set, and the hand-edited folder is gone rather than merged into it.
        Assert.Equal(before, ModScenario.FileSet(scenario.GameFolder("pfUI")));

        InstalledMod? mod = scenario.Manifest.Find(ModScenario.GameId, ModId);

        Assert.NotNull(mod);
        Assert.True(mod!.Enabled);

        Assert.False(Directory.Exists(scenario.ParkedFolder("pfUI")));

        // The parked-copies directory is housekeeping the pipeline created, so it goes with the last parked addon.
        Assert.False(Directory.Exists(scenario.DisabledRoot));

        // Order, on one interleaved call log: snapshot directory, then the backup's copy, then the first write
        // inside the game folder (the swap's .game-polish-new-* tree).
        string snapshot = Path.Combine(scenario.BackupsRoot, result.BackupTimestamp!);
        string strayInSnapshot = Path.Combine(snapshot, "Interface", "AddOns", "pfUI", "stray.txt");

        int snapshotDirectory = scenario.CallIndex($"CreateDirectory({snapshot})");
        int backupCopy = scenario.CallIndex($"CopyFile({scenario.GameFolder("pfUI")}{Path.DirectorySeparatorChar}stray.txt => {strayInSnapshot})");
        int firstGameWrite = scenario.CallIndexContaining(".game-polish-new-");

        Assert.True(snapshotDirectory >= 0, "the enable took no snapshot");
        Assert.True(backupCopy > snapshotDirectory, "the stray folder was not copied into the snapshot");
        Assert.True(firstGameWrite > backupCopy, "the game folder was written before the backup's copies finished");
        Assert.Equal("hand-edited", File.ReadAllText(strayInSnapshot));
    }

    /// <summary>
    /// Criterion 5: one <see cref="BackupTrigger.EnableDisable"/> snapshot per transition that changed state, and
    /// none for the no-ops - a double-click must not cost the user a second snapshot.
    /// </summary>
    [Fact]
    public async Task DisableEnableDisable_TakesOneBackupPerStateChangeAndNoneForNoOps()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        Assert.Equal(0, scenario.BackupCount(BackupTrigger.EnableDisable));

        OperationResult first = await Disable();
        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(1, scenario.BackupCount(BackupTrigger.EnableDisable));

        // Already disabled: success, and no snapshot.
        OperationResult repeated = await Disable();
        Assert.Equal(OperationStatus.Succeeded, repeated.Status);
        Assert.Contains("already disabled", repeated.Message, StringComparison.Ordinal);
        Assert.Null(repeated.BackupTimestamp);
        Assert.Equal(1, scenario.BackupCount(BackupTrigger.EnableDisable));

        OperationResult enabled = await Enable();
        Assert.Equal(OperationStatus.Succeeded, enabled.Status);
        Assert.Equal(2, scenario.BackupCount(BackupTrigger.EnableDisable));

        // Already enabled: success, and no snapshot.
        OperationResult enabledAgain = await Enable();
        Assert.Equal(OperationStatus.Succeeded, enabledAgain.Status);
        Assert.Contains("already enabled", enabledAgain.Message, StringComparison.Ordinal);
        Assert.Null(enabledAgain.BackupTimestamp);
        Assert.Equal(2, scenario.BackupCount(BackupTrigger.EnableDisable));

        OperationResult finalDisable = await Disable();
        Assert.Equal(OperationStatus.Succeeded, finalDisable.Status);
        Assert.Equal(3, scenario.BackupCount(BackupTrigger.EnableDisable));

        // Every snapshot the three transitions made is the same trigger, because 205 has one for both directions.
        Assert.All(
            scenario.Backups.List(ModScenario.GameId),
            entry => Assert.Equal(BackupTrigger.EnableDisable, entry.Trigger));

        Assert.False(scenario.Manifest.Find(ModScenario.GameId, ModId)!.Enabled);

        Task<OperationResult> Disable() => scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Task<OperationResult> Enable() => scenario.Toggle.EnableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);
    }

    /// <summary>
    /// Criterion 6: a parked copy that is gone cannot be enabled, and the failure changes nothing - no flag flip, no
    /// folder, no snapshot - while naming the newest backup as the recovery route.
    /// </summary>
    [Fact]
    public async Task Enable_WhenTheParkedCopyIsMissing_IsStagedCopyMissingAndChangesNothing()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        OperationResult disabled = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, disabled.Status);

        // Someone deleted the data root's staging folder, or a half-finished cleanup did: the mod's only copy is
        // gone and neither location has it.
        Directory.Delete(scenario.ParkedFolder("pfUI"), recursive: true);

        OperationResult result = await scenario.Toggle.EnableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.StagedCopyMissing, result.Code);
        Assert.Contains("staging area", result.Message, StringComparison.Ordinal);
        Assert.Contains("Reinstall", result.Message, StringComparison.Ordinal);

        // The recovery hint is the newest snapshot for the mod, which is the disable's own.
        Assert.NotNull(disabled.BackupTimestamp);
        Assert.Contains(disabled.BackupTimestamp!, result.Message, StringComparison.Ordinal);

        Assert.False(Directory.Exists(scenario.GameFolder("pfUI")));
        Assert.False(scenario.Manifest.Find(ModScenario.GameId, ModId)!.Enabled);

        // Nothing was written: no second snapshot, and no game folder invented to hold a flag flip.
        Assert.Equal(1, scenario.BackupCount());
    }

    /// <summary>
    /// Criterion 7: enabling an already-enabled mod succeeds without taking a second snapshot.
    /// </summary>
    [Fact]
    public async Task EnableTwice_IsSuccessTwiceWithOneBackup()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        OperationResult disabled = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, disabled.Status);
        Assert.Equal(1, scenario.BackupCount());

        OperationResult first = await scenario.Toggle.EnableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.NotNull(first.BackupTimestamp);
        Assert.Equal(2, scenario.BackupCount());

        OperationResult second = await scenario.Toggle.EnableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, second.Status);
        Assert.Equal(OperationErrorCode.None, second.Code);
        Assert.Null(second.BackupTimestamp);
        Assert.Equal(2, scenario.BackupCount());

        Assert.True(scenario.Manifest.Find(ModScenario.GameId, ModId)!.Enabled);
    }

    /// <summary>
    /// Criterion 9: a failure during the disable's move leaves the mod enabled, its folder intact and the manifest
    /// unchanged. The snapshot taken before the move is kept - it is the record that the operation was attempted.
    /// </summary>
    /// <remarks>
    /// <see cref="UnauthorizedAccessException"/> rather than <see cref="IOException"/> on purpose: an
    /// <see cref="IOException"/> from a rename is the cross-volume signal, and the pipeline answers that by copying
    /// instead. A permission failure is the one the fallback cannot rescue.
    /// </remarks>
    [Fact]
    public async Task AFailedDisableLeavesTheModEnabledAndTheFolderIntact()
    {
        ModScenario scenario = new(
            _fixture,
            failMove: static (_, _) => new UnauthorizedAccessException("injected move failure"));

        scenario.Seed();

        string folder = scenario.GameFolder("pfUI");
        IReadOnlyList<string> before = ModScenario.FileSet(folder);

        OperationResult result = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.PermissionDenied, result.Code);

        // The three things criterion 9 names.
        Assert.True(scenario.Manifest.Find(ModScenario.GameId, ModId)!.Enabled, "the manifest was flipped by a failed disable");
        Assert.Equal(before, ModScenario.FileSet(folder));

        // No parked copy was left behind, because the move never completed...
        Assert.False(Directory.Exists(scenario.ParkedFolder("pfUI")));

        // ...and the snapshot the disable took first is kept, because it is the record of the attempt.
        Assert.NotNull(result.BackupTimestamp);
        Assert.Equal(1, scenario.BackupCount(BackupTrigger.EnableDisable));
    }

    /// <summary>
    /// The cross-volume fallback: a rename that fails with <see cref="IOException"/> is answered by copying,
    /// verifying every byte, and only then deleting the game folder's copy.
    /// </summary>
    [Fact]
    public async Task ACrossVolumeRenameFallsBackToAVerifiedCopyThenDelete()
    {
        // IOException is exactly what Directory.Move raises for a rename across volumes, which is the default
        // shape of this product: the data root is under the user profile and the game may be on another drive.
        ModScenario scenario = new(
            _fixture,
            failMove: static (_, _) => new IOException("cannot rename across volumes"));

        scenario.Seed();

        string folder = scenario.GameFolder("pfUI");
        IReadOnlyList<string> before = ModScenario.FileSet(folder);
        string parked = scenario.ParkedFolder("pfUI");

        OperationResult result = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);

        // The rename was attempted and failed, the tree was copied, and the copy is byte-identical.
        Assert.Contains(scenario.Calls, call => call.Contains("failed", StringComparison.Ordinal));
        Assert.Contains(scenario.Calls, call => call.StartsWith($"CopyFile({folder}", StringComparison.Ordinal));
        Assert.Equal(before, ModScenario.FileSet(parked));

        // Only after every copied byte verified was the game's copy removed - a copy that did not verify would
        // have left it alone.
        int lastCopy = scenario.Calls.FindLastIndex(call => call.StartsWith($"CopyFile({folder}", StringComparison.Ordinal));
        int delete = scenario.CallIndex($"DeleteDirectory({folder}, recursive)");

        Assert.True(delete > lastCopy, "the game folder was deleted before every file had been copied");

        Assert.False(Directory.Exists(folder));
        Assert.False(scenario.Manifest.Find(ModScenario.GameId, ModId)!.Enabled);
    }

    /// <summary>
    /// A folder the manifest promises but the game does not have is a failure, not a flag flip: telling the user
    /// the mod is disabled while its files are nowhere would be the one lie the Library cannot recover from.
    /// </summary>
    [Fact]
    public async Task Disable_OfAModWhoseFolderIsGone_IsModFolderMissing()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        Directory.Delete(scenario.GameFolder("pfUI"), recursive: true);

        OperationResult result = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.ModFolderMissing, result.Code);
        Assert.Equal(0, scenario.BackupCount());
        Assert.True(scenario.Manifest.Find(ModScenario.GameId, ModId)!.Enabled);
    }

    /// <summary>
    /// Criterion 10, for both directions of the checkbox: every mutated path is inside the game's install directory
    /// or the data root, and no third root appears.
    /// </summary>
    [Fact]
    public async Task EnableAndDisable_TouchOnlyTheGameFolderAndTheDataRoot()
    {
        ModScenario scenario = new(_fixture);
        scenario.Seed();

        OperationResult disabled = await scenario.Toggle.DisableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, disabled.Status);

        OperationResult enabled = await scenario.Toggle.EnableAsync(
            ModScenario.GameId,
            ModId,
            progress: null,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, enabled.Status);
        Assert.NotEmpty(scenario.Calls);
        UninstallPipelineTests.AssertEveryPathIsInsideTheTwoRoots(scenario);
    }
}
