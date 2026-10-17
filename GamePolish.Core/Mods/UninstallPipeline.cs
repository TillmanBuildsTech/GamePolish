using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Mods;

/// <summary>
/// The uninstall operation: snapshot the mod, delete its folder, forget its manifest entry - in that order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the whole promise.</b> The snapshot is created before the first byte is deleted (invariant
/// #1), the manifest entry is removed last, and a failure to remove the entry after the files are gone is
/// reported rather than swallowed - the files are deleted by then, so pretending the operation never happened
/// would be a lie the Backups view would have to correct.
/// </para>
/// <para>
/// <b>A disabled mod is uninstalled from the data root, not the game.</b> Its files are parked under
/// <c>staging/disabled/&lt;FolderName&gt;</c>, but the backup's <c>relativePaths</c> are still game-root-relative
/// (<c>&lt;mod_layout.destination&gt;/&lt;FolderName&gt;/...</c>), because that is where a restore has to put them
/// back. There is deliberately no second backup layout for the disabled case: the Backups view shows one shape of
/// path for both, and a restore from either snapshot lands where the game expects the addon.
/// </para>
/// <para>
/// <b>The bytes of a parked addon come from the parked tree.</b> That is what <c>205</c>'s optional
/// <c>sourceRoot</c> and its per-path map exist for: the snapshot records the game-relative path while reading the
/// content from <c>staging/disabled/&lt;FolderName&gt;</c>. Recording the right paths without the content would
/// make this backup a promise the product cannot keep - a restore of it would produce an empty folder - so the
/// uninstall of a disabled mod is the one caller that has to pass both.
/// </para>
/// <para>
/// <b>Nothing here is retention policy.</b> Uninstall never deletes the backups it makes; removing a mod's
/// backups is the Backups view's job (<c>306</c>) under the retention policy (<c>604</c>).
/// </para>
/// </remarks>
public sealed class UninstallPipeline
{
    private const string ResolvingPhase = "resolving";
    private const string BackupPhase = "backing-up";
    private const string RemovingPhase = "removing";
    private const string RecordingPhase = "recording";

    private readonly IGamesRegistry _games;
    private readonly IGameDescriptorLoader _descriptors;
    private readonly IBackupEngine _backups;
    private readonly IManifestStore _manifest;
    private readonly StagingArea _staging;
    private readonly IFileOps _files;
    private readonly IOperationLog? _log;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the pipeline with the production implementations of everything it needs.</summary>
    /// <param name="paths">The resolved data root: snapshots and the manifest live under it.</param>
    /// <remarks>
    /// The convenience constructor the CLI (<c>213</c>) and the desktop app use. A caller that needs a
    /// different seam - a test, or a view model that shares one manifest store - uses the full constructor.
    /// </remarks>
    public UninstallPipeline(GamePolishPaths paths)
        : this(
            paths,
            new GamesRegistry(paths, new GameDescriptorLoader(paths)),
            new GameDescriptorLoader(paths),
            new BackupEngine(paths),
            new ManifestStore(paths),
            new PathValidator(),
            new FileOps())
    {
    }

    /// <summary>Initializes the pipeline with injected collaborators.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="games">Which games are tracked, and where each is installed.</param>
    /// <param name="descriptors">The descriptor source: the folder mods live in.</param>
    /// <param name="backups">The snapshot engine (<c>205</c>).</param>
    /// <param name="manifest">The installed-mod record (<c>206</c>).</param>
    /// <param name="pathValidator">Validates every path that came from the manifest (<c>204</c>).</param>
    /// <param name="files">The mutating-call seam, shared with the snapshot so the order is observable.</param>
    /// <param name="log">The rolling log, or <see langword="null"/> when nobody keeps one (tests).</param>
    /// <param name="timeProvider">The clock behind the operation's log entries.</param>
    public UninstallPipeline(
        GamePolishPaths paths,
        IGamesRegistry games,
        IGameDescriptorLoader descriptors,
        IBackupEngine backups,
        IManifestStore manifest,
        IPathValidator pathValidator,
        IFileOps files,
        IOperationLog? log = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pathValidator);
        ArgumentNullException.ThrowIfNull(files);

        _games = games;
        _descriptors = descriptors;
        _backups = backups;
        _manifest = manifest;
        _staging = new StagingArea(paths, pathValidator, files);
        _files = files;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Removes a mod's files and its manifest entry, after snapshotting them.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary, and before the delete.</param>
    /// <returns>
    /// <see cref="OperationStatus.Succeeded"/> with the backup timestamp and the file count in the message;
    /// <see cref="OperationStatus.Failed"/> with a code when the mod is not installed, its game is gone, or the
    /// delete or the manifest write failed; <see cref="OperationStatus.Cancelled"/> when the caller stopped it
    /// before the delete, with the snapshot kept.
    /// </returns>
    /// <remarks>
    /// A cancellation after the folder is deleted is not reported as cancelled: the files are gone, and a
    /// "nothing was changed" toast under a folder that is no longer there is the one message this product must
    /// never show. Cancelling is only observed up to the delete.
    /// </remarks>
    public Task<OperationResult> UninstallAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        return Task.FromResult(Uninstall(gameId, modId, progress, cancellationToken));
    }

    private OperationResult Uninstall(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ModRun run = new(_timeProvider, _log);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, ResolvingPhase, 0, 0, modId);

            InstalledMod mod = _manifest.Find(gameId, modId)
                ?? throw new ModStateException(
                    OperationErrorCode.ModNotInstalled,
                    $"'{modId}' is not installed for '{gameId}', so there is nothing to remove.");

            ModTarget target = ModTarget.Resolve(_games, _descriptors, gameId, mod);

            run.ModId = mod.Id;
            run.Name = mod.Name;
            run.Entry(
                "info",
                $"Removing {mod.Name} ({mod.FolderName}) from '{Path.GetFileName(target.AddonRoot)}' under {target.InstallPath}.");

            // Where the files actually are. For a disabled mod that is the parked copy under the data root;
            // the manifest's Enabled flag is the only thing that says which, and it is the reason a disable
            // has to write it.
            bool parked = !mod.Enabled;
            string sourceFolder = parked ? _staging.DisabledPath(gameId, mod.FolderName) : target.GameFolder;
            bool filesPresent = Directory.Exists(sourceFolder);
            int fileCount = filesPresent ? CountFiles(sourceFolder) : 0;

            if (!filesPresent)
            {
                run.Entry(
                    "warn",
                    $"The '{mod.FolderName}' folder is not where the manifest says it is, so only the record is left to remove.",
                    DescribeFiles(mod));
            }

            // 1. Snapshot. Before the first byte is deleted, and after nothing but reads. A parked addon's
            //    content is not under the game root, so it is read through sourceRoot while its recorded paths
            //    stay game-relative - see the type's remarks.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, BackupPhase, 0, 0, mod.FolderName);

            IReadOnlyList<string> backupPaths = target.BackupPathsFor(sourceFolder);
            BackupEvent created = _backups.Create(
                gameId,
                target.InstallPath,
                BackupTrigger.ModUninstall,
                sourceName: mod.Name,
                sourceVersion: mod.Version,
                relativePaths: backupPaths,
                sourceRoot: parked ? _staging.DisabledRoot(gameId) : null,
                sourceRelativePaths: parked ? target.BackupSourceMapFor(sourceFolder) : null);

            run.BackupTimestamp = created.Timestamp;
            run.Entry(
                "info",
                $"Took backup {created.Timestamp} of {backupPaths.Count} path(s) before removing anything.",
                created.Timestamp);

            // 2. Delete the folder - and only the folder. The addon root is the game's and is never touched.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, RemovingPhase, 0, Math.Max(fileCount, 0), mod.FolderName);

            if (filesPresent)
            {
                _files.DeleteDirectory(sourceFolder, recursive: true);

                run.Entry("info", $"Deleted '{mod.FolderName}' ({fileCount} file(s)).", sourceFolder);

                if (!mod.Enabled)
                {
                    DeleteEmptyDisabledRoot(gameId, run);
                }
            }

            // 3. Forget it. This is the step a user cannot do by hand: a folder without a record is an orphan.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, RecordingPhase, 0, 1, modId);

            _manifest.Remove(gameId, modId);

            run.Entry("info", $"Removed the manifest entry for {mod.Id}.");

            string message = filesPresent
                ? $"Removed {mod.Name}: {fileCount} file(s) deleted. Backup {created.Timestamp} keeps the previous copy."
                : $"{mod.Name} had no files left in the game folder; only its install record was removed.";

            return OperationResult.Success(
                message,
                gameId,
                modId,
                created.Timestamp,
                run.Entries,
                run.Detail());
        }
        catch (Exception exception)
        {
            return Abandon(run, exception, gameId, modId);
        }
    }

    /// <summary>
    /// Turns a thrown failure into a result, with the two messages the caller has to distinguish: a failure
    /// before the delete changed nothing, and a failure after it left files gone and a record behind.
    /// </summary>
    /// <remarks>
    /// The second case is the one this method exists for. It names the backup, because the backup is the only
    /// thing that can put the files back, and it does not attempt a restore on the user's behalf: restoring
    /// them would re-create an addon the user asked to remove.
    /// </remarks>
    private OperationResult Abandon(ModRun run, Exception exception, string gameId, string modId)
    {
        OperationResult mapped = OperationResult.FromException(exception, gameId, modId, run.Entries);

        if (mapped.Status == OperationStatus.Cancelled)
        {
            return OperationResult.Cancelled(
                run.BackupTimestamp is null
                    ? "Uninstall cancelled. Nothing was removed."
                    : $"Uninstall cancelled. Nothing was removed, and the backup {run.BackupTimestamp} is kept.",
                gameId,
                modId,
                run.BackupTimestamp,
                run.Entries,
                run.Detail());
        }

        bool manifestStillLists = _manifest.Find(gameId, modId) is not null;

        string message = manifestStillLists && run.BackupTimestamp is not null
            ? $"The files for '{run.Name ?? modId}' were removed, but its install record could not be updated. "
                + $"Restore backup {run.BackupTimestamp} to put the files back."
            : mapped.Message;

        // The manifest is the authority on "did the delete happen and the record survive": if it still lists
        // the mod after a delete that succeeded, that is the state the user has to be told about.
        return OperationResult.Failure(
            mapped.Code,
            message,
            Combine(mapped.Detail, run.Detail()),
            cause: null,
            gameId: gameId,
            modId: modId,
            backupTimestamp: run.BackupTimestamp,
            entries: run.Entries);
    }

    /// <summary>Deletes <c>staging/disabled</c> when the last parked addon just left it empty.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="run">The run, for the log entry.</param>
    /// <remarks>
    /// The engine created this directory, so removing it when it is empty leaves the layout exactly as it was
    /// before the first disable. A refusal is ignored: an empty directory under the data root is housekeeping,
    /// not a failure to remove a mod.
    /// </remarks>
    private void DeleteEmptyDisabledRoot(string gameId, ModRun run)
    {
        if (_staging.TryRemoveEmptyRoot(gameId))
        {
            run.Entry("info", $"Removed the empty '{StagingArea.DisabledDirectoryName}' folder from the staging area.");
        }
    }

    private static int CountFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();

    /// <summary>The manifest's file list, for the detail of a failure that needs it as evidence.</summary>
    private static string DescribeFiles(InstalledMod mod) =>
        mod.Files.Count == 0
            ? "The manifest lists no files for this mod."
            : "The manifest lists these files:\n" + string.Join('\n', mod.Files.Select(file => "  " + file.RelativePath));

    private static string? Combine(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first.TrimEnd()}\n\n{second}";
    }
}
