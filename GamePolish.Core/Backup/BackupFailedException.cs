namespace GamePolish.Core.Backup;

/// <summary>
/// Thrown when a snapshot could not be completed: the filesystem refused a copy, or the disk filled.
/// </summary>
/// <remarks>
/// The snapshot directory is removed and the index is left untouched before this is thrown, so nothing
/// downstream can believe a backup exists. <b>The caller must treat this as a hard stop:</b> no mod
/// content may be written when the backup failed - that is invariant #1's whole point.
/// </remarks>
public sealed class BackupFailedException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="gameId">The game the backup was for.</param>
    /// <param name="trigger">What the backup was taken for.</param>
    /// <param name="innerException">The filesystem failure that stopped it.</param>
    public BackupFailedException(string gameId, BackupTrigger trigger, Exception innerException)
        : base(
            $"The backup before this {Describe(trigger)} could not be completed, so the operation was stopped "
            + "before anything was written. No files were changed.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameId = gameId;
        Trigger = trigger;
    }

    /// <summary>The game the backup was for.</summary>
    public string GameId { get; }

    /// <summary>What the backup was taken for.</summary>
    public BackupTrigger Trigger { get; }

    private static string Describe(BackupTrigger trigger) => trigger switch
    {
        BackupTrigger.ModInstall => "mod install",
        BackupTrigger.ModUpdate => "mod update",
        BackupTrigger.ModUninstall => "mod uninstall",
        BackupTrigger.EnableDisable => "enable/disable",
        BackupTrigger.SettingsChange => "settings change",
        BackupTrigger.Restore => "restore",
        _ => trigger.ToString(),
    };
}
