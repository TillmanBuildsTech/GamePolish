namespace GamePolish.Core.Operations;

/// <summary>Progress of a long-running operation, as the UI needs it: a phase, a count, and the item being worked on.</summary>
/// <param name="Phase">What the operation is doing now, e.g. <c>downloading</c>, <c>backing up</c>, <c>copying</c>.</param>
/// <param name="Completed">Items finished in this phase.</param>
/// <param name="Total">
/// Items in this phase, or <c>0</c> when the total is not known yet - a git clone has no file count until
/// it starts producing one, and a determinate bar that jumps from 0% to 100% is worse than an indeterminate one.
/// </param>
/// <param name="CurrentItem">The file or mod being handled, when naming it helps; otherwise <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// Progress is reported separately from <see cref="OperationResult"/> rather than being a property of it:
/// an operation reports progress while it runs (through a callback the pipeline owns, task 208's seam) and
/// returns exactly one result when it ends. Nesting a mutable progress object inside the result would make
/// the result something that changes after it is returned.
/// </para>
/// <para>
/// No percentage is computed here. <paramref name="Total"/> being <c>0</c> is the "indeterminate" signal, and
/// only the view knows whether it can render an indeterminate bar; deriving a percentage in Core would
/// encode a UI decision in the engine.
/// </para>
/// </remarks>
public sealed record OperationProgress(string Phase, int Completed, int Total, string? CurrentItem = null);
