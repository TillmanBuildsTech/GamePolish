namespace GamePolish.Core.Operations;

/// <summary>How an operation ended.</summary>
/// <remarks>
/// <para>
/// Three outcomes, not two. A cancelled install is not a failure: the user asked for it, nothing is
/// wrong, and the UI must not offer "try again" with an error banner as though something broke. It is
/// also not a success, because nothing was installed and the toast must not claim otherwise. Keeping
/// the three apart here means every consumer - the toast, the log drawer, the CLI's exit code - branches
/// on one value instead of guessing from <see cref="OperationResult.Code"/> or from the message text.
/// </para>
/// <para>
/// Values are explicit and append-only: they are persisted in the structured result (task 213's
/// <c>--json</c>) and in log lines, so inserting a member in the middle would silently change the
/// meaning of everything already written.
/// </para>
/// </remarks>
public enum OperationStatus
{
    /// <summary>The operation completed. <see cref="OperationErrorCode.None"/> is the matching code.</summary>
    Succeeded = 0,

    /// <summary>The operation stopped before completing; <see cref="OperationResult.Code"/> says why.</summary>
    Failed = 1,

    /// <summary>
    /// The caller cancelled. Nothing was left half-written, and the same operation can be retried as-is.
    /// </summary>
    Cancelled = 2,
}
