namespace GamePolish.Core.Games;

/// <summary>
/// Thrown when a folder cannot be accepted as a game's install directory: it does not exist, is not an
/// absolute path, shares the data root with Game Polish's own files, does not contain the descriptor's
/// verify file, or is already tracked for a different game.
/// </summary>
public sealed class InstallPathInvalidException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="gameId">The game id the folder was offered for.</param>
    /// <param name="installPath">The offending path, as the caller supplied it or as it normalized to.</param>
    /// <param name="message">A message that says what to do instead.</param>
    public InstallPathInvalidException(string gameId, string installPath, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameId = gameId;
        InstallPath = installPath;
    }

    /// <summary>The game id the folder was offered for.</summary>
    public string GameId { get; }

    /// <summary>The offending path.</summary>
    public string InstallPath { get; }
}
