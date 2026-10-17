using GamePolish.Cli.CommandLine;
using GamePolish.Core.Operations;

namespace GamePolish.Cli.Commands;

/// <summary>
/// Routes a parsed command to the class that maps it onto Core.
/// </summary>
/// <remarks>
/// A switch, not a framework: the catalog is sixteen commands and the dispatcher is the only place that knows
/// which class handles which. Each branch is one call, so the mapping from the work order's command list to the
/// Core method behind it is readable in one screen.
/// </remarks>
public static class CommandDispatcher
{
    /// <summary>Runs <paramref name="command"/> against <paramref name="composition"/>.</summary>
    /// <param name="command">The parsed command.</param>
    /// <param name="composition">The constructed Core graph.</param>
    /// <param name="reporter">Where progress and the result go.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl-C; every pipeline observes it.</param>
    public static Task<CommandOutcome> RunAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(reporter);

        _ = composition;
        _ = reporter;
        _ = cancellationToken;

        return Task.FromResult(CommandOutcome.FromResult(
            OperationResult.Failure(OperationErrorCode.Unknown, $"'{command.Spec.Name}' is not wired up yet.")));
    }
}
