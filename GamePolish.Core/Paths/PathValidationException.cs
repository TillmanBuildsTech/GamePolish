namespace GamePolish.Core.Paths;

/// <summary>
/// Thrown when one or more paths from a mod source are refused.
/// </summary>
/// <remarks>
/// Carries <b>every</b> failure, not just the first: an archive with six bad entries should be reported
/// once, with six named problems, rather than making the user re-run the install six times. A caller
/// that only wants to know whether anything failed can look at <see cref="Exception.Message"/>;
/// a caller that wants to explain it to the user reads <see cref="Failures"/>.
/// </remarks>
public sealed class PathValidationException : Exception
{
    /// <summary>Initializes the exception from the failures found.</summary>
    /// <param name="failures">At least one failure.</param>
    public PathValidationException(IReadOnlyList<PathValidationFailure> failures)
        : base(BuildMessage(failures))
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (failures.Count == 0)
        {
            throw new ArgumentException("At least one failure is required.", nameof(failures));
        }

        Failures = failures;
    }

    /// <summary>Every refused path, in the order it was supplied.</summary>
    public IReadOnlyList<PathValidationFailure> Failures { get; }

    private static string BuildMessage(IReadOnlyList<PathValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        return $"Refused {failures.Count} path(s) from the mod source:"
            + Environment.NewLine
            + "  - "
            + string.Join(Environment.NewLine + "  - ", failures.Select(failure => failure.Message));
    }
}
