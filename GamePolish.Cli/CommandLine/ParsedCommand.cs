namespace GamePolish.Cli.CommandLine;

/// <summary>
/// A parsed command: the positional values in order, the valued options by name, and the flags that were set.
/// </summary>
/// <remarks>
/// Only the parser constructs one, and it only hands one out after every rule in
/// <see cref="CommandSpec"/> passed - so a command body can read an argument it needs without re-checking it,
/// which is what keeps the largest command body at "read the arguments, make one call".
/// </remarks>
public sealed record ParsedCommand
{
    /// <summary>What the command is, and therefore what its arguments mean.</summary>
    public required CommandSpec Spec { get; init; }

    /// <summary>The positional values, in <see cref="CommandSpec.Positionals"/> order.</summary>
    public required IReadOnlyList<string> Positionals { get; init; }

    /// <summary>The valued options that were given, keyed by name without the leading <c>--</c>.</summary>
    public required IReadOnlyDictionary<string, string> Options { get; init; }

    /// <summary>The flags that were set.</summary>
    public required IReadOnlySet<string> Flags { get; init; }

    /// <summary>The positional at <paramref name="index"/>, which the parser guarantees exists.</summary>
    /// <param name="index">Zero-based position.</param>
    public string Positional(int index) => Positionals[index];

    /// <summary>An option's value, or <see langword="null"/> when it was not given or is optional.</summary>
    /// <param name="name">The option name without the leading <c>--</c>.</param>
    public string? Option(string name) => Options.TryGetValue(name, out string? value) ? value : null;

    /// <summary>An option's value, for an option <see cref="CommandSpec.RequiredOptions"/> lists.</summary>
    /// <param name="name">The option name without the leading <c>--</c>.</param>
    /// <exception cref="CliUsageException">The option is absent, which means the parser's own contract was broken.</exception>
    public string Required(string name) =>
        Option(name) ?? throw new CliUsageException($"'{Spec.Name}' needs --{name}.");

    /// <summary>True when the flag was set.</summary>
    /// <param name="name">The flag name without the leading <c>--</c>.</param>
    public bool Flag(string name) => Flags.Contains(name);
}
