namespace GamePolish.Core.Manifest;

/// <summary>One file a mod placed on disk.</summary>
/// <param name="RelativePath">
/// Path relative to the <b>addon root</b>, always with <c>/</c> separators, e.g. <c>pfUI/pfUI.toc</c> -
/// not relative to the game root. The backup engine (205) wants game-root-relative paths, so the
/// install pipeline joins the addon root prefix on when it calls it.
/// </param>
/// <param name="SizeBytes">Size when it was installed, for display and for the backup's audit trail.</param>
public sealed record InstalledFileEntry(string RelativePath, long SizeBytes);
