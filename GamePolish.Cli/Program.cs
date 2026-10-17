using GamePolish.Cli.CommandLine;
using GamePolish.Cli.Commands;
using GamePolish.Core.Operations;
using GamePolish.Core.Storage;

namespace GamePolish.Cli;

/// <summary>
/// Entry point: parse, resolve the data root, compose, run one command, print its result, exit with its code.
/// </summary>
/// <remarks>
/// <para>
/// This file holds the process-level contract and nothing else: the exit-code table, the two streams, Ctrl-C, and
/// the fact that a failure is printed as a <see cref="OperationResult"/> rather than as an unhandled exception. A
/// command body is argument reading plus one Core call - if a command needs more than that, the missing
/// behaviour belongs in Core, not here.
/// </para>
/// <para>
/// The data root is printed before anything else and before Core is constructed, so a transcript proves which
/// root it ran against even when the command then fails.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ParsedCommandLine parsed;

        try
        {
            parsed = CommandParser.Parse(args);
        }
        catch (CliUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Usage.Write(Console.Error);

            return ExitCodes.Usage;
        }

        if (parsed.Global.Help || parsed.Command is null)
        {
            Usage.Write(Console.Out);

            return ExitCodes.Success;
        }

        Reporter reporter = new(Console.Out, Console.Error, parsed.Global.Json, parsed.Global.Debug);

        GamePolishPaths paths;

        try
        {
            paths = GamePolishPaths.Resolve(parsed.Global.DataRoot);
        }
        catch (ArgumentException exception)
        {
            // The root is unusable before anything runs: a relative path, a file, or a drive root. That is the
            // environment, not the operation.
            reporter.WriteResult(OperationResult.Failure(OperationErrorCode.DataRootUnavailable, exception.Message));

            return ExitCodes.Environment;
        }

        reporter.WriteDataRoot(paths.Root);

        Composition composition = Composition.Create(paths, parsed.Global.LogLevel);

        using CancellationScope cancellation = new();

        try
        {
            CommandOutcome outcome = await CommandDispatcher
                .RunAsync(parsed.Command, composition, reporter, cancellation.Token)
                .ConfigureAwait(false);

            reporter.WriteResult(outcome.Result);

            return outcome.EffectiveExitCode;
        }
        catch (CliEnvironmentException exception)
        {
            reporter.WriteResult(OperationResult.Failure(
                exception.Code,
                exception.Message,
                exception.InnerException?.ToString()));

            return ExitCodes.Environment;
        }
        catch (OperationCanceledException exception)
        {
            reporter.WriteResult(OperationResult.FromException(
                exception,
                parsed.Command.Spec.Name,
                null));

            return ExitCodes.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An expected failure comes back as a result; reaching here means something unexpected escaped. It is
            // still printed as a result - one line and a code - because a stack trace on stdout is not a report,
            // and the detail is kept for --log-level debug.
            reporter.WriteResult(OperationResult.FromException(exception, null, null));

            return ExitCodes.Failure;
        }
    }
}
