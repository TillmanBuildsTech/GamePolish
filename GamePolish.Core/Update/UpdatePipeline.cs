using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Install;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Update;

/// <summary>
/// Brings an installed addon up to what its source serves now, without losing the copy that is on disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Re-stage and swap - never <c>fetch</c> and <c>reset</c>.</b> The design note's *Addon Layout* table
/// documents the incumbent's shape, <c>git fetch --depth=1 &amp;&amp; git reset --hard origin/BRANCH</c>, run
/// inside the game's addons folder. Task <c>207</c> decided otherwise and this task is built on that
/// decision: a reset writes the new tree file by file into the folder the running game is reading, so a drop
/// in the connection, a full disk or a cancel leaves a half-updated addon under its real name and nothing
/// puts the old one back. Instead the new version is staged under the data root and moved into place by
/// <see cref="SwapOperations"/>, which can undo every step - and an update is exactly the case that needs it,
/// because the thing being replaced already exists and the user may have edited it. <b>The design note's table
/// is superseded by this file</b> and should be corrected where it is written.
/// </para>
/// <para>
/// <b>The eight phases, and the order is the specification.</b> Resolve, re-stage, validate, scan conflicts,
/// scan local edits, back up, swap, record. Two orderings inside that list are the product's promises:
/// the backup precedes the first byte written anywhere inside the game directory (invariant #1), and the
/// local-edit scan precedes the backup so the report can be in the result the user reads afterwards.
/// </para>
/// <para>
/// <b>The conflict scan does not re-stage.</b> Task <c>208</c>'s <c>DetectConflictsAsync</c> stages its own
/// source, so calling it here would clone the repository a second time for every update.
/// <see cref="DetectConflicts"/> answers the same question from the manifest instead - which mods claim this
/// folder and these paths - and returns <c>208</c>'s <see cref="ConflictReport"/> type so the shape the UI
/// already knows is unchanged.
/// </para>
/// <para>
/// <b>What an update refuses, and why refusing is the kind answer.</b> A disabled mod (its files are not in
/// the game folder), an archive-sourced mod (there is no upstream), a source that renamed its addon folder
/// (the swap would leave the old folder orphaned beside a new one), and a source that collides with another
/// installed mod. Each is reported before anything is staged or written.
/// </para>
/// <para>
/// <b>What it does not do.</b> It never merges local edits: it reports them and names the backup that holds
/// them. It never touches <c>InstalledAt</c> (an update is not a reinstall) and never flips <c>Enabled</c>
/// (an update is not an enable).
/// </para>
/// </remarks>
public sealed class UpdatePipeline : IUpdatePipeline
{
    private const string ResolvingPhase = "resolving";
    private const string StagingPhase = "staging";
    private const string ValidatingPhase = "validating";
    private const string ConflictPhase = "conflicts";
    private const string ScanningPhase = "scanning";
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
    private readonly IVersionProbe _probe;
    private readonly SwapOperations _swap;
    private readonly IOperationLog? _log;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the pipeline with the production implementations of everything it sequences.
    /// </summary>
    /// <param name="paths">The resolved data root: staging, snapshots and the manifest all live under it.</param>
    /// <remarks>
    /// The convenience constructor the CLI (<c>213</c>) and the desktop app use. A caller that needs a
    /// different seam - a test, or a view model that already owns a shared <see cref="ISourceAcquirer"/> -
    /// uses the full constructor below.
    /// </remarks>
    public UpdatePipeline(GamePolishPaths paths)
        : this(
            paths,
            new GamesRegistry(paths, new GameDescriptorLoader(paths)),
            new GameDescriptorLoader(paths),
            new SourceAcquirer(paths),
            new BackupEngine(paths),
            new ManifestStore(paths),
            new PathValidator(),
            new GitVersionProbe(),
            new FileOps())
    {
    }

    /// <summary>Initializes the pipeline with injected collaborators.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="games">Which games are tracked, and where each is installed.</param>
    /// <param name="descriptors">The descriptor source: the addons folder and the game's display name.</param>
    /// <param name="sources">The staging component (<c>207</c>), which is the only way to fetch a version.</param>
    /// <param name="backups">The snapshot engine (<c>205</c>).</param>
    /// <param name="manifest">The installed-mod record (<c>206</c>).</param>
    /// <param name="pathValidator">Validates every path that came from a mod source (<c>204</c>).</param>
    /// <param name="probe">Answers "what does the source serve now?" without cloning.</param>
    /// <param name="files">The mutating-call seam, shared by the snapshot and the swap so their order is observable.</param>
    /// <param name="log">The rolling log, or <see langword="null"/> when nobody keeps one (tests).</param>
    /// <param name="timeProvider">The clock behind the operation timestamps and the swap directory names.</param>
    public UpdatePipeline(
        GamePolishPaths paths,
        IGamesRegistry games,
        IGameDescriptorLoader descriptors,
        ISourceAcquirer sources,
        IBackupEngine backups,
        IManifestStore manifest,
        IPathValidator pathValidator,
        IVersionProbe probe,
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
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(files);

        _paths = paths;
        _games = games;
        _descriptors = descriptors;
        _sources = sources;
        _backups = backups;
        _manifest = manifest;
        _pathValidator = pathValidator;
        _probe = probe;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _swap = new SwapOperations(files, pathValidator, _timeProvider);
    }

    /// <inheritdoc/>
    public async Task<UpdateAvailability> CheckAsync(
        string gameId,
        string modId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        cancellationToken.ThrowIfCancellationRequested();

        string installed = string.Empty;

        try
        {
            InstalledMod? mod = _manifest.Find(gameId, modId);

            if (mod is null)
            {
                return UpdateAvailability.Unknown(
                    modId,
                    installed,
                    $"'{modId}' is not installed for this game, so there is no installed version to compare.");
            }

            installed = mod.Version;

            // An archive has no upstream. Returning Unknown without calling the probe is not an
            // optimisation - there is nothing a probe could be pointed at, and a request would be a network
            // call whose answer is discarded. A probe double that throws if invoked is the test for this.
            if (mod.SourceType != ModSourceType.Github)
            {
                return UpdateAvailability.Unknown(
                    modId,
                    installed,
                    $"'{mod.Name}' was installed from an archive, which has no upstream version to compare against.");
            }

            if (string.IsNullOrWhiteSpace(installed))
            {
                return UpdateAvailability.Unknown(
                    modId,
                    installed,
                    $"The record of '{mod.Name}' does not say which version is installed, so there is nothing to compare.");
            }

            VersionInfo remote = await _probe
                .GetRemoteVersionAsync(mod.SourceUrl, branch: null, cancellationToken)
                .ConfigureAwait(false);

            // A probe that honoured the token has already stopped; this covers the one that finished first.
            cancellationToken.ThrowIfCancellationRequested();

            return SameVersion(installed, remote)
                ? UpdateAvailability.UpToDate(modId, installed)
                : UpdateAvailability.Available(modId, installed, remote.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // CheckAsync is called for every row of an always-open view, so it must not be able to take the
            // view down. The mapped code is the same one an install would report for the same failure, and
            // it is what the log drawer and the retry button key on.
            OperationResult mapped = OperationResult.FromException(exception, gameId, modId);

            return UpdateAvailability.Failed(modId, installed, $"{mapped.Code}: {mapped.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<OperationResult> UpdateAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        Run run = new(gameId, modId, _timeProvider, _log);

        try
        {
            // 1. Resolve: the game, the folder, and the record of what is about to be replaced.
            Resolve(run, progress, cancellationToken);

            // 2. Re-stage. Nothing inside the game folder has been touched at any point so far.
            await StageAsync(run, progress, cancellationToken).ConfigureAwait(false);

            // 3. Validate the staged paths, then refuse to take another mod's files.
            Validate(run, progress, cancellationToken);
            DetectConflicts(run, progress, cancellationToken);

            // 4. What the user changed by hand, before the backup so the report can be in the result.
            ScanLocalEdits(run, progress, cancellationToken);

            // 5. Backup - before the first byte.
            cancellationToken.ThrowIfCancellationRequested();
            TakeBackup(run, progress);

            // 6. Swap. From here on the game folder changes, and every failure has to be undone.
            cancellationToken.ThrowIfCancellationRequested();
            PerformSwap(run, progress, cancellationToken);

            // 7. Record. This is what makes the new version the installed one.
            cancellationToken.ThrowIfCancellationRequested();
            Record(run, progress);

            // 8. Commit the swap, then report.
            Finish(run, progress);

            return OperationResult.Success(
                $"Updated {run.Installed!.Name} {run.Installed.Version} -> {run.Staged!.Version}",
                gameId,
                modId,
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

    /// <summary>
    /// Phase 1: the tracked game, its descriptor, the addons folder, and the manifest entry - or the refusal
    /// that means there is nothing to update.
    /// </summary>
    private void Resolve(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        run.Progress(progress, ResolvingPhase, 0, 0, run.GameId);

        TrackedGame? tracked = _games.Get(run.GameId);

        if (tracked is null)
        {
            throw new UpdateException(
                OperationErrorCode.GameInstallPathMissing,
                $"'{run.GameId}' is not tracked yet, so there is no installed copy to update. Add the game first.");
        }

        if (!Directory.Exists(tracked.InstallPath))
        {
            throw new UpdateException(
                OperationErrorCode.GameInstallPathMissing,
                $"{tracked.Name} is recorded at {tracked.InstallPath}, but that folder is gone. Check the game's install folder, then try again.");
        }

        GameDescriptor? descriptor = _descriptors.LoadById(run.GameId);

        if (descriptor is null)
        {
            throw new UpdateException(
                OperationErrorCode.GameNotFound,
                $"'{run.GameId}' is not a supported game: no descriptor named {run.GameId}.yaml was found.");
        }

        string addonRoot = descriptor.AddonRoot(tracked.InstallPath);

        if (!Directory.Exists(addonRoot))
        {
            throw new UpdateException(
                OperationErrorCode.GameInstallPathMissing,
                $"{tracked.Name} has no '{descriptor.ModLayout.Destination}' folder under {tracked.InstallPath}, so there is nothing to update.");
        }

        InstalledMod? installed = _manifest.Find(run.GameId, run.ModId);

        if (installed is null)
        {
            // Never "install over the folder anyway": a folder on disk the manifest does not know about is
            // an orphan, and adopting it would give the user a mod they cannot uninstall.
            throw new UpdateException(
                OperationErrorCode.ModNotInstalled,
                $"'{run.ModId}' is not installed for {descriptor.Name}, so there is nothing to update. Install it first.");
        }

        // A disabled mod's files are not in the game folder in any supported layout - 210 keeps them aside -
        // so staging over it would write a second copy and then record a mod the user deliberately turned
        // off. Refused before the network is touched, and the message says what to do about it. The decision
        // to refuse rather than "update the files where 210 happens to keep them" is deliberate: this
        // pipeline would have to guess that location, and a wrong guess writes a mod into a game the user
        // disabled it for.
        if (!installed.Enabled)
        {
            throw new UpdateException(
                OperationErrorCode.ModNotInstalled,
                $"'{installed.Name}' is disabled, so its files are not in the game folder to replace. Enable it first, then update.");
        }

        // An archive was installed from a file the user picked; the file is gone from this process's point of
        // view, and there is no repository to re-stage. Reported as an unusable source rather than as
        // "already installed"/"not installed", because the fix is "install the newer archive".
        if (installed.SourceType != ModSourceType.Github)
        {
            throw new UpdateException(
                OperationErrorCode.SourceInvalid,
                $"'{installed.Name}' was installed from an archive ('{installed.SourceUrl}'), which has no upstream to update from. Install the newer archive instead; the installed copy is untouched.");
        }

        if (string.IsNullOrWhiteSpace(installed.SourceUrl))
        {
            throw new UpdateException(
                OperationErrorCode.SourceInvalid,
                $"The record of '{installed.Name}' has no source URL, so there is nothing to re-stage. Reinstall it from its source.");
        }

        string folder = Path.Combine(addonRoot, installed.FolderName);

        if (!Directory.Exists(folder))
        {
            throw new UpdateException(
                OperationErrorCode.ModFolderMissing,
                $"{descriptor.Name} has no '{installed.FolderName}' folder under {addonRoot}, so there is nothing to replace. Restore it from a backup, or reinstall it.");
        }

        run.Tracked = tracked;
        run.Descriptor = descriptor;
        run.AddonRoot = addonRoot;
        run.Installed = installed;

        run.Entry(
            "info",
            $"Resolved {installed.Name}: '{installed.FolderName}' under {addonRoot} is at {installed.VersionKind} {installed.Version}.");
    }

    /// <summary>
    /// Phase 2: stage the source again, from scratch, under the data root.
    /// </summary>
    /// <remarks>
    /// A fresh clone rather than a fetch into the installed copy, per the model <c>207</c> decided: the
    /// staging directory is the only place a partial download may exist. The operation id is new for every
    /// attempt, so two updates of the same mod can never share a directory and a crash leaves exactly one
    /// sweepable orphan.
    /// </remarks>
    private async Task StageAsync(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InstalledMod mod = run.Installed!;
        run.Progress(progress, StagingPhase, 0, 0, mod.SourceUrl);

        SourceRequest request = new()
        {
            GameId = run.GameId,
            SourceUrl = mod.SourceUrl,
            SourceType = ModSourceType.Github,
            DataRoot = _paths.Root,
            OperationId = Guid.NewGuid().ToString("N"),

            // No ExpectedFolderName and no Branch. The folder name is *checked* against the record instead
            // of being forced onto the source (see Validate): forcing it would let a repository that
            // restructured itself be written into the folder name the user happens to have, hiding the
            // change. And the manifest does not record a branch - 206's shape is fixed for this task - so an
            // update takes the remote's default branch, exactly as a fresh install with no branch does.
            Branch = null,
        };

        StagedSource staged = await _sources.StageGitAsync(request, cancellationToken).ConfigureAwait(false);

        run.Staged = staged;

        run.Entry(
            "info",
            $"Staged '{staged.FolderName}' from {staged.SourceUrl} - {staged.Files.Count} file(s), {staged.VersionKind} {staged.Version}.",
            staged.StagingDirectory);
    }

    /// <summary>Phase 3a: prove every staged path is safe, inside the folder, and still the same folder.</summary>
    private void Validate(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StagedSource staged = run.Staged!;
        InstalledMod mod = run.Installed!;
        run.Progress(progress, ValidatingPhase, 0, staged.Files.Count, null);

        if (staged.Files.Count == 0)
        {
            throw new UpdateException(
                OperationErrorCode.SourceInvalid,
                $"'{staged.FolderName}' staged no files, so there is nothing to put in place. Nothing was changed.");
        }

        // The swap replaces the folder by name. If upstream now calls the addon something else, swapping
        // would create the new name beside the old one, leaving an orphaned folder the manifest no longer
        // points at - and the game would load both copies. Refused with the way out in the message.
        if (!string.Equals(staged.FolderName, mod.FolderName, StringComparison.Ordinal))
        {
            throw new UpdateException(
                OperationErrorCode.SourceInvalid,
                $"The source now calls its folder '{staged.FolderName}', but '{mod.FolderName}' is what is installed. Nothing was changed: remove '{mod.FolderName}' and install it again to move to the renamed folder.");
        }

        IReadOnlyList<PathValidationFailure> failures =
            _pathValidator.ValidateAll(run.AddonRoot!, staged.Files.Select(file => file.RelativePath));

        if (failures.Count > 0)
        {
            // 204's own exception: its message and its Failures list are what FromException turns into
            // PathUnsafe plus a detail that names every refused path.
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

            throw new UpdateException(
                OperationErrorCode.SourceInvalid,
                $"'{staged.FolderName}' lists {outside.Length} file(s) outside its own folder, so nothing was updated.");
        }

        run.Entry("info", $"Validated {staged.Files.Count} path(s) against {run.AddonRoot}.");
    }

    /// <summary>
    /// Phase 3b: does this update take anything another installed mod claims?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered from the manifest rather than by re-staging: <c>208</c>'s <c>DetectConflictsAsync</c> stages
    /// its own copy of the source, so calling it here would clone the repository twice per update. The two
    /// questions are the same - who owns this folder, and who owns these paths - and the manifest is the
    /// authority on both.
    /// </para>
    /// <para>
    /// One bucket is deliberately always empty for an update. A file that exists on disk and no mod claims is
    /// inside <i>this</i> mod's folder by construction (every staged path starts with it), so it is a local
    /// edit the user made, not somebody else's file. It is reported by <see cref="ScanLocalEdits"/> and never
    /// blocks: refusing to update a mod because its owner edited it would make the update button fail for the
    /// one user who most needs it.
    /// </para>
    /// </remarks>
    private void DetectConflicts(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StagedSource staged = run.Staged!;
        run.Progress(progress, ConflictPhase, 0, 0, staged.FolderName);

        List<string> ownedByOtherMods = [];
        List<string> ownedByThisMod = [];

        foreach (string owner in _manifest.FindFolderOwners(run.GameId, staged.FolderName))
        {
            if (!IsSameId(owner, run.ModId))
            {
                AddDistinct(ownedByOtherMods, staged.FolderName + "/");
            }
        }

        foreach (InstalledFileEntry file in staged.Files)
        {
            bool mine = false;
            bool others = false;

            foreach (string owner in _manifest.FindOwnersOfPath(run.GameId, file.RelativePath))
            {
                if (IsSameId(owner, run.ModId))
                {
                    mine = true;
                }
                else
                {
                    others = true;
                }
            }

            // Another mod's claim wins over this mod's, which is the answer that makes the update stop.
            if (others)
            {
                AddDistinct(ownedByOtherMods, file.RelativePath);
            }
            else if (mine)
            {
                AddDistinct(ownedByThisMod, file.RelativePath);
            }
        }

        run.Conflicts = new ConflictReport
        {
            OwnedByThisMod = ownedByThisMod,
            OwnedByOtherMods = ownedByOtherMods,

            // See the remark: unowned-but-present paths inside this folder are local edits, and they are
            // reported by the next phase rather than counted as a collision.
            UnownedExisting = [],
        };

        if (run.Conflicts.IsBlocked)
        {
            run.Note(run.Conflicts.Describe());

            throw new UpdateException(
                OperationErrorCode.ConflictsUnresolved,
                $"'{staged.FolderName}' would replace {run.Conflicts.BlockingCount} path(s) that another installed mod claims. Nothing was updated.");
        }

        run.Entry(
            "info",
            $"No other installed mod claims '{staged.FolderName}' or anything the new version writes into it.");
    }

    /// <summary>
    /// Phase 4: what the user changed inside the folder, compared with what the manifest recorded.
    /// </summary>
    /// <remarks>
    /// Size is the only signal available: <c>206</c> records each file's size and no hash, and changing that
    /// shape is out of this task's scope. A recorded path whose size differs is modified, a path on disk the
    /// record does not list is an addition. A recorded path that is missing is reported in the log but is not
    /// in the report - nothing was edited, something was deleted, and the update puts it back either way.
    /// </remarks>
    private void ScanLocalEdits(Run run, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InstalledMod mod = run.Installed!;
        string addonRoot = run.AddonRoot!;
        run.Progress(progress, ScanningPhase, 0, 0, mod.FolderName);

        Dictionary<string, long> recorded = new(StringComparer.FromComparison(PathComparer.PathComparison));

        foreach (InstalledFileEntry file in mod.Files)
        {
            recorded[file.RelativePath] = file.SizeBytes;
        }

        List<string> modified = [];
        List<string> added = [];
        List<string> missing = [];
        HashSet<string> onDisk = new(recorded.Comparer);

        foreach (string absolute in Directory.EnumerateFiles(Path.Combine(addonRoot, mod.FolderName), "*", SearchOption.AllDirectories))
        {
            string relative = GameRelative(addonRoot, absolute);

            onDisk.Add(relative);

            if (recorded.TryGetValue(relative, out long installedSize))
            {
                if (new FileInfo(absolute).Length != installedSize)
                {
                    modified.Add(relative);
                }

                continue;
            }

            added.Add(relative);
        }

        foreach (string path in recorded.Keys)
        {
            if (!onDisk.Contains(path))
            {
                missing.Add(path);
            }
        }

        modified.Sort(StringComparer.Ordinal);
        added.Sort(StringComparer.Ordinal);
        missing.Sort(StringComparer.Ordinal);

        run.Edits = new LocalEditReport { ModifiedPaths = modified, AddedPaths = added };

        if (missing.Count > 0)
        {
            run.Entry(
                "warn",
                $"{missing.Count} file(s) the record lists for '{mod.Name}' are no longer in the folder; the update puts them back.",
                string.Join('\n', missing));
        }

        if (run.Edits.IsEmpty)
        {
            run.Entry("info", $"Nothing in '{mod.FolderName}' was changed by hand since it was installed.");

            return;
        }

        // Logged only; the report reaches the result's Detail from Run.Edits, so noting it here as well
        // would print the same list twice.
        run.Entry(
            "warn",
            $"{modified.Count} file(s) changed and {added.Count} added by hand in '{mod.FolderName}'; the update replaces them and names them in the result.",
            run.Edits.Describe());
    }

    /// <summary>Phase 5: the snapshot, before anything in the game folder is written.</summary>
    /// <remarks>
    /// <b><c>sourceVersion</c> is the label the Backups view shows verbatim</b> - <c>backups.md</c>'s
    /// "Mod update — SkyUI v5.1 → v5.2" is this string with an arrow drawn by the view - so it is exactly
    /// <c>"&lt;oldVersion&gt; -&gt; &lt;newVersion&gt;"</c> and nothing else.
    /// </remarks>
    private void TakeBackup(Run run, IProgress<OperationProgress>? progress)
    {
        InstalledMod mod = run.Installed!;
        StagedSource staged = run.Staged!;

        run.Progress(progress, BackupPhase, 0, 0, mod.FolderName);

        IReadOnlyList<string> paths = BackupPaths(run);

        // The local-edit report would ideally ride along in the event's Note, and 205's Create has no
        // parameter for it. Adding one is a change to the backup engine, which this task does not make; the
        // report is in the result's Detail and in the operation log instead, which is where the user reads it
        // immediately after the update.
        BackupEvent created = _backups.Create(
            run.GameId,
            run.Tracked!.InstallPath,
            BackupTrigger.ModUpdate,
            sourceName: mod.Name,
            sourceVersion: $"{mod.Version} -> {staged.Version}",
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
        SwapSession swap = _swap.Begin(run.AddonRoot!, run.Installed!.FolderName);
        run.Swap = swap;

        swap.CopyStagedTree(run.Staged!, progress, cancellationToken);

        // Between the files and the renames the token is checked again: cancelling with the copy complete
        // must not proceed to the point where the game folder is briefly missing.
        cancellationToken.ThrowIfCancellationRequested();

        swap.Activate();

        run.Entry(
            "info",
            $"Moved '{swap.Paths.FolderName}' into place from {swap.Paths.NewDirectory}.",
            swap.Paths.Destination);
    }

    /// <summary>Phase 7: record the new version. A failure here rolls the folder back.</summary>
    /// <remarks>
    /// <c>InstalledAt</c> is carried over and <c>Enabled</c> is carried over: an update changes what is in the
    /// folder and when it last changed, and nothing else. Writing <c>Enabled = true</c> here would silently
    /// re-enable a mod the user turned off, which is why a disabled mod never reaches this method.
    /// </remarks>
    private void Record(Run run, IProgress<OperationProgress>? progress)
    {
        InstalledMod mod = run.Installed!;
        StagedSource staged = run.Staged!;

        run.Progress(progress, RecordingPhase, 0, 1, run.ModId);

        InstalledMod updated = new()
        {
            Id = mod.Id,
            Name = mod.Name,
            SourceUrl = mod.SourceUrl,
            SourceType = mod.SourceType,
            FolderName = mod.FolderName,
            Version = staged.Version,
            VersionKind = staged.VersionKind,
            Enabled = mod.Enabled,
            InstalledAt = mod.InstalledAt,

            // The moment the operation started, not when this line ran: the operation log and the record
            // then agree on one timestamp even if the copy took a minute.
            UpdatedAt = run.StartedAt,

            // Verbatim: the staged list is addons-folder-relative and already carries the folder name, which
            // is exactly what the manifest stores.
            Files = staged.Files,
        };

        _manifest.Upsert(run.GameId, updated);
        run.Recorded = true;

        run.Entry("info", $"Recorded {updated.Id} ({updated.Name}) at {updated.VersionKind} {updated.Version}, {updated.Files.Count} file(s).");
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
    /// One place for every failure path, mirroring <c>208</c> step for step, plus the one thing an update has
    /// that an install does not: a manifest entry that already existed. If the failure lands after the record
    /// was written and before the swap committed, the previous entry is written back - so "the old mod is
    /// still installed" is true of the record as well as the folder.
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

        if (run.Recorded && run.Installed is { } original)
        {
            try
            {
                _manifest.Upsert(run.GameId, original);

                run.Entry(
                    "info",
                    $"Restored the record of '{original.Name}' to {original.VersionKind} {original.Version}.");
            }
            catch (Exception restoreFailure) when (restoreFailure is not OperationCanceledException)
            {
                // The folder was rolled back, so the files on disk are the old version while the record says
                // the new one. This is exactly the case the backup exists for, and the only honest thing
                // left is to say so loudly in the detail. Catching everything except cancellation is
                // deliberate: a failure here must not replace the failure that caused the rollback.
                run.Entry(
                    "error",
                    "The record of the mod could not be restored to the previous version.",
                    restoreFailure.Message);
            }
        }

        OperationResult mapped = OperationResult.FromException(exception, run.GameId, run.ModId, run.Entries);

        string? detail = Combine(mapped.Detail, rollback?.Describe(run.BackupTimestamp));
        detail = Combine(detail, run.Detail());

        if (mapped.Status == OperationStatus.Cancelled)
        {
            string message = rollback is { Succeeded: false }
                ? "Update cancelled, but the game folder could not be fully restored. See the details for where the files are."
                : run.BackupTimestamp is null
                    ? "Update cancelled. Nothing was changed."
                    : "Update cancelled. The game folder was left as it was, and the backup taken before the update is kept.";

            return OperationResult.Cancelled(
                message,
                run.GameId,
                run.ModId,
                run.BackupTimestamp,
                run.Entries,
                detail);
        }

        string failureMessage = rollback is { Succeeded: false, StrandedAddonDirectory: { } stranded }
            ? $"The update failed and the add-on could not be moved back. It is at {Path.GetFileName(stranded)} in the game's addons folder; the full path is in the details."
            : mapped.Message;

        return OperationResult.Failure(
            mapped.Code,
            failureMessage,
            detail,
            cause: null,
            gameId: run.GameId,
            modId: run.ModId,
            backupTimestamp: run.BackupTimestamp,
            entries: run.Entries);
    }

    /// <summary>
    /// The game-root-relative paths the snapshot must cover: the folder, and every file already in it.
    /// </summary>
    /// <remarks>
    /// Every existing file, not just the manifest's list: the swap removes files the new version does not
    /// ship, so a hand-added file is only recoverable if the snapshot holds it - and it is not in the
    /// manifest. Enumerating the folder is what makes the backup a real safety net here.
    /// </remarks>
    private static IReadOnlyList<string> BackupPaths(Run run)
    {
        string installPath = run.Tracked!.InstallPath;
        string folder = Path.Combine(run.AddonRoot!, run.Installed!.FolderName);

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

    /// <summary>
    /// True when the installed version and the probe's answer name the same thing.
    /// </summary>
    /// <remarks>
    /// Commit ids are compared when both sides resolved one, because a probe that labels the head with a tag
    /// must still answer "up to date" for an install that recorded the same commit. Everything else compares
    /// the display values. Case-insensitive: a SHA written by git is lowercase, and one copied out of a URL
    /// may not be.
    /// </remarks>
    private static bool SameVersion(string installed, VersionInfo remote)
    {
        if (VersionInfo.IsCommitSha(installed) && VersionInfo.IsCommitSha(remote.Sha))
        {
            return string.Equals(installed, remote.Sha, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(installed, remote.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDistinct(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.Ordinal))
        {
            paths.Add(path);
        }
    }

    private static string GameRelative(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    private static string? Combine(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first.TrimEnd()}\n\n{second}";
    }

    private static bool IsSameId(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal);

    /// <summary>Everything one update attempt knows, plus the entries it has logged so far.</summary>
    private sealed class Run
    {
        private readonly List<OperationLogEntry> _entries = [];
        private readonly List<string> _notes = [];
        private readonly IOperationLog? _log;
        private readonly TimeProvider _clock;

        public Run(string gameId, string modId, TimeProvider clock, IOperationLog? log)
        {
            GameId = gameId;
            ModId = modId;
            _clock = clock;
            _log = log;
            StartedAt = clock.GetUtcNow();
        }

        public string GameId { get; }

        public string ModId { get; }

        public DateTimeOffset StartedAt { get; }

        public TrackedGame? Tracked { get; set; }

        public GameDescriptor? Descriptor { get; set; }

        public string? AddonRoot { get; set; }

        /// <summary>The record as it was before this update; the thing a rollback has to put back.</summary>
        public InstalledMod? Installed { get; set; }

        public StagedSource? Staged { get; set; }

        public SwapSession? Swap { get; set; }

        public ConflictReport? Conflicts { get; set; }

        public LocalEditReport? Edits { get; set; }

        public string? BackupTimestamp { get; set; }

        /// <summary>True once the new version is in the manifest, so a rollback also has a record to undo.</summary>
        public bool Recorded { get; set; }

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
        public string? Detail()
        {
            List<string> parts = [];

            if (Edits is { IsEmpty: false } edits)
            {
                parts.Add(edits.Describe());
            }

            if (BackupTimestamp is { } timestamp)
            {
                parts.Add($"Backup taken before the update: {timestamp} (restore it from the Backups view).");
            }

            parts.AddRange(_notes);

            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }

        /// <summary>
        /// Removes the staging directory, once. A failure to clean up is logged and never fails the update:
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
