using GamePolish.Core.Operations;

namespace GamePolish.Core.Update;

/// <summary>
/// The two questions the Library asks about an installed mod: "is there a newer one?" and "do it".
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CheckAsync"/> is cheap and never mutates; <see cref="UpdateAsync"/> is expensive and mutates
/// under the product's invariants. They are separate calls on purpose. A row that is repainted on scroll
/// must not be able to start a download, and a confirm button must not be able to run on a stale answer -
/// the caller checks, shows the sheet with the changelog (task 503) and the version, then updates.
/// </para>
/// <para>
/// Neither method installs a mod that is not already installed. "Update" is not "install" with a different
/// button label: <see cref="CheckAsync"/> reports <see cref="UpdateState.Unknown"/> for an unknown mod, and
/// <see cref="UpdateAsync"/> fails with <see cref="OperationErrorCode.ModNotInstalled"/> rather than
/// adopting a folder the manifest does not know about.
/// </para>
/// </remarks>
public interface IUpdatePipeline
{
    /// <summary>
    /// Answers whether the source serves something other than what is installed.
    /// </summary>
    /// <param name="gameId">The game the mod belongs to.</param>
    /// <param name="modId">The mod, matched exactly against the manifest.</param>
    /// <param name="cancellationToken">Abandons the check. Cancelling leaves no trace anywhere.</param>
    /// <returns>
    /// One of the four <see cref="UpdateState"/> answers. <see cref="UpdateState.Unknown"/> and
    /// <see cref="UpdateState.CheckFailed"/> both mean "show no badge"; the UI must render
    /// <see cref="UpdateState.Unknown"/> as <b>nothing at all</b>, never as "up to date", because an
    /// archive-sourced or unrecorded mod has no upstream to be current with.
    /// </returns>
    /// <remarks>
    /// <b>Never mutates.</b> No backup, no manifest write, no staging directory - not even when the check is
    /// cancelled halfway. That is why it can be called for every row of the Library, on a timer, without
    /// leaving a footprint.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled the check.</exception>
    Task<UpdateAvailability> CheckAsync(string gameId, string modId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces an installed mod's folder with what its source serves now, after backing up what is there.
    /// </summary>
    /// <param name="gameId">The game the mod belongs to.</param>
    /// <param name="modId">The mod, matched exactly against the manifest.</param>
    /// <param name="progress">Reported per phase and per copied file; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels before the backup (nothing changed) or after it (rolled back).</param>
    /// <returns>
    /// A result whose message names both versions, whose <see cref="OperationResult.Detail"/> lists any local
    /// edits the update replaced, and whose <see cref="OperationResult.BackupTimestamp"/> points at the
    /// snapshot taken before the first byte moved.
    /// </returns>
    /// <remarks>
    /// The eight phases are: resolve, re-stage, validate, scan conflicts, scan local edits, back up, swap,
    /// record. A failure at any point leaves the previous folder and the previous manifest entry in place; a
    /// backup already taken is kept.
    /// </remarks>
    Task<OperationResult> UpdateAsync(
        string gameId,
        string modId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
