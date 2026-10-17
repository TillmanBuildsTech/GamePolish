namespace GamePolish.Cli.CommandLine;

/// <summary>
/// Turns the process's arguments into a <see cref="ParsedCommandLine"/>, or refuses them with a
/// <see cref="CliUsageException"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two passes, because the two kinds of argument are different kinds of thing. The first pulls out the global
/// options, which may appear anywhere and are the harness's own settings; the second matches the remaining words
/// against <see cref="CommandCatalog"/> and validates the command's arguments against its
/// <see cref="CommandSpec"/>. Every refusal happens here, before the data root is resolved and before Core is
/// constructed, which is what makes <c>2</c> mean "nothing was attempted".
/// </para>
/// <para>
/// No defaults are invented. A missing required argument is refused rather than filled in from the environment,
/// because a smoke harness whose runs differ by ambient state produces transcripts nobody can reproduce.
/// </para>
/// </remarks>
public static class CommandParser
{
    /// <summary>The value <c>--log-level</c> accepts. Anything else is a usage error, not a silent fallback.</summary>
    private static readonly string[] LogLevels = ["debug", "information", "warn", "error"];

    /// <summary>Parses a full argument vector (without the program name).</summary>
    /// <param name="args">The process arguments.</param>
    /// <exception cref="CliUsageException">The command line is not one this tool understands.</exception>
    public static ParsedCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        (GlobalOptions global, List<string> remaining) = ExtractGlobal(args);

        if (remaining.Count == 0)
        {
            if (global.Help)
            {
                return new ParsedCommandLine(global, null);
            }

            throw new CliUsageException("No command was given. Run with --help for the command list.");
        }

        (CommandSpec spec, int consumed) = MatchCommand(remaining);

        ParsedCommand command = ParseArguments(spec, remaining, consumed);

        return new ParsedCommandLine(global, command);
    }

    /// <summary>
    /// Pulls the global options out of the vector, leaving the command and its own arguments in order.
    /// </summary>
    /// <remarks>
    /// Unknown <c>--options</c> are left in place on purpose: they belong to the command, and refusing them here
    /// would mean this pass had to know every command's options - the duplication the catalog exists to avoid.
    /// </remarks>
    private static (GlobalOptions Global, List<string> Remaining) ExtractGlobal(IReadOnlyList<string> args)
    {
        string? dataRoot = null;
        string? logLevel = null;
        bool json = false;
        bool help = false;
        List<string> remaining = [];

        for (int index = 0; index < args.Count; index++)
        {
            string token = args[index];

            switch (token)
            {
                case "--help" or "-h":
                    help = true;

                    break;

                case "--json":
                    if (json)
                    {
                        throw new CliUsageException("--json was given twice.");
                    }

                    json = true;

                    break;

                case "--data-root":
                    if (dataRoot is not null)
                    {
                        throw new CliUsageException("--data-root was given twice.");
                    }

                    dataRoot = NextValue(args, ref index, "--data-root");

                    break;

                case "--log-level":
                    if (logLevel is not null)
                    {
                        throw new CliUsageException("--log-level was given twice.");
                    }

                    logLevel = NextValue(args, ref index, "--log-level");
                    if (!LogLevels.Contains(logLevel, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new CliUsageException(
                            $"--log-level '{logLevel}' is not one of {string.Join(", ", LogLevels)}.");
                    }

                    logLevel = logLevel.ToLowerInvariant();

                    break;

                default:
                    remaining.Add(token);

                    break;
            }
        }

        return (new GlobalOptions { DataRoot = dataRoot, Json = json, LogLevel = logLevel, Help = help }, remaining);
    }

    /// <summary>
    /// Finds the command the leading words name, returning it and how many words it consumed.
    /// </summary>
    /// <remarks>
    /// <c>backup</c> alone is not a command but a namespace: it is refused with the subcommands it does have,
    /// because "unknown command backup" would hide the one thing the user needs to know.
    /// </remarks>
    private static (CommandSpec Spec, int Consumed) MatchCommand(IReadOnlyList<string> remaining)
    {
        string first = remaining[0];

        if (first.StartsWith('-'))
        {
            throw new CliUsageException($"Expected a command, but found the option '{first}'.");
        }

        CommandSpec? exact = CommandCatalog.All.FirstOrDefault(
            spec => spec.Tokens.Count == 1 && string.Equals(spec.Tokens[0], first, StringComparison.Ordinal));

        if (exact is not null)
        {
            return (exact, 1);
        }

        List<CommandSpec> nested = [.. CommandCatalog.All.Where(
            spec => spec.Tokens.Count > 1 && string.Equals(spec.Tokens[0], first, StringComparison.Ordinal))];

        if (nested.Count == 0)
        {
            throw new CliUsageException($"Unknown command '{first}'. Run with --help for the command list.");
        }

        string subcommands = string.Join(", ", nested.Select(spec => spec.Tokens[1]));

        if (remaining.Count < 2)
        {
            throw new CliUsageException($"'{first}' needs a subcommand: {subcommands}.");
        }

        CommandSpec? match = nested.FirstOrDefault(
            spec => string.Equals(spec.Tokens[1], remaining[1], StringComparison.Ordinal));

        return match is null
            ? throw new CliUsageException($"Unknown subcommand '{first} {remaining[1]}'. Expected one of: {subcommands}.")
            : (match, 2);
    }

    /// <summary>Validates the words after the command name against its spec.</summary>
    private static ParsedCommand ParseArguments(CommandSpec spec, IReadOnlyList<string> remaining, int consumed)
    {
        List<string> positionals = [];
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        HashSet<string> flags = new(StringComparer.Ordinal);

        for (int index = consumed; index < remaining.Count; index++)
        {
            string token = remaining[index];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);

                continue;
            }

            string name = token[2..];

            if (spec.Flags.Contains(name, StringComparer.Ordinal))
            {
                if (!flags.Add(name))
                {
                    throw new CliUsageException($"--{name} was given twice.");
                }

                continue;
            }

            if (!spec.Options.Contains(name, StringComparer.Ordinal))
            {
                throw new CliUsageException($"'{spec.Name}' does not take --{name}. Try --help.");
            }

            if (options.ContainsKey(name))
            {
                throw new CliUsageException($"--{name} was given twice.");
            }

            options[name] = NextValue(remaining, ref index, "--" + name);
        }

        ValidateArguments(spec, positionals, options);

        return new ParsedCommand
        {
            Spec = spec,
            Positionals = positionals,
            Options = options,
            Flags = flags,
        };
    }

    /// <summary>Refuses a command line whose argument counts or required options do not fit the spec.</summary>
    private static void ValidateArguments(
        CommandSpec spec,
        IReadOnlyList<string> positionals,
        IReadOnlyDictionary<string, string> options)
    {
        if (positionals.Count < spec.Positionals.Count)
        {
            throw new CliUsageException(
                $"'{spec.Name}' needs {spec.Positionals[positionals.Count]}. Usage: {spec.Usage}");
        }

        if (positionals.Count > spec.Positionals.Count)
        {
            throw new CliUsageException($"'{spec.Name}' does not take the argument '{positionals[spec.Positionals.Count]}'.");
        }

        foreach (string required in spec.RequiredOptions)
        {
            if (!options.ContainsKey(required))
            {
                throw new CliUsageException($"'{spec.Name}' needs --{required}. Usage: {spec.Usage}");
            }
        }

        foreach (IReadOnlyList<string> group in spec.ExclusiveGroups)
        {
            string[] given = [.. group.Where(options.ContainsKey)];

            if (given.Length == 1)
            {
                continue;
            }

            string names = string.Join(" or ", group.Select(name => "--" + name));

            throw new CliUsageException(
                given.Length == 0
                    ? $"'{spec.Name}' needs one of {names}. Usage: {spec.Usage}"
                    : $"'{spec.Name}' takes {names}, not both.");
        }
    }

    /// <summary>Reads the token after an option, refusing an absent value or another option in its place.</summary>
    /// <remarks>
    /// <c>--game --mod x</c> is the interesting case: treating <c>--mod</c> as the game id would turn a typo into
    /// an operation against a game named "--mod". Refusing is the only behaviour that reports the mistake.
    /// </remarks>
    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"{option} needs a value.");
        }

        index++;

        return args[index];
    }
}
