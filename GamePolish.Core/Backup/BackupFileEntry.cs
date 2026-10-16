namespace GamePolish.Core.Backup;

/// <summary>One file covered by a backup event.</summary>
/// <param name="RelativePath">
/// Path relative to the game's install root, always with <c>/</c> separators, e.g.
/// <c>the mod folder/pfUI/pfUI.toc</c>. This is the value the Backups view shows as the original path
/// and the value a restore resolves against the install root again.
/// </param>
/// <param name="SizeBytes">
/// Size when the backup was taken. <c>0</c> also means "this file did not exist yet" - a pre-install
/// backup names the files the install is about to create - so a restore must decide by whether the
/// snapshot holds the file, never by this number alone.
/// </param>
/// <param name="ModifiedAt">The file's last-write time, or the backup time when the file was absent.</param>
public sealed record BackupFileEntry(string RelativePath, long SizeBytes, DateTimeOffset ModifiedAt);
