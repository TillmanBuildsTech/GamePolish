using GamePolish.Core.Operations;

namespace GamePolish.Cli;

/// <summary>
/// The five ways a CLI invocation can end, as one table the parser test can assert.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are a contract with task <c>405</c>, whose acceptance script branches on them, so they are
/// spelled out here rather than derived from <see cref="GamePolish.Core.Operations.OperationStatus"/>: the
/// process exit code and the operation status are different vocabularies, and a shell script cannot read an
/// enum. <c>0</c>/<c>1</c>/<c>130</c> happen to match the shell convention, <c>2</c> is the conventional usage
/// error, and <c>3</c> is this tool's own "the machine is not ready" code.
/// </para>
/// <para>
/// The mapping lives in one place because two places that map "how it ended" differently is how a script ends
/// up treating a cancelled install as a failure.
/// </para>
/// </remarks>
public static class ExitCodes
{
    /// <summary>The command completed.</summary>
    public const int Success = 0;

    /// <summary>The operation failed. The result carries the code and the message.</summary>
    public const int Failure = 1;

    /// <summary>
    /// Usage error: an unknown command, an unknown option, or a missing, duplicate or contradictory
    /// argument. Nothing was attempted, so nothing was changed.
    /// </summary>
    public const int Usage = 2;

    /// <summary>
    /// Environment error, detected before the operation started: the data root is not writable, git is not on
    /// <c>PATH</c>, the game is not tracked, or its install folder has gone missing.
    /// </summary>
    public const int Environment = 3;

    /// <summary>The caller pressed Ctrl-C. Core rolled the operation back, so nothing is half-written.</summary>
    public const int Cancelled = 130;

    /// <summary>Maps an operation result onto its exit code.</summary>
    /// <param name="result">The result every Core command returns.</param>
    public static int For(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            OperationStatus.Succeeded => Success,
            OperationStatus.Cancelled => Cancelled,

            // Failed, and anything a future status adds: a status this build does not know about is not a
            // success, and claiming one would make a script report a broken run as green.
            _ => Failure,
        };
    }

    /// <summary>Maps an exception that escaped a command onto its exit code.</summary>
    /// <param name="exception">The failure.</param>
    /// <remarks>
    /// Only the three types this tool itself raises or receives are special-cased. Everything else is
    /// <see cref="Failure"/>: a Core exception is an operation that failed, which is what <c>1</c> means.
    /// </remarks>
    public static int For(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            CliUsageException => Usage,
            CliEnvironmentException => Environment,
            OperationCanceledException => Cancelled,
            _ => Failure,
        };
    }
}
