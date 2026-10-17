namespace GamePolish.Cli.CommandLine;

/// <summary>
/// One command's shape: what it is called, which positionals it takes, which options and flags it accepts, and
/// which of those are required.
/// </summary>
/// <remarks>
/// <para>
/// The commands are described rather than hand-parsed so that one parser enforces every rule once: an unknown
/// option, a duplicated option, a missing required one, a surplus positional and a contradictory pair are five
/// usage errors that must all print the same way and exit <c>2</c>. A command that parsed its own arguments
/// would get one of those five wrong.
/// </para>
/// <para>
/// <see cref="Tokens"/> is the command's name split into words (<c>backup list</c> is two), because that is how
/// it is typed; <see cref="Name"/> is the display spelling used in <c>--help</c> and in error messages.
/// </para>
/// </remarks>
public sealed record CommandSpec
{
    /// <summary>The command as it is typed, one entry per word, e.g. <c>["backup", "list"]</c>.</summary>
    public required IReadOnlyList<string> Tokens { get; init; }

    /// <summary>Positional arguments, in order, as they are named in <c>--help</c> and in error messages.</summary>
    public IReadOnlyList<string> Positionals { get; init; } = [];

    /// <summary>Options that take a value, without the leading <c>--</c>.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Options that must be present.</summary>
    public IReadOnlyList<string> RequiredOptions { get; init; } = [];

    /// <summary>Valueless switches, e.g. <c>--overwrite</c>.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>
    /// Groups of options of which exactly one must be given, e.g. an install is from <c>--source</c> or from
    /// <c>--zip</c> but never both.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ExclusiveGroups { get; init; } = [];

    /// <summary>The command's one-line description, for <c>--help</c>.</summary>
    public required string Summary { get; init; }

    /// <summary>The command spelled out with its arguments, as <c>--help</c> prints it.</summary>
    public required string Usage { get; init; }

    /// <summary>The display name, e.g. <c>backup list</c>.</summary>
    public string Name => string.Join(' ', Tokens);

    /// <summary>True when this spec is the one the given leading tokens name.</summary>
    /// <param name="tokens">The already-consumed words of the command line.</param>
    public bool Matches(IReadOnlyList<string> tokens) =>
        tokens.Count == Tokens.Count
        && Tokens.Select((token, index) => string.Equals(token, tokens[index], StringComparison.Ordinal)).All(match => match);
}
