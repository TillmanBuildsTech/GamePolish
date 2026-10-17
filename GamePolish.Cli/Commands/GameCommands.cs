using GamePolish.Cli.CommandLine;
using GamePolish.Core.Games;
using GamePolish.Core.Operations;

namespace GamePolish.Cli.Commands;

/// <summary>
/// <c>add-game</c>, <c>list-games</c> and <c>remove-game</c>: the tracked-games registry, task <c>203</c>.
/// </summary>
/// <remarks>
/// This is the group that turns a folder on disk into "the game the app is allowed to write into", and it is the
/// only way a game ever becomes tracked. <c>add-game</c> validates the folder through Core
/// (<see cref="IGamesRegistry.Add"/> checks the descriptor, the verify file, the data-root overlap and the
/// duplicate) - the CLI never decides what counts as an install.
/// </remarks>
internal static class GameCommands
{
    /// <summary>Records a game and its install folder.</summary>
    /// <returns><c>0</c> when the game was added; <c>1</c> when Core refused the folder; <c>3</c> when the game
    /// has no descriptor or the data root is not writable.</returns>
    internal static Task<CommandOutcome> AddAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Positional(0);
        string installPath = command.Required("path");

        Preflight.RequireDescriptor(composition.Descriptors, gameId);
        Preflight.RequireWritableDataRoot(composition.Paths);

        TrackedGame added = composition.Games.Add(gameId, installPath);

        return Task.FromResult(CommandOutcome.FromResult(
            OperationResult.Success($"{added.Name} is now tracked at {added.InstallPath}.", added.Id)));
    }

    /// <summary>Lists tracked games, marking the ones whose folder or verify file has gone.</summary>
    /// <returns>Always <c>0</c>: an empty library and an unresolved path are both facts, not failures.</returns>
    internal static Task<CommandOutcome> ListAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrackedGame> games = composition.Games.List();

        HashSet<string> unresolved = [.. composition.Games.ListUnresolved().Select(game => game.Id)];

        IReadOnlyList<OperationLogEntry> rows = Rows.Table(
        [
            .. games.Select(game => (IReadOnlyList<string>)
                [game.Id, game.Name, game.InstallPath, unresolved.Contains(game.Id) ? "unresolved" : "ok"]),
        ]);

        string message = games.Count == 0
            ? "No games are tracked. Add one with: add-game <gameId> --path <install dir>"
            : $"{games.Count} game(s) tracked.";

        return Task.FromResult(CommandOutcome.FromResult(OperationResult.Success(message, entries: rows)));
    }

    /// <summary>Untracks a game. Mods, manifests and backups stay on disk.</summary>
    /// <returns><c>0</c> when an entry was removed; <c>1</c> when the game was not tracked.</returns>
    internal static Task<CommandOutcome> RemoveAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Positional(0);

        if (!composition.Games.Remove(gameId))
        {
            return Task.FromResult(CommandOutcome.FromResult(OperationResult.Failure(
                OperationErrorCode.GameNotFound,
                $"'{gameId}' is not tracked, so there was nothing to remove.")));
        }

        return Task.FromResult(CommandOutcome.FromResult(OperationResult.Success(
            $"{gameId} is no longer tracked. Its mods, manifests and backups were left on disk.",
            gameId)));
    }
}
