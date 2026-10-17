using GamePolish.Cli.CommandLine;
using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Operations;

namespace GamePolish.Cli.Commands;

/// <summary>
/// <c>backup list</c> and <c>backup restore</c>: the snapshot engine, task <c>205</c>.
/// </summary>
/// <remarks>
/// The restore mirrors the Backups view's Restore Flow: <see cref="IBackupEngine.Restore"/> takes its own snapshot
/// of the current state first, so a restore is itself reversible, and the CLI does not add a confirm step - a
/// one-shot command with explicit arguments is its own confirmation, and <c>405</c> needs it scriptable.
/// </remarks>
internal static class BackupCommands
{
    /// <summary>Lists a game's backups, newest first.</summary>
    /// <returns>Always <c>0</c>: no backups is a fact about a fresh library.</returns>
    internal static Task<CommandOutcome> ListAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");

        IReadOnlyList<BackupEvent> events = composition.Backups.List(gameId);

        IReadOnlyList<OperationLogEntry> rows = Rows.Table(
        [
            .. events.Select(backup => (IReadOnlyList<string>)
            [
                backup.Timestamp,
                backup.Trigger.ToString(),
                string.IsNullOrWhiteSpace(backup.SourceVersion)
                    ? backup.SourceName
                    : $"{backup.SourceName} {backup.SourceVersion}",
                $"{backup.Files.Count} file(s)",
                Rows.Bytes(backup.TotalSizeBytes),
            ]),
        ]);

        string message = events.Count == 0
            ? $"No backups for {gameId}."
            : $"{events.Count} backup(s) for {gameId}, {Rows.Bytes(composition.Backups.GetDiskUsageBytes(gameId))} on disk.";

        return Task.FromResult(CommandOutcome.FromResult(OperationResult.Success(message, gameId, entries: rows)));
    }

    /// <summary>Restores a backup into the game folder, snapshotting the current state first.</summary>
    /// <returns><c>0</c> restored; <c>1</c> the timestamp is unknown or the restore failed; <c>130</c>
    /// cancelled.</returns>
    internal static Task<CommandOutcome> RestoreAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");
        string timestamp = command.Required("timestamp");

        TrackedGame game = Preflight.RequireTrackedGame(composition.Games, gameId);

        // Core throws BackupNotFoundException for an unknown timestamp, which maps to backup_not_found and exit 1;
        // a pre-flight "timestamp is unknown" check would only duplicate the lookup.
        composition.Backups.Restore(gameId, game.InstallPath, timestamp);

        return Task.FromResult(CommandOutcome.FromResult(OperationResult.Success(
            $"{game.Name} restored from backup {timestamp}.",
            gameId,
            backupTimestamp: timestamp)));
    }
}
