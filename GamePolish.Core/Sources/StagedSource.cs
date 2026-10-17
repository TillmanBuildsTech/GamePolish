using GamePolish.Core.Manifest;

namespace GamePolish.Core.Sources;

/// <summary>
/// A mod source materialised under the data root, validated, and ready for the install pipeline to move
/// into a game directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordinate system.</b> <see cref="Files"/> is relative to the <b>addon root</b> - the game's mod
/// destination, the addons folder named by the descriptor's <c>mod_layout.destination</c> - so every entry
/// already carries
/// <see cref="FolderName"/> as its first segment (<c>pfUI/pfUI.toc</c>). That is exactly the contract
/// <see cref="InstalledFileEntry"/> documents and what <c>206</c> stores, which is what lets the install
/// pipeline record <c>Files = staged.Files</c> without translating anything. It is also a deliberate
/// departure from the inline comment in the work order's sketch ("relative to FolderName"): the manifest
/// is already written, tested and shipped against addon-root-relative paths, and two coordinate systems
/// in one field is how a file list silently stops matching the files on disk.
/// </para>
/// <para>
/// <b>Two fields the sketch does not have.</b> <see cref="GameId"/> and <see cref="OperationId"/> are
/// here because <c>ISourceAcquirer.Cleanup</c> has to delete exactly one directory and has nothing else
/// to go on; deriving it by walking up from <see cref="StagingDirectory"/> would make "which directory
/// may I delete recursively" a guess.
/// </para>
/// <para>
/// A staged tree is <b>not</b> a promise to the game directory: it is a copy under the data root, and the
/// game directory stays byte-identical until the pipeline's swap step.
/// </para>
/// </remarks>
public sealed record StagedSource
{
    /// <summary>The game this source is staged for.</summary>
    public required string GameId { get; init; }

    /// <summary>The operation that staged it, which names the directory <see cref="Cleanup"/> removes.</summary>
    public required string OperationId { get; init; }

    /// <summary><c>&lt;data root&gt;/&lt;game&gt;/staging/&lt;opId&gt;/&lt;FolderName&gt;</c>. Inside the data root, always.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>The addon folder name to create under the game's mod destination.</summary>
    public required string FolderName { get; init; }

    /// <summary>What was staged: a 40-character commit SHA, or an archive fingerprint. See <see cref="VersionKind"/>.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// How to read <see cref="Version"/>: <c>sha</c> for a repository clone, <c>zip</c> for a supplied
    /// archive. Not the <c>sha</c>/<c>tag</c> pair the work order's sketch lists - see the note on
    /// version reporting in <c>SourceAcquirer</c>.
    /// </summary>
    public required string VersionKind { get; init; }

    /// <summary>Which kind of source this was staged from.</summary>
    public required ModSourceType SourceType { get; init; }

    /// <summary>The repository URL, or the archive's original file name.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Every file in the staged tree, addon-root-relative and ordinally sorted, with the size each had
    /// when it was staged. <c>.git</c> is excluded when <see cref="HasGitDirectory"/> is set.
    /// </summary>
    public required IReadOnlyList<InstalledFileEntry> Files { get; init; }

    /// <summary>
    /// True for a repository clone, whose staging directory still holds a <c>.git</c> folder. The install
    /// pipeline excludes it when it copies the tree: the game ignores it, but the user's game folder
    /// should not carry a second copy of the repository's history.
    /// </summary>
    public required bool HasGitDirectory { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{FolderName} ({SourceType}) {VersionKind} {Version}";
}
