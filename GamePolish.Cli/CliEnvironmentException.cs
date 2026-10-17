using GamePolish.Core.Operations;

namespace GamePolish.Cli;

/// <summary>
/// The machine is not ready for the operation: the data root cannot be written, git is missing from
/// <c>PATH</c>, the game is not tracked, or its install folder is gone.
/// </summary>
/// <remarks>
/// <para>
/// This is the CLI's <see cref="ExitCodes.Environment"/> condition, and it is a distinct type so the boundary is
/// mechanical rather than a judgement call at a catch site: if a command raises this, the operation never
/// started and nothing was changed. That is exactly the distinction <c>3</c> promises a script, and it is why
/// the same failure surfaced later (a <c>git</c> that exits non-zero mid-clone) is <c>1</c> instead.
/// </para>
/// <para>
/// It derives from <see cref="CoreException"/> so the code it carries is the same vocabulary
/// <c>OperationResult.FromException</c> maps: a pre-flight failure still serialises as a normal failed result,
/// with a code a script can branch on.
/// </para>
/// </remarks>
public sealed class CliEnvironmentException : CoreException
{
    /// <summary>Initializes the exception with the code it reports and the sentence shown to the user.</summary>
    /// <param name="code">Why the environment is not ready; must not be <see cref="OperationErrorCode.None"/>.</param>
    /// <param name="message">One sentence naming the broken thing and how to fix it.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public CliEnvironmentException(OperationErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
