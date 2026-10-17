using GamePolish.Core.Operations;

namespace GamePolish.Core.Install;

/// <summary>
/// The one entry point that turns "game + source" into a working addon in the game's addons folder - or
/// into a game folder that is exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>The promise.</b> A successful call leaves the addon's files inside the game, one manifest entry that
/// describes them, and one backup snapshot taken before the first byte was written. A failed or cancelled
/// call leaves the game folder byte-identical to what it was, keeps any snapshot it already took, and never
/// leaves a manifest entry for files that are not there.
/// </para>
/// <para>
/// <b>Nothing throws for an expected failure.</b> A missing game, a broken clone, a refused path, a conflict,
/// a full disk: all of them are <see cref="OperationResult"/> values, so the toast, the CLI's exit code and
/// the log drawer all read one shape. Only a programming error (a null request) throws.
/// </para>
/// <para>
/// <b>Progress vocabulary</b> (<see cref="OperationProgress.Phase"/>), in the order the phases run:
/// <c>resolving</c>, <c>staging</c>, <c>validating</c>, <c>conflicts</c>, <c>backing-up</c>,
/// <c>copying</c>, <c>recording</c>, <c>cleaning-up</c>. <c>copying</c> is the only phase that reports a
/// useful total, because it is the only one whose item count is known up front; the rest report
/// <c>Total = 0</c> (indeterminate) rather than a bar that jumps.
/// </para>
/// </remarks>
public interface IInstallPipeline
{
    /// <summary>
    /// Reports what installing this request would replace, without replacing anything.
    /// </summary>
    /// <param name="request">The install to inspect. <see cref="InstallRequest.AcceptOverwrites"/> is ignored here.</param>
    /// <param name="cancellationToken">Cancels the staging step.</param>
    /// <returns>The three buckets of paths the install would touch.</returns>
    /// <exception cref="InstallException">
    /// The request cannot be inspected at all: the game is not tracked, the descriptor is missing, the source
    /// could not be staged. Its <see cref="Operations.CoreException.Code"/> names the failure.
    /// </exception>
    /// <exception cref="Paths.PathValidationException">The source contains a path that is not safe to write.</exception>
    /// <remarks>
    /// Side-effect free where it matters: it takes no snapshot, writes no manifest, and leaves the game
    /// directory untouched. It does stage the source under the data root - a conflict report about a
    /// repository that cannot be cloned would be a guess - and it removes that staging before it returns.
    /// It is the call the Add Mod flow makes first, so the user sees the dialog before the pipeline's own
    /// conflict check can refuse.
    /// </remarks>
    Task<ConflictReport> DetectConflictsAsync(InstallRequest request, CancellationToken cancellationToken);

    /// <summary>Installs the mod, or leaves the game folder as it was.</summary>
    /// <param name="request">What to install, and whether the caller has accepted overwrites.</param>
    /// <param name="progress">Per-phase progress, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="cancellationToken">
    /// Checked at every phase boundary and inside the copy loop. Cancelling before the backup writes nothing;
    /// cancelling after it rolls the game folder back and keeps the snapshot.
    /// </param>
    /// <returns>
    /// <see cref="OperationStatus.Succeeded"/> with the mod, folder, version and backup timestamp in it;
    /// <see cref="OperationStatus.Failed"/> with a code and the whole trail in <see cref="OperationResult.Detail"/>;
    /// or <see cref="OperationStatus.Cancelled"/> when the caller asked it to stop.
    /// </returns>
    Task<OperationResult> InstallAsync(
        InstallRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
