namespace GamePolish.Core.Backup;

/// <summary>Thrown when a snapshot is asked for by a timestamp the index does not know.</summary>
public sealed class BackupNotFoundException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="gameId">The game the snapshot was expected for.</param>
    /// <param name="timestamp">The snapshot directory name that was not found.</param>
    public BackupNotFoundException(string gameId, string timestamp)
        : base($"There is no backup '{timestamp}' for '{gameId}'. It may have been deleted, or the game may have been untracked since.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameId = gameId;
        Timestamp = timestamp;
    }

    /// <summary>The game the snapshot was expected for.</summary>
    public string GameId { get; }

    /// <summary>The snapshot directory name that was not found.</summary>
    public string Timestamp { get; }
}
