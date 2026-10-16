namespace GamePolish.Core.Backup;

/// <summary>
/// The safety net: snapshots the files an operation is about to touch, before it touches them.
/// </summary>
/// <remarks>
/// Every mutating flow in the product opens with a call to <see cref="Create"/>, before its first write
/// (invariant #1). A flow that writes without one is a bug even when the write succeeds.
/// </remarks>
public interface IBackupEngine
{
    /// <summary>
    /// Creates a snapshot of <paramref name="relativePaths"/> and records it in the index.
    /// </summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="destinationRoot">The game's install root: the paths are relative to it.</param>
    /// <param name="trigger">What is about to happen.</param>
    /// <param name="sourceName">The mod name or settings file name this backup protects.</param>
    /// <param name="sourceVersion">Version transition for an update, or <see langword="null"/>.</param>
    /// <param name="relativePaths">Every path the operation may write. Paths that do not exist are recorded, not created.</param>
    /// <returns>The event that was appended to the index.</returns>
    /// <exception cref="Paths.PathValidationException">A path is unsafe. Nothing was created.</exception>
    /// <exception cref="BackupFailedException">The copy failed; the partial snapshot was removed.</exception>
    BackupEvent Create(
        string gameId,
        string destinationRoot,
        BackupTrigger trigger,
        string sourceName,
        string? sourceVersion,
        IEnumerable<string> relativePaths);

    /// <summary>Lists a game's backups, newest first.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    IReadOnlyList<BackupEvent> List(string gameId);

    /// <summary>Returns one backup event, or <see langword="null"/> when the timestamp is unknown.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="timestamp">The snapshot directory name.</param>
    BackupEvent? Get(string gameId, string timestamp);

    /// <summary>
    /// Puts the snapshotted files back where they came from. A restore takes its own snapshot of the
    /// current state first, so it is always reversible.
    /// </summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="destinationRoot">The game's install root.</param>
    /// <param name="timestamp">The snapshot to restore.</param>
    /// <exception cref="BackupNotFoundException">The timestamp is unknown.</exception>
    /// <exception cref="Paths.PathValidationException">The index names a path that is not safe to write.</exception>
    void Restore(string gameId, string destinationRoot, string timestamp);

    /// <summary>Deletes one snapshot and its index entry.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="timestamp">The snapshot to delete.</param>
    /// <exception cref="BackupNotFoundException">The timestamp is unknown.</exception>
    void Delete(string gameId, string timestamp);

    /// <summary>Bytes every one of a game's snapshots occupies on disk.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    long GetDiskUsageBytes(string gameId);
}
