namespace GamePolish.Core.Backup;

/// <summary>The contents of <c>&lt;data root&gt;/&lt;game_id&gt;/backups/index.json</c>.</summary>
/// <remarks>
/// This is the audit trail the Backups view renders and the only record of what each snapshot holds, so
/// it is written through <c>AtomicJson</c> and is never reset when it is malformed.
/// </remarks>
public sealed class BackupIndex
{
    /// <summary>Schema version of the file, written as <c>schema_version</c>.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Every backup taken for this game, oldest first as appended.</summary>
    public List<BackupEvent> Events { get; init; } = [];
}
