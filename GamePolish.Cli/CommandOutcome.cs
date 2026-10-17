using GamePolish.Core.Operations;

namespace GamePolish.Cli;

/// <summary>
/// What a command hands back: the Core result, plus the one case where the exit code is not the status's.
/// </summary>
/// <remarks>
/// <para>
/// Almost every command's exit code is a pure function of its result - succeeded is <c>0</c>, cancelled is
/// <c>130</c>, failed is <c>1</c> - and <see cref="ExitCodes.For(OperationResult)"/> computes it. The exception is
/// <c>doctor</c>: its report is a single document, and "this machine is not ready" must exit <c>3</c> rather than
/// <c>1</c> even though the result is a failed one. Rather than special-casing doctor at the call site, a command
/// may pin its own code here and everything else keeps the derived one.
/// </para>
/// </remarks>
public sealed record CommandOutcome
{
    /// <summary>The Core result to print.</summary>
    public required OperationResult Result { get; init; }

    /// <summary>An explicit exit code, or <see langword="null"/> to derive it from <see cref="Result"/>.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The code the process exits with.</summary>
    public int EffectiveExitCode => ExitCode ?? ExitCodes.For(Result);

    /// <summary>Wraps a Core result, deriving the exit code from its status.</summary>
    /// <param name="result">The result.</param>
    public static CommandOutcome FromResult(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CommandOutcome { Result = result };
    }
}
