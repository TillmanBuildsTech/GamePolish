using System.Diagnostics;

using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Sources;
using GamePolish.Core.Update;

namespace GamePolish.Core.Tests.Update;

/// <summary>
/// The production probe against the process seam: what it asks git for, and what it makes of the answer.
/// </summary>
/// <remarks>
/// The cheapness claim is asserted, not described: the recorded argv is one <c>ls-remote</c> and the
/// <c>--version</c> availability probe, with no <c>clone</c> and no <c>rev-parse</c> anywhere. That is the
/// property the Library row depends on - a check per row must not download a repository.
/// </remarks>
public class GitVersionProbeTests
{
    private const string Head = "40dd9dd6db1cb8d15d565ec5bdaf39e70175e611";
    private const string Tagged = "acab272ec0543d1eef01e2ea183663cfe39779e6";
    private const string BranchTip = "b4d1f0a3c2e5468790abcdef1234567890abcdef";
    private const string Source = "https://github.com/brues-code/pfUI";

    private static readonly DateTimeOffset Observed = new(2026, 10, 15, 21, 42, 3, 123, TimeSpan.Zero);

    [Fact]
    public async Task ItReadsTheRemoteHeadWithASingleLsRemote()
    {
        FakeGitProcessLauncher launcher = Answering($"{Head}\tHEAD\n");
        GitVersionProbe probe = Probe(launcher);

        VersionInfo version = await probe.GetRemoteVersionAsync(Source, branch: null, TestContext.Current.CancellationToken);

        Assert.Equal(Head, version.Value);
        Assert.Equal(GitVersionProbe.CommitKind, version.Kind);
        Assert.Equal(Head, version.Sha);
        Assert.Equal(Observed, version.ObservedAt);
        Assert.True(version.IsCommit);

        IReadOnlyList<string> arguments = ArgumentsOf(launcher, "ls-remote");
        string[] expected = ["ls-remote", "--quiet", "--", Source, "HEAD", "refs/tags/*"];

        Assert.Equal(expected, arguments);

        // One network command and the availability probe. No clone, no working tree, nothing to clean up.
        Assert.Equal(2, launcher.ArgumentLines.Count);
        Assert.DoesNotContain(launcher.ArgumentLines, line => line.Contains("clone", StringComparison.Ordinal));
        Assert.DoesNotContain(launcher.ArgumentLines, line => line.Contains("rev-parse", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tag is the label a user recognises, and the commit is what a comparison needs. An annotated tag
    /// arrives as two lines - the tag object and its peeled commit - and it is the peeled one that has to
    /// match the head.
    /// </summary>
    [Fact]
    public async Task ItPrefersTheTagThatSitsOnTheHeadsCommit()
    {
        FakeGitProcessLauncher launcher = Answering(
            $"{Tagged}\tHEAD\n"
            + $"82d887509eb5111d61424aa716516e4d3bde0d46\trefs/tags/v9.0.0\n"
            + $"ef94f9087683f615bf5c926b38fb8c981351247c\trefs/tags/v9.0.0^{{}}\n"
            + $"{Tagged}\trefs/tags/v9.0.10\n"
            + $"{Tagged}\trefs/tags/v9.0.10^{{}}\n");

        VersionInfo version = await Probe(launcher).GetRemoteVersionAsync(
            Source,
            branch: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("v9.0.10", version.Value);
        Assert.Equal(GitVersionProbe.TagKind, version.Kind);

        // The label changed; the commit did not, which is what keeps "up to date" working for a tagged head.
        Assert.Equal(Tagged, version.Sha);
    }

    [Fact]
    public async Task ItReadsTheRequestedBranchRatherThanTheRemoteHead()
    {
        FakeGitProcessLauncher launcher = Answering(
            $"{Head}\tHEAD\n"
            + $"{BranchTip}\trefs/heads/release-1.0\n");

        VersionInfo version = await Probe(launcher).GetRemoteVersionAsync(
            Source,
            "release-1.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(BranchTip, version.Value);
        Assert.Contains("refs/heads/release-1.0", ArgumentsOf(launcher, "ls-remote"));
    }

    [Fact]
    public async Task ItWrapsAMissingHeadIntoASourceFailure()
    {
        FakeGitProcessLauncher launcher = Answering(string.Empty);

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Probe(launcher).GetRemoteVersionAsync(Source, branch: null, TestContext.Current.CancellationToken));

        Assert.Equal(SourceAcquisitionFailure.Unspecified, failure.Failure);
        Assert.Contains("did not report a commit", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A URL is refused before git is asked to do anything: no process, no message carrying a credential.
    /// </summary>
    [Theory]
    [InlineData("http://github.com/brues-code/pfUI")]
    [InlineData("file:///C:/Users/me/pfUI")]
    [InlineData("https://user:token@github.com/brues-code/pfUI")]
    [InlineData("not a url")]
    public async Task ItRefusesAnUnusableSourceWithoutStartingGit(string sourceUrl)
    {
        FakeGitProcessLauncher launcher = Answering($"{Head}\tHEAD\n");

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Probe(launcher).GetRemoteVersionAsync(sourceUrl, branch: null, TestContext.Current.CancellationToken));

        Assert.Equal(SourceAcquisitionFailure.InvalidSource, failure.Failure);
        Assert.DoesNotContain(launcher.ArgumentLines, line => line.Contains("ls-remote", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-upload-pack=evil")]
    [InlineData("../heads/main")]
    [InlineData("main:refs/heads/other")]
    public async Task ItRefusesAnUnusableBranchWithoutStartingGit(string branch)
    {
        FakeGitProcessLauncher launcher = Answering($"{Head}\tHEAD\n");

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Probe(launcher).GetRemoteVersionAsync(Source, branch, TestContext.Current.CancellationToken));

        Assert.Equal(SourceAcquisitionFailure.InvalidSource, failure.Failure);
        Assert.DoesNotContain(launcher.ArgumentLines, line => line.Contains("ls-remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItHonoursCancellationBeforeGitStarts()
    {
        FakeGitProcessLauncher launcher = Answering($"{Head}\tHEAD\n");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Probe(launcher).GetRemoteVersionAsync(Source, branch: null, cancelled.Token));

        Assert.DoesNotContain(launcher.ArgumentLines, line => line.Contains("ls-remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItReportsAMissingGitAsAnUnavailableGit()
    {
        FakeGitProcessLauncher launcher = new() { GitInstalled = false };

        await Assert.ThrowsAsync<GitUnavailableException>(
            () => Probe(launcher).GetRemoteVersionAsync(Source, branch: null, TestContext.Current.CancellationToken));
    }

    private static GitVersionProbe Probe(FakeGitProcessLauncher launcher) =>
        new(new GitRunner(launcher), new FixedTimeProvider(Observed));

    private static FakeGitProcessLauncher Answering(string output) => new()
    {
        OnRun = (_, _) => Task.FromResult(new GitProcessResult(0, output, string.Empty)),
    };

    private static IReadOnlyList<string> ArgumentsOf(FakeGitProcessLauncher launcher, string subcommand)
    {
        ProcessStartInfo invocation = launcher.StartInfos.Single(
            info => info.ArgumentList.Contains(subcommand));

        return [.. invocation.ArgumentList];
    }
}
