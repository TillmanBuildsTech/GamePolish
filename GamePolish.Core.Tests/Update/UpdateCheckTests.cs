using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Install;
using GamePolish.Core.Tests.Storage;
using GamePolish.Core.Update;

namespace GamePolish.Core.Tests.Update;

/// <summary>
/// The check contract: four states, no mutation, and no network for a mod that has no upstream.
/// </summary>
/// <remarks>
/// Criterion 1 (the three states) and criterion 2 (the check leaves everything byte-identical) live here.
/// The probe is the seam: a double that records its calls makes "no network access was attempted" a fact
/// rather than a reading of the code.
/// </remarks>
[Collection(StorageCollection.Name)]
public class UpdateCheckTests
{
    private readonly StorageFixture _fixture;

    public UpdateCheckTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CheckAsyncReportsAnUpdateWhenTheRemoteCommitDiffers()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.Probe.Next = new VersionInfo(UpdateScenario.NewVersion, GitVersionProbe.CommitKind, UpdateScenario.Start)
        {
            Sha = UpdateScenario.NewVersion,
        };

        UpdateAvailability availability = await scenario.Pipeline().CheckAsync(
            UpdateScenario.GameId,
            "pfui",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpdateAvailable, availability.State);
        Assert.Equal(UpdateScenario.OldVersion, availability.InstalledVersion);
        Assert.Equal(UpdateScenario.NewVersion, availability.LatestVersion);
        Assert.True(availability.HasUpdate);
        Assert.Null(availability.Reason);
        Assert.Equal("pfui", availability.ModId);
        Assert.Single(scenario.Probe.Requests);
        Assert.Equal("https://github.com/brues-code/pfUI", scenario.Probe.Requests[0].SourceUrl);
    }

    [Fact]
    public async Task CheckAsyncReportsUpToDateWhenTheCommitMatches()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.Probe.Next = new VersionInfo(UpdateScenario.OldVersion, GitVersionProbe.CommitKind, UpdateScenario.Start)
        {
            Sha = UpdateScenario.OldVersion,
        };

        UpdateAvailability availability = await scenario.Pipeline().CheckAsync(
            UpdateScenario.GameId,
            "pfui",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, availability.State);
        Assert.Equal(UpdateScenario.OldVersion, availability.LatestVersion);
        Assert.False(availability.HasUpdate);
        Assert.False(availability.IsIndeterminate);
    }

    /// <summary>
    /// A tag that sits on the head is the reason <see cref="VersionInfo.Sha"/> exists: the label the probe
    /// returns is the tag, but the comparison has to use the commit, or every tagged release would look like
    /// an update forever.
    /// </summary>
    [Fact]
    public async Task CheckAsyncComparesCommitsEvenWhenTheProbeLabelsTheHeadWithATag()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.Probe.Next = new VersionInfo("v5.2", GitVersionProbe.TagKind, UpdateScenario.Start)
        {
            Sha = UpdateScenario.OldVersion,
        };

        UpdateAvailability availability = await scenario.Pipeline().CheckAsync(
            UpdateScenario.GameId,
            "pfui",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, availability.State);
        Assert.Equal(UpdateScenario.OldVersion, availability.LatestVersion);
    }

    /// <summary>
    /// Criterion 1's third case: an archive has no upstream, so the probe must not be asked at all. The double
    /// fails if it is invoked, and <see cref="FakeVersionProbe.Calls"/> is asserted to be zero as well.
    /// </summary>
    [Fact]
    public async Task CheckAsyncIsUnknownForAnArchiveSourcedModWithoutAskingTheProbe()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install(sourceType: ModSourceType.Zip);
        scenario.Probe.FailWith = new InvalidOperationException("The probe must not be called for an archive.");

        UpdateAvailability availability = await scenario.Pipeline().CheckAsync(
            UpdateScenario.GameId,
            "pfui",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Unknown, availability.State);
        Assert.Equal(0, scenario.Probe.Calls);
        Assert.True(availability.IsIndeterminate);
        Assert.Null(availability.LatestVersion);
        Assert.Contains("archive", availability.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncIsUnknownForAModThatIsNotInstalled()
    {
        UpdateScenario scenario = new(_fixture);

        UpdateAvailability availability = await scenario.Pipeline().CheckAsync(
            UpdateScenario.GameId,
            "pfui",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Unknown, availability.State);
        Assert.Equal(0, scenario.Probe.Calls);
        Assert.Contains("not installed", availability.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncReportsAFailedCheckInsteadOfThrowing()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.Probe.FailWith = new SourceAcquisitionException(
            SourceAcquisitionFailure.GitFailed,
            "git ls-remote exited with code 128: repository not found");

        UpdateAvailability availability = await scenario.Pipeline().CheckAsync(
            UpdateScenario.GameId,
            "pfui",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.CheckFailed, availability.State);
        Assert.True(availability.IsIndeterminate);
        Assert.Null(availability.LatestVersion);

        // The mapped code is what the log drawer and the retry button key on, so it is in the reason.
        Assert.Contains(nameof(OperationErrorCode.GitFailed), availability.Reason, StringComparison.Ordinal);
        Assert.Contains("128", availability.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Criterion 2. The check runs against a data root full of real state - a manifest, an installed folder -
    /// and leaves all of it byte-identical: no snapshot, no manifest write, no staging directory.
    /// </summary>
    [Fact]
    public async Task CheckAsyncLeavesTheDataRootAndTheGameFolderByteIdentical()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();
        scenario.StageUpdate();
        scenario.Probe.Next = new VersionInfo(UpdateScenario.NewVersion, GitVersionProbe.CommitKind, UpdateScenario.Start)
        {
            Sha = UpdateScenario.NewVersion,
        };

        string dataRootBefore = TreeHash.Of(scenario.Paths.Root);
        string addonRootBefore = TreeHash.Of(scenario.AddonRoot);
        byte[] manifestBefore = await File.ReadAllBytesAsync(scenario.ManifestFile, TestContext.Current.CancellationToken);

        await scenario.Pipeline().CheckAsync(UpdateScenario.GameId, "pfui", TestContext.Current.CancellationToken);

        Assert.Equal(dataRootBefore, TreeHash.Of(scenario.Paths.Root));
        Assert.Equal(addonRootBefore, TreeHash.Of(scenario.AddonRoot));
        Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(scenario.ManifestFile, TestContext.Current.CancellationToken));
        Assert.Empty(scenario.Backups());

        // The staging tree this scenario prepared is still exactly one directory: the check did not stage.
        Assert.Equal(0, scenario.Sources.StageCalls);
        Assert.Single(Directory.EnumerateDirectories(scenario.StagingRoot));
    }

    [Fact]
    public async Task CheckAsyncHonoursCancellationWithoutAskingTheProbe()
    {
        UpdateScenario scenario = new(_fixture);
        scenario.Install();

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scenario.Pipeline().CheckAsync(UpdateScenario.GameId, "pfui", cancelled.Token));

        Assert.Equal(0, scenario.Probe.Calls);
    }
}
