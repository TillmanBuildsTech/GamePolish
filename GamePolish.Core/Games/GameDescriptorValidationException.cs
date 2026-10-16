namespace GamePolish.Core.Games;

/// <summary>
/// Thrown when a descriptor file exists but cannot be used: unreadable, oversized, not valid YAML,
/// or missing/malformed keys.
/// </summary>
/// <remarks>
/// Carries <b>every</b> problem found, not just the first, so one load reports one complete list
/// instead of making the author re-run the app per mistake. Every entry in <see cref="Problems"/>
/// starts with the descriptor path and names the offending key, the offending value and what was
/// expected.
/// </remarks>
public sealed class GameDescriptorValidationException : Exception
{
    /// <summary>Initializes the exception for <paramref name="descriptorPath"/>.</summary>
    /// <param name="descriptorPath">Absolute path of the offending descriptor file.</param>
    /// <param name="problems">One human-readable line per problem; must not be empty.</param>
    public GameDescriptorValidationException(string descriptorPath, IReadOnlyList<string> problems)
        : base(BuildMessage(descriptorPath, problems))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count == 0)
        {
            throw new ArgumentException("At least one problem is required.", nameof(problems));
        }

        DescriptorPath = descriptorPath;
        Problems = problems;
    }

    /// <summary>Absolute path of the descriptor file that failed to load.</summary>
    public string DescriptorPath { get; }

    /// <summary>Every problem found, in validation order. Never empty.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string BuildMessage(string descriptorPath, IReadOnlyList<string> problems) =>
        $"The game descriptor '{descriptorPath}' is not usable:"
        + Environment.NewLine
        + "  - "
        + string.Join(Environment.NewLine + "  - ", problems);
}
