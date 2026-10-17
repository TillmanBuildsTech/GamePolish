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
    /// <param name="sourceRoot">
    /// Where to read the bytes from when they are not under <paramref name="destinationRoot"/>, or
    /// <see langword="null"/> for the normal case (the content is inside the game). Every recorded path and the
    /// snapshot's own layout stay relative to <paramref name="destinationRoot"/>; only the existence check, the
    /// recorded size and the file that gets copied move. This exists for one situation: a parked addon is content
    /// that belongs at a game-relative path but currently lives under the data root, so a snapshot of it has to
    /// name <c>&lt;mod_layout.destination&gt;/&lt;FolderName&gt;/...</c> while reading the bytes from the parked copy.
    /// </param>
    /// <param name="sourceRelativePaths">
    /// Where each recorded path's content lives under <paramref name="sourceRoot"/>, when that tree does not mirror
    /// the game's layout. Keyed by the recorded path (portable <c>/</c> separators); a recorded path that is not
    /// named here is read from the same relative path under <paramref name="sourceRoot"/>. Required for the parked
    /// case, because <c>staging/disabled/&lt;FolderName&gt;</c> stands in for the addon root rather than for the
    /// game root. Ignored when <paramref name="sourceRoot"/> is <see langword="null"/>.
    /// </param>
    /// <returns>The event that was appended to the index.</returns>
    /// <exception cref="Paths.PathValidationException">A path is unsafe. Nothing was created.</exception>
    /// <exception cref="BackupFailedException">The copy failed; the partial snapshot was removed.</exception>
    BackupEvent Create(
        string gameId,
        string destinationRoot,
        BackupTrigger trigger,
        string sourceName,
        string? sourceVersion,
        IEnumerable<string> relativePaths,
        string? sourceRoot = null,
        IReadOnlyDictionary<string, string>? sourceRelativePaths = null);

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
