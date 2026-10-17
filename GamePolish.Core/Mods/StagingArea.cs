using GamePolish.Core.IO;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Mods;

/// <summary>
/// The staging area under the data root, and the one place that knows where a disabled addon is parked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three kinds of content live under <c>&lt;data root&gt;/&lt;game_id&gt;/staging/</c>, and only three.</b>
/// An operation directory (<c>staging/&lt;opId&gt;/</c>) holds a source being staged for an in-flight install
/// or update, and its lifetime is one operation - <c>207</c>'s stale-staging sweep removes those. The
/// <c>staging/disabled/&lt;FolderName&gt;/</c> directory holds the parked copy of a <b>disabled</b> addon, and
/// its lifetime is until the mod is enabled, uninstalled, or its manifest entry is removed. A version probe
/// (<c>staging/check-&lt;modId&gt;-&lt;opId&gt;/</c>, <c>209</c>) is scratch for one call.
/// </para>
/// <para>
/// <b>Why the disabled children need their own class.</b> The three kinds look identical to a sweep that
/// judges by age, and one of them must never be swept: a mod can stay disabled for months, so "older than
/// 24 hours" is not evidence that a parked addon is abandoned. Naming the directory here - and having
/// <see cref="Sources.SourceAcquirer.SweepStaleStaging"/> skip it by this class's own constant - is what
/// keeps the layout and the sweep from drifting apart.
/// </para>
/// <para>
/// <b>Not a mirror of the game directory.</b> For an enabled mod the game directory is canonical and no
/// parked copy exists; for a disabled one the parked copy is canonical and the game directory has no such
/// folder. A mod therefore has exactly one copy of its files, in one of the product's two roots
/// (invariant #4), and <c>InstalledMod.Enabled</c> says which.
/// </para>
/// </remarks>
public sealed class StagingArea
{
    /// <summary>
    /// The directory under <c>staging/</c> that holds parked copies, one child per installed folder name.
    /// </summary>
    /// <remarks>
    /// Public because <c>207</c>'s sweep must recognise it and because a test asserts the two agree. A name
    /// here that the sweep does not know is data loss with a 24-hour delay.
    /// </remarks>
    public const string DisabledDirectoryName = "disabled";

    private readonly GamePolishPaths _paths;
    private readonly IPathValidator _pathValidator;
    private readonly IFileOps _files;

    /// <summary>Initializes the staging layout over a resolved data root.</summary>
    /// <param name="paths">The data root: staging lives under <c>&lt;root&gt;/&lt;game_id&gt;/staging</c>.</param>
    /// <param name="pathValidator">Validates the folder name before it becomes a path.</param>
    /// <param name="files">The mutating-call seam, used only to remove the parked-copies directory itself.</param>
    public StagingArea(GamePolishPaths paths, IPathValidator pathValidator, IFileOps files)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pathValidator);
        ArgumentNullException.ThrowIfNull(files);

        _paths = paths;
        _pathValidator = pathValidator;
        _files = files;
    }

    /// <summary>
    /// True when <paramref name="directoryName"/> is one of the staging area's own reserved directories.
    /// </summary>
    /// <param name="directoryName">A direct child name of a game's staging directory.</param>
    /// <remarks>
    /// The sweep asks this question about every direct child it is about to delete. It is a static predicate
    /// rather than a property of an instance so <c>207</c> can call it without owning a data root of its own.
    /// </remarks>
    public static bool IsReservedDirectoryName(string? directoryName) =>
        string.Equals(directoryName, DisabledDirectoryName, StringComparison.Ordinal);

    /// <summary>The directory that holds every parked addon for a game, <c>&lt;staging&gt;/disabled</c>.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <remarks>
    /// Not created here, and not created by any read: the pipelines create it with <c>IFileOps</c> at the
    /// moment they are about to park something, which is also what keeps the creation inside the recording
    /// call log a backup-ordering test reads.
    /// </remarks>
    public string DisabledRoot(string gameId) =>
        Path.Combine(_paths.StagingDirectory(gameId), DisabledDirectoryName);

    /// <summary>Where a disabled mod's files are parked, <c>&lt;staging&gt;/disabled/&lt;FolderName&gt;</c>.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="folderName">The mod's folder name from the manifest.</param>
    /// <exception cref="PathValidationException">
    /// <paramref name="folderName"/> is not a safe single directory name.
    /// </exception>
    /// <remarks>
    /// The name is validated even though the manifest wrote it: a manifest can predate a game being moved or
    /// copied from another machine, and this method turns it into a directory the product renames into and
    /// out of. The validator is asked about the closest root that exists, because it refuses a missing
    /// destination root and the parked directory legitimately does not exist before the first disable.
    /// </remarks>
    public string DisabledPath(string gameId, string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        string root = DisabledRoot(gameId);
        string anchor = Directory.Exists(root) ? root : _paths.GameDirectory(gameId);
        string resolved = _pathValidator.ResolveSafeDirectoryName(anchor, folderName);

        return Path.Combine(root, Path.GetFileName(resolved));
    }

    /// <summary>Whether this mod's files are currently parked in the staging area.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="folderName">The mod's folder name from the manifest.</param>
    /// <remarks>
    /// Answers <see langword="false"/> rather than throwing when the data root has no game directory at all:
    /// nothing can be parked where nothing has ever been written, and a caller deciding whether to enable a
    /// mod should not have to distinguish "no data root" from "no parked copy".
    /// </remarks>
    public bool HasDisabledCopy(string gameId, string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        if (!Directory.Exists(DisabledRoot(gameId)) && !Directory.Exists(_paths.GameDirectory(gameId)))
        {
            return false;
        }

        return Directory.Exists(DisabledPath(gameId, folderName));
    }

    /// <summary>Every folder name currently parked for a game, ordinally sorted.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <remarks>
    /// Sorted, not filesystem order, so a report or a test comparing two lists has one stable order. This is
    /// the reconciliation question - "which parked copies does the manifest not know about?" - and it is why
    /// it returns names rather than paths: the caller already knows the root.
    /// </remarks>
    public IReadOnlyList<string> ListDisabled(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        string root = DisabledRoot(gameId);

        if (!Directory.Exists(root))
        {
            return [];
        }

        List<string> names = [];

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);

            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);

        return names;
    }

    /// <summary>
    /// Removes the parked-copies directory when nothing is parked in it any more.
    /// </summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <returns>
    /// <see langword="true"/> when the directory is gone, or was never there; <see langword="false"/> when it
    /// still holds a parked addon or the delete was refused.
    /// </returns>
    /// <remarks>
    /// Called after the last parked copy leaves - an enable, or the uninstall of a disabled mod - so the layout
    /// returns to what it was before the first disable. A refusal is not an error: an empty directory under the
    /// data root costs nothing, and a caller reporting "the mod is enabled" must not contradict itself over
    /// housekeeping. Never recursive, so a non-empty directory is left alone by construction.
    /// </remarks>
    public bool TryRemoveEmptyRoot(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        string root = DisabledRoot(gameId);

        if (!Directory.Exists(root))
        {
            return true;
        }

        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            return false;
        }

        try
        {
            _files.DeleteDirectory(root, recursive: false);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
