using System.Globalization;

using GamePolish.Cli.CommandLine;
using GamePolish.Core.Games;
using GamePolish.Core.Operations;
using GamePolish.Core.Registry;

namespace GamePolish.Cli.Commands;

/// <summary>
/// <c>registry refresh</c>: the registry fetch and cache, task <c>211</c>.
/// </summary>
/// <remarks>
/// A forced refresh, because the command exists to answer "can this machine reach the registry right now" - the
/// cached path is what <c>GetAsync</c> already does on every ordinary call, and reporting a seven-day-old cache hit
/// as a successful refresh would be exactly the kind of claim the plan's evidence rules exist to prevent.
/// </remarks>
internal static class RegistryCommands
{
    /// <summary>Forces a registry fetch and prints the mod count and fetch time.</summary>
    /// <returns><c>0</c> fetched (or served from cache while offline, which the message says); <c>1</c> the fetch
    /// or the payload failed; <c>3</c> the game has no descriptor.</returns>
    internal static async Task<CommandOutcome> RefreshAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");

        GameDescriptor descriptor = Preflight.RequireDescriptor(composition.Descriptors, gameId);

        RegistryResult result = await composition.Registry
            .GetAsync(gameId, descriptor.RegistryUrl, forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);

        List<OperationLogEntry> rows =
        [
            Rows.Line($"mods: {result.Registry.Mods.Count}"),
            Rows.Line($"last fetched: {result.LastFetched.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}"),
            Rows.Line($"source: {(result.FromCache ? "cache" : "network")}{(result.Offline ? " (offline - the fetch failed)" : string.Empty)}"),
            Rows.Line($"stale: {(result.IsStale ? "yes" : "no")}"),
        ];

        string message = result.Offline
            ? $"The registry fetch failed; showing {result.Registry.Mods.Count} cached mod(s) last fetched "
                + $"{result.LastFetched.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}."
            : $"{result.Registry.Mods.Count} mod(s) in the {gameId} registry, last fetched "
                + $"{result.LastFetched.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}.";

        return CommandOutcome.FromResult(OperationResult.Success(message, gameId, entries: rows));
    }
}
