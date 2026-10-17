using GamePolish.Core.Install;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Install;

/// <summary>
/// Covers the swap on its own (.todo/208, and the shared component task 209 builds on): the names it reserves,
/// the phase it reports at every micro-step, and the rollback that has to work from each of them.
/// </summary>
[Collection(StorageCollection.Name)]
public class SwapOperationsTests
{
    private readonly StorageFixture _fixture;

    public SwapOperationsTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void TheSwapNamesAreReservedSiblingsOfTheAddonFolder()
    {
        InstallScenario scenario = new(_fixture);
        SwapOperations operations = new(scenario.Files, scenario.Validator, scenario.Clock);

        SwapSession swap = operations.Begin(scenario.AddonRoot, "pfUI");

        Assert.Equal(Path.Combine(scenario.AddonRoot, "pfUI"), swap.Paths.Destination);
        Assert.Equal(scenario.AddonRoot, Path.GetDirectoryName(swap.Paths.NewDirectory));
        Assert.Equal(scenario.AddonRoot, Path.GetDirectoryName(swap.Paths.OldDirectory));

        // The name carries the backup engine's timestamp format, so a swap directory and the snapshot that
        // protects it sort together.
        Assert.Equal("20261015T214203123Z", swap.Paths.Timestamp);
        Assert.Equal(".game-polish-new-pfUI-20261015T214203123Z", Path.GetFileName(swap.Paths.NewDirectory));
        Assert.Equal(".game-polish-old-pfUI-20261015T214203123Z", Path.GetFileName(swap.Paths.OldDirectory));

        // 207 reserves the prefix so the startup sweep can find these; a name that stopped matching it would
        // leave the leftovers forever.
        Assert.True(AddonFolderName.IsReserved(Path.GetFileName(swap.Paths.NewDirectory)));
        Assert.True(AddonFolderName.IsReserved(Path.GetFileName(swap.Paths.OldDirectory)));
        Assert.Equal(SwapPhase.Prepared, swap.Phase);
        Assert.False(swap.HasMutated);
    }

    [Fact]
    public void ACopyFailureRollsBackByDeletingOnlyTheNewDirectory()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 4);
        scenario.WriteAddonFile("unrelated/keep.lua", "-- not part of the swap");
        string before = TreeHash.Of(scenario.AddonRoot);

        RecordingFileOps files = new()
        {
            OnCopyFile = source =>
                source.StartsWith(scenario.StagingRoot, StringComparison.Ordinal)
                    ? new IOException("injected copy failure")
                    : null,
        };

        SwapOperations operations = new(files, scenario.Validator, scenario.Clock);
        SwapSession swap = operations.Begin(scenario.AddonRoot, staged.FolderName);

        Assert.Throws<IOException>(() => swap.CopyStagedTree(staged, progress: null, CancellationToken.None));

        Assert.Equal(SwapPhase.Prepared, swap.Phase);
        Assert.True(swap.HasMutated);

        SwapRollback rollback = swap.Rollback();

        Assert.True(rollback.Succeeded);
        Assert.Equal(SwapPhase.Prepared, rollback.ReachedPhase);
        Assert.Empty(rollback.Failures);
        Assert.Null(rollback.StrandedAddonDirectory);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
    }

    [Fact]
    public void ACancelledCopyAlsoRollsBackCleanly()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 4);
        string before = TreeHash.Of(scenario.AddonRoot);

        using CancellationTokenSource cancellation = new();
        int copies = 0;
        RecordingFileOps files = new()
        {
            OnCopyFile = source =>
            {
                if (source.StartsWith(scenario.StagingRoot, StringComparison.Ordinal) && ++copies == 2)
                {
                    cancellation.Cancel();
                }

                return null;
            },
        };

        SwapSession swap = new SwapOperations(files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        Assert.Throws<OperationCanceledException>(
            () => swap.CopyStagedTree(staged, progress: null, cancellation.Token));

        Assert.True(swap.Rollback().Succeeded);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
    }

    [Fact]
    public void ARenameFailureAfterTheOldFolderWasMovedAsideRestoresIt()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 3);
        scenario.WriteAddonFile("pfUI/pfUI.toc", "## the version already there");
        string before = TreeHash.Of(scenario.AddonRoot);

        string destination = Path.Combine(scenario.AddonRoot, "pfUI");
        int failures = 0;
        MoveFailingFileOps files = new()
        {
            FailMove = (_, target) =>
                string.Equals(target, destination, StringComparison.Ordinal) && failures++ == 0
                    ? new IOException("injected rename failure")
                    : null,
        };

        SwapSession swap = new SwapOperations(files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        swap.CopyStagedTree(staged, progress: null, CancellationToken.None);

        Assert.Throws<IOException>(swap.Activate);
        Assert.Equal(SwapPhase.OldMovedAside, swap.Phase);
        Assert.False(Directory.Exists(destination));

        SwapRollback rollback = swap.Rollback();

        Assert.True(rollback.Succeeded);
        Assert.True(rollback.OldFolderRestored);
        Assert.Equal(SwapPhase.OldMovedAside, rollback.ReachedPhase);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.Null(rollback.StrandedAddonDirectory);
    }

    [Fact]
    public void ARollbackAfterActivationRemovesTheNewFolderAndPutsTheOldOneBack()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 3);
        scenario.WriteAddonFile("pfUI/pfUI.toc", "## the version already there");
        string before = TreeHash.Of(scenario.AddonRoot);

        SwapSession swap = new SwapOperations(scenario.Files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        swap.CopyStagedTree(staged, progress: null, CancellationToken.None);
        swap.Activate();

        Assert.Equal(SwapPhase.Activated, swap.Phase);
        Assert.Equal("-- pfUI/pfUI.toc\nreturn 1\n", File.ReadAllText(Path.Combine(scenario.AddonRoot, "pfUI", "pfUI.toc")));

        // Stands in for a manifest write that failed: the folder is in place but nothing records it.
        SwapRollback rollback = swap.Rollback();

        Assert.True(rollback.Succeeded);
        Assert.True(rollback.OldFolderRestored);
        Assert.Equal(SwapPhase.Activated, rollback.ReachedPhase);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.Equal("## the version already there", File.ReadAllText(Path.Combine(scenario.AddonRoot, "pfUI", "pfUI.toc")));
    }

    [Fact]
    public void CommitDeletesTheOldFolderAndRollbackAfterwardsIsRefused()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 2);
        scenario.WriteAddonFile("pfUI/pfUI.toc", "## the version already there");

        SwapSession swap = new SwapOperations(scenario.Files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        swap.CopyStagedTree(staged, progress: null, CancellationToken.None);
        swap.Activate();

        Assert.True(swap.Commit());
        Assert.Equal(SwapPhase.Committed, swap.Phase);
        Assert.Null(swap.LeftoverOldDirectory);
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.OldPrefix + "*"));
        Assert.True(File.Exists(Path.Combine(scenario.AddonRoot, "pfUI", "pfUI.toc")));

        SwapRollback rollback = swap.Rollback();

        Assert.False(rollback.Succeeded);
        Assert.Contains(rollback.Failures, failure => failure.Contains("already deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void RollbackIsIdempotent()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 2);

        SwapSession swap = new SwapOperations(scenario.Files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        swap.CopyStagedTree(staged, progress: null, CancellationToken.None);
        swap.Activate();

        SwapRollback first = swap.Rollback();
        SwapRollback second = swap.Rollback();

        Assert.True(first.Succeeded);
        Assert.Same(first, second);
    }

    [Fact]
    public void TheCopyReportsProgressPerFile()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(files: 3);

        SwapSession swap = new SwapOperations(scenario.Files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        RecordingProgress progress = new();
        swap.CopyStagedTree(staged, progress, CancellationToken.None);

        int[] completed = [1, 2, 3];

        Assert.Equal(completed, progress.Reports.Select(report => report.Completed));
        Assert.All(progress.Reports, report => Assert.Equal(3, report.Total));
        Assert.All(progress.Reports, report => Assert.Equal("copying", report.Phase));
        Assert.Equal(staged.Files.Select(file => file.RelativePath), progress.Reports.Select(report => report.CurrentItem));
    }

    [Fact]
    public void AStagedTreeForAnotherFolderIsRefused()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.Stage(folderName: "pfUI", files: 1);

        SwapSession swap = new SwapOperations(scenario.Files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, "other");

        Assert.Throws<ArgumentException>(
            () => swap.CopyStagedTree(staged, progress: null, CancellationToken.None));
        Assert.Equal(SwapPhase.Prepared, swap.Phase);
    }

    [Fact]
    public void RepositoryMetadataInAStagedFileListIsNeverCopied()
    {
        InstallScenario scenario = new(_fixture);
        StagedSource staged = scenario.StageRaw("pfUI", "pfUI/pfUI.toc", "pfUI/.git/config");

        SwapSession swap = new SwapOperations(scenario.Files, scenario.Validator, scenario.Clock)
            .Begin(scenario.AddonRoot, staged.FolderName);

        Assert.Throws<InvalidOperationException>(
            () => swap.CopyStagedTree(staged, progress: null, CancellationToken.None));

        Assert.True(swap.Rollback().Succeeded);
        Assert.False(Directory.Exists(Path.Combine(swap.Paths.Destination, "pfUI.toc")));
    }
}
