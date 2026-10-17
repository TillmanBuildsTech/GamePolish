using System.Security.Cryptography;

using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// Covers staging end to end (.todo/207, acceptance criteria 1, 4-8): the clone's argv and hardening files,
/// the staged tree's shape, the version metadata, the folder-name derivation, the fail-clean property of
/// every failure path, cancellation, and the sweep that reclaims what a crash left behind.
/// </summary>
[Collection(StorageCollection.Name)]
public class SourceAcquirerTests
{
    private const string GameId = "octowow";
    private const string PfUiUrl = "https://github.com/brues-code/pfUI";

    private readonly GamePolishPaths _paths;
    private readonly FakeGitProcessLauncher _launcher = new();
    private readonly RecordingFileOps _files = new();
    private readonly string _gameDirectory;

    public SourceAcquirerTests(StorageFixture fixture)
    {
        _paths = new GamePolishPaths(fixture.NewSubdirectory($"sources-{Guid.NewGuid():N}"));
        _gameDirectory = fixture.NewInstallDirectory($"octowow-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path.Combine(_gameDirectory, "Interface", "AddOns"));
        File.WriteAllText(Path.Combine(_gameDirectory, "WoW.exe"), "not really a binary");
    }

    [Fact]
    public async Task ACloneIsStagedWithItsShaItsFilesAndAnUntouchedGameDirectory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string before = HashDirectory(_gameDirectory);

        StagedSource staged = await Acquirer().StageGitAsync(GitRequest("op-clone"), cancellationToken);

        Assert.Equal(FakeGitProcessLauncher.Head, staged.Version);
        Assert.Equal(40, staged.Version.Length);
        Assert.All(staged.Version, character => Assert.True(char.IsAsciiHexDigit(character)));
        Assert.Equal("sha", staged.VersionKind);
        Assert.Equal("pfUI", staged.FolderName);
        Assert.Equal(ModSourceType.Github, staged.SourceType);
        Assert.Equal(PfUiUrl, staged.SourceUrl);
        Assert.True(staged.HasGitDirectory);

        // Addon-root-relative, sorted, and with the repository's metadata left out (criterion 1 + the
        // coordinate system 206 already stores).
        Assert.Equal(["pfUI/pfUI.lua", "pfUI/pfUI.toc"], staged.Files.Select(file => file.RelativePath));
        Assert.Equal([7L, 34L], staged.Files.Select(file => file.SizeBytes));

        // The .git directory is still on disk so the pipeline knows to exclude it, and it is not in the list.
        Assert.True(File.Exists(Path.Combine(staged.StagingDirectory, ".git", "HEAD")));
        Assert.Equal(
            Path.Combine(_paths.StagingDirectory(GameId), "op-clone", "pfUI"),
            staged.StagingDirectory);

        Assert.Equal(before, HashDirectory(_gameDirectory));
    }

    [Fact]
    public async Task TheCloneIsTheHardenedArgvWithAnEmptyHooksDirectory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Acquirer().StageGitAsync(GitRequest("op-argv"), cancellationToken);

        string operationDirectory = Path.Combine(_paths.StagingDirectory(GameId), "op-argv");
        System.Diagnostics.ProcessStartInfo clone = _launcher.StartInfos.Single(
            startInfo => startInfo.ArgumentList.Contains("clone"));

        Assert.Equal(
            [
                "-c",
                $"core.hooksPath={Path.Combine(operationDirectory, SourceAcquirer.HardeningDirectoryName, SourceAcquirer.HooksDirectoryName)}",
                "-c",
                "core.symlinks=false",
                "-c",
                "core.longpaths=true",
                "-c",
                "protocol.file.allow=never",
                "clone",
                "--depth=1",
                "--single-branch",
                "--no-tags",
                "--no-recurse-submodules",
                "--quiet",
                "--",
                PfUiUrl,
                Path.Combine(operationDirectory, SourceAcquirer.WorkingDirectoryName),
            ],
            clone.ArgumentList);

        Assert.False(clone.UseShellExecute);

        string hooks = Path.Combine(operationDirectory, SourceAcquirer.HardeningDirectoryName, SourceAcquirer.HooksDirectoryName);

        Assert.True(Directory.Exists(hooks));
        Assert.Empty(Directory.GetFileSystemEntries(hooks));

        string globalConfig = Path.Combine(operationDirectory, SourceAcquirer.HardeningDirectoryName, SourceAcquirer.EmptyConfigFileName);

        Assert.True(File.Exists(globalConfig));
        Assert.Equal(0, new FileInfo(globalConfig).Length);
        Assert.Equal(globalConfig, clone.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("0", clone.Environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public async Task AnExpectedFolderNameWinsOverTheToc()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        StagedSource staged = await Acquirer().StageGitAsync(
            GitRequest("op-expected", expectedFolderName: "pfUI-classic"),
            cancellationToken);

        Assert.Equal("pfUI-classic", staged.FolderName);
        Assert.Equal(["pfUI-classic/pfUI.lua", "pfUI-classic/pfUI.toc"], staged.Files.Select(file => file.RelativePath));
        Assert.True(File.Exists(Path.Combine(staged.StagingDirectory, "pfUI.toc")));
    }

    [Theory]
    [InlineData("file:///C:/tmp/x")]
    [InlineData("ssh://git@host/x")]
    [InlineData("git://host/x")]
    [InlineData("http://github.com/brues-code/pfUI")]
    [InlineData("https://user:secret@github.com/brues-code/pfUI")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task ARefusedSourceStartsNoProcess(string sourceUrl)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Acquirer().StageGitAsync(GitRequest("op-refused", url: sourceUrl), cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.InvalidSource, failure.Failure);
        Assert.Empty(_launcher.ArgumentLines);
        Assert.Empty(_launcher.StartInfos);
        Assert.False(Directory.Exists(_paths.StagingDirectory(GameId)));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/foo")]
    [InlineData("release-1.12.1")]
    public async Task ARealBranchNameReachesGit(string branch)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        StagedSource staged = await Acquirer().StageGitAsync(GitRequest("op-branch", branch: branch), cancellationToken);

        Assert.Equal("pfUI", staged.FolderName);
        Assert.Contains($"--branch {branch}", _launcher.ArgumentsOf("clone"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("-c")]
    [InlineData("main; rm -rf /")]
    [InlineData("main --upload-pack=evil")]
    public async Task ABranchThatIsNotABranchNameIsRefused(string branch)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Acquirer().StageGitAsync(GitRequest("op-branch-refused", branch: branch), cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.InvalidSource, failure.Failure);
        Assert.Empty(_launcher.ArgumentLines);
    }

    [Fact]
    public async Task AFailedCloneLeavesNoStagingDirectory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _launcher.OnRun = (_, _) => Task.FromResult(new GitProcessResult(128, string.Empty, "fatal: repository not found"));

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Acquirer().StageGitAsync(GitRequest("op-failed"), cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.GitFailed, failure.Failure);
        Assert.Contains("not found", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-failed")));
    }

    [Fact]
    public async Task ACloneWithoutACommitIsRefusedAndCleanedUp()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // An empty repository: git answers with an empty line, which is not a SHA.
        _launcher.OnRun = (invocation, _) => Task.FromResult(
            invocation.StartInfo.ArgumentList.Contains("rev-parse")
                ? new GitProcessResult(0, string.Empty, string.Empty)
                : CannedClone(invocation));

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Acquirer().StageGitAsync(GitRequest("op-empty"), cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.GitFailed, failure.Failure);
        Assert.Contains("not a commit SHA", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-empty")));
    }

    [Fact]
    public async Task CancellingACloneLeavesNoStagingDirectoryAndNoTraceInTheGameDirectory()
    {
        string before = HashDirectory(_gameDirectory);

        _launcher.OnRun = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new GitProcessResult(0, string.Empty, string.Empty);
        };

        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Acquirer().StageGitAsync(GitRequest("op-cancel"), cancellation.Token));

        Assert.Equal(SourceAcquisitionFailure.Cancelled, failure.Failure);
        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-cancel")));
        Assert.Equal(before, HashDirectory(_gameDirectory));
    }

    [Fact]
    public async Task ACloneThatOutlastsItsTimeoutIsStopped()
    {
        _launcher.OnRun = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new GitProcessResult(0, string.Empty, string.Empty);
        };

        SourceRequest request = GitRequest("op-timeout") with { TimeoutSeconds = 1 };

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Acquirer().StageGitAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(SourceAcquisitionFailure.Timeout, failure.Failure);
        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-timeout")));
    }

    [Fact]
    public async Task ARequestForAnotherDataRootIsRefusedBeforeAnythingIsCreated()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SourceRequest request = GitRequest("op-elsewhere") with { DataRoot = _gameDirectory };

        await Assert.ThrowsAsync<ArgumentException>(() => Acquirer().StageGitAsync(request, cancellationToken));

        Assert.Empty(_launcher.ArgumentLines);
        Assert.False(Directory.Exists(_paths.StagingDirectory(GameId)));
    }

    [Fact]
    public async Task StagingThenCleaningUpLeavesTheStagingAreaEmpty()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SourceAcquirer acquirer = Acquirer();

        StagedSource staged = await acquirer.StageGitAsync(GitRequest("op-cleanup"), cancellationToken);

        acquirer.Cleanup(staged);

        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-cleanup")));
        Assert.Empty(Directory.GetDirectories(_paths.StagingDirectory(GameId)));

        // Cleaning up twice is not an error.
        acquirer.Cleanup(staged);
    }

    [Fact]
    public void CleaningUpAlsoRemovesTheReadOnlyFilesGitLeavesBehind()
    {
        // git marks its object store read-only, and Windows refuses to delete a read-only file: without the
        // attribute-clearing retry this tree survives every cleanup and every sweep, and the leak is silent
        // because the failure is swallowed.
        string operation = Path.Combine(_paths.StagingDirectory(GameId), "op-readonly");
        string pack = Path.Combine(operation, "pfUI", ".git", "objects", "pack", "pack-0.pack");

        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        File.WriteAllText(pack, "pack");
        File.SetAttributes(pack, FileAttributes.ReadOnly);

        StagedSource staged = new()
        {
            GameId = GameId,
            OperationId = "op-readonly",
            StagingDirectory = Path.Combine(operation, "pfUI"),
            FolderName = "pfUI",
            Version = FakeGitProcessLauncher.Head,
            VersionKind = "sha",
            SourceType = ModSourceType.Github,
            SourceUrl = PfUiUrl,
            Files = [],
            HasGitDirectory = true,
        };

        Acquirer().Cleanup(staged);

        Assert.False(Directory.Exists(operation));
    }

    [Fact]
    public void SweepingRemovesOnlyWhatIsOlderThanTheThreshold()
    {
        string stale = CreateStagingDirectory("old-operation", TimeSpan.FromHours(-30));
        string fresh = CreateStagingDirectory("recent-operation", TimeSpan.FromHours(-1));
        string excluded = CreateStagingDirectory("running-operation", TimeSpan.FromHours(-30));

        int removed = Acquirer().SweepStaleStaging(GameId, TimeSpan.FromHours(24), excludeOperationId: "running-operation");

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(excluded));

        // Without an exclusion the second stale directory goes too, and a 30 h old one is the only kind that
        // qualifies: the sweep must never take the directory an operation is using right now.
        Assert.Equal(1, Acquirer().SweepStaleStaging(GameId, TimeSpan.FromHours(24), excludeOperationId: null));
        Assert.False(Directory.Exists(excluded));
        Assert.True(Directory.Exists(fresh));
        Assert.Equal(0, Acquirer().SweepStaleStaging(GameId, TimeSpan.FromHours(24), excludeOperationId: null));
    }

    [Fact]
    public void SweepingAGameWithNoStagingAreaIsNotAnError()
    {
        Assert.Equal(0, Acquirer().SweepStaleStaging("never-installed", TimeSpan.FromHours(24), excludeOperationId: null));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Acquirer().SweepStaleStaging(GameId, TimeSpan.FromHours(-1), excludeOperationId: null));
    }

    [Fact]
    public async Task AnArchiveIsStagedNamedFromItsTocAndFingerprinted()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using MemoryStream archive = ZipBuilder.Build(
            ("pfUI/pfUI.toc", "## Interface: 11200"),
            ("pfUI/libs/x.lua", "-- x"));
        string before = HashDirectory(_gameDirectory);

        StagedSource staged = await Acquirer().StageZipAsync(archive, "pfUI-v5.1.zip", ZipRequest("op-zip"), cancellationToken);

        Assert.Equal("pfUI", staged.FolderName);
        Assert.Equal("zip", staged.VersionKind);
        Assert.Equal(16, staged.Version.Length);
        Assert.All(staged.Version, character => Assert.True(char.IsAsciiHexDigit(character)));
        Assert.False(staged.HasGitDirectory);
        Assert.Equal(ModSourceType.Zip, staged.SourceType);
        Assert.Equal("pfUI-v5.1.zip", staged.SourceUrl);
        Assert.Equal(["pfUI/libs/x.lua", "pfUI/pfUI.toc"], staged.Files.Select(file => file.RelativePath));
        Assert.True(File.Exists(Path.Combine(staged.StagingDirectory, "pfUI.toc")));
        Assert.Equal(before, HashDirectory(_gameDirectory));
    }

    [Fact]
    public async Task TheSameArchiveStagedTwiceIsTheSameVersionAndDifferentContentIsNot()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SourceAcquirer acquirer = Acquirer();

        using MemoryStream archive = ZipBuilder.Build(("pfUI/pfUI.toc", "## Interface: 11200"));

        StagedSource first = await acquirer.StageZipAsync(archive, "pfUI.zip", ZipRequest("op-zip-1"), cancellationToken);
        archive.Position = 0;
        StagedSource second = await acquirer.StageZipAsync(archive, "pfUI.zip", ZipRequest("op-zip-2"), cancellationToken);

        Assert.Equal(first.Version, second.Version);

        using MemoryStream changed = ZipBuilder.Build(("pfUI/pfUI.toc", "## Interface: 11201"));
        StagedSource third = await acquirer.StageZipAsync(changed, "pfUI.zip", ZipRequest("op-zip-3"), cancellationToken);

        Assert.NotEqual(first.Version, third.Version);
    }

    [Fact]
    public async Task ARefusedArchiveLeavesNoStagingDirectory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using MemoryStream archive = ZipBuilder.Build(("pfUI/pfUI.toc", "## pfUI"), ("pfUI/../escape.txt", "payload"));

        await Assert.ThrowsAsync<PathValidationException>(
            () => Acquirer().StageZipAsync(archive, "evil.zip", ZipRequest("op-evil"), cancellationToken));

        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-evil")));
    }

    [Fact]
    public async Task AnArchiveRefusedForItsFolderNameLeavesNoStagingDirectory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using MemoryStream archive = ZipBuilder.Build(("pfUI/pfUI.toc", "## pfUI"));

        PathValidationException failure = await Assert.ThrowsAsync<PathValidationException>(
            () => Acquirer().StageZipAsync(
                archive,
                "pfUI.zip",
                ZipRequest("op-reserved", expectedFolderName: ".game-polish-old-pfUI"),
                cancellationToken));

        Assert.Equal(UnsafePathReason.ReservedPrefix, Assert.Single(failure.Failures).Reason);
        Assert.False(Directory.Exists(Path.Combine(_paths.StagingDirectory(GameId), "op-reserved")));
    }

    [Fact]
    public async Task StagingWritesNothingOutsideTheDataRoot()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string before = HashDirectory(_gameDirectory);

        using MemoryStream archive = ZipBuilder.Build(("pfUI.toc", "## pfUI"));
        await Acquirer().StageZipAsync(archive, "pfUI.zip", ZipRequest("op-outside"), cancellationToken);
        await Acquirer().StageGitAsync(GitRequest("op-outside-git"), cancellationToken);

        Assert.Equal(before, HashDirectory(_gameDirectory));

        // Everything the two operations wrote is under the game's staging directory in the data root.
        Assert.Equal(
            ["op-outside", "op-outside-git"],
            Directory.GetDirectories(_paths.StagingDirectory(GameId)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    private SourceAcquirer Acquirer(IFileOps? files = null) =>
        new(_paths, new PathValidator(), files ?? _files, new GitRunner(_launcher));

    private SourceRequest GitRequest(
        string operationId,
        string url = PfUiUrl,
        string? branch = null,
        string? expectedFolderName = null) => new()
        {
            GameId = GameId,
            SourceUrl = url,
            SourceType = ModSourceType.Github,
            Branch = branch,
            ExpectedFolderName = expectedFolderName,
            DataRoot = _paths.Root,
            OperationId = operationId,
        };

    private SourceRequest ZipRequest(string operationId, string? expectedFolderName = null) =>
        GitRequest(operationId, url: "pfUI-v5.1.zip", expectedFolderName: expectedFolderName) with
        {
            SourceType = ModSourceType.Zip,
        };

    /// <summary>Creates an operation directory whose last write is <paramref name="age"/> ago.</summary>
    /// <param name="name">The operation id, which is the directory name.</param>
    /// <param name="age">Negative means "in the past".</param>
    /// <remarks>
    /// The tree holds a read-only file on purpose: a real staging directory always does once git has cloned
    /// into it, and a sweep that cannot remove those reports a count it did not achieve.
    /// </remarks>
    private string CreateStagingDirectory(string name, TimeSpan age)
    {
        string directory = Path.Combine(_paths.StagingDirectory(GameId), name);
        string pack = Path.Combine(directory, "pfUI", ".git", "objects", "pack", "pack-0.pack");

        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        File.WriteAllText(pack, "pack");
        File.SetAttributes(pack, FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.Add(age));

        return directory;
    }

    /// <summary>The fake launcher's clone behaviour, for the one test that replaces <c>rev-parse</c> only.</summary>
    private static GitProcessResult CannedClone(GitInvocation invocation)
    {
        string destination = invocation.StartInfo.ArgumentList[^1];

        Directory.CreateDirectory(Path.Combine(destination, ".git"));
        File.WriteAllText(Path.Combine(destination, "pfUI.toc"), "## Interface: 11200");

        return new GitProcessResult(0, string.Empty, string.Empty);
    }

    /// <summary>A recursive content hash, used to prove a directory was never touched.</summary>
    private static string HashDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        return string.Join(
            '\n',
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => $"{Path.GetRelativePath(root, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));
    }
}
