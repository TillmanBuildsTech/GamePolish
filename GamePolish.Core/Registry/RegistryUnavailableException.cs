namespace GamePolish.Core.Registry;

/// <summary>
/// Thrown when the registry could not be fetched and there is no cached copy to show. Its
/// <see cref="Exception.Message"/> is the Discover error banner's text.
/// </summary>
/// <remarks>
/// <para>
/// This is the "no fallback left" failure, so it is deliberately the only registry failure the UI has to
/// turn into the banner: a failed fetch <i>with</i> a cache is not an exception, it is a
/// <see cref="RegistryResult"/> marked offline. That split keeps the banner's condition in one place -
/// "the service threw" - instead of a flag the view has to interpret.
/// </para>
/// <para>
/// <see cref="Diagnostic"/> carries the technical sentence (the HTTP status, the socket error, the
/// timeout) for the log and the details drawer. It is kept out of <see cref="Exception.Message"/> on
/// purpose: the banner has one job and a sentence like "connection refused (127.0.0.1:443)" is not it.
/// Note that it is never a request dump - this service attaches no <c>Authorization</c> header and logs
/// no headers at all (the GitHub PAT arrives in task 504, whose redactor is the second line of defence,
/// not the first).
/// </para>
/// </remarks>
public sealed class RegistryUnavailableException : Exception
{
    /// <summary>The banner sentence. Kept as a constant so a test can assert the UI's contract.</summary>
    public const string BannerText = "Couldn't load registry. Check your connection.";

    /// <summary>Initializes the exception for a game with no usable registry copy.</summary>
    /// <param name="gameId">The game whose registry was requested.</param>
    /// <param name="registryUrl">The URL that could not be fetched.</param>
    /// <param name="diagnostic">The technical cause, for the log and the details drawer.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public RegistryUnavailableException(
        string gameId,
        string registryUrl,
        string diagnostic,
        Exception? innerException = null)
        : base($"{BannerText} There is no cached copy of the registry for '{gameId}' to fall back on.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameId = gameId;
        RegistryUrl = registryUrl;
        Diagnostic = diagnostic;
    }

    /// <summary>The game whose registry was requested.</summary>
    public string GameId { get; }

    /// <summary>The URL that could not be fetched, so the retry targets the same place.</summary>
    public string RegistryUrl { get; }

    /// <summary>The technical cause: an HTTP status, a socket error, or a timeout.</summary>
    public string Diagnostic { get; }
}
