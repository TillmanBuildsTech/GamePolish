using GamePolish.Core.Operations;

namespace GamePolish.Core.Update;

/// <summary>
/// A step of the update pipeline refused to continue, with the code the UI branches on.
/// </summary>
/// <remarks>
/// <para>
/// The update path throws where the refusal is a decision rather than a result: "this mod is disabled",
/// "this mod came from an archive", "another mod owns this folder". Those are detected before anything is
/// staged or written, and the pipeline's one failure handler turns each into an
/// <see cref="OperationResult"/> - so the throw never crosses the public surface.
/// </para>
/// <para>
/// Deliberately its own type rather than <c>InstallException</c>: the two pipelines share the swap and the
/// conflict shape, and everything else about them is different enough - a disabled mod, an archive source, a
/// folder the user is about to lose edits in - that one exception type claiming to describe both would make
/// the log read as if an install had failed. Deriving from <see cref="CoreException"/> means
/// <see cref="OperationResult.FromException"/> maps it with no new table row.
/// </para>
/// </remarks>
public sealed class UpdateException : CoreException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="code">Why the update cannot proceed. Must not be <see cref="OperationErrorCode.None"/>.</param>
    /// <param name="message">One user-facing sentence: what is wrong and what to do about it.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public UpdateException(OperationErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
