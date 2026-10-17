using GamePolish.Core.Backup;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Install;
using GamePolish.Core.Tests.Storage;
using GamePolish.Core.Update;

namespace GamePolish.Core.Tests.Update;

/// <summary>
/// The update round trip and its failure modes: backup ordering, local-edit reporting, rollback, cancellation,
/// and the version label the Backups view shows.
/// </summary>
/// <remarks>
/// Criteria 3 to 9. The load-bearing ones are 3 (the snapshot precedes every byte in the game folder), 5 (the
/// report names both paths and the backup holds the edited content) and 6 (a failure inside the swap leaves
/// both the folder and the record exactly as they were); the rest guard the promises around them.
/// </remarks>
[Collection(StorageCollection.Name)]
public class UpdatePipelineTests
{
    private readonly StorageFixture _fixture;

    public UpdatePipelineTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Criterion 3: read <c>205</c>'s recorded call log. Every copy that happens before the swap's first
    /// write has to be a copy into the snapshot - anything else would be content landing in the game folder
    /// without a backup behind it.
    /// </summary>
    [Fact]
    public async Task TheBackupIsTakenBeforeAnythingInTheGameFolderIsWritten()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.StageUpdate();

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);

        List<string> calls = scenario.Files.Calls;
        int firstSwapWrite = calls.FindIndex(call => call.Contains(".game-polish-new-", StringComparison.Ordinal));

        Assert.True(firstSwapWrite >= 0, "The swap should have created a .game-polish-new-* directory.");

        int snapshotDirectory = calls.FindIndex(
            call => call.StartsWith($"CreateDirectory({scenario.BackupsRoot}", StringComparison.Ordinal));

        Assert.True(snapshotDirectory >= 0, "The backup engine should have created its snapshot directory.");
        Assert.True(snapshotDirectory < firstSwapWrite, "The snapshot directory must exist before the swap writes.");

        int backupCopy = calls.FindIndex(
            call => call.StartsWith("CopyFile(", StringComparison.Ordinal)
                && call.Contains(scenario.BackupsRoot, StringComparison.Ordinal));

        Assert.True(backupCopy >= 0, "The backup should have copied at least one file.");
        Assert.True(backupCopy < firstSwapWrite, "The backup's copies must precede the swap's first write.");

        foreach (string copy in calls.Take(firstSwapWrite).Where(call => call.StartsWith("CopyFile(", StringComparison.Ordinal)))
        {
            string destination = copy[(copy.IndexOf(" => ", StringComparison.Ordinal) + 4)..^1];

            Assert.StartsWith(scenario.BackupsRoot, destination, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Criterion 4: the round trip. The version changes, <c>UpdatedAt</c> advances, <c>InstalledAt</c> does
    /// not, the record still says the mod is enabled, and the folder holds the new tree with no repository
    /// metadata in it.
    /// </summary>
    [Fact]
    public async Task UpdateReplacesTheFolderAndRecordsTheNewVersion()
    {
        UpdateScenario scenario = new(_fixture);
        InstalledMod before = scenario.Install();
        scenario.StageUpdate();
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(OperationErrorCode.None, result.Code);
        Assert.Contains("->", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.BackupTimestamp);
        Assert.Equal(UpdateScenario.GameId, result.GameId);
        Assert.Equal("pfui", result.ModId);

        InstalledMod after = Assert.IsType<InstalledMod>(scenario.Manifest.Find(UpdateScenario.GameId, "pfui"));

        Assert.Equal(UpdateScenario.NewVersion, after.Version);
        Assert.Equal("sha", after.VersionKind);
        Assert.Equal(before.InstalledAt, after.InstalledAt);
        Assert.Equal(UpdateScenario.Start, after.InstalledAt);
        Assert.Equal(scenario.Clock.GetUtcNow(), after.UpdatedAt);
        Assert.True(after.UpdatedAt > before.UpdatedAt);
        Assert.True(after.Enabled);
        Assert.Equal(3, after.Files.Count);
        Assert.Contains(after.Files, file => file.RelativePath == "pfUI/new-in-this-release.lua");

        Assert.Equal("-- 222222\nreturn 2\n", File.ReadAllText(scenario.AddonFile("pfUI/pfUI.lua")));
        Assert.True(File.Exists(scenario.AddonFile("pfUI/new-in-this-release.lua")));
        Assert.False(Directory.Exists(Path.Combine(scenario.Folder(), ".git")), "The game folder must not carry the repository's history.");

        Assert.Contains(after.Files, file => file.RelativePath == "pfUI/pfUI.toc");
        Assert.Contains(after.Files, file => file.RelativePath == "pfUI/pfUI.lua");

        Assert.DoesNotContain(
            Directory.EnumerateDirectories(scenario.AddonRoot),
            directory => Path.GetFileName(directory).StartsWith(".game-polish", StringComparison.Ordinal));

        // The staging directory is gone: the update cleaned up after itself.
        Assert.Single(scenario.Sources.Cleaned);
        Assert.False(Directory.Exists(scenario.StagingRoot) && Directory.EnumerateDirectories(scenario.StagingRoot).Any());
    }

    /// <summary>
    /// Criterion 5: one file edited (its size changes) and one added. The result names both, and the backup
    /// taken before the swap holds the edited bytes - the only copy of them that survives the update.
    /// </summary>
    [Fact]
    public async Task UpdateReportsTheLocalEditsItReplacedAndBacksTheirContentUp()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();

        const string edited = "-- hand edited, and much longer than what the install wrote\nreturn 99\n";

        File.WriteAllText(scenario.AddonFile("pfUI/pfUI.lua"), edited);
        File.WriteAllText(scenario.AddonFile("pfUI/my-tweaks.lua"), "-- mine\n");

        scenario.StageUpdate();

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);

        string detail = Assert.IsType<string>(result.Detail);

        Assert.Contains("Local edits", detail, StringComparison.Ordinal);
        Assert.Contains("pfUI/pfUI.lua", detail, StringComparison.Ordinal);
        Assert.Contains("pfUI/my-tweaks.lua", detail, StringComparison.Ordinal);

        Assert.DoesNotContain("pfUI/pfUI.toc", detail, StringComparison.Ordinal);

        BackupEvent backup = Assert.Single(scenario.Backups());

        string editedSnapshot = scenario.SnapshotFile(backup.Timestamp, GameRelative(scenario, "pfUI/pfUI.lua"));

        Assert.True(File.Exists(editedSnapshot), "The snapshot must hold the edited file.");
        Assert.Equal(edited, File.ReadAllText(editedSnapshot));

        Assert.True(
            File.Exists(scenario.SnapshotFile(backup.Timestamp, GameRelative(scenario, "pfUI/my-tweaks.lua"))),
            "The snapshot must hold the file the user added.");
    }

    /// <summary>
    /// Criterion 6: the swap fails on its third copy. The folder is byte-identical - no partial tree, no
    /// leftover sibling - and the manifest still records the old version and the old file list.
    /// </summary>
    [Fact]
    public async Task AFailureInsideTheSwapLeavesTheOldFolderAndTheOldRecordIntact()
    {
        UpdateScenario scenario = new(_fixture);
        InstalledMod before = scenario.Install();
        scenario.StageUpdate();
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);
        string staging = scenario.StagingRoot;
        int copies = 0;

        RecordingFileOps failing = new()
        {
            OnCopyFile = source =>
            {
                if (!source.StartsWith(staging, StringComparison.Ordinal))
                {
                    return null;
                }

                copies++;

                return copies == 3 ? new IOException("the disk went away") : null;
            },
        };

        OperationResult result = await scenario.Pipeline(failing).UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.IoError, result.Code);
        Assert.Equal(3, copies);

        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));

        InstalledMod after = Assert.IsType<InstalledMod>(scenario.Manifest.Find(UpdateScenario.GameId, "pfui"));

        Assert.Equal(before.Version, after.Version);
        Assert.Equal(
            before.Files.Select(file => file.RelativePath),
            after.Files.Select(file => file.RelativePath));
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        // The backup was already taken, and a failed update keeps it: it is the record of what the attempt
        // would have replaced.
        Assert.Single(scenario.Backups());
        Assert.NotNull(result.BackupTimestamp);
    }

    /// <summary>Criterion 7, first half: no manifest entry, no update - and nothing staged or written.</summary>
    [Fact]
    public async Task UpdateRefusesAModThatIsNotInstalled()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.StageUpdate();

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.ModNotInstalled, result.Code);
        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Empty(scenario.Backups());
        Assert.Equal(0, scenario.Sources.StageCalls);
    }

    /// <summary>
    /// Criterion 7, second half: a disabled mod's files are not in the game folder - that is what disabling
    /// means in any layout 210 can pick - so there is nothing to replace, and the update says to enable it
    /// rather than guessing where 210 put the files. Nothing is staged, written or backed up, and the record
    /// still says the mod is disabled.
    /// </summary>
    [Fact]
    public async Task UpdateRefusesADisabledModBeforeStagingOrWritingAnything()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install(enabled: false);
        scenario.StageUpdate();

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);
        byte[] manifestBefore = await File.ReadAllBytesAsync(scenario.ManifestFile, TestContext.Current.CancellationToken);

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.ModNotInstalled, result.Code);
        Assert.Contains("enable", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(scenario.ManifestFile, TestContext.Current.CancellationToken));
        Assert.Empty(scenario.Backups());
        Assert.Equal(0, scenario.Sources.StageCalls);

        Assert.False(Assert.IsType<InstalledMod>(scenario.Manifest.Find(UpdateScenario.GameId, "pfui")).Enabled);
    }

    /// <summary>
    /// Criterion 8: the user cancels while files are being copied. The old folder is intact, the result is
    /// cancelled rather than failed, the staging directory is gone, and the backup taken a moment earlier is
    /// kept.
    /// </summary>
    [Fact]
    public async Task CancellingDuringTheCopyLeavesTheOldFolderIntactAndKeepsTheBackup()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install(files: ManyFiles("pfUI", 6));
        scenario.StageUpdate(files: ManyFiles("pfUI", 6));

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);

        using CancellationTokenSource source = new();
        CancellingProgress progress = new(source, "copying", 3);

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress,
            source.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
        Assert.NotNull(result.BackupTimestamp);
        Assert.Single(scenario.Backups());
        Assert.True(progress.Reports.Exists(report => report.Phase == "copying"));

        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Equal(
            UpdateScenario.OldVersion,
            Assert.IsType<InstalledMod>(scenario.Manifest.Find(UpdateScenario.GameId, "pfui")).Version);

        Assert.Single(scenario.Sources.Cleaned);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(scenario.AddonRoot),
            directory => Path.GetFileName(directory).StartsWith(".game-polish", StringComparison.Ordinal));
    }

    /// <summary>
    /// Criterion 9: the label <c>backups.md</c> renders as "Mod update — pfUI v5.1 → v5.2" is this exact
    /// string, with the versions on either side of the arrow.
    /// </summary>
    [Fact]
    public async Task TheBackupRecordsTheUpdateAsOldVersionToNewVersion()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.StageUpdate();

        await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        BackupEvent backup = Assert.Single(scenario.Backups());

        Assert.Equal($"{UpdateScenario.OldVersion} -> {UpdateScenario.NewVersion}", backup.SourceVersion);
        Assert.Equal(BackupTrigger.ModUpdate, backup.Trigger);
        Assert.Equal("pfUI", backup.SourceName);
    }

    /// <summary>
    /// An update that would take a file another installed mod claims is a failure, not a warning, and it is
    /// refused before the backup - there is nothing worth snapshotting for an operation that will not run.
    /// </summary>
    [Fact]
    public async Task UpdateRefusesAFileAnotherInstalledModClaims()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();

        scenario.Manifest.Upsert(UpdateScenario.GameId, new InstalledMod
        {
            Id = "othermod",
            Name = "Other Mod",
            SourceUrl = "https://github.com/example/othermod",
            SourceType = ModSourceType.Github,
            FolderName = "OtherMod",
            Version = "3333333333333333333333333333333333333333",
            VersionKind = "sha",
            Enabled = true,
            InstalledAt = UpdateScenario.Start,
            UpdatedAt = UpdateScenario.Start,
            Files = [new InstalledFileEntry("pfUI/pfUI.lua", 12)],
        });

        scenario.StageUpdate();

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.ConflictsUnresolved, result.Code);
        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Empty(scenario.Backups());

        string detail = Assert.IsType<string>(result.Detail);

        Assert.Contains("pfUI/pfUI.lua", detail, StringComparison.Ordinal);
        Assert.Contains("Owned by other mods", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The swap replaces the folder by name. A source that renamed its addon folder would leave the old
    /// folder orphaned beside the new one, so it is refused with the way out in the message.
    /// </summary>
    [Fact]
    public async Task UpdateRefusesASourceThatRenamedItsAddonFolder()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.StageUpdate(folderName: "pfUI2");

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.SourceInvalid, result.Code);
        Assert.Contains("pfUI2", result.Message, StringComparison.Ordinal);
        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Empty(scenario.Backups());

        // The tree it staged was cleaned up even though the update was refused.
        Assert.Single(scenario.Sources.Cleaned);
    }

    /// <summary>An archive has no upstream, so there is nothing to re-stage and nothing to write.</summary>
    [Fact]
    public async Task UpdateRefusesAnArchiveSourcedMod()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install(sourceType: ModSourceType.Zip);
        scenario.StageUpdate();

        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);

        OperationResult result = await scenario.Pipeline().UpdateAsync(
            UpdateScenario.GameId,
            "pfui",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.SourceInvalid, result.Code);
        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Empty(scenario.Backups());
        Assert.Equal(0, scenario.Sources.StageCalls);
    }

    private static string GameRelative(UpdateScenario scenario, string addonRelativePath) =>
        Path.GetRelativePath(scenario.InstallRoot, scenario.AddonFile(addonRelativePath)).Replace('\\', '/');

    /// <summary>A <c>.toc</c> plus <paramref name="count"/> Lua files, enough for a cancel to land mid-copy.</summary>
    private static (string Path, string Content)[] ManyFiles(string folderName, int count) =>
    [
        ($"{folderName}/{folderName}.toc", "## Interface: 11200\n"),
        .. Enumerable.Range(1, count).Select(index => ($"{folderName}/file{index:D2}.lua", $"-- file {index}\nreturn {index}\n")),
    ];
}
