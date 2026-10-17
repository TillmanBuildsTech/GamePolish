using System.Globalization;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Install;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Install;

/// <summary>
/// Covers the install pipeline (.todo/208, acceptance criteria 1-11): the ordering that makes "snapshot before
/// the first write" a fact rather than a habit, the rollback that makes "the game folder is exactly as it was"
/// true at every micro-step, conflict handling, cancellation, and the proof that the destination comes from the
/// descriptor rather than from a constant in code.
/// </summary>
[Collection(StorageCollection.Name)]
public class InstallPipelineTests
{
    private readonly StorageFixture _fixture;

    public InstallPipelineTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    private InstallScenario NewScenario() => new(_fixture);

    [Fact]
    public async Task TheSnapshotIsTakenBeforeTheFirstWriteInsideTheGameFolder()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 3);

        // Something already in the folder, so the snapshot has real files to copy rather than only recording
        // destinations that do not exist yet.
        scenario.WriteAddonFile("pfUI/pfUI.toc", "## hand-made");
        scenario.WriteAddonFile("pfUI/notes.txt", "mine");

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(acceptOverwrites: true), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.BackupTimestamp));

        List<string> calls = scenario.Files.Calls;

        string snapshot = Path.Combine(scenario.BackupsRoot, result.BackupTimestamp!);
        int snapshotIndex = calls.IndexOf($"CreateDirectory({snapshot})");

        Assert.True(snapshotIndex >= 0, $"no snapshot directory was created: {string.Join(" | ", calls)}");

        // Everything that ran before the snapshot may only read the game folder. The backup's copies read
        // from it; anything else touching it would be the write invariant #1 forbids.
        Assert.All(
            calls.Take(snapshotIndex),
            call => Assert.True(
                call.StartsWith($"CreateDirectory({scenario.BackupsRoot}", StringComparison.Ordinal)
                || call.StartsWith($"CopyFile({scenario.InstallRoot}", StringComparison.Ordinal),
                $"the game folder was written before the snapshot existed: {call}"));

        int firstGameFolderWrite = calls.FindIndex(call => call.Contains(SwapOperations.NewPrefix, StringComparison.Ordinal));

        Assert.True(
            firstGameFolderWrite > snapshotIndex,
            $"a write inside the game folder preceded the snapshot: {string.Join(" | ", calls)}");

        List<int> backupCopies =
        [
            .. calls
                .Select((call, index) => (Call: call, Index: index))
                .Where(entry => entry.Call.StartsWith($"CopyFile({scenario.InstallRoot}", StringComparison.Ordinal))
                .Select(entry => entry.Index),
        ];

        Assert.NotEmpty(backupCopies);
        Assert.All(backupCopies, index => Assert.True(index < firstGameFolderWrite));
    }

    [Fact]
    public async Task AFailedCopyLeavesTheGameFolderByteIdenticalAndTheManifestUntouched()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 10);
        scenario.SeedInstalledMod("other", "other");

        string before = TreeHash.Of(scenario.InstallRoot);
        byte[] manifestBefore = File.ReadAllBytes(scenario.ManifestFile);

        int stagedCopies = 0;
        RecordingFileOps files = new()
        {
            OnCopyFile = source =>
            {
                if (!source.StartsWith(scenario.StagingRoot, StringComparison.Ordinal))
                {
                    return null;
                }

                stagedCopies++;

                return stagedCopies == 3 ? new IOException("injected copy failure") : null;
            },
        };

        OperationResult result = await scenario
            .Pipeline(files)
            .InstallAsync(scenario.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.NotEqual(OperationErrorCode.None, result.Code);
        Assert.Equal(before, TreeHash.Of(scenario.InstallRoot));
        Assert.Equal(manifestBefore, File.ReadAllBytes(scenario.ManifestFile));
        Assert.Equal(3, stagedCopies);

        Assert.NotNull(result.BackupTimestamp);
        Assert.True(
            Directory.Exists(Path.Combine(scenario.BackupsRoot, result.BackupTimestamp!)),
            "the backup named in the result does not exist on disk");

        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.NewPrefix + "*"));
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.OldPrefix + "*"));
        Assert.DoesNotContain(scenario.Manifest.List(InstallScenario.GameId), mod => mod.Id == "pfui");
    }

    [Fact]
    public async Task AFailureAfterTheOldFolderWasMovedAsidePutsItBack()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 3);
        scenario.SeedInstalledMod("other", "pfUI", "pfUI/pfUI.toc", "pfUI/mine.lua");

        string before = TreeHash.Of(scenario.AddonRoot);
        string destination = Path.Combine(scenario.AddonRoot, "pfUI");

        // Fails the rename that would put the new tree in place - the moment the old folder is already at its
        // .game-polish-old-* name and the addon folder is briefly missing.
        int failures = 0;
        MoveFailingFileOps files = new()
        {
            FailMove = (_, target) =>
                string.Equals(target, destination, StringComparison.Ordinal) && failures++ == 0
                    ? new IOException("injected rename failure")
                    : null,
        };

        OperationResult result = await scenario
            .Pipeline(files)
            .InstallAsync(scenario.Request(acceptOverwrites: true), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.True(Directory.Exists(destination));
        Assert.True(File.Exists(Path.Combine(destination, "mine.lua")));
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.NewPrefix + "*"));
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.OldPrefix + "*"));
    }

    [Fact]
    public async Task AFailedRollbackSaysWhereTheAddonIsInsteadOfClaimingSuccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 3);
        scenario.SeedInstalledMod("other", "pfUI", "pfUI/pfUI.toc");

        string destination = Path.Combine(scenario.AddonRoot, "pfUI");

        // Every rename into the addon folder's own name fails, so the new tree cannot land and the old folder
        // cannot be put back either.
        MoveFailingFileOps files = new()
        {
            FailMove = (_, target) => string.Equals(target, destination, StringComparison.Ordinal)
                ? new IOException("injected rename failure")
                : null,
        };

        OperationResult result = await scenario
            .Pipeline(files)
            .InstallAsync(scenario.Request(acceptOverwrites: true), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);

        string oldDirectory = SwapOperations.OldDirectoryName(
            "pfUI",
            scenario.Clock.GetUtcNow().UtcDateTime.ToString(SwapOperations.TimestampFormat, CultureInfo.InvariantCulture));

        Assert.Contains(oldDirectory, result.Message, StringComparison.Ordinal);
        Assert.Contains(result.BackupTimestamp!, result.Detail!, StringComparison.Ordinal);

        string oldFolder = Path.Combine(scenario.AddonRoot, oldDirectory);

        Assert.True(Directory.Exists(oldFolder));
        Assert.True(File.Exists(Path.Combine(oldFolder, "pfUI.toc")));
    }

    [Fact]
    public async Task AFirstInstallCreatesTheFolderRecordsItAndLeavesNoSwapSibling()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        StagedSource staged = scenario.Stage(files: 5, withGitDirectory: true);

        RecordingProgress progress = new();

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(), progress, cancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(OperationErrorCode.None, result.Code);
        Assert.Equal("pfui", result.ModId);
        Assert.False(string.IsNullOrWhiteSpace(result.BackupTimestamp));

        string folder = Path.Combine(scenario.AddonRoot, "pfUI");
        Assert.True(Directory.Exists(folder));

        foreach (InstalledFileEntry file in staged.Files)
        {
            string installed = Path.Combine(scenario.AddonRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(installed), $"'{installed}' was not installed");
        }

        // The repository's history is not part of the addon.
        Assert.False(Directory.Exists(Path.Combine(folder, ".git")));

        InstalledMod mod = Assert.Single(scenario.Manifest.List(InstallScenario.GameId));

        Assert.Equal("pfUI", mod.Name);
        Assert.Equal("pfUI", mod.FolderName);
        Assert.True(mod.Enabled);
        Assert.Equal(staged.Version, mod.Version);
        Assert.Equal(staged.VersionKind, mod.VersionKind);
        Assert.Equal(
            staged.Files.Select(file => file.RelativePath),
            mod.Files.Select(file => file.RelativePath));
        Assert.Equal(InstallScenario.Start, mod.InstalledAt);

        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.NewPrefix + "*"));
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.OldPrefix + "*"));

        Assert.Contains(progress.Reports, report => report.Phase == "copying" && report.Completed == 5 && report.Total == 5);
        Assert.Contains(progress.Reports, report => report.Phase == "backing-up");
    }

    [Fact]
    public async Task AFailedFirstInstallWritesNoManifestAtAll()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 4);

        RecordingFileOps files = new()
        {
            OnCopyFile = source =>
                source.StartsWith(scenario.StagingRoot, StringComparison.Ordinal)
                    ? new IOException("injected copy failure")
                    : null,
        };

        OperationResult result = await scenario
            .Pipeline(files)
            .InstallAsync(scenario.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(File.Exists(scenario.ManifestFile));
        Assert.Empty(scenario.Manifest.List(InstallScenario.GameId));
    }

    [Fact]
    public async Task ATraversalPathIsRefusedBeforeTheBackupAndBeforeAnyWrite()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.StageRaw("pfUI", "pfUI/pfUI.toc", "../evil.txt");

        string before = TreeHash.Of(scenario.AddonRoot);

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.PathUnsafe, result.Code);

        // No mutation of any kind: no snapshot, no swap directory, no manifest.
        Assert.Empty(scenario.Files.Calls);
        Assert.False(Directory.Exists(scenario.BackupsRoot));
        Assert.False(File.Exists(scenario.ManifestFile));
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.Contains("evil.txt", result.Detail!, StringComparison.Ordinal);

        // The staging directory does not outlive the attempt.
        Assert.Equal(1, scenario.Sources.CleanupCount);
    }

    [Fact]
    public async Task AnotherModsFolderBlocksTheInstallUntilOverwritesAreAccepted()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 3);
        scenario.SeedInstalledMod("other", "pfUI", "pfUI/pfUI.toc");

        string before = TreeHash.Of(scenario.AddonRoot);
        byte[] manifestBefore = File.ReadAllBytes(scenario.ManifestFile);

        OperationResult blocked = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, blocked.Status);
        Assert.Equal(OperationErrorCode.ConflictsUnresolved, blocked.Code);
        Assert.Contains("pfUI/pfUI.toc", blocked.Detail!, StringComparison.Ordinal);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.Equal(manifestBefore, File.ReadAllBytes(scenario.ManifestFile));
        Assert.False(Directory.Exists(scenario.BackupsRoot));

        // A fresh staging directory: the first attempt cleaned up the one it used, as 207's Cleanup does.
        scenario.Stage(files: 3);

        OperationResult accepted = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(acceptOverwrites: true), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Succeeded, accepted.Status);
        Assert.Equal("pfui", accepted.ModId);

        // The overwritten file is recorded in the result, so a caller can show what changed.
        Assert.Contains("pfUI/pfUI.toc", accepted.Detail!, StringComparison.Ordinal);
        Assert.Contains("other", accepted.Detail!, StringComparison.Ordinal);

        InstalledMod? installed = scenario.Manifest.Find(InstallScenario.GameId, "pfui");

        Assert.NotNull(installed);
        Assert.Equal("pfUI", installed.FolderName);
    }

    [Fact]
    public async Task FilesTheUserMadeInTheFolderBlockTheInstallToo()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 2);
        scenario.WriteAddonFile("pfUI/pfUI.toc", "## my own copy");

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationErrorCode.ConflictsUnresolved, result.Code);
        Assert.Equal("## my own copy", File.ReadAllText(Path.Combine(scenario.AddonRoot, "pfUI", "pfUI.toc")));
        Assert.False(File.Exists(scenario.ManifestFile));
    }

    [Fact]
    public async Task ReinstallingAModThatIsAlreadyRecordedIsRefusedAndPointsAtUpdate()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 2);
        scenario.SeedInstalledMod("pfui", "pfUI", "pfUI/pfUI.toc");

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(acceptOverwrites: true), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.ModAlreadyInstalled, result.Code);
        Assert.Contains("Update", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(scenario.BackupsRoot));
    }

    [Fact]
    public async Task AnUntrackedGameAndAnUnknownDescriptorBothFailBeforeAnyWrite()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        InstallScenario untracked = NewScenario();
        untracked.Stage(files: 1);
        untracked.Games.Games.Clear();

        OperationResult notTracked = await untracked
            .Pipeline()
            .InstallAsync(untracked.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, notTracked.Status);
        Assert.Equal(OperationErrorCode.GameInstallPathMissing, notTracked.Code);
        Assert.Equal(0, untracked.Sources.StageCalls);
        Assert.Empty(untracked.Files.Calls);
        Assert.False(Directory.Exists(untracked.BackupsRoot));

        InstallScenario unknown = NewScenario();
        unknown.Stage(files: 1);
        unknown.Descriptors.Descriptors.Clear();

        OperationResult noDescriptor = await unknown
            .Pipeline()
            .InstallAsync(unknown.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, noDescriptor.Status);
        Assert.Equal(OperationErrorCode.GameNotFound, noDescriptor.Code);
        Assert.Empty(unknown.Files.Calls);
        Assert.False(Directory.Exists(unknown.BackupsRoot));
    }

    [Fact]
    public async Task CancellingDuringTheCopyLeavesTheGameFolderAsItWasAndKeepsTheBackup()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 10);
        scenario.SeedInstalledMod("other", "pfUI", "pfUI/pfUI.toc");

        string before = TreeHash.Of(scenario.AddonRoot);

        using CancellationTokenSource cancellation = new();
        int stagedCopies = 0;
        RecordingFileOps files = new()
        {
            OnCopyFile = source =>
            {
                if (source.StartsWith(scenario.StagingRoot, StringComparison.Ordinal) && ++stagedCopies == 3)
                {
                    cancellation.Cancel();
                }

                return null;
            },
        };

        OperationResult result = await scenario
            .Pipeline(files)
            .InstallAsync(scenario.Request(acceptOverwrites: true), progress: null, cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.NewPrefix + "*"));
        Assert.Empty(Directory.GetDirectories(scenario.AddonRoot, SwapOperations.OldPrefix + "*"));
        Assert.Null(scenario.Manifest.Find(InstallScenario.GameId, "pfui"));

        Assert.NotNull(result.BackupTimestamp);
        Assert.True(Directory.Exists(Path.Combine(scenario.BackupsRoot, result.BackupTimestamp!)));
    }

    [Fact]
    public async Task CancellingBeforeTheBackupWritesNothingAtAll()
    {
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 3);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(), progress: null, cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Null(result.BackupTimestamp);
        Assert.Empty(scenario.Files.Calls);
        Assert.False(Directory.Exists(scenario.BackupsRoot));
        Assert.False(File.Exists(scenario.ManifestFile));
        Assert.Equal(0, scenario.Sources.StageCalls);
    }

    [Fact]
    public async Task DetectConflictsChangesNothingOnDisk()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Stage(files: 3);
        scenario.SeedInstalledMod("other", "pfUI", "pfUI/pfUI.toc");

        string before = TreeHash.Of(scenario.AddonRoot);
        byte[] manifestBefore = File.ReadAllBytes(scenario.ManifestFile);

        ConflictReport report = await scenario
            .Pipeline()
            .DetectConflictsAsync(scenario.Request(), cancellationToken);

        Assert.True(report.IsBlocked);
        Assert.Contains("pfUI/pfUI.toc", report.OwnedByOtherMods);
        Assert.Contains("pfUI/", report.OwnedByOtherMods);

        Assert.Empty(scenario.Files.Calls);
        Assert.False(Directory.Exists(scenario.BackupsRoot));
        Assert.Equal(manifestBefore, File.ReadAllBytes(scenario.ManifestFile));
        Assert.Equal(before, TreeHash.Of(scenario.AddonRoot));
        Assert.Equal(1, scenario.Sources.CleanupCount);
    }

    [Fact]
    public async Task TheInstallTargetIsTheDescriptorsDestinationNotAConstant()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();

        // The real YAML loader over the fixtures this project copies into "<output>/games", so the destination
        // provably comes from a descriptor file rather than from a fake object built in the test.
        GameDescriptorLoader loader = new(
            [Path.Combine(AppContext.BaseDirectory, GameDescriptorLoader.DescriptorDirectoryName)]);
        GameDescriptor descriptor = loader.LoadById("modsgame")!;

        Assert.Equal("Mods", descriptor.ModLayout.Destination);

        scenario.Descriptors.Descriptors.Clear();
        scenario.Descriptors.Descriptors.Add(descriptor);
        scenario.Games.Games.Clear();
        scenario.Track("modsgame", descriptor.Name);

        string modsRoot = Path.Combine(scenario.InstallRoot, "Mods");
        Directory.CreateDirectory(modsRoot);

        scenario.Stage(files: 2);

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(gameId: "modsgame"), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(File.Exists(Path.Combine(modsRoot, "pfUI", "pfUI.toc")));
        Assert.False(Directory.Exists(Path.Combine(scenario.AddonRoot, "pfUI")));
        Assert.Single(scenario.Manifest.List("modsgame"));
    }

    [Fact]
    public async Task AStagingFailureIsReportedWithItsOwnCodeAndWritesNothing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InstallScenario scenario = NewScenario();
        scenario.Sources.FailWith = new GitUnavailableException();

        OperationResult result = await scenario
            .Pipeline()
            .InstallAsync(scenario.Request(), progress: null, cancellationToken);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.GitNotFound, result.Code);
        Assert.Empty(scenario.Files.Calls);
        Assert.False(Directory.Exists(scenario.BackupsRoot));
        Assert.False(File.Exists(scenario.ManifestFile));
    }
}
