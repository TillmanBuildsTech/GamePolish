using System.Security.Cryptography;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.IO;
using GamePolish.Core.Install;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Mods;

/// <summary>
/// The checkbox: disable parks a mod's folder under the data root, enable puts it back - both after a snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disable moves; enable copies.</b> Parking is a rename of <c>&lt;addonRoot&gt;/&lt;FolderName&gt;</c> into
/// <c>staging/disabled/&lt;FolderName&gt;</c>, which leaves exactly one copy of the mod on disk (invariant #4's
/// two roots, no third). Enabling copies the parked tree into the addon folder through <c>208</c>'s swap and
/// only deletes the parked copy once the manifest says the mod is enabled - so a failure anywhere in the middle
/// leaves the parked copy, the one copy that must never be lost, untouched. That is the difference between
/// "a failure leaves the mod disabled" and "a failure leaves the mod nowhere".
/// </para>
/// <para>
/// <b>A disable takes a backup even though it only moves files.</b> Invariant #1 does not say "before a delete",
/// it says before the first byte changes in a game directory, and a move out of the game directory changes it.
/// </para>
/// <para>
/// <b>Both directions are idempotent and take no snapshot when nothing changes.</b> A double-click on the
/// checkbox must not produce a second backup: the state is read from the manifest first, and an operation that
/// finds the mod already where the caller wants it returns success before the snapshot step.
/// </para>
/// <para>
/// <b>Copies, not links.</b> <c>library.md</c> says "copies/symlinks"; the MVP copies. A symlinked addon would
/// need Developer Mode on Windows and would put the game's loader on a path under the data root, which is a
/// <c>604</c>-era optimisation rather than a phase-2 one.
/// </para>
/// </remarks>
public sealed class EnableDisablePipeline
{
    private const string ValidatingPhase = "validating";
    private const string BackupPhase = "backing-up";
    private const string ParkingPhase = "parking";
    private const string EnablingPhase = "enabling";
    private const string RecordingPhase = "recording";
    private const string CleaningPhase = "cleaning-up";

    private readonly IGamesRegistry _games;
    private readonly IGameDescriptorLoader _descriptors;
    private readonly IBackupEngine _backups;
    private readonly IManifestStore _manifest;
    private readonly StagingArea _staging;
    private readonly IPathValidator _pathValidator;
    private readonly SwapOperations _swap;
    private readonly IFileOps _files;
    private readonly IOperationLog? _log;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the pipeline with the production implementations of everything it needs.</summary>
    /// <param name="paths">The resolved data root: the parked copies live under it.</param>
    /// <remarks>
    /// The convenience constructor the CLI (<c>213</c>) and the desktop app use. A caller that needs a different
    /// seam - a test, or a view model that shares one manifest store - uses the full constructor.
    /// </remarks>
    public EnableDisablePipeline(GamePolishPaths paths)
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
    /// <param name="backups">The snapshot engine (<c>205</c>), which must record the trigger and source name.</param>
    /// <param name="manifest">The installed-mod record (<c>206</c>), which says which copy is canonical.</param>
    /// <param name="pathValidator">Validates every path that came from the manifest (<c>204</c>).</param>
    /// <param name="files">The mutating-call seam, shared with the snapshot and the swap so the order is observable.</param>
    /// <param name="log">The rolling log, or <see langword="null"/> when nobody keeps one (tests).</param>
    /// <param name="timeProvider">The clock behind the log entries and the swap directory names.</param>
    public EnableDisablePipeline(
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
        _pathValidator = pathValidator;
        _files = files;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _swap = new SwapOperations(files, pathValidator, _timeProvider);
    }

    /// <summary>Puts a disabled mod's files back into the game folder, after snapshotting the game folder.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary and before every file copied.</param>
    /// <returns>
    /// <see cref="OperationStatus.Succeeded"/> - including when the mod was already enabled, without a snapshot;
    /// <see cref="OperationStatus.Failed"/> with <c>StagedCopyMissing</c> when the parked copy is gone and the
    /// game folder does not have it either; <see cref="OperationStatus.Cancelled"/> when the caller stopped it,
    /// with the mod still disabled and its parked copy intact.
    /// </returns>
    public Task<OperationResult> EnableAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        return Task.FromResult(Enable(gameId, modId, progress, cancellationToken));
    }

    /// <summary>Parks a mod's files under the data root, after snapshotting the game folder.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary, up to the manifest write.</param>
    /// <returns>
    /// <see cref="OperationStatus.Succeeded"/> - including when the mod was already disabled, without a snapshot;
    /// <see cref="OperationStatus.Failed"/> with <c>ModFolderMissing</c> when the game folder does not have the
    /// mod's folder; <see cref="OperationStatus.Cancelled"/> when the caller stopped it, with the mod left
    /// enabled and the game folder intact.
    /// </returns>
    public Task<OperationResult> DisableAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        return Task.FromResult(Disable(gameId, modId, progress, cancellationToken));
    }

    private OperationResult Enable(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ModRun run = new(_timeProvider, _log);
        SwapSession? swap = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            InstalledMod mod = Find(gameId, modId, run);
            run.Progress(progress, ValidatingPhase, 0, mod.Files.Count, mod.FolderName);

            // No-op before anything else: the state in the manifest is the answer, and asking the file system
            // whether it agrees is how a second click turns into a second backup (criterion 7).
            if (mod.Enabled)
            {
                run.Entry("info", $"{mod.Name} is already enabled; no files were copied and no backup was taken.");

                return OperationResult.Success(
                    $"{mod.Name} is already enabled. Nothing was changed.",
                    gameId,
                    modId,
                    entries: run.Entries);
            }

            ModTarget target = ModTarget.Resolve(_games, _descriptors, gameId, mod);
            string parked = _staging.DisabledPath(gameId, mod.FolderName);

            // 1. The only copy. If it is gone there is nothing to put back, and saying so is better than
            //    flipping the flag and leaving the user with a Library row that claims files that do not exist.
            if (!Directory.Exists(parked))
            {
                string? recovery = NewestBackupFor(gameId, mod.Name);

                run.Note(DescribeFiles(mod));

                throw new ModStateException(
                    OperationErrorCode.StagedCopyMissing,
                    $"The files for '{mod.Name}' are neither in the game folder nor in the staging area, so it cannot be enabled. Reinstall it from its source."
                    + (recovery is null ? string.Empty : $" The newest backup for it is {recovery}; restore it from the Backups view."));
            }

            // 2. The manifest can predate a game being moved or copied from another machine, so its file list
            //    is re-validated against the addon root before a byte of it is written (work order, step 3).
            IReadOnlyList<PathValidationFailure> failures =
                _pathValidator.ValidateAll(target.AddonRoot, mod.Files.Select(file => file.RelativePath));

            if (failures.Count > 0)
            {
                throw new PathValidationException(failures);
            }

            // 3. Snapshot - always, even when the destination has nothing to copy. That is what library.md
            //    means by "a backup is created on first enable", and 205 creates the snapshot directory even
            //    with an empty copy list.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, BackupPhase, 0, 0, mod.FolderName);

            BackupEvent created = _backups.Create(
                gameId,
                target.InstallPath,
                BackupTrigger.EnableDisable,
                sourceName: mod.Name,
                sourceVersion: mod.Version,
                relativePaths: target.BackupPathsFor(target.GameFolder));

            run.BackupTimestamp = created.Timestamp;
            run.Entry(
                "info",
                $"Took backup {created.Timestamp} of {created.Files.Count} path(s) before putting '{mod.FolderName}' back.",
                created.Timestamp);

            if (Directory.Exists(target.GameFolder))
            {
                run.Entry(
                    "warn",
                    $"A '{mod.FolderName}' folder was already in the game folder; it was backed up and is replaced by the parked copy.",
                    target.GameFolder);
            }

            // 4. The swap: copy into the .game-polish-new-* sibling, then rename it into place.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, EnablingPhase, 0, mod.Files.Count, mod.FolderName);

            swap = _swap.Begin(target.AddonRoot, mod.FolderName);
            swap.CopyExistingDirectory(parked, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            swap.Activate();

            run.Entry("info", $"Copied the parked '{mod.FolderName}' folder into the game.", swap.Paths.Destination);

            // 5. Record, then clean up.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, RecordingPhase, 0, 1, modId);

            _manifest.SetEnabled(gameId, modId, true);

            run.Progress(progress, CleaningPhase, 0, 1, mod.FolderName);

            Commit(run, swap);
            DeleteParkedCopy(gameId, parked, run);

            int fileCount = CountFiles(target.GameFolder);

            run.Entry("info", $"{mod.Name} is enabled: {fileCount} file(s) in the game folder.", target.GameFolder);

            return OperationResult.Success(
                $"{mod.Name} is enabled: {fileCount} file(s) back in the game folder. Backup {created.Timestamp}.",
                gameId,
                modId,
                created.Timestamp,
                run.Entries);
        }
        catch (Exception exception)
        {
            return AbandonEnable(run, exception, gameId, modId, swap);
        }
    }

    private OperationResult Disable(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ModRun run = new(_timeProvider, _log);
        ModTarget? target = null;
        string? parked = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            InstalledMod mod = Find(gameId, modId, run);
            run.Progress(progress, ValidatingPhase, 0, mod.Files.Count, mod.FolderName);

            if (!mod.Enabled)
            {
                run.Entry("info", $"{mod.Name} is already disabled; no files were moved and no backup was taken.");

                return OperationResult.Success(
                    $"{mod.Name} is already disabled. Nothing was changed.",
                    gameId,
                    modId,
                    entries: run.Entries);
            }

            target = ModTarget.Resolve(_games, _descriptors, gameId, mod);

            // The folder has to be there to be moved. The manifest's file list is the evidence the user is
            // given - "it says these files exist" - and the answer is a failure, never a silent flag flip.
            if (!Directory.Exists(target.GameFolder))
            {
                run.Note(DescribeFiles(mod));

                throw new ModStateException(
                    OperationErrorCode.ModFolderMissing,
                    $"The '{mod.FolderName}' folder is not in the game folder any more, so there is nothing to disable. Nothing was changed.");
            }

            int fileCount = CountFiles(target.GameFolder);

            // 1. Snapshot, before the move. A disable changes the game directory even though it deletes nothing.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, BackupPhase, 0, 0, mod.FolderName);

            BackupEvent created = _backups.Create(
                gameId,
                target.InstallPath,
                BackupTrigger.EnableDisable,
                sourceName: mod.Name,
                sourceVersion: mod.Version,
                relativePaths: target.BackupPathsFor(target.GameFolder));

            run.BackupTimestamp = created.Timestamp;
            run.Entry(
                "info",
                $"Took backup {created.Timestamp} of the '{mod.FolderName}' folder before moving it.",
                created.Timestamp);

            // 2. Park it. The parked copy is never deleted while the mod is disabled: a disable has to be
            //    reversible with no network access, which is the whole reason it is a move and not a delete.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, ParkingPhase, 0, 1, mod.FolderName);

            parked = _staging.DisabledPath(gameId, mod.FolderName);
            _files.CreateDirectory(_staging.DisabledRoot(gameId));

            if (Directory.Exists(parked))
            {
                // Only reachable when an earlier enable could not remove its duplicate. The game folder is the
                // copy the manifest vouches for and the snapshot above protects it, so the stale one goes.
                run.Entry(
                    "warn",
                    $"A stale parked copy of '{mod.FolderName}' was replaced; the game folder is the copy the manifest vouches for.",
                    parked);

                _files.DeleteDirectory(parked, recursive: true);
            }

            MoveIntoStagingArea(target.GameFolder, parked, run);

            // 3. Flip the flag. There is no cancellation check after this: the manifest write is the commit
            //    point, and reporting a cancellation for a completed transition would leave the checkbox wrong.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, RecordingPhase, 0, 1, modId);

            _manifest.SetEnabled(gameId, modId, false);

            run.Entry("info", $"{mod.Name} is disabled: {fileCount} file(s) parked in the staging area.", parked);

            return OperationResult.Success(
                $"{mod.Name} is disabled: {fileCount} file(s) moved to the staging area. Backup {created.Timestamp}.",
                gameId,
                modId,
                created.Timestamp,
                run.Entries);
        }
        catch (Exception exception)
        {
            return AbandonDisable(run, exception, gameId, modId, target, parked);
        }
    }

    /// <summary>Reads the manifest entry, or refuses with the code the UI branches on.</summary>
    private InstalledMod Find(string gameId, string modId, ModRun run)
    {
        InstalledMod mod = _manifest.Find(gameId, modId)
            ?? throw new ModStateException(
                OperationErrorCode.ModNotInstalled,
                $"'{modId}' is not installed for '{gameId}', so there is nothing to enable or disable.");

        run.ModId = mod.Id;
        run.Name = mod.Name;

        return mod;
    }

    /// <summary>
    /// Moves a directory into the staging area, falling back to a verified copy when a rename cannot cross volumes.
    /// </summary>
    /// <param name="source">The mod's folder in the game directory.</param>
    /// <param name="destination">The parked location under the data root.</param>
    /// <param name="run">The run, for the log entries.</param>
    /// <remarks>
    /// <para>
    /// <b>Why the fallback exists.</b> <c>Directory.Move</c> is a rename, and a rename cannot cross volumes: a game
    /// on <c>D:</c> with the data root on <c>C:</c> - the default, because the default data root is under the user
    /// profile - fails with <see cref="IOException"/> and would otherwise make disable impossible for the most
    /// common install there is.
    /// </para>
    /// <para>
    /// <b>Copy, verify, then delete - in that order.</b> Every file is copied, then every copy is checked against
    /// the source by length and SHA-256, and only when all of them match is the source tree deleted. A failure
    /// anywhere before that leaves the game folder untouched and removes the partial copy, which is what makes
    /// "a failed disable changes nothing" true (criterion 9).
    /// </para>
    /// </remarks>
    private void MoveIntoStagingArea(string source, string destination, ModRun run)
    {
        try
        {
            _files.MoveDirectory(source, destination);

            run.Entry("info", $"Moved '{Path.GetFileName(source)}' into the staging area.", destination);

            return;
        }
        catch (IOException exception)
        {
            run.Entry(
                "warn",
                $"'{Path.GetFileName(source)}' is on a different volume from the data root, so its files are copied, verified, then removed from the game folder.",
                exception.Message);
        }

        try
        {
            int copied = CopyVerifyAndDelete(source, destination);

            run.Entry(
                "info",
                $"Copied and verified {copied} file(s) into the staging area, then removed the game folder's copy.",
                destination);
        }
        catch
        {
            // A partial copy is not a parked addon; leaving it would make the next enable restore half a mod.
            TryDelete(destination);

            throw;
        }
    }

    /// <summary>Copies a tree, verifies every byte of it, and only then deletes the original.</summary>
    /// <param name="source">The tree to move.</param>
    /// <param name="destination">Where it goes; must not exist.</param>
    /// <returns>The number of files copied and verified.</returns>
    /// <exception cref="IOException">A copy did not verify, so the original was left in place.</exception>
    private int CopyVerifyAndDelete(string source, string destination)
    {
        List<(string RelativePath, long Length, string Hash)> files = Fingerprint(source);

        _files.CreateDirectory(destination);

        foreach ((string relativePath, _, _) in files)
        {
            _files.CopyFile(
                Path.Combine(source, ToPlatformPath(relativePath)),
                Path.Combine(destination, ToPlatformPath(relativePath)),
                overwrite: false);
        }

        foreach ((string relativePath, long length, string hash) in files)
        {
            string copy = Path.Combine(destination, ToPlatformPath(relativePath));

            if (!File.Exists(copy) || new FileInfo(copy).Length != length || HashOf(copy) != hash)
            {
                throw new IOException(
                    $"The copy of '{relativePath}' did not match the original, so '{Path.GetFileName(source)}' was left where it was.");
            }
        }

        _files.DeleteDirectory(source, recursive: true);

        return files.Count;
    }

    /// <summary>Deletes the parked copy once the game folder is canonical again.</summary>
    /// <param name="parked">The parked folder.</param>
    /// <param name="run">The run, for the log entries.</param>
    /// <remarks>
    /// A refusal is a warning, not a failure: the mod is enabled, the game folder is complete, and the leftover
    /// is a duplicate under the data root that the next disable replaces. Turning a successful enable into a
    /// failed one over housekeeping would be the wrong trade - the same judgement <c>208</c> makes about its
    /// leftover <c>.game-polish-old-*</c> folders.
    /// </remarks>
    private void DeleteParkedCopy(string gameId, string parked, ModRun run)
    {
        if (!Directory.Exists(parked))
        {
            return;
        }

        try
        {
            _files.DeleteDirectory(parked, recursive: true);

            run.Entry("info", "Removed the parked copy; the game folder is canonical again.", parked);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Entry(
                "warn",
                "The parked copy could not be removed after the mod was enabled; it is a duplicate under the data root and the next disable replaces it.",
                exception.Message);
        }

        // The layout returns to what it was before the first disable, when this was the last parked addon.
        _staging.TryRemoveEmptyRoot(gameId);
    }

    /// <summary>Removes the folder the previous version of the addon occupied, after the manifest was written.</summary>
    /// <param name="run">The run, for the log entries.</param>
    /// <param name="swap">The swap that just landed.</param>
    private static void Commit(ModRun run, SwapSession swap)
    {
        if (swap.Commit())
        {
            run.Entry("info", $"Removed the previous '{swap.Paths.FolderName}' folder from the game.");

            return;
        }

        run.Entry(
            "warn",
            $"The previous '{swap.Paths.FolderName}' folder could not be deleted and was left at {swap.LeftoverOldDirectory}. It is harmless and is swept at the next start.",
            swap.LeftoverOldDirectory);
    }

    /// <summary>
    /// Turns a failed or cancelled enable into a result, after undoing the copy it may have started.
    /// </summary>
    /// <remarks>
    /// The rollback is safe because the enable copies rather than moves: deleting the <c>-new-</c> tree and the
    /// folder that landed cannot touch the parked copy, so the mod is exactly as disabled as it was. That is the
    /// property the copy buys, and it is why this path never has to restore from a backup.
    /// </remarks>
    private OperationResult AbandonEnable(ModRun run, Exception exception, string gameId, string modId, SwapSession? swap)
    {
        SwapRollback? rollback = null;

        if (swap is { HasMutated: true } session && session.Phase != SwapPhase.Committed)
        {
            rollback = session.Rollback();

            run.Entry(
                rollback.Succeeded ? "info" : "error",
                rollback.Succeeded
                    ? "Rolled the game folder back; the mod is still disabled and its parked copy is untouched."
                    : "The game folder could not be fully rolled back.",
                rollback.Describe(run.BackupTimestamp));
        }

        OperationResult mapped = OperationResult.FromException(exception, gameId, modId, run.Entries);

        string? detail = Combine(mapped.Detail, rollback?.Describe(run.BackupTimestamp));
        detail = Combine(detail, run.Detail());

        if (mapped.Status == OperationStatus.Cancelled)
        {
            return OperationResult.Cancelled(
                "Enable cancelled. The mod is still disabled, its parked copy is intact, and the backup taken before the enable is kept.",
                gameId,
                modId,
                run.BackupTimestamp,
                run.Entries,
                detail);
        }

        return OperationResult.Failure(
            mapped.Code,
            mapped.Message,
            detail,
            cause: null,
            gameId: gameId,
            modId: modId,
            backupTimestamp: run.BackupTimestamp,
            entries: run.Entries);
    }

    /// <summary>
    /// Turns a failed or cancelled disable into a result, after putting the folder back if it was already parked.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="exception">What stopped the operation.</param>
    /// <param name="gameId">The game id.</param>
    /// <param name="modId">The mod id.</param>
    /// <param name="target">The resolved game and mod, when resolution got that far.</param>
    /// <param name="parked">The parked location, when the move had started.</param>
    /// <remarks>
    /// Undoing is the reverse move, and it covers three states: the move completed and the manifest write did not
    /// (put it back), the move completed and the source could not be deleted after a verified copy (the game
    /// folder is canonical, so drop the duplicate), and the move never completed (nothing to do). A rollback that
    /// cannot finish reports where the files are rather than deleting anything else.
    /// </remarks>
    private OperationResult AbandonDisable(
        ModRun run,
        Exception exception,
        string gameId,
        string modId,
        ModTarget? target,
        string? parked)
    {
        bool restored = true;
        string? stranded = null;

        if (target is not null && parked is not null && Directory.Exists(parked))
        {
            if (Directory.Exists(target.GameFolder))
            {
                TryDelete(parked);
            }
            else
            {
                try
                {
                    MoveBackIntoGameFolder(parked, target.GameFolder, run);
                }
                catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
                {
                    restored = false;
                    stranded = parked;

                    run.Entry(
                        "error",
                        "The game folder could not be put back after the failed disable.",
                        moveException.Message);
                }
            }
        }

        OperationResult mapped = OperationResult.FromException(exception, gameId, modId, run.Entries);

        string? detail = Combine(mapped.Detail, run.Detail());

        if (stranded is not null)
        {
            detail = Combine(
                detail,
                $"The mod's folder is at {stranded} and could not be moved back to {target!.GameFolder}. "
                + "Restore the backup named in this result to put it back.");
        }

        if (mapped.Status == OperationStatus.Cancelled)
        {
            return OperationResult.Cancelled(
                restored
                    ? "Disable cancelled. The game folder is unchanged, and the backup taken before the move is kept."
                    : "Disable cancelled, but the game folder could not be restored. See the details for where the files are.",
                gameId,
                modId,
                run.BackupTimestamp,
                run.Entries,
                detail);
        }

        return OperationResult.Failure(
            mapped.Code,
            restored ? mapped.Message : "The disable failed and the mod's folder could not be moved back. See the details for where it is.",
            detail,
            cause: null,
            gameId: gameId,
            modId: modId,
            backupTimestamp: run.BackupTimestamp,
            entries: run.Entries);
    }

    /// <summary>Moves a parked folder back into the game folder, with the same cross-volume fallback.</summary>
    /// <param name="source">The parked folder.</param>
    /// <param name="destination">The mod's folder in the game directory.</param>
    /// <param name="run">The run, for the log entries.</param>
    /// <remarks>
    /// The reverse direction of <see cref="MoveIntoStagingArea"/> and the same cross-volume problem, so a rename
    /// that fails falls back to the verified copy. Kept separate rather than parameterised because the success
    /// message would otherwise describe the wrong direction, and a log that lies about which way files went is
    /// worse than two small methods.
    /// </remarks>
    private void MoveBackIntoGameFolder(string source, string destination, ModRun run)
    {
        try
        {
            _files.MoveDirectory(source, destination);

            run.Entry("info", $"Moved '{Path.GetFileName(source)}' back into the game folder.", destination);
        }
        catch (IOException exception)
        {
            run.Entry(
                "warn",
                "The game folder is on a different volume from the data root, so the parked files are copied back, verified, then removed.",
                exception.Message);

            int copied = CopyVerifyAndDelete(source, destination);

            run.Entry("info", $"Copied and verified {copied} file(s) back into the game folder.", destination);
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                _files.DeleteDirectory(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original failure is what the caller is reporting; a leftover duplicate is swept later.
        }
    }

    /// <summary>The newest backup taken for this mod, as a recovery hint, or <see langword="null"/>.</summary>
    /// <param name="gameId">The game id.</param>
    /// <param name="name">The mod's display name, which is what the backup recorded as its source name.</param>
    /// <remarks>
    /// A hint only. If the index is unreadable the enable still has to fail with the reason it actually has, so
    /// nothing here is allowed to throw - the snapshot index is repaired from the Backups view.
    /// </remarks>
    private string? NewestBackupFor(string gameId, string name)
    {
        try
        {
            return _backups
                .List(gameId)
                .FirstOrDefault(candidate => string.Equals(candidate.SourceName, name, StringComparison.Ordinal))
                ?.Timestamp;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Every file in a tree with its length and hash, ordinally sorted so a report is stable.</summary>
    /// <param name="root">The tree to fingerprint.</param>
    private static List<(string RelativePath, long Length, string Hash)> Fingerprint(string root)
    {
        List<(string, long, string)> files = [];

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add((Portable(Path.GetRelativePath(root, file)), new FileInfo(file).Length, HashOf(file)));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Item1, right.Item1));

        return files;
    }

    private static string HashOf(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int CountFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();

    /// <summary>The manifest's file list, for the detail of a failure that needs it as evidence.</summary>
    private static string DescribeFiles(InstalledMod mod) =>
        mod.Files.Count == 0
            ? "The manifest lists no files for this mod."
            : "The manifest lists these files:\n" + string.Join('\n', mod.Files.Select(file => "  " + file.RelativePath));

    private static string Portable(string path) => path.Replace('\\', '/');

    private static string ToPlatformPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static string? Combine(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first.TrimEnd()}\n\n{second}";
    }
}
