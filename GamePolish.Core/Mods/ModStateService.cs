using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Mods;

/// <summary>
/// Wires the two mod-state pipelines together behind <see cref="IModStateService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin: it owns no state and makes no decisions beyond "which pipeline does this?" - in particular
/// whether a checkbox click is an enable or a disable. Any logic that grew here would be logic the CLI
/// (<c>213</c>) would have to duplicate or bypass, and the pipelines are the units that own the invariants.
/// </para>
/// <para>
/// The shared collaborators are constructed once, so the manifest store the rows come from is the same one the
/// pipelines write: a view that read a different instance would be reading the same file anyway, but building
/// both from one set of seams makes that a property of the wiring rather than an accident of the file format.
/// </para>
/// </remarks>
public sealed class ModStateService : IModStateService
{
    private readonly IManifestStore _manifest;
    private readonly UninstallPipeline _uninstall;
    private readonly EnableDisablePipeline _toggle;

    /// <summary>Initializes the service with the production implementations of everything it sequences.</summary>
    /// <param name="paths">The resolved data root: snapshots, staging and the manifest all live under it.</param>
    /// <remarks>
    /// The convenience constructor the CLI (<c>213</c>) and the desktop app use. A caller that needs a different
    /// seam - a test with an injected failure, or a view model that already owns a manifest store - uses the full
    /// constructor.
    /// </remarks>
    public ModStateService(GamePolishPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        IPathValidator pathValidator = new PathValidator();
        IFileOps files = new FileOps();
        IManifestStore manifest = new ManifestStore(paths);
        IBackupEngine backups = new BackupEngine(paths);
        IGameDescriptorLoader descriptors = new GameDescriptorLoader(paths);
        IGamesRegistry games = new GamesRegistry(paths, descriptors);

        _manifest = manifest;
        _uninstall = new UninstallPipeline(paths, games, descriptors, backups, manifest, pathValidator, files);
        _toggle = new EnableDisablePipeline(paths, games, descriptors, backups, manifest, pathValidator, files);
    }

    /// <summary>Initializes the service with injected pipelines, for a caller that owns their seams.</summary>
    /// <param name="manifest">The installed-mod record, which is also where <see cref="GetRows"/> reads from.</param>
    /// <param name="uninstall">The uninstall pipeline.</param>
    /// <param name="toggle">The enable/disable pipeline.</param>
    public ModStateService(
        IManifestStore manifest,
        UninstallPipeline uninstall,
        EnableDisablePipeline toggle)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(toggle);

        _manifest = manifest;
        _uninstall = uninstall;
        _toggle = toggle;
    }

    /// <inheritdoc/>
    public IReadOnlyList<InstalledMod> GetRows(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        return _manifest.List(gameId);
    }

    /// <inheritdoc/>
    public Task<OperationResult> SetEnabledAsync(
        string gameId,
        string modId,
        bool enabled,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) =>
        enabled
            ? _toggle.EnableAsync(gameId, modId, progress, cancellationToken)
            : _toggle.DisableAsync(gameId, modId, progress, cancellationToken);

    /// <inheritdoc/>
    public Task<OperationResult> EnableAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) =>
        _toggle.EnableAsync(gameId, modId, progress, cancellationToken);

    /// <inheritdoc/>
    public Task<OperationResult> DisableAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) =>
        _toggle.DisableAsync(gameId, modId, progress, cancellationToken);

    /// <inheritdoc/>
    public Task<OperationResult> UninstallAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) =>
        _uninstall.UninstallAsync(gameId, modId, progress, cancellationToken);
}
