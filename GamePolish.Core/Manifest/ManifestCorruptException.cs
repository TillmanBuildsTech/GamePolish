namespace GamePolish.Core.Manifest;

/// <summary>
/// Thrown when <c>manifest.json</c> could not be read. The file is <b>moved aside, never deleted</b>:
/// it is the user's only record of what was installed, and the only thing standing between them and an
/// addon folder nobody can explain.
/// </summary>
/// <remarks>
/// The store quarantines the file and then throws, so the caller can surface this to the user and retry:
/// the next call reads an empty manifest. Plumbing the message into a user-visible operation result is
/// task 212's job.
/// </remarks>
public sealed class ManifestCorruptException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="gameId">The game whose manifest could not be read.</param>
    /// <param name="manifestPath">The manifest path that was read.</param>
    /// <param name="quarantinedPath">Where the unreadable file was moved to.</param>
    /// <param name="innerException">The parse or shape failure, when there was one.</param>
    public ManifestCorruptException(string gameId, string manifestPath, string quarantinedPath, Exception? innerException)
        : base(
            $"The record of installed mods for '{gameId}' could not be read, so it was moved aside to "
            + $"'{quarantinedPath}' and Game Polish starts from an empty list. The file was not deleted; "
            + "restoring it by hand is possible.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameId = gameId;
        ManifestPath = manifestPath;
        QuarantinedPath = quarantinedPath;
    }

    /// <summary>The game whose manifest could not be read.</summary>
    public string GameId { get; }

    /// <summary>The manifest path that was read.</summary>
    public string ManifestPath { get; }

    /// <summary>Where the unreadable file was moved to, alongside the manifest.</summary>
    public string QuarantinedPath { get; }
}
