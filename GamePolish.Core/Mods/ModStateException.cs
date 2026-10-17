using GamePolish.Core.Operations;

namespace GamePolish.Core.Mods;

/// <summary>
/// A step of one of the three mod-state operations refused to continue, with the code the UI branches on.
/// </summary>
/// <remarks>
/// <para>
/// The pipelines return <see cref="OperationResult"/> for everything they expect, so this type is for the
/// places where a throw is still the right shape: a check that happens before any result exists (the mod is
/// not installed, the game is no longer tracked) and a refusal that has to unwind a step which is already
/// half done.
/// </para>
/// <para>
/// Deriving from <see cref="CoreException"/> means <see cref="OperationResult.FromException"/> maps it with no
/// new row in its table: <c>ModNotInstalled</c>, <c>ModFolderMissing</c>, <c>StagedCopyMissing</c> and
/// <c>GameInstallPathMissing</c> are declared at the throw site, next to the sentence the user reads.
/// </para>
/// </remarks>
internal sealed class ModStateException : CoreException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="code">Why the operation cannot proceed. Must not be <see cref="OperationErrorCode.None"/>.</param>
    /// <param name="message">One user-facing sentence: what is wrong and what to do about it.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public ModStateException(OperationErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
