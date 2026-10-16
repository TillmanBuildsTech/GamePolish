namespace GamePolish.Core.Games;

/// <summary>
/// Thrown when the game is already tracked. Carries both paths, so the UI can show the existing folder
/// next to the one the user just picked.
/// </summary>
public sealed class GameAlreadyTrackedException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="gameName">The display name recorded when the game was added.</param>
    /// <param name="existingPath">The install folder already recorded.</param>
    /// <param name="requestedPath">The folder the caller just offered.</param>
    public GameAlreadyTrackedException(string gameId, string gameName, string existingPath, string requestedPath)
        : base($"{gameName} is already tracked at {existingPath}. Use 'change install path' to move it.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameId = gameId;
        GameName = gameName;
        ExistingPath = existingPath;
        RequestedPath = requestedPath;
    }

    /// <summary>The game id.</summary>
    public string GameId { get; }

    /// <summary>The display name recorded when the game was added.</summary>
    public string GameName { get; }

    /// <summary>The install folder already recorded.</summary>
    public string ExistingPath { get; }

    /// <summary>The folder the caller just offered.</summary>
    public string RequestedPath { get; }
}
