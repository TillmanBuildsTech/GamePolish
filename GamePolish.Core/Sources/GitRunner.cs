using System.ComponentModel;
using System.Diagnostics;

namespace GamePolish.Core.Sources;

/// <summary>The hardened configuration every git invocation runs under.</summary>
/// <param name="HooksDirectory">
/// An existing, empty directory for <c>core.hooksPath</c>. Pointing it at nothing is what makes "a
/// repository can never run code on this machine" true by construction rather than by hope (invariant #2):
/// git finds no hook to execute, whatever the repository ships.
/// </param>
/// <param name="GlobalConfigFile">
/// An existing, empty file for <c>GIT_CONFIG_GLOBAL</c>. A real file, not <c>/dev/null</c> - that path does
/// not exist on Windows, and the alternative (inheriting the user's git config) lets a hostile
/// <c>url.*.insteadOf</c> or <c>core.fsmonitor</c> rewrite what this process believes it is cloning.
/// </param>
public sealed record GitHardening(string HooksDirectory, string GlobalConfigFile);

/// <summary>One git invocation, built and ready to start.</summary>
/// <param name="StartInfo">The process configuration: executable, argv and environment.</param>
/// <param name="Timeout">How long it may take, for the launcher's own bookkeeping.</param>
public sealed record GitInvocation(ProcessStartInfo StartInfo, TimeSpan Timeout);

/// <summary>What a finished git process produced.</summary>
/// <param name="ExitCode">The process's exit code.</param>
/// <param name="StandardOutput">Everything git wrote to stdout, trimmed.</param>
/// <param name="StandardError">Everything git wrote to stderr, trimmed.</param>
public sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when git exited zero.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The process seam: the thing <see cref="GitRunner"/> uses to start a program, so that tests can assert
/// the exact argv and environment of every git call without a network or a repository.
/// </summary>
/// <remarks>
/// Implementations must not capture a synchronization context: <see cref="GitRunner.IsAvailable"/> waits
/// for a probe synchronously, and a launcher that resumed on a UI thread would deadlock the app at startup.
/// </remarks>
public interface IGitProcessLauncher
{
    /// <summary>Starts the configured process, waits for it, and returns what it said.</summary>
    /// <param name="invocation">The process to start.</param>
    /// <param name="cancellationToken">Cancelling kills the process tree before this returns.</param>
    Task<GitProcessResult> RunAsync(GitInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// The real launcher: the only code in Core that starts a process, and the only place a
/// <see cref="ProcessStartInfo"/> is turned into a running program.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseShellExecute = false</c> plus <c>ArgumentList</c> everywhere upstream means no shell is involved
/// and no argument is ever re-parsed: a URL cannot smuggle a second command through a space or a
/// semicolon (invariant #2).
/// </para>
/// <para>
/// Output is read asynchronously on both streams. Reading one of them synchronously before the other is
/// the classic way to deadlock on a full pipe, and git writes plenty to stderr on a bad clone.
/// </para>
/// </remarks>
public sealed class GitProcessLauncher : IGitProcessLauncher
{
    /// <summary>How long to wait for the output readers after the process itself has exited.</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public async Task<GitProcessResult> RunAsync(GitInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        using Process process = new() { StartInfo = invocation.StartInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{invocation.StartInfo.FileName}'.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using (cancellationToken.Register(() => KillProcessTree(process)))
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);

                throw;
            }
        }

        // The process has exited, but a reader can still be holding the tail of its output.
        await Task.WhenAny(
            Task.WhenAll(standardOutput, standardError),
            Task.Delay(OutputDrainTimeout, CancellationToken.None)).ConfigureAwait(false);

        return new GitProcessResult(
            process.ExitCode,
            ReadDrained(standardOutput),
            ReadDrained(standardError));
    }

    /// <summary>Reads a reader's text when it finished, and nothing when it did not.</summary>
    private static string ReadDrained(Task<string> reader) =>
        reader.IsCompletedSuccessfully ? reader.Result.TrimEnd() : string.Empty;

    /// <summary>Kills git and anything it started. Best-effort: this runs on a cancellation callback.</summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process was never started, or it exited between the two calls. Either way: nothing to kill.
        }
        catch (Win32Exception)
        {
            // The kill itself failed - it was already gone, or this process cannot signal it.
        }
        catch (NotSupportedException)
        {
            // A remote process cannot be killed; git never runs remotely here.
        }
    }
}

/// <summary>
/// Runs git, with the hardening list applied to every single call, and turns its exit code into either a
/// result or a <see cref="SourceAcquisitionException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The hardening is not decoration; each item closes a specific hole:
/// </para>
/// <list type="bullet">
/// <item><description><c>core.hooksPath</c> at an empty directory: a hook can never run (invariant #2).</description></item>
/// <item><description><c>core.symlinks=false</c>: a symlink in a repository is checked out as a plain file
/// holding its target path, so a clone cannot plant an escaping link (invariant #3).</description></item>
/// <item><description><c>protocol.file.allow=never</c>: no <c>file://</c> and no local path, so a repository
/// cannot make git read this machine's disk through a submodule or a redirect.</description></item>
/// <item><description><c>--no-recurse-submodules</c>: a submodule is a second repository, and this product
/// installs one addon.</description></item>
/// <item><description><c>GIT_TERMINAL_PROMPT=0</c> and an empty <c>GIT_ASKPASS</c>: git can never block on a
/// credential prompt it has no way to answer - a silent hang inside a background install is worse than a
/// failed clone.</description></item>
/// <item><description><c>GIT_LFS_SKIP_SMUDGE=1</c>: no LFS payload is fetched, which is both a download this
/// product did not ask for and a filter whose output is not the mod.</description></item>
/// <item><description><c>GIT_CONFIG_NOSYSTEM</c> plus an empty <c>GIT_CONFIG_GLOBAL</c>: the machine's git
/// configuration does not participate in an install.</description></item>
/// </list>
/// <para>
/// git itself is probed exactly once, lazily, and the answer is cached: a missing executable is a user
/// problem with a user message, and it should be discovered before a staging directory is created rather
/// than as a <see cref="Win32Exception"/> from the middle of an operation.
/// </para>
/// </remarks>
public sealed class GitRunner
{
    /// <summary>The executable looked up on <c>PATH</c>.</summary>
    public const string DefaultExecutable = "git";

    /// <summary>How long the availability probe may take.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Longest slice of git's own output kept in a message.</summary>
    private const int MaximumReportedOutputLength = 600;

    private readonly IGitProcessLauncher _launcher;
    private readonly string _executable;
    private readonly object _probeLock = new();

    private bool? _available;

    /// <summary>Initializes the runner with the real process launcher and <c>git</c> from <c>PATH</c>.</summary>
    public GitRunner()
        : this(new GitProcessLauncher(), DefaultExecutable)
    {
    }

    /// <summary>Initializes the runner with injected collaborators.</summary>
    /// <param name="launcher">The process seam.</param>
    /// <param name="executable">The executable to run; <c>git</c> unless a caller knows better.</param>
    public GitRunner(IGitProcessLauncher launcher, string executable = DefaultExecutable)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        _launcher = launcher;
        _executable = executable;
    }

    /// <summary>
    /// True when a <c>git</c> executable answers. Probed once and cached for the life of the runner, so a
    /// missing git costs one failed process start, not one per install attempt.
    /// </summary>
    public bool IsAvailable()
    {
        lock (_probeLock)
        {
            if (_available is bool known)
            {
                return known;
            }

            _available = Probe();

            return _available.Value;
        }
    }

    /// <summary>Runs one git command and returns what it produced.</summary>
    /// <param name="command">The git arguments, without the executable and without the hardening.</param>
    /// <param name="workingDirectory">The directory to run in, or <see langword="null"/> for the current one.</param>
    /// <param name="hardening">The hardening configuration, or <see langword="null"/> for a read-only call.</param>
    /// <param name="timeout">How long the call may take; on expiry the process tree is killed.</param>
    /// <param name="cancellationToken">Checked before starting and passed to the launcher.</param>
    /// <exception cref="GitUnavailableException">git is not installed.</exception>
    /// <exception cref="SourceAcquisitionException">git failed, timed out, or the caller cancelled.</exception>
    public async Task<GitProcessResult> RunAsync(
        IReadOnlyList<string> command,
        string? workingDirectory,
        GitHardening? hardening,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Count == 0)
        {
            throw new ArgumentException("A git command needs at least a subcommand.", nameof(command));
        }

        if (!IsAvailable())
        {
            throw new GitUnavailableException();
        }

        ProcessStartInfo startInfo = BuildStartInfo(command, workingDirectory, hardening);
        GitProcessResult result;

        using (CancellationTokenSource timeoutSource = new(timeout))
        using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
        {
            try
            {
                result = await _launcher.RunAsync(new GitInvocation(startInfo, timeout), linked.Token).ConfigureAwait(false);
            }
            catch (Win32Exception exception)
            {
                // It answered a moment ago and now it is gone (uninstalled, or a broken PATH entry).
                _available = false;

                throw new GitUnavailableException(GitUnavailableException.UserMessage, exception);
            }
            catch (OperationCanceledException exception)
            {
                bool cancelled = cancellationToken.IsCancellationRequested;

                throw new SourceAcquisitionException(
                    cancelled ? SourceAcquisitionFailure.Cancelled : SourceAcquisitionFailure.Timeout,
                    cancelled
                        ? $"git {command[0]} was cancelled."
                        : $"git {command[0]} did not finish within {timeout.TotalSeconds:N0} seconds and was stopped.",
                    exception);
            }
        }

        if (!result.Succeeded)
        {
            throw new SourceAcquisitionException(SourceAcquisitionFailure.GitFailed, Describe(command, result));
        }

        return result;
    }

    /// <summary>Runs <c>git --version</c> once, to answer whether git exists at all.</summary>
    private bool Probe()
    {
        try
        {
            GitProcessResult probe = _launcher
                .RunAsync(
                    new GitInvocation(BuildStartInfo(["--version"], null, null), ProbeTimeout),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return probe.Succeeded;
        }
        catch (Win32Exception)
        {
            // The executable is not on PATH. This is the ordinary "git is not installed" case.
            return false;
        }
        catch (InvalidOperationException)
        {
            // It exists but could not be started (a permission problem, or a broken binary).
            return false;
        }
    }

    /// <summary>Builds the process configuration: hardened argv first, then the command, then the environment.</summary>
    private ProcessStartInfo BuildStartInfo(
        IReadOnlyList<string> command,
        string? workingDirectory,
        GitHardening? hardening)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (hardening is not null)
        {
            foreach (string configuration in HardenedConfiguration(hardening))
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(configuration);
            }
        }

        foreach (string argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_ASKPASS"] = string.Empty;
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        if (hardening is not null)
        {
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = hardening.GlobalConfigFile;
        }

        return startInfo;
    }

    /// <summary>The <c>-c</c> settings, in a fixed order so a recorded argv is stable.</summary>
    private static IReadOnlyList<string> HardenedConfiguration(GitHardening hardening) =>
    [
        $"core.hooksPath={hardening.HooksDirectory}",
        "core.symlinks=false",
        "core.longpaths=true",
        "protocol.file.allow=never",
    ];

    /// <summary>Turns a failed run into something a user can act on, without pasting the whole log.</summary>
    private static string Describe(IReadOnlyList<string> command, GitProcessResult result)
    {
        string said = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;

        if (said.Length > MaximumReportedOutputLength)
        {
            said = said[..MaximumReportedOutputLength] + "...";
        }

        return $"git {command[0]} exited with code {result.ExitCode}: {said}";
    }
}
