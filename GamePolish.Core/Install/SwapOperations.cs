using System.Globalization;
using System.Text;

using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;

namespace GamePolish.Core.Install;

/// <summary>How far a swap got. Every phase is reversible from the one before it.</summary>
/// <remarks>
/// The order is the rollback order read backwards, and the values are explicit and append-only because the
/// phase is written into the operation log and read by whoever is debugging a half-finished install.
/// </remarks>
public enum SwapPhase
{
    /// <summary>Named, nothing written.</summary>
    Prepared = 0,

    /// <summary>The staged tree was copied into the <c>-new-</c> sibling. The game folder is untouched.</summary>
    Copied = 1,

    /// <summary>The existing folder was moved to the <c>-old-</c> sibling. The game folder is temporarily missing.</summary>
    OldMovedAside = 2,

    /// <summary>The <c>-new-</c> directory became the addon folder. The previous one still exists as <c>-old-</c>.</summary>
    Activated = 3,

    /// <summary>The <c>-old-</c> directory is gone. There is nothing to roll back to.</summary>
    Committed = 4,
}

/// <summary>The absolute paths of one swap, decided before anything is written.</summary>
/// <remarks>
/// <see cref="NewDirectory"/> and <see cref="OldDirectory"/> are siblings of <see cref="Destination"/> inside
/// <see cref="AddonRoot"/> - not temporaries under the data root. Two reasons, and both were decided in
/// <c>207</c>: a rename inside one volume is atomic, while a copy across volumes is not, and that is what
/// makes "the addon folder is either the old tree or the new one, never a mixture" true; and the sibling is
/// inside the game's install directory, which is one of the only two roots invariant #4 allows this product
/// to write to.
/// </remarks>
public sealed record SwapPaths
{
    /// <summary>The game's addons folder, e.g. <c>D:\Games\OctoWoW\&lt;mod_layout.destination&gt;</c>.</summary>
    public required string AddonRoot { get; init; }

    /// <summary>The folder the addon will occupy, as validated by <c>204</c>.</summary>
    public required string FolderName { get; init; }

    /// <summary>Where the addon ends up: <c>&lt;AddonRoot&gt;/&lt;FolderName&gt;</c>.</summary>
    public required string Destination { get; init; }

    /// <summary>The staging sibling the new tree is copied into, <c>.game-polish-new-&lt;name&gt;-&lt;ts&gt;</c>.</summary>
    public required string NewDirectory { get; init; }

    /// <summary>The sibling the previous folder is moved to, <c>.game-polish-old-&lt;name&gt;-&lt;ts&gt;</c>.</summary>
    public required string OldDirectory { get; init; }

    /// <summary>The timestamp in both names, <c>yyyyMMddTHHmmssfffZ</c> - the backup engine's format.</summary>
    public required string Timestamp { get; init; }
}

/// <summary>What a rollback managed to undo, and what it could not.</summary>
/// <remarks>
/// A rollback that cannot finish is the one failure this product must never swallow: the user's addon is
/// somewhere unexpected and they need to be told exactly where. Hence <see cref="StrandedAddonDirectory"/> -
/// the path the caller puts in the toast's one line - and <see cref="Describe"/>, which the caller puts in
/// the result's detail together with the backup timestamp.
/// </remarks>
public sealed record SwapRollback
{
    /// <summary>How far the swap had got when the rollback started.</summary>
    public required SwapPhase ReachedPhase { get; init; }

    /// <summary>True when the addon folder is back to what it was - there, or never touched.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>True when a folder that had been moved aside is back at its proper name.</summary>
    public required bool OldFolderRestored { get; init; }

    /// <summary>
    /// Where the user's addon is right now, when a failed rollback left it somewhere other than its proper
    /// name. <see langword="null"/> when nothing is stranded.
    /// </summary>
    public string? StrandedAddonDirectory { get; init; }

    /// <summary>One sentence per step that could not be undone, naming the path and the reason.</summary>
    public required IReadOnlyList<string> Failures { get; init; }

    /// <summary>The technical half of a failed result: what was reached, what was left where, and the backup to restore from.</summary>
    /// <param name="backupTimestamp">The snapshot taken before the swap, when one was taken.</param>
    public string Describe(string? backupTimestamp)
    {
        StringBuilder builder = new();

        builder.Append("Swap rollback after ").Append(ReachedPhase).Append(": ")
            .Append(Succeeded ? "the addon folder is unchanged." : "INCOMPLETE.")
            .Append('\n');

        if (StrandedAddonDirectory is not null)
        {
            builder.Append("The addon folder is at ").Append(StrandedAddonDirectory)
                .Append(" and could not be moved back.\n");
        }

        foreach (string failure in Failures)
        {
            builder.Append("  - ").Append(failure).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(backupTimestamp))
        {
            builder.Append("Backup taken before the swap: ").Append(backupTimestamp)
                .Append(" (restore it from the Backups view).\n");
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{ReachedPhase}: {(Succeeded ? "rolled back" : "incomplete")}"
        + (StrandedAddonDirectory is null ? string.Empty : " at " + StrandedAddonDirectory);
}

/// <summary>
/// The staged-to-addon-folder swap: the only code in the product that renames a mod's folder into place, and
/// the only code that knows how to undo it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a directory swap at all.</b> Copying over the addon folder in place cannot be undone: a copy that
/// fails at file 3 of 10 has already replaced files 1 and 2, and the backup holds them but nothing
/// automatically puts them back. A rename sequence can be undone at every step, which is what lets this
/// product promise that a failed install leaves the game folder exactly as it was.
/// </para>
/// <para>
/// <b>The sequence, and why the old folder outlives the manifest write.</b> Copy into
/// <c>.game-polish-new-&lt;name&gt;-&lt;ts&gt;</c>; move the existing folder to
/// <c>.game-polish-old-&lt;name&gt;-&lt;ts&gt;</c>; move the new one into place; write the manifest; only
/// then delete the old one. Deleting the old folder before the manifest write would make "the manifest write
/// failed, put it back" impossible, and a folder on disk that the manifest does not know about is an orphan
/// the user cannot uninstall. So the old tree is kept until the record exists, and
/// <see cref="SwapSession.Commit"/> is the caller's last step, not part of <see cref="SwapSession.Activate"/>.
/// </para>
/// <para>
/// <b>Shared with <c>209</c> and <c>210</c>.</b> An update is the same sequence with a different backup
/// trigger and a different manifest entry, and an uninstall is "move aside, delete the record, delete the
/// folder". Neither should re-implement any of this, which is why it lives here rather than inside the install
/// pipeline.
/// </para>
/// <para>
/// Timestamps come from <see cref="TimeProvider"/> and use the backup engine's format, so a swap directory
/// and the snapshot that protects it sort together and can be read in one glance.
/// </para>
/// </remarks>
public sealed class SwapOperations
{
    /// <summary>Prefix of a directory Game Polish is still building. Reserved by <c>207</c>.</summary>
    public const string NewPrefix = AddonFolderName.ReservedPrefix + "new-";

    /// <summary>Prefix of a directory holding the folder that was moved aside. Reserved by <c>207</c>.</summary>
    public const string OldPrefix = AddonFolderName.ReservedPrefix + "old-";

    /// <summary>The swap timestamp format, identical to the backup engine's.</summary>
    public const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly IFileOps _files;
    private readonly IPathValidator _pathValidator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the swap.</summary>
    /// <param name="files">The mutating-call seam: every rename, copy and delete goes through it.</param>
    /// <param name="pathValidator">Validates the folder name and every staged path before they become paths.</param>
    /// <param name="timeProvider">The clock behind both directory names.</param>
    public SwapOperations(IFileOps files, IPathValidator pathValidator, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pathValidator);

        _files = files;
        _pathValidator = pathValidator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The name of the directory a staged tree is copied into.</summary>
    /// <param name="folderName">The addon folder name.</param>
    /// <param name="timestamp">A <see cref="TimestampFormat"/> timestamp.</param>
    public static string NewDirectoryName(string folderName, string timestamp) => NewPrefix + folderName + "-" + timestamp;

    /// <summary>The name of the directory the previous folder is moved to.</summary>
    /// <param name="folderName">The addon folder name.</param>
    /// <param name="timestamp">A <see cref="TimestampFormat"/> timestamp.</param>
    public static string OldDirectoryName(string folderName, string timestamp) => OldPrefix + folderName + "-" + timestamp;

    /// <summary>
    /// Names a swap and validates the folder name. Writes nothing.
    /// </summary>
    /// <param name="addonRoot">The game's addons folder, which must exist.</param>
    /// <param name="folderName">The folder the addon will occupy.</param>
    /// <exception cref="PathValidationException">The folder name is not a safe single directory name.</exception>
    /// <remarks>
    /// The name is validated here even though the pipeline validated it one phase earlier: <c>209</c> and
    /// <c>210</c> call this directly, and a swap that will rename a directory into the game folder is the
    /// wrong place to trust a caller.
    /// </remarks>
    public SwapSession Begin(string addonRoot, string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addonRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        string root = Path.GetFullPath(addonRoot);
        string resolved = _pathValidator.ResolveSafeDirectoryName(root, folderName);
        string name = Path.GetFileName(resolved);
        string timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        // Two swaps of the same folder inside one millisecond (a batch install, or a clock that went
        // backwards) would otherwise collide on the rename and fail with a confusing "directory exists".
        string candidate = timestamp;
        for (int suffix = 2; Directory.Exists(Path.Combine(root, NewDirectoryName(name, candidate)))
            || Directory.Exists(Path.Combine(root, OldDirectoryName(name, candidate))); suffix++)
        {
            candidate = $"{timestamp}-{suffix}";
        }

        return new SwapSession(
            _files,
            _pathValidator,
            new SwapPaths
            {
                AddonRoot = root,
                FolderName = name,
                Destination = Path.Combine(root, name),
                NewDirectory = Path.Combine(root, NewDirectoryName(name, candidate)),
                OldDirectory = Path.Combine(root, OldDirectoryName(name, candidate)),
                Timestamp = candidate,
            });
    }
}

/// <summary>
/// One swap in progress: copy, activate, commit - or roll back.
/// </summary>
/// <remarks>
/// <para>
/// Stateful on purpose. The pipeline needs to know how far the swap got when something failed, because
/// "undo" is a different sequence depending on whether the old folder was moved aside and whether the new one
/// landed. The phase is the whole of that state, and every mutation sets it before returning, so a caller that
/// catches an exception can always ask where things stopped.
/// </para>
/// <para>
/// <b>Rollback never deletes anything it did not create.</b> It removes the <c>-new-</c> tree, removes the
/// new tree from the addon folder's name if it landed there, and puts the <c>-old-</c> tree back. It never
/// deletes the <c>-old-</c> tree, even when it cannot restore it: that tree is the user's mod.
/// </para>
/// </remarks>
public sealed class SwapSession
{
    private readonly IFileOps _files;
    private readonly IPathValidator _pathValidator;
    private SwapRollback? _lastRollback;
    private bool _createdNewDirectory;
    private bool _oldMovedAside;
    private bool _rolledBack;

    internal SwapSession(IFileOps files, IPathValidator pathValidator, SwapPaths paths)
    {
        _files = files;
        _pathValidator = pathValidator;
        Paths = paths;
    }

    /// <summary>The absolute paths this swap will use.</summary>
    public SwapPaths Paths { get; }

    /// <summary>How far the swap got.</summary>
    public SwapPhase Phase { get; private set; } = SwapPhase.Prepared;

    /// <summary>True once this session has written anything inside the game's addons folder.</summary>
    public bool HasMutated => _createdNewDirectory;

    /// <summary>
    /// The <c>-old-</c> directory that <see cref="Commit"/> could not delete, or <see langword="null"/> when
    /// there is none. A leftover is swept at the next start, so it is a warning rather than a failure.
    /// </summary>
    public string? LeftoverOldDirectory { get; private set; }

    /// <summary>Copies the staged tree into the <c>-new-</c> sibling, one file at a time.</summary>
    /// <param name="staged">The staged source. Its file list is relative to the addons folder.</param>
    /// <param name="progress">Reported per copied file, with the addon-relative path as the current item.</param>
    /// <param name="cancellationToken">Checked before every file, so a cancel stops between files, not inside one.</param>
    /// <exception cref="InvalidOperationException">The session is past <see cref="SwapPhase.Prepared"/>.</exception>
    /// <exception cref="ArgumentException">The staged source names a different folder.</exception>
    /// <exception cref="PathValidationException">A staged path is not safe, or escapes the addons folder.</exception>
    /// <remarks>
    /// A per-file copy rather than a directory copy, for three reasons: it reports progress, it makes the
    /// failure point injectable (<c>205</c>'s <c>IFileOps</c>), and it lets <c>.git</c> be left behind - the
    /// game ignores it and the user's install folder should not carry a second copy of the repository's
    /// history. The staging directory on the same volume as the game would make a rename possible, but the
    /// data root and the game install are frequently on different volumes, and a rename across volumes is a
    /// copy that can fail halfway anyway.
    /// </remarks>
    public void CopyStagedTree(
        StagedSource staged,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);

        if (Phase != SwapPhase.Prepared)
        {
            throw new InvalidOperationException(
                $"The swap for '{Paths.FolderName}' has already reached {Phase}; it cannot copy a staged tree now.");
        }

        if (!string.Equals(staged.FolderName, Paths.FolderName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The staged source names the folder '{staged.FolderName}', but this swap was planned for '{Paths.FolderName}'.",
                nameof(staged));
        }

        _files.CreateDirectory(Paths.NewDirectory);
        _createdNewDirectory = true;

        int total = staged.Files.Count;

        for (int index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            InstalledFileEntry file = staged.Files[index];
            string withinFolder = RelativeWithinFolder(file.RelativePath);

            // Validated again here, not because the pipeline did not: this is the last point before a
            // mod-supplied string becomes a write, and 209/210 reach this class without the pipeline's
            // earlier pass.
            _pathValidator.ResolveSafe(Paths.AddonRoot, file.RelativePath);

            _files.CopyFile(
                Path.Combine(staged.StagingDirectory, ToPlatformPath(withinFolder)),
                Path.Combine(Paths.NewDirectory, ToPlatformPath(withinFolder)),
                overwrite: true);

            progress?.Report(new OperationProgress("copying", index + 1, total, file.RelativePath));
        }

        Phase = SwapPhase.Copied;
    }

    /// <summary>Moves the existing folder aside, then moves the new one into its place.</summary>
    /// <exception cref="InvalidOperationException">The session is not at <see cref="SwapPhase.Copied"/>.</exception>
    /// <remarks>
    /// The gap between the two renames - a moment where the addon folder does not exist - is as short as the
    /// filesystem allows. The game is not reading the folder at that instant in any supported scenario (a
    /// mod manager installs with the game closed), and a crash inside the gap is recoverable by hand from the
    /// two siblings, which is why both names carry the folder name and the timestamp.
    /// </remarks>
    public void Activate()
    {
        if (Phase != SwapPhase.Copied)
        {
            throw new InvalidOperationException(
                $"The swap for '{Paths.FolderName}' is at {Phase}; it can only be activated from {SwapPhase.Copied}.");
        }

        if (Directory.Exists(Paths.Destination))
        {
            _files.MoveDirectory(Paths.Destination, Paths.OldDirectory);
            _oldMovedAside = true;
            Phase = SwapPhase.OldMovedAside;
        }

        _files.MoveDirectory(Paths.NewDirectory, Paths.Destination);
        Phase = SwapPhase.Activated;
    }

    /// <summary>Deletes the folder that was moved aside. The caller runs this after the manifest records the install.</summary>
    /// <returns><see langword="true"/> when nothing was left behind; <see langword="false"/> when the old folder is still there.</returns>
    /// <exception cref="InvalidOperationException">The session is not at <see cref="SwapPhase.Activated"/>.</exception>
    /// <remarks>
    /// A delete that fails does not fail the install. The files are in place and recorded; an extra
    /// <c>.game-polish-old-*</c> directory is housekeeping, and the startup sweep removes those. Turning a
    /// successful install into a failed one because a delete was refused would be the wrong trade.
    /// </remarks>
    public bool Commit()
    {
        if (Phase != SwapPhase.Activated)
        {
            throw new InvalidOperationException(
                $"The swap for '{Paths.FolderName}' is at {Phase}; it can only be committed from {SwapPhase.Activated}.");
        }

        bool clean = true;

        if (_oldMovedAside && Directory.Exists(Paths.OldDirectory))
        {
            try
            {
                _files.DeleteDirectory(Paths.OldDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LeftoverOldDirectory = Paths.OldDirectory;
                clean = false;
            }
        }

        Phase = SwapPhase.Committed;

        return clean;
    }

    /// <summary>
    /// Puts the addons folder back the way it was, as far as the swap got.
    /// </summary>
    /// <returns>What was undone and what was not. Never throws for a filesystem refusal.</returns>
    /// <remarks>
    /// <para>
    /// The order is the only one that works: take the new tree out of the addon folder's name first, because
    /// a rename cannot land on an existing directory, then put the old tree back. Deleting the <c>-new-</c>
    /// tree covers every failure before the second rename; deleting the addon folder's contents covers a
    /// failure after it.
    /// </para>
    /// <para>
    /// Idempotent: a second call returns the first call's verdict instead of re-running, so a pipeline that
    /// rolls back in its own catch and again while unwinding cannot make things worse.
    /// </para>
    /// </remarks>
    public SwapRollback Rollback()
    {
        if (_rolledBack)
        {
            return _lastRollback!;
        }

        _rolledBack = true;

        List<string> failures = [];
        SwapPhase reached = Phase;

        if (Phase == SwapPhase.Committed)
        {
            failures.Add(
                $"The previous '{Paths.FolderName}' folder was already deleted, so there is nothing to restore. "
                + "Use the backup taken before the swap to get its files back.");

            return Remember(new SwapRollback
            {
                ReachedPhase = reached,
                Succeeded = false,
                OldFolderRestored = false,
                Failures = failures,
            });
        }

        if (Phase == SwapPhase.Activated && Directory.Exists(Paths.Destination))
        {
            TryDelete(failures, Paths.Destination, "the folder that was moved into place");
        }

        if (Directory.Exists(Paths.NewDirectory))
        {
            TryDelete(failures, Paths.NewDirectory, "the copy of the new files");
        }

        bool restored = !_oldMovedAside;

        if (_oldMovedAside)
        {
            if (Directory.Exists(Paths.Destination))
            {
                failures.Add(
                    $"'{Paths.Destination}' could not be cleared, so '{Paths.OldDirectory}' was left where it is.");
            }
            else if (!Directory.Exists(Paths.OldDirectory))
            {
                failures.Add(
                    $"'{Paths.OldDirectory}' is gone, so the folder that was there before could not be put back.");
            }
            else
            {
                try
                {
                    _files.MoveDirectory(Paths.OldDirectory, Paths.Destination);
                    restored = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add(
                        $"'{Paths.OldDirectory}' could not be moved back to '{Paths.Destination}': {exception.Message}");
                }
            }
        }

        return Remember(new SwapRollback
        {
            ReachedPhase = reached,
            Succeeded = failures.Count == 0 && restored,
            OldFolderRestored = restored,
            StrandedAddonDirectory = _oldMovedAside && !restored ? Paths.OldDirectory : null,
            Failures = failures,
        });
    }

    private SwapRollback Remember(SwapRollback rollback)
    {
        _lastRollback = rollback;

        return rollback;
    }

    private void TryDelete(List<string> failures, string directory, string what)
    {
        try
        {
            _files.DeleteDirectory(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add($"'{directory}' ({what}) could not be deleted: {exception.Message}");
        }
    }

    /// <summary>
    /// The part of an addons-folder-relative path below the addon folder, or a refusal.
    /// </summary>
    /// <remarks>
    /// The staged list is addons-folder-relative and already starts with the folder name, which is what the
    /// manifest stores; the staging directory is the folder itself, so the prefix has to be stripped exactly
    /// once. Stripping it twice is the classic way an install produces <c>pfUI/pfUI/pfUI.toc</c>.
    /// </remarks>
    private string RelativeWithinFolder(string addonRootRelativePath)
    {
        string prefix = Paths.FolderName + "/";

        if (!addonRootRelativePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{addonRootRelativePath}' is not inside the '{Paths.FolderName}' folder, so it cannot be swapped in.");
        }

        string within = addonRootRelativePath[prefix.Length..];

        // The staged file list already excludes .git, but a source that got that wrong must not put a
        // repository's history into the user's game folder.
        if (within.Length == 0 || within.Split('/').Any(segment => segment == ".git"))
        {
            throw new InvalidOperationException(
                $"'{addonRootRelativePath}' is repository metadata, not addon content, so it cannot be installed.");
        }

        return within;
    }

    private static string ToPlatformPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);
}
