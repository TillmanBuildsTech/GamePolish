using GamePolish.Cli.CommandLine;
using GamePolish.Core.Games;
using GamePolish.Core.Install;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Update;

namespace GamePolish.Cli.Commands;

/// <summary>
/// <c>list</c>, <c>install</c>, <c>update</c>, <c>check</c>, <c>uninstall</c>, <c>enable</c> and <c>disable</c>:
/// the mod lifecycle, tasks <c>206</c>, <c>208</c>, <c>209</c> and <c>210</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation here is one call into a pipeline, on purpose. The pipeline owns the order of backup, swap,
/// manifest write and rollback, and it is the only thing that can honour "a backup precedes the first byte"
/// (invariant #1) - a CLI that opened a file itself would be a second, unaudited writer into the game folder.
/// </para>
/// <para>
/// The state changes go through <see cref="IModStateService"/>, the same facade the Library view uses, so a bug
/// this tool reproduces is a bug the UI will hit.
/// </para>
/// </remarks>
internal static class ModCommands
{
    /// <summary>Lists the installed mods of one game.</summary>
    /// <returns>Always <c>0</c>: an empty list is a fact.</returns>
    internal static Task<CommandOutcome> ListAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");

        IReadOnlyList<InstalledMod> mods = composition.Mods.GetRows(gameId);

        IReadOnlyList<OperationLogEntry> rows = Rows.Table(
        [
            .. mods.Select(mod => (IReadOnlyList<string>)
                [mod.Id, mod.Name, mod.Version, mod.VersionKind, mod.Enabled ? "enabled" : "disabled", mod.FolderName]),
        ]);

        string message = mods.Count == 0
            ? $"No mods are installed for {gameId}."
            : $"{mods.Count} mod(s) installed for {gameId}.";

        return Task.FromResult(CommandOutcome.FromResult(OperationResult.Success(message, gameId, entries: rows)));
    }

    /// <summary>Installs a mod from a GitHub repository or from a ZIP the user has on disk.</summary>
    /// <returns><c>0</c> installed; <c>1</c> the pipeline failed; <c>130</c> cancelled; <c>3</c> the game or git
    /// was not ready.</returns>
    internal static async Task<CommandOutcome> InstallAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");

        Preflight.RequireTrackedGame(composition.Games, gameId);

        if (command.Option("source") is string source)
        {
            Preflight.RequireGit(composition.Git);

            return CommandOutcome.FromResult(await composition.Install.InstallAsync(
                new InstallRequest
                {
                    GameId = gameId,
                    SourceType = ModSourceType.Github,
                    SourceUrl = source,
                    Branch = command.Option("branch"),
                    DisplayName = command.Option("name"),
                    ModId = command.Option("mod"),
                    AcceptOverwrites = command.Flag("overwrite"),
                },
                reporter,
                cancellationToken).ConfigureAwait(false));
        }

        // --zip is the other half of the exclusive group, so the parser has already refused "neither" and "both".
        // What it cannot know is that --branch, --mod and --overwrite describe a repository: ignoring them would
        // let a caller believe they had taken effect.
        if (command.Option("branch") is not null || command.Option("mod") is not null || command.Flag("overwrite"))
        {
            throw new CliUsageException("--branch, --mod and --overwrite only apply to --source. A ZIP install takes --zip and --name.");
        }

        string zipPath = command.Required("zip");

        if (!File.Exists(zipPath))
        {
            throw new CliEnvironmentException(
                OperationErrorCode.SourceNotFound,
                $"The archive '{zipPath}' does not exist.");
        }

        // Streamed straight into staging. Reading the archive into a byte[] first would put a user's whole addon
        // ZIP in memory for no reason, and staging is what decides whether it is a usable mod.
        await using FileStream zip = File.OpenRead(zipPath);

        return CommandOutcome.FromResult(await composition.Install.InstallAsync(
            new InstallRequest
            {
                GameId = gameId,
                SourceType = ModSourceType.Zip,
                ZipStream = zip,
                ZipFileName = Path.GetFileName(zipPath),
                DisplayName = command.Option("name"),
            },
            reporter,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Replaces an installed mod with what its source serves now.</summary>
    /// <returns><c>0</c> updated, or already up to date; <c>1</c> the pipeline failed; <c>130</c> cancelled.</returns>
    internal static async Task<CommandOutcome> UpdateAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");
        string modId = ResolveMod(composition, gameId, command.Required("mod"));

        Preflight.RequireTrackedGame(composition.Games, gameId);
        Preflight.RequireGit(composition.Git);

        return CommandOutcome.FromResult(await composition.Update
            .UpdateAsync(gameId, modId, reporter, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Prints the update state of one mod, or of every installed mod.</summary>
    /// <returns>
    /// <c>0</c> once the states are printed, including when an update is available or a check failed: this command
    /// reports, it does not act, and "there is a newer version" is not an error.
    /// </returns>
    internal static async Task<CommandOutcome> CheckAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");

        Preflight.RequireTrackedGame(composition.Games, gameId);

        IReadOnlyList<string> modIds = command.Option("mod") is string requestedMod
            ? [ResolveMod(composition, gameId, requestedMod)]
            : [.. composition.Mods.GetRows(gameId).Select(mod => mod.Id)];

        if (modIds.Count == 0)
        {
            return CommandOutcome.FromResult(OperationResult.Success(
                $"No mods are installed for {gameId}, so there is nothing to check.",
                gameId));
        }

        List<IReadOnlyList<string>> cells = [];

        foreach (string modId in modIds)
        {
            UpdateAvailability availability = await composition.Update
                .CheckAsync(gameId, modId, cancellationToken)
                .ConfigureAwait(false);

            cells.Add(
            [
                modId,
                availability.InstalledVersion,
                availability.LatestVersion ?? "-",
                availability.State.ToString(),
                availability.Reason ?? "-",
            ]);
        }

        return CommandOutcome.FromResult(OperationResult.Success(
            $"{modIds.Count} mod(s) checked for {gameId}.",
            gameId,
            entries: Rows.Table(cells)));
    }

    /// <summary>Removes an installed mod and its manifest entry, after a backup.</summary>
    /// <returns><c>0</c> removed; <c>1</c> the pipeline failed; <c>130</c> cancelled.</returns>
    internal static async Task<CommandOutcome> UninstallAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");
        string modId = ResolveMod(composition, gameId, command.Required("mod"));

        Preflight.RequireTrackedGame(composition.Games, gameId);

        return CommandOutcome.FromResult(await composition.Mods
            .UninstallAsync(gameId, modId, reporter, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Puts a mod's files back into the game folder, or parks them under the data root.</summary>
    /// <param name="enabled">True for <c>enable</c>, false for <c>disable</c>.</param>
    /// <returns><c>0</c> changed or already in that state; <c>1</c> the pipeline failed; <c>130</c> cancelled.</returns>
    internal static async Task<CommandOutcome> SetEnabledAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        bool enabled,
        CancellationToken cancellationToken)
    {
        string gameId = command.Required("game");
        string modId = ResolveMod(composition, gameId, command.Required("mod"));

        Preflight.RequireTrackedGame(composition.Games, gameId);

        return CommandOutcome.FromResult(await composition.Mods
            .SetEnabledAsync(gameId, modId, enabled, reporter, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Turns a user-typed mod reference into the manifest id, accepting the id or the folder name.
    /// </summary>
    /// <param name="composition">The wired Core components, for the manifest rows.</param>
    /// <param name="gameId">The game the mod belongs to.</param>
    /// <param name="requested">What the user typed after <c>--mod</c>.</param>
    /// <remarks>
    /// The manifest matches ids exactly, and an installed mod's id is the registry id or a slug of the folder
    /// name (<c>pfui</c> for the folder <c>pfUI</c>) - so the name a user reads off the folder, and that
    /// <c>list</c> prints as the last column, is not always the id. Resolving id first and folder name second
    /// keeps the exact-id rule intact while making <c>--mod pfUI</c> work, which is what the acceptance script
    /// in the work order types. An unknown reference is passed through unchanged so the pipeline reports it with
    /// the name the user actually used.
    /// </remarks>
    private static string ResolveMod(Composition composition, string gameId, string requested)
    {
        IReadOnlyList<InstalledMod> rows = composition.Mods.GetRows(gameId);

        InstalledMod? match =
            rows.FirstOrDefault(mod => string.Equals(mod.Id, requested, StringComparison.Ordinal))
            ?? rows.FirstOrDefault(mod => string.Equals(mod.FolderName, requested, PathComparer.PathComparison));

        return match?.Id ?? requested;
    }
}
