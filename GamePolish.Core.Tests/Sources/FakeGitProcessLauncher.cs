using System.ComponentModel;
using System.Diagnostics;

using GamePolish.Core.Sources;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// The process seam, faked: records every git call as an argv line and an environment, and can be told to
/// answer with canned output, fail, hang, or claim git is not installed at all.
/// </summary>
/// <remarks>
/// This is what makes the hardening list an assertion rather than a reading exercise: a test compares the
/// recorded argv with the flags the work order names, and nothing has to reach GitHub for it. The default
/// clone behaviour writes a small plausible repository (a <c>.git</c> directory with a HEAD, a
/// <c>.toc</c> and a Lua file) so the staging code above it has something real to walk, rename and
/// validate.
/// </remarks>
internal sealed class FakeGitProcessLauncher : IGitProcessLauncher
{
    /// <summary>The commit the fake repository reports, and a valid 40-character SHA.</summary>
    public const string Head = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";

    /// <summary>Every call, as one argv line, in the order git was asked.</summary>
    public List<string> ArgumentLines { get; } = [];

    /// <summary>Every call's process configuration, for the environment assertions.</summary>
    public List<ProcessStartInfo> StartInfos { get; } = [];

    /// <summary>False makes every call throw the "executable not found" failure Windows gives.</summary>
    public bool GitInstalled { get; set; } = true;

    /// <summary>Replaces the canned answer for any call that is not the <c>--version</c> probe.</summary>
    public Func<GitInvocation, CancellationToken, Task<GitProcessResult>>? OnRun { get; set; }

    /// <summary>The clone destination recorded by the last successful clone, for tests that need to look at it.</summary>
    public string? LastCloneDestination { get; private set; }

    /// <inheritdoc/>
    public Task<GitProcessResult> RunAsync(GitInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        ArgumentLines.Add(string.Join(' ', invocation.StartInfo.ArgumentList));
        StartInfos.Add(invocation.StartInfo);

        if (!GitInstalled)
        {
            throw new Win32Exception(2, "The system cannot find the file specified");
        }

        if (contains("--version"))
        {
            return Task.FromResult(new GitProcessResult(0, "git version 2.53.0", string.Empty));
        }

        if (OnRun is not null)
        {
            return OnRun(invocation, cancellationToken);
        }

        return Task.FromResult(Canned(invocation));

        bool contains(string argument) => invocation.StartInfo.ArgumentList.Contains(argument);
    }

    /// <summary>The argv of the first call whose subcommand is <paramref name="subcommand"/>.</summary>
    /// <param name="subcommand">E.g. <c>clone</c>.</param>
    public string ArgumentsOf(string subcommand) =>
        ArgumentLines.Single(line => line.Split(' ').Contains(subcommand));

    /// <summary>
    /// A clone that writes a repository, and a <c>rev-parse</c> that answers with <see cref="Head"/>.
    /// </summary>
    private GitProcessResult Canned(GitInvocation invocation)
    {
        IReadOnlyList<string> arguments = invocation.StartInfo.ArgumentList;
        string subcommand = Subcommand(arguments);

        if (subcommand == "clone")
        {
            string destination = arguments[^1];

            Directory.CreateDirectory(Path.Combine(destination, ".git"));
            File.WriteAllText(Path.Combine(destination, ".git", "HEAD"), "ref: refs/heads/main");
            File.WriteAllText(Path.Combine(destination, "pfUI.toc"), "## Interface: 11200\n## Title: pfUI");
            File.WriteAllText(Path.Combine(destination, "pfUI.lua"), "-- pfUI");

            LastCloneDestination = destination;

            return new GitProcessResult(0, string.Empty, string.Empty);
        }

        return subcommand == "rev-parse"
            ? new GitProcessResult(0, Head + Environment.NewLine, string.Empty)
            : new GitProcessResult(0, string.Empty, string.Empty);
    }

    /// <summary>The first argument that is not an option and not an option's value.</summary>
    private static string Subcommand(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == "-c")
            {
                index++;

                continue;
            }

            if (!arguments[index].StartsWith('-'))
            {
                return arguments[index];
            }
        }

        return string.Empty;
    }
}
