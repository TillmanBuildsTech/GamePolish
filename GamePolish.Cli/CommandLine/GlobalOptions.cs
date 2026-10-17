namespace GamePolish.Cli.CommandLine;

/// <summary>
/// The global options, which may appear anywhere on the command line and belong to no single command.
/// </summary>
/// <remarks>
/// They are extracted before the command is parsed because they are the harness's own settings - where the data
/// root is, how output is shaped, how loud the log is - and a command must never see them as its arguments.
/// </remarks>
public sealed record GlobalOptions
{
    /// <summary>
    /// The <c>--data-root</c> value, or <see langword="null"/> to let <c>GamePolishPaths.Resolve</c> fall
    /// through to <c>GAMEPOLISH_DATA_ROOT</c> and then to the default.
    /// </summary>
    public string? DataRoot { get; init; }

    /// <summary>True when <c>--json</c> was given: print the <c>OperationResult</c> as JSON on stdout, nothing else.</summary>
    public bool Json { get; init; }

    /// <summary>The <c>--log-level</c> value, or <see langword="null"/> to let the settings and environment decide.</summary>
    public string? LogLevel { get; init; }

    /// <summary>True when <c>--help</c> or <c>-h</c> was given.</summary>
    public bool Help { get; init; }

    /// <summary>
    /// True when <c>--log-level debug</c> was given, which is what makes a failure print its technical detail
    /// (including the stack) instead of only the one-line message.
    /// </summary>
    public bool Debug => string.Equals(LogLevel, "debug", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A command line that parsed: the global options plus, unless <c>--help</c> was given alone, the command.</summary>
/// <param name="Global">The harness-wide options.</param>
/// <param name="Command">The command, or <see langword="null"/> when the line only asked for help.</param>
public sealed record ParsedCommandLine(GlobalOptions Global, ParsedCommand? Command);
