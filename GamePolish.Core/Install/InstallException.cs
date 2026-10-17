using GamePolish.Core.Operations;

namespace GamePolish.Core.Install;

/// <summary>
/// A step of the install pipeline refused to continue, with the code the UI branches on.
/// </summary>
/// <remarks>
/// <para>
/// Most of what the pipeline reports is already a result rather than an exception, so this type exists for
/// the two places where a throw is the right shape after all: <see cref="IInstallPipeline.DetectConflictsAsync"/>,
/// whose only other outcome is a report, and the internal "this request cannot be staged at all" checks that
/// happen before either public method has anything to return.
/// </para>
/// <para>
/// Deriving from <see cref="CoreException"/> means <see cref="OperationResult.FromException"/> maps it without
/// a new row in its table: the code declared at the throw site is the code the user's toast branches on, which
/// is exactly what that base class exists for.
/// </para>
/// </remarks>
public sealed class InstallException : CoreException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="code">Why the install cannot proceed. Must not be <see cref="OperationErrorCode.None"/>.</param>
    /// <param name="message">One user-facing sentence: what is wrong and what to do about it.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public InstallException(OperationErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
