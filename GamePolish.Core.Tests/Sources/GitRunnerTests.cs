using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// Covers the git seam (.todo/207, acceptance criteria 2 and 3): the exact argv and environment every call
/// runs under, the probe that decides whether git exists at all, and the mapping from git's exit code,
/// timeout and cancellation onto the exception the pipeline reads.
/// </summary>
[Collection(StorageCollection.Name)]
public class GitRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly FakeGitProcessLauncher _launcher = new();
    private readonly GitHardening _hardening;
    private readonly string _configFile;

    public GitRunnerTests(StorageFixture fixture)
    {
        string hooks = fixture.NewSubdirectory($"hooks-{Guid.NewGuid():N}");

        _configFile = Path.Combine(fixture.NewSubdirectory($"config-{Guid.NewGuid():N}"), "gitconfig-empty");
        File.WriteAllText(_configFile, string.Empty);
        _hardening = new GitHardening(hooks, _configFile);
    }

    [Fact]
    public async Task TheHardeningIsInFrontOfEveryCommand()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Runner().RunAsync(["rev-parse", "HEAD"], null, _hardening, Generous, cancellationToken);

        Assert.Equal(
            [
                "-c",
                $"core.hooksPath={_hardening.HooksDirectory}",
                "-c",
                "core.symlinks=false",
                "-c",
                "core.longpaths=true",
                "-c",
                "protocol.file.allow=never",
                "rev-parse",
                "HEAD",
            ],
            StartInfoOf("rev-parse").ArgumentList);
    }

    [Fact]
    public async Task TheChildCannotPromptAndCannotReadTheMachinesGitConfiguration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Runner().RunAsync(["rev-parse", "HEAD"], null, _hardening, Generous, cancellationToken);

        System.Diagnostics.ProcessStartInfo startInfo = StartInfoOf("rev-parse");

        Assert.Equal("git", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute, "a shell would re-parse the arguments, which is how a URL smuggles a second command.");
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(string.Empty, startInfo.Environment["GIT_ASKPASS"]);
        Assert.Equal("1", startInfo.Environment["GIT_LFS_SKIP_SMUDGE"]);
        Assert.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(_configFile, startInfo.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.True(File.Exists(_configFile));
        Assert.Equal(0, new FileInfo(_configFile).Length);
    }

    [Fact]
    public async Task AReadOnlyCallRunsWithoutTheHardening()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Runner().RunAsync(["rev-parse", "HEAD"], null, hardening: null, Generous, cancellationToken);

        System.Diagnostics.ProcessStartInfo startInfo = _launcher.StartInfos[^1];

        Assert.Equal(["rev-parse", "HEAD"], startInfo.ArgumentList);
        Assert.False(
            startInfo.Environment.TryGetValue("GIT_CONFIG_GLOBAL", out string? globalConfig) && globalConfig == _configFile,
            "a read-only call must not point git at the operation's hardening file.");
    }

    [Fact]
    public async Task AMissingGitIsReportedOnceAndThenRemembered()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GitRunner git = Runner();

        _launcher.GitInstalled = false;

        Assert.False(git.IsAvailable());
        Assert.Single(_launcher.ArgumentLines);

        GitUnavailableException failure = await Assert.ThrowsAsync<GitUnavailableException>(
            () => git.RunAsync(["clone"], null, _hardening, Generous, cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.GitUnavailable, failure.Failure);
        Assert.Contains("Git is required", failure.Message, StringComparison.Ordinal);

        // The probe was the only process this runner ever attempted.
        Assert.Single(_launcher.ArgumentLines);
    }

    [Fact]
    public async Task GitIsProbedOncePerRunner()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GitRunner git = Runner();

        await git.RunAsync(["rev-parse", "HEAD"], null, _hardening, Generous, cancellationToken);
        await git.RunAsync(["rev-parse", "HEAD"], null, _hardening, Generous, cancellationToken);

        Assert.Equal(3, _launcher.ArgumentLines.Count);
    }

    [Fact]
    public async Task ANonZeroExitCarriesWhatGitSaid()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _launcher.OnRun = (_, _) => Task.FromResult(new GitProcessResult(128, string.Empty, "fatal: repository 'https://github.com/x/y' not found"));

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Runner().RunAsync(["clone"], null, _hardening, Generous, cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.GitFailed, failure.Failure);
        Assert.Contains("exited with code 128", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not found", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsReportedAsATimeoutAndNotAsACancellation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _launcher.OnRun = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new GitProcessResult(0, string.Empty, string.Empty);
        };

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Runner().RunAsync(["clone"], null, _hardening, TimeSpan.FromMilliseconds(250), cancellationToken));

        Assert.Equal(SourceAcquisitionFailure.Timeout, failure.Failure);
        Assert.Contains("did not finish within", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCallIsReportedAsACancellation()
    {
        _launcher.OnRun = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new GitProcessResult(0, string.Empty, string.Empty);
        };

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        SourceAcquisitionException failure = await Assert.ThrowsAsync<SourceAcquisitionException>(
            () => Runner().RunAsync(["clone"], null, _hardening, Generous, cancellation.Token));

        Assert.Equal(SourceAcquisitionFailure.Cancelled, failure.Failure);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task AnEmptyCommandIsARefusedArgument()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(
            () => Runner().RunAsync([], null, _hardening, Generous, cancellationToken));
    }

    private GitRunner Runner() => new(_launcher);

    private System.Diagnostics.ProcessStartInfo StartInfoOf(string subcommand) =>
        _launcher.StartInfos.Single(
            startInfo => startInfo.ArgumentList.Contains(subcommand) && !startInfo.ArgumentList.Contains("--version"));
}
