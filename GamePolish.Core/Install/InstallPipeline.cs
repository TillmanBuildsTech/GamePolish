using System.Text;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Install;

/// <summary>
/// The install pipeline: the first caller that sequences every phase-2 component, and therefore the place
/// where the product's central promise either holds or does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The eight phases, in this order, and the order is the specification.</b> Resolve the game, stage the
/// source, validate every path, look for conflicts, take the snapshot, swap the folder, write the manifest,
/// clean up and report. Each phase either writes nothing at all or writes only inside the data root until
/// the snapshot exists; <see cref="_backups"/>'s <c>Create</c> is called before <c>SwapOperations</c> is
/// asked for its first directory, and that is invariant #1 in one sentence.
/// </para>
/// <para>
/// <b>Identity.</b> The manifest's mod id is <see cref="InstallRequest.ModId"/> when the caller has one (the
/// Discover view always does), and otherwise a slug of the staged addon folder name - <c>pfUI</c> becomes
/// <c>pfui</c>. The folder name is the only stable name an ad-hoc GitHub URL or a ZIP has, and slugging it
/// rather than storing it verbatim is what makes "the same mod" mean the same thing on a reinstall after the
/// user typed the URL with different capitalisation. The display name is
/// <see cref="InstallRequest.DisplayName"/> when given, else the folder name - never the slug, because the
/// Library shows the folder name as the mod's name for every unregistered mod anyway.
/// </para>
/// <para>
/// <b>What a failure is allowed to leave behind.</b> A snapshot is kept: it is harmless, it is the record of
/// what the attempt would have touched, and the invariant says the snapshot precedes the write. The staging
/// directory is removed. The game folder is byte-identical. No manifest entry exists for files that are not
/// there - a folder on disk the manifest does not know about is an orphan the user cannot uninstall, which is
/// why the previous tree is not deleted until <see cref="SwapSession.Commit"/> runs after the manifest write.
/// </para>
/// </remarks>
public sealed class InstallPipeline : IInstallPipeline
{
    private const string ResolvingPhase = "resolving";
    private const string StagingPhase = "staging";
    private const string ValidatingPhase = "validating";
    private const string ConflictPhase = "conflicts";
    private const string BackupPhase = "backing-up";
    private const string RecordingPhase = "recording";
    private const string CleaningPhase = "cleaning-up";

    private readonly GamePolishPaths _paths;
    private readonly IGamesRegistry _games;
    private readonly IGameDescriptorLoader _descriptors;
    private readonly ISourceAcquirer _sources;
    private readonly IBackupEngine _backups;
    private readonly IManifestStore _manifest;
    private readonly IPathValidator _pathValidator;
    private readonly SwapOperations _swap;
    private readonly IOperationLog? _log;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the pipeline with the production implementations of everything it sequences.
    /// </summary>
    /// <param name="paths">The resolved data root: staging, snapshots and the manifest all live under it.</param>
    /// <remarks>
    /// The convenience constructor the CLI (<c>213</c>) and the desktop app use. A caller that needs a
    /// different seam - a test, or a view model that owns one shared <c>ISourceAcquirer</c> - uses the full
    /// constructor below.
    /// </remarks>
    public InstallPipeline(GamePolishPaths paths)
        : this(
            paths,
            new GamesRegistry(paths, new GameDescriptorLoader(paths)),
            new GameDescriptorLoader(paths),
            new SourceAcquirer(paths),
            new BackupEngine(paths),
            new ManifestStore(paths),
            new PathValidator(),
            new FileOps())
    {
    }

    /// <summary>Initializes the pipeline with injected collaborators.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="games">Which games are tracked, and where each is installed.</param>
    /// <param name="descriptors">The descriptor source: the addons folder and the game's display name.</param>
    /// <param name="sources">The staging component (<c>207</c>).</param>
    /// <param name="backups">The snapshot engine (<c>205</c>).</param>
    /// <param name="manifest">The installed-mod record (<c>206</c>).</param>
    /// <param name="pathValidator">Validates every path that came from a mod source (<c>204</c>).</param>
    /// <param name="files">The mutating-call seam, shared by the snapshot and the swap so their order is observable.</param>
    /// <param name="log">The rolling log, or <see langword="null"/> when nobody keeps one (tests).</param>
    /// <param name="timeProvider">The clock behind the operation timestamps and the swap directory names.</param>
    public InstallPipeline(
        GamePolishPaths paths,
        IGamesRegistry games,
        IGameDescriptorLoader descriptors,
        ISourceAcquirer sources,
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
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pathValidator);
        ArgumentNullException.ThrowIfNull(files);

        _paths = paths;
        _games = games;
        _descriptors = descriptors;
        _sources = sources;
        _backups = backups;
        _manifest = manifest;
        _pathValidator = pathValidator;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _swap = new SwapOperations(files, pathValidator, _timeProvider);
    }

    /// <inheritdoc/>
    public async Task<ConflictReport> DetectConflictsAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Run run = new(request, _timeProvider, _log);

        try
        {
            Resolve(run, progress: null, cancellationToken);
            await StageAsync(run, progress: null, cancellationToken).ConfigureAwait(false);
            Validate(run, progress: null, cancellationToken);
            Identify(run);

            cancellationToken.ThrowIfCancellationRequested();

            run.Progress(null, ConflictPhase, 0, 0, run.Staged!.FolderName);
            run.ConflictReport = DetectConflicts(run);
            run.Entry(
                "info",
                run.ConflictReport.IsBlocked
                    ? $"{run.ConflictReport.BlockingCount} path(s) in '{run.Staged.FolderName}' would replace something this mod does not own."
                    : $"Nothing in '{run.Staged.FolderName}' conflicts with an installed mod or the user's own files.",
                run.ConflictReport.Describe());

            return run.ConflictReport;
        }
        finally
        {
            run.ReleaseStaging(_sources);
        }
    }

    /// <inheritdoc/>
    public async Task<OperationResult> InstallAsync(
        InstallRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Run run = new(request, _timeProvider, _log);

        try
        {
            // 1. Resolve.
            Resolve(run, progress, cancellationToken);

            // 2. Stage. Nothing inside the game's install folder has been touched at any point so far.
            await StageAsync(run, progress, cancellationToken).ConfigureAwait(false);

            // 3. Validate. Still nothing written anywhere but the data root.
            Validate(run, progress, cancellationToken);

            // 4. Conflicts.
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(progress, ConflictPhase, 0, 0, run.Staged!.FolderName);
            Identify(run);
            RefuseIfAlreadyInstalled(run);
            run.ConflictReport = DetectConflicts(run);
            RefuseIfBlocked(run);

            // 5. Backup - before the first byte.
            cancellationToken.ThrowIfCancellationRequested();
            TakeBackup(run, progress);

            // 6. Swap. From here on the game folder changes, and every failure has to be undone.
            cancellationToken.ThrowIfCancellationRequested();
            PerformSwap(run, progress, cancellationToken);

            // 7. Record. The manifest write is what turns a folder on disk into an installed mod.
            cancellationToken.ThrowIfCancellationRequested();
            Record(run, progress);

            // 8. Commit the swap, clean up, report.
            Finish(run, progress);

            return OperationResult.Success(
                $"{run.Name} {run.Staged.Version} installed into the '{run.Staged.FolderName}' folder.",
                request.GameId,
                run.ModId,
                run.BackupTimestamp,
                run.Entries,
                run.Detail());
        }
        catch (Exception exception)
        {
            return Abandon(run, exception);
        }
        finally
        {
            run.ReleaseStaging(_sources);
        }
    }

    /// <summary>Phase 1: the tracked game, its descriptor, and the folder mods live in.</summary>
    private void Resolve(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InstallRequest request = run.Request;
        run.Progress(progress, ResolvingPhase, 0, 0, request.GameId);

        TrackedGame? tracked = _games.Get(request.GameId);

        if (tracked is null)
        {
            throw new InstallException(
                OperationErrorCode.GameInstallPathMissing,
                $"'{request.GameId}' is not tracked yet, so there is no folder to install into. Add the game first.");
        }

        if (!Directory.Exists(tracked.InstallPath))
        {
            throw new InstallException(
                OperationErrorCode.GameInstallPathMissing,
                $"{tracked.Name} is recorded at {tracked.InstallPath}, but that folder is gone. Check the game's install folder, then try again.");
        }

        GameDescriptor? descriptor = _descriptors.LoadById(request.GameId);

        if (descriptor is null)
        {
            throw new InstallException(
                OperationErrorCode.GameNotFound,
                $"'{request.GameId}' is not a supported game: no descriptor named {request.GameId}.yaml was found.");
        }

        string addonRoot = descriptor.AddonRoot(tracked.InstallPath);

        if (!Directory.Exists(addonRoot))
        {
            throw new InstallException(
                OperationErrorCode.GameInstallPathMissing,
                $"{tracked.Name} has no '{descriptor.ModLayout.Destination}' folder under {tracked.InstallPath}, so there is nowhere to install to.");
        }

        run.Tracked = tracked;
        run.Descriptor = descriptor;
        run.AddonRoot = addonRoot;

        run.Entry(
            "info",
            $"Resolved {descriptor.Name}: mods live in '{descriptor.ModLayout.Destination}' under {tracked.InstallPath}.");
    }

    /// <summary>Phase 2: materialise the source under the data root.</summary>
    private async Task StageAsync(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InstallRequest request = run.Request;
        run.Progress(progress, StagingPhase, 0, 0, request.SourceUrl);

        SourceRequest sourceRequest = new()
        {
            GameId = request.GameId,
            SourceUrl = request.SourceType == ModSourceType.Zip ? ArchiveName(request) : request.SourceUrl,
            SourceType = request.SourceType,
            Branch = request.Branch,
            DataRoot = _paths.Root,

            // A fresh id per attempt: two installs of the same mod must not share a staging directory, and
            // the id is what 207's stale-staging sweep names.
            OperationId = Guid.NewGuid().ToString("N"),
        };

        StagedSource staged = request.SourceType switch
        {
            ModSourceType.Github => await _sources.StageGitAsync(sourceRequest, cancellationToken).ConfigureAwait(false),
            ModSourceType.Zip => await _sources
                .StageZipAsync(ArchiveStream(request), ArchiveName(request), sourceRequest, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InstallException(
                OperationErrorCode.SourceInvalid,
                $"'{request.SourceType}' is not a source type this installer can fetch."),
        };

        run.Staged = staged;

        run.Entry(
            "info",
            $"Staged '{staged.FolderName}' from {staged.SourceType} - {staged.Files.Count} file(s), {staged.VersionKind} {staged.Version}.",
            staged.StagingDirectory);
    }

    /// <summary>Phase 3: prove every staged path is safe, and sits inside the folder about to be created.</summary>
    private void Validate(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StagedSource staged = run.Staged!;
        run.Progress(progress, ValidatingPhase, 0, staged.Files.Count, null);

        IReadOnlyList<PathValidationFailure> failures =
            _pathValidator.ValidateAll(run.AddonRoot!, staged.Files.Select(file => file.RelativePath));

        if (failures.Count > 0)
        {
            // 204's own exception, not a wrapped one: its message and its Failures list are what
            // OperationResult.FromException already knows how to turn into PathUnsafe plus a full detail.
            throw new PathValidationException(failures);
        }

        string prefix = staged.FolderName + "/";
        string[] outside =
        [
            .. staged.Files
                .Select(file => file.RelativePath)
                .Where(path => !path.StartsWith(prefix, StringComparison.Ordinal)),
        ];

        if (outside.Length > 0)
        {
            run.Note("Files listed outside the addon folder:\n" + string.Join('\n', outside));

            throw new InstallException(
                OperationErrorCode.SourceInvalid,
                $"'{staged.FolderName}' lists {outside.Length} file(s) outside its own folder, so nothing was installed.");
        }

        run.Entry(
            "info",
            $"Validated {staged.Files.Count} path(s) against {run.AddonRoot}.");
    }

    /// <summary>Phase 4a: the mod's id and display name, from the request or from the staged folder.</summary>
    private static void Identify(Run run)
    {
        InstallRequest request = run.Request;
        StagedSource staged = run.Staged!;

        run.ModId = string.IsNullOrWhiteSpace(request.ModId) ? Slug(staged.FolderName) : request.ModId.Trim();
        run.Name = string.IsNullOrWhiteSpace(request.DisplayName) ? staged.FolderName : request.DisplayName.Trim();

        if (run.ModId.Length == 0)
        {
            throw new InstallException(
                OperationErrorCode.SourceInvalid,
                $"'{staged.FolderName}' does not yield a usable mod id, so the install could not be recorded. Pass a mod id with the request.");
        }
    }

    /// <summary>
    /// Phase 4b: refuse to install over an existing install of the same mod.
    /// </summary>
    /// <remarks>
    /// Accepted overwrites do not change this. "Install" and "update" are different promises - an update keeps
    /// the original install time and compares versions, and it is what the Library's Update button runs - and
    /// letting an install quietly take the update path would make the two indistinguishable in the manifest.
    /// The message names Update so the UI can offer it.
    /// </remarks>
    private void RefuseIfAlreadyInstalled(Run run)
    {
        if (_manifest.Find(run.Request.GameId, run.ModId!) is null)
        {
            return;
        }

        throw new InstallException(
            OperationErrorCode.ModAlreadyInstalled,
            $"'{run.Name}' is already installed. Use Update to replace it with this version, or remove it first.");
    }

    /// <summary>Phase 4c: what the install would replace, in the three buckets that mean different things.</summary>
    private ConflictReport DetectConflicts(Run run)
    {
        string gameId = run.Request.GameId;
        string modId = run.ModId!;
        string addonRoot = run.AddonRoot!;
        StagedSource staged = run.Staged!;

        Buckets buckets = new();

        foreach (string owner in _manifest.FindFolderOwners(gameId, staged.FolderName))
        {
            buckets.Add(IsSameId(owner, modId), staged.FolderName + "/");
        }

        HashSet<string> stagedPaths = new(StringComparer.FromComparison(PathComparer.PathComparison));
        foreach (InstalledFileEntry file in staged.Files)
        {
            stagedPaths.Add(file.RelativePath);
            Classify(buckets, gameId, modId, file.RelativePath, File.Exists(Resolve(addonRoot, file.RelativePath)));
        }

        // The swap replaces the whole folder, so anything already inside it that the new tree does not list
        // is about to disappear - even when no staged file collides with it. Enumerating what is there is the
        // difference between "the user's hand-edited file was reported" and "the user's hand-edited file was
        // silently deleted".
        string folder = Path.Combine(addonRoot, staged.FolderName);

        if (Directory.Exists(folder))
        {
            foreach (string existing in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(addonRoot, existing).Replace('\\', '/');

                if (!stagedPaths.Contains(relative))
                {
                    Classify(buckets, gameId, modId, relative, exists: true);
                }
            }
        }

        return new ConflictReport
        {
            OwnedByThisMod = buckets.OwnedByThisMod,
            OwnedByOtherMods = buckets.OwnedByOtherMods,
            UnownedExisting = buckets.UnownedExisting,
        };
    }

    /// <summary>Phase 4d: stop before the backup unless the caller accepted replacing what is not ours.</summary>
    private static void RefuseIfBlocked(Run run)
    {
        ConflictReport report = run.ConflictReport!;

        if (report.IsBlocked && !run.Request.AcceptOverwrites)
        {
            run.Note(report.Describe());

            throw new InstallException(
                OperationErrorCode.ConflictsUnresolved,
                $"'{run.Staged!.FolderName}' would replace {report.BlockingCount} file(s) that another mod or you put there. Nothing was installed.");
        }

        if (report.IsBlocked)
        {
            run.Note("Replaced with the caller's consent:\n" + report.Describe());
            run.Entry(
                "warn",
                $"Replacing {report.BlockingCount} path(s) that '{run.Staged!.FolderName}' does not own, at the caller's request.",
                report.Describe());
        }
        else
        {
            run.Entry("info", $"No conflicts for '{run.Staged!.FolderName}'.");
        }
    }

    /// <summary>Phase 5: the snapshot, before anything in the game folder is written.</summary>
    private void TakeBackup(Run run, IProgress<OperationProgress>? progress)
    {
        InstallRequest request = run.Request;
        StagedSource staged = run.Staged!;

        run.Progress(progress, BackupPhase, 0, 0, staged.FolderName);

        IReadOnlyList<string> paths = BackupPaths(run);

        BackupEvent created = _backups.Create(
            request.GameId,
            run.Tracked!.InstallPath,
            BackupTrigger.ModInstall,
            sourceName: run.Name ?? staged.FolderName,
            sourceVersion: staged.Version,
            relativePaths: paths);

        run.BackupTimestamp = created.Timestamp;

        run.Entry(
            "info",
            $"Took backup {created.Timestamp} of {paths.Count} path(s) before writing anything.",
            created.Timestamp);
    }

    /// <summary>Phase 6: copy into the sibling, move the old folder aside, move the new one into place.</summary>
    private void PerformSwap(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        SwapSession swap = _swap.Begin(run.AddonRoot!, run.Staged!.FolderName);
        run.Swap = swap;

        swap.CopyStagedTree(run.Staged, progress, cancellationToken);

        // Between files and renames the token is checked once more: cancelling with the copy complete must
        // not proceed to the point where the game folder is briefly missing.
        cancellationToken.ThrowIfCancellationRequested();

        swap.Activate();

        run.Entry(
            "info",
            $"Moved '{run.Staged.FolderName}' into place from {swap.Paths.NewDirectory}.",
            swap.Paths.Destination);
    }

    /// <summary>Phase 7: record the install. A failure here rolls the folder back.</summary>
    private void Record(Run run, IProgress<OperationProgress>? progress)
    {
        StagedSource staged = run.Staged!;

        run.Progress(progress, RecordingPhase, 0, 1, run.ModId);

        InstalledMod mod = new()
        {
            Id = run.ModId!,
            Name = run.Name!,
            SourceUrl = staged.SourceUrl,
            SourceType = staged.SourceType,
            FolderName = staged.FolderName,
            Version = staged.Version,
            VersionKind = staged.VersionKind,
            Enabled = true,
            InstalledAt = run.StartedAt,
            UpdatedAt = run.StartedAt,

            // Verbatim: the staged list is addons-folder-relative and already carries the folder name, which
            // is exactly what the manifest stores. Prefixing or stripping here is how a file list stops
            // matching the files on disk.
            Files = staged.Files,
        };

        _manifest.Upsert(run.Request.GameId, mod);

        run.Entry("info", $"Recorded {mod.Id} ({mod.Name}) as installed, {mod.Files.Count} file(s).");
    }

    /// <summary>Phase 8: drop the tree that was moved aside, then report.</summary>
    private void Finish(Run run, IProgress<OperationProgress>? progress)
    {
        SwapSession swap = run.Swap!;

        if (swap.Commit())
        {
            run.Entry("info", $"Removed the previous '{swap.Paths.FolderName}' folder.");
        }
        else
        {
            run.Entry(
                "warn",
                $"The previous '{swap.Paths.FolderName}' folder could not be deleted and was left at {swap.LeftoverOldDirectory}. It is harmless and is swept at the next start.",
                swap.LeftoverOldDirectory);

            run.Note($"Leftover folder (swept at next start): {swap.LeftoverOldDirectory}");
        }

        run.Progress(progress, CleaningPhase, 0, 0, null);
    }

    /// <summary>
    /// Turns a thrown failure into a result, after undoing whatever the swap managed to do.
    /// </summary>
    /// <remarks>
    /// One place for every failure path, so "the game folder is exactly as it was" is a property of a single
    /// method rather than a habit repeated in eight <c>catch</c> blocks. The rollback is logged even when it
    /// worked: a user reading the log after a failed install needs to know it was undone, not infer it.
    /// </remarks>
    private OperationResult Abandon(Run run, Exception exception)
    {
        SwapRollback? rollback = null;

        if (run.Swap is { HasMutated: true } swap && swap.Phase != SwapPhase.Committed)
        {
            rollback = swap.Rollback();

            if (rollback.Succeeded)
            {
                run.Entry(
                    "info",
                    "Rolled the game folder back to its previous state.",
                    rollback.Describe(run.BackupTimestamp));
            }
            else
            {
                run.Entry(
                    "error",
                    "The game folder could not be fully rolled back.",
                    rollback.Describe(run.BackupTimestamp));
            }
        }

        OperationResult mapped = OperationResult.FromException(exception, run.Request.GameId, run.ModId, run.Entries);

        string? detail = Combine(mapped.Detail, rollback?.Describe(run.BackupTimestamp));
        detail = Combine(detail, run.Detail());

        if (mapped.Status == OperationStatus.Cancelled)
        {
            string message = rollback is { Succeeded: false }
                ? "Install cancelled, but the game folder could not be fully restored. See the details for where the files are."
                : run.BackupTimestamp is null
                    ? "Install cancelled. Nothing was changed."
                    : "Install cancelled. The game folder was left as it was, and the backup taken before the install is kept.";

            return OperationResult.Cancelled(
                message,
                run.Request.GameId,
                run.ModId,
                run.BackupTimestamp,
                run.Entries,
                detail);
        }

        string failureMessage = rollback is { Succeeded: false, StrandedAddonDirectory: { } stranded }
            ? $"The install failed and the add-on could not be moved back. It is at {Path.GetFileName(stranded)} in the game's addons folder; the full path is in the details."
            : mapped.Message;

        return OperationResult.Failure(
            mapped.Code,
            failureMessage,
            detail,
            cause: null,
            gameId: run.Request.GameId,
            modId: run.ModId,
            backupTimestamp: run.BackupTimestamp,
            entries: run.Entries);
    }

    /// <summary>The game-root-relative paths the snapshot must cover: the folder, and every file already in it.</summary>
    private static IReadOnlyList<string> BackupPaths(Run run)
    {
        string installPath = run.Tracked!.InstallPath;
        string folder = Path.Combine(run.AddonRoot!, run.Staged!.FolderName);

        List<string> paths = [GameRelative(installPath, folder)];

        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                paths.Add(GameRelative(installPath, file));
            }
        }

        paths.Sort(StringComparer.Ordinal);

        return paths;
    }

    /// <summary>Buckets one path by who owns it, treating an existing unowned path as the user's own work.</summary>
    private void Classify(Buckets buckets, string gameId, string modId, string relativePath, bool exists)
    {
        IReadOnlyList<string> owners = _manifest.FindOwnersOfPath(gameId, relativePath);

        bool mine = false;
        bool others = false;

        foreach (string owner in owners)
        {
            if (IsSameId(owner, modId))
            {
                mine = true;
            }
            else
            {
                others = true;
            }
        }

        if (others)
        {
            buckets.Add(isOurs: false, relativePath);
        }
        else if (mine)
        {
            buckets.Add(isOurs: true, relativePath);
        }
        else if (exists)
        {
            buckets.Add(isOurs: null, relativePath);
        }
    }

    private static string Resolve(string addonRoot, string relativePath) =>
        Path.Combine(addonRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string GameRelative(string installPath, string absolutePath) =>
        Path.GetRelativePath(installPath, absolutePath).Replace('\\', '/');

    private static string? Combine(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first.TrimEnd()}\n\n{second}";
    }

    private static string ArchiveName(InstallRequest request)
    {
        string name = string.IsNullOrWhiteSpace(request.ZipFileName) ? request.SourceUrl : request.ZipFileName;

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InstallException(
                OperationErrorCode.SourceInvalid,
                "An archive install needs the archive's file name, because that is what the library shows as its source.");
        }

        return name;
    }

    private static Stream ArchiveStream(InstallRequest request) =>
        request.ZipStream ?? throw new InstallException(
            OperationErrorCode.SourceInvalid,
            "An archive install needs the archive itself, but the request carried no stream.");

    /// <summary>
    /// A stable id from a folder name: lowercase, with anything that is not a letter, a digit, a dash or an
    /// underscore folded into a single dash.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>AddonFolderName.FromSourceName</c>: that answers "what should the folder be
    /// called", which keeps the source's capitalisation because Windows displays it; this answers "what is
    /// this mod called", which has to be the same string on every reinstall. <c>pfUI</c> and <c>Pfui</c> are
    /// one mod, and their ids must agree or the Library shows two.
    /// </remarks>
    private static string Slug(string value)
    {
        StringBuilder builder = new(value.Length);
        bool pendingDash = false;

        foreach (char character in value)
        {
            char lower = char.ToLowerInvariant(character);

            if (char.IsAsciiLetterOrDigit(lower) || lower is '-' or '_')
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(lower);
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static bool IsSameId(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal);

    /// <summary>The three conflict buckets, with their de-duplication and their ordering rules.</summary>
    private sealed class Buckets
    {
        private readonly List<string> _ownedByThisMod = [];
        private readonly List<string> _ownedByOtherMods = [];
        private readonly List<string> _unownedExisting = [];

        public IReadOnlyList<string> OwnedByThisMod => _ownedByThisMod;

        public IReadOnlyList<string> OwnedByOtherMods => _ownedByOtherMods;

        public IReadOnlyList<string> UnownedExisting => _unownedExisting;

        /// <summary>
        /// Files this mod owns are reported once; a path that both this mod and another one claim counts as
        /// the other mod's, because that is the answer that makes the install stop.
        /// </summary>
        public void Add(bool? isOurs, string path)
        {
            if (isOurs is null)
            {
                AddTo(_unownedExisting, path);

                return;
            }

            AddTo(isOurs.Value ? _ownedByThisMod : _ownedByOtherMods, path);
        }

        private static void AddTo(List<string> paths, string path)
        {
            if (!paths.Contains(path, StringComparer.Ordinal))
            {
                paths.Add(path);
            }
        }
    }

    /// <summary>Everything one install attempt knows, plus the entries it has logged so far.</summary>
    private sealed class Run
    {
        private readonly List<OperationLogEntry> _entries = [];
        private readonly List<string> _notes = [];
        private readonly IOperationLog? _log;
        private readonly TimeProvider _clock;

        public Run(InstallRequest request, TimeProvider clock, IOperationLog? log)
        {
            Request = request;
            _clock = clock;
            _log = log;
            StartedAt = clock.GetUtcNow();
        }

        public InstallRequest Request { get; }

        public DateTimeOffset StartedAt { get; }

        public TrackedGame? Tracked { get; set; }

        public GameDescriptor? Descriptor { get; set; }

        public string? AddonRoot { get; set; }

        public StagedSource? Staged { get; set; }

        public SwapSession? Swap { get; set; }

        public ConflictReport? ConflictReport { get; set; }

        public string? ModId { get; set; }

        public string? Name { get; set; }

        public string? BackupTimestamp { get; set; }

        public IReadOnlyList<OperationLogEntry> Entries => _entries;

        public void Note(string detail)
        {
            if (!string.IsNullOrWhiteSpace(detail))
            {
                _notes.Add(detail);
            }
        }

        public void Entry(string level, string message, string? detail = null)
        {
            OperationLogEntry entry = new(_clock.GetUtcNow(), level, message, detail);

            _entries.Add(entry);
            _log?.Write(entry);
        }

        public void Progress(IProgress<OperationProgress>? progress, string phase, int completed, int total, string? current) =>
            progress?.Report(new OperationProgress(phase, completed, total, current));

        /// <summary>The accumulated technical notes, joined for a result's Detail.</summary>
        public string? Detail() => _notes.Count == 0 ? null : string.Join("\n", _notes);

        /// <summary>
        /// Removes the staging directory, once. A failure to clean up is logged and never fails the install:
        /// the directory is under the data root, and 207's sweep exists for exactly this leftover.
        /// </summary>
        public void ReleaseStaging(ISourceAcquirer sources)
        {
            if (Staged is not { } staged)
            {
                return;
            }

            Staged = null;

            try
            {
                sources.Cleanup(staged);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Entry(
                    "warn",
                    $"The staging directory for '{staged.FolderName}' could not be removed; it is swept at the next start.",
                    exception.Message);
            }
        }
    }
}
