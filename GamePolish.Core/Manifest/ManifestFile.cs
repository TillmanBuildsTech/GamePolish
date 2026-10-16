namespace GamePolish.Core.Manifest;

/// <summary>The contents of <c>&lt;data root&gt;/&lt;game_id&gt;/manifest.json</c>.</summary>
/// <remarks>
/// This is the durable record of what Game Polish put on disk. Together with the backups (205) it is
/// what makes an uninstall safe after the user has forgotten what they installed, so it is written
/// through <c>AtomicJson</c> and a corrupted file is quarantined rather than reset.
/// </remarks>
public sealed class ManifestFile
{
    /// <summary>Schema version of the file, written as <c>schema_version</c>.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Every mod installed into this game.</summary>
    public List<InstalledMod> Mods { get; init; } = [];
}
