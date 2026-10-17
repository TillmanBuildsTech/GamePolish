using GamePolish.Cli.CommandLine;
using GamePolish.Core.Operations;

namespace GamePolish.Cli.Commands;

/// <summary>
/// <c>descriptors</c>: what this build knows how to install, and what is wrong with the files it found.
/// </summary>
/// <remarks>
/// The report is the answer to "why does <c>add-game octowow</c> say the game is not supported": it lists each
/// directory that was searched, each file that loaded, and the first validation problem in each file that did not.
/// A descriptor is the only thing that makes a game exist (invariant #6), so an invalid one is a failure of this
/// command rather than a warning it buries.
/// </remarks>
internal static class DescriptorCommands
{
    /// <summary>Lists every descriptor, its addon destination, and any validation error.</summary>
    /// <returns><c>0</c> when at least one descriptor loaded and none is invalid; <c>1</c> otherwise.</returns>
    internal static Task<CommandOutcome> ListAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DescriptorScan.Entry> entries = DescriptorScan.Run(composition.Descriptors);

        IReadOnlyList<string> searched = composition.Descriptors.DescriptorDirectories;

        if (entries.Count == 0)
        {
            return Task.FromResult(CommandOutcome.FromResult(OperationResult.Failure(
                OperationErrorCode.DescriptorInvalid,
                $"No game descriptors were found. Searched: {string.Join(", ", searched)}. "
                + "Put a <id>.yaml in one of those directories.")));
        }

        int invalid = entries.Count(entry => entry.Descriptor is null);

        IReadOnlyList<OperationLogEntry> rows = Rows.Table(
        [
            .. entries.Select(entry => (IReadOnlyList<string>)
            [
                entry.Id,
                entry.Descriptor?.Name ?? "-",
                entry.Descriptor?.ModLayout.Destination ?? "-",
                entry.Descriptor is null ? "invalid" : "ok",
                entry.Descriptor is null ? "-" : entry.Descriptor.RegistryUrl,
            ]),
            .. entries
                .Where(entry => entry.Problem is not null)
                .Select(entry => (IReadOnlyList<string>)["", "", "", "problem", entry.Problem!]),
        ]);

        string message = invalid == 0
            ? $"{entries.Count} descriptor(s) found; searched {searched.Count} director(y/ies)."
            : $"{entries.Count} descriptor(s) found, {invalid} invalid.";

        OperationResult result = invalid == 0
            ? OperationResult.Success(message, entries: rows)
            : OperationResult.Failure(
                OperationErrorCode.DescriptorInvalid,
                message,
                detail: string.Join(Environment.NewLine, entries.Where(entry => entry.Problem is not null)
                    .Select(entry => $"{entry.File}: {entry.Problem}")),
                entries: rows);

        return Task.FromResult(CommandOutcome.FromResult(result));
    }
}
