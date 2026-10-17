using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;

namespace GamePolish.Core.Mods;

/// <summary>
/// The three things the Library row can do to an installed mod, behind one interface.
/// </summary>
/// <remarks>
/// <para>
/// The view model (<c>302</c>) talks to this rather than to <c>UninstallPipeline</c> and
/// <c>EnableDisablePipeline</c> directly, so it never has to decide which pipeline a click belongs to or which
/// order the manifest, the backup and the swap have to happen in. The row's spinner, the checkbox lock and the
/// confirm dialogs are the view's; everything here returns an <see cref="OperationResult"/> and reports
/// <see cref="OperationProgress"/>, and nothing else.
/// </para>
/// <para>
/// Every member is idempotent where that is meaningful: enabling an enabled mod, disabling a disabled one and
/// uninstalling what is already gone all succeed without taking a second snapshot. A double-click on the
/// checkbox is therefore safe, which is the property the checkboxes actually need.
/// </para>
/// </remarks>
public interface IModStateService
{
    /// <summary>
    /// The rows the Library's mod list renders, straight from the manifest.
    /// </summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <remarks>
    /// The manifest entry <i>is</i> the row's data - name, version, enabled flag, file list. Grouping into
    /// Enabled and Disabled sections, filtering by the search box and sorting are the view model's, because they
    /// are presentation rather than state.
    /// </remarks>
    IReadOnlyList<InstalledMod> GetRows(string gameId);

    /// <summary>Sets one mod's enabled state, which is what the row's checkbox asks for.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="enabled">The state the checkbox should end up in.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary.</param>
    /// <remarks>
    /// One call for both directions, because the checkbox is one control: a view that had to decide between
    /// enable and disable would have to re-derive the state it is trying to change.
    /// </remarks>
    Task<OperationResult> SetEnabledAsync(
        string gameId,
        string modId,
        bool enabled,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Puts a disabled mod's files back into the game folder, after snapshotting it.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary.</param>
    Task<OperationResult> EnableAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Parks a mod's files under the data root, after snapshotting the game folder.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary.</param>
    Task<OperationResult> DisableAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Removes a mod's files and its manifest entry, after snapshotting them.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The installed mod's id.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">Checked at every step boundary, up to the delete.</param>
    Task<OperationResult> UninstallAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
