namespace GamePolish.Core.Registry;

/// <summary>
/// Thrown when a registry payload was received but cannot be trusted, and there is no cached copy to
/// fall back on.
/// </summary>
/// <remarks>
/// <para>
/// It always carries <b>every</b> problem found, not the first one. A registry is hand-authored JSON and
/// a broken one usually breaks in several places at once (a renamed id, the source it forgot, the two
/// rows that now collide); reporting them one per round trip turns a five-minute fix into five fetches
/// against a live URL.
/// </para>
/// <para>
/// When a good cache exists this exception is <b>not</b> thrown: the cached registry is returned with
/// <see cref="RegistryResult.Offline"/> set and the problems go to the warning sink, because a
/// malformed upstream file must not take away the user's ability to install (see
/// <see cref="RegistryService"/>).
/// </para>
/// </remarks>
public sealed class RegistryValidationException : Exception
{
    /// <summary>Initializes the exception from the complete list of problems.</summary>
    /// <param name="gameId">The game whose registry was requested.</param>
    /// <param name="registryUrl">The URL the payload came from.</param>
    /// <param name="problems">Every problem found, in payload order. Must not be empty.</param>
    /// <param name="innerException">The parse failure this one wraps, when there is one.</param>
    /// <exception cref="ArgumentException"><paramref name="problems"/> is empty.</exception>
    public RegistryValidationException(
        string gameId,
        string registryUrl,
        IReadOnlyList<RegistryProblem> problems,
        Exception? innerException = null)
        : base(BuildMessage(gameId, problems), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count == 0)
        {
            throw new ArgumentException(
                "A validation failure has to name at least one problem; an empty list would leave the user with nothing to fix.",
                nameof(problems));
        }

        GameId = gameId;
        RegistryUrl = registryUrl;
        Problems = problems;
    }

    /// <summary>The game whose registry was requested.</summary>
    public string GameId { get; }

    /// <summary>The URL the payload came from, for the log line and the retry.</summary>
    public string RegistryUrl { get; }

    /// <summary>Everything wrong with the payload, so a fix can be a single edit.</summary>
    public IReadOnlyList<RegistryProblem> Problems { get; }

    /// <summary>Builds the message from the problems, which are the actionable part.</summary>
    private static string BuildMessage(string gameId, IReadOnlyList<RegistryProblem>? problems)
    {
        if (problems is null || problems.Count == 0)
        {
            return $"The registry for '{gameId}' failed validation.";
        }

        string detail = string.Join(" | ", problems.Select(problem => problem.Message));

        return $"The registry for '{gameId}' failed validation with {problems.Count} problem(s): {detail}";
    }
}

/// <summary>
/// One thing wrong with a registry payload: a stable <see cref="Reason"/> for the code path that found
/// it and an actionable <see cref="Message"/> for the person editing the JSON.
/// </summary>
/// <remarks>
/// The reason values are kebab-case strings rather than an enum because they end up in log lines and in
/// bug reports from registry authors, where <c>game-id-mismatch</c> is worth more than
/// <c>RegistryProblemReason.GameIdMismatch</c>; and because task 402's authoring tooling can compare
/// them without a shared enum. The constants on this type are the complete set this build produces.
/// </remarks>
public sealed record RegistryProblem
{
    /// <summary>The payload is larger than <see cref="RegistryService.MaximumPayloadBytes"/>.</summary>
    public const string ReasonTooLarge = "too-large";

    /// <summary>The body is not JSON, or not JSON of the expected shape.</summary>
    public const string ReasonMalformedJson = "malformed-json";

    /// <summary><c>game_id</c> is absent.</summary>
    public const string ReasonMissingGameId = "missing-game-id";

    /// <summary><c>game_id</c> names a different game than the one requested.</summary>
    public const string ReasonGameIdMismatch = "game-id-mismatch";

    /// <summary><c>updated_at</c> is absent.</summary>
    public const string ReasonMissingUpdatedAt = "missing-updated-at";

    /// <summary><c>mods</c> is absent or null.</summary>
    public const string ReasonMissingMods = "missing-mods";

    /// <summary>A mod's <c>id</c> is absent or does not match <c>^[a-z0-9][a-z0-9-]*$</c>.</summary>
    public const string ReasonInvalidModId = "invalid-mod-id";

    /// <summary>Two mods share an <c>id</c>.</summary>
    public const string ReasonDuplicateModId = "duplicate-mod-id";

    /// <summary>A required mod field (<c>name</c>, <c>author</c>, <c>description</c>, <c>source</c>) is empty.</summary>
    public const string ReasonMissingField = "missing-field";

    /// <summary>A mod's <c>tags</c> is absent or null. Empty is fine.</summary>
    public const string ReasonMissingTags = "missing-tags";

    /// <summary>A mod's <c>source</c> is not an absolute URL.</summary>
    public const string ReasonInvalidSource = "invalid-source";

    /// <summary>A mod's <c>source</c> is not <c>https://</c>.</summary>
    public const string ReasonNonHttpsSource = "non-https-source";

    /// <summary>A mod's <c>source</c> carries a user name, password or token in the URL.</summary>
    public const string ReasonCredentialInSource = "credential-in-source";

    /// <summary>Machine-readable reason, one of the constants on this type.</summary>
    public required string Reason { get; init; }

    /// <summary>The actionable sentence: what is wrong, where, and what a valid value looks like.</summary>
    public required string Message { get; init; }

    /// <summary>The mod the problem belongs to, when it belongs to one.</summary>
    public string? ModId { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Reason}: {Message}";
}
