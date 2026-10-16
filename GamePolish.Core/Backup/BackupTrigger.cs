namespace GamePolish.Core.Backup;

/// <summary>What caused a backup. Rendered as the second line of every row in the Backups view.</summary>
public enum BackupTrigger
{
    /// <summary>A mod was installed.</summary>
    ModInstall = 0,

    /// <summary>A mod was updated.</summary>
    ModUpdate = 1,

    /// <summary>A mod was uninstalled.</summary>
    ModUninstall = 2,

    /// <summary>A mod was enabled or disabled, which moves its files in or out of the game directory.</summary>
    EnableDisable = 3,

    /// <summary>A game settings file was written.</summary>
    SettingsChange = 4,

    /// <summary>
    /// The pre-restore snapshot a restore creates for itself. It exists so a restore is reversible and
    /// so the audit trail does not have to lie about what happened - reusing <see cref="ModUpdate"/> for
    /// this would claim a mod was updated when none was.
    /// </summary>
    Restore = 5,
}
