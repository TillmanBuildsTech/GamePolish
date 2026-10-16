namespace GamePolish.Core.Storage;

/// <summary>
/// Non-sensitive application preferences, persisted to <c>&lt;data root&gt;/app-settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything in this record is plain text on disk.</b> A GitHub PAT, cloud-storage access IDs and
/// auth material live in the OS keychain - Windows' protected store, macOS Keychain - never in this
/// file, never in a log, never in an export. The rule is structural, not stylistic:
/// <c>StorageConventionsTests</c> fails if a property name here ever looks like one of those things.
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> is the only migration signal for this file. When a later task adds a
/// field it must raise this number and add a migration. Unknown properties already present in the
/// file are dropped on load and are not written back - the serializer carries no extension data for
/// them. They are not logged either, because the operation log does not exist yet (task 212); until
/// it does, the drop is silent.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Schema version of the file, written as <c>schema_version</c>.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UI theme: <c>system</c> (follow the OS), <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; init; } = "system";

    /// <summary>Accent colour preset: <c>blue</c>, <c>purple</c>, <c>green</c> or <c>orange</c>.</summary>
    public string Accent { get; init; } = "blue";

    /// <summary>Mod update check: <c>session</c> (on session start) or <c>never</c>.</summary>
    public string UpdateCheckOnStart { get; init; } = "session";

    /// <summary>Minimum level the rolling file log records. Consumed by task 212.</summary>
    public string LogLevel { get; init; } = "information";

    /// <summary>
    /// Reserved: when set, backups move to this location instead of
    /// <see cref="GamePolishPaths.BackupsDirectory"/>. Wired up with the data-dir move in task 606 -
    /// nothing reads it yet.
    /// </summary>
    public string? BackupLocationOverride { get; init; }
}
