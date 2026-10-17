using GamePolish.Core.Games;
using GamePolish.Core.Operations;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;

namespace GamePolish.Cli;

/// <summary>
/// The checks that decide <c>3</c>: is the machine able to run this operation at all?
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs before Core is asked to do anything, and everything here raises
/// <see cref="CliEnvironmentException"/>, which is the one type that maps to
/// <see cref="ExitCodes.Environment"/>. The distinction the work order draws is worth keeping sharp: a game
/// whose install folder was moved is a fact about the machine (fix the path, retry), while a clone that fails
/// because the repository does not exist is an operation that failed (pick another mod). Conflating them would
/// make the exit code useless to a script.
/// </para>
/// <para>
/// <see cref="ProbeDataRoot"/> is read-only in effect but not literally: proving a directory is writable without
/// writing is not possible portably (a Windows directory's read-only attribute does not mean anything, and the
/// ACL API is platform-specific), so it creates one empty uniquely named file with
/// <see cref="FileOptions.DeleteOnClose"/> - the OS removes it as the handle closes, including on an exception.
/// The directory is byte-identical afterwards, which is what <c>405</c>'s before/after hash asserts.
/// </para>
/// </remarks>
public static class Preflight
{
    /// <summary>What the data root looks like right now.</summary>
    /// <param name="Exists">True when the root is an existing directory.</param>
    /// <param name="Writable">True when a probe file could be created in it and removed again.</param>
    public sealed record DataRootState(bool Exists, bool Writable);

    /// <summary>
    /// Creates the data root if it is missing and proves it is writable, or refuses the command with <c>3</c>.
    /// </summary>
    /// <param name="paths">The resolved data root.</param>
    /// <exception cref="CliEnvironmentException">The root cannot be created or written.</exception>
    /// <remarks>
    /// Only commands that mutate call this. A <c>list</c> that created the root would be a read command with a
    /// side effect, and <c>doctor</c> must leave no trace at all.
    /// </remarks>
    public static void RequireWritableDataRoot(GamePolishPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            Directory.CreateDirectory(paths.Root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CliEnvironmentException(
                OperationErrorCode.DataRootUnavailable,
                $"The data root '{paths.Root}' could not be created: {exception.Message}",
                exception);
        }

        DataRootState state = ProbeDataRoot(paths);

        if (!state.Writable)
        {
            throw new CliEnvironmentException(
                OperationErrorCode.DataRootUnavailable,
                $"The data root '{paths.Root}' is not writable. Choose another one with --data-root.");
        }
    }

    /// <summary>Reports whether the data root exists and can be written, without creating it.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <remarks>
    /// A missing root is not an error: a fresh machine has none until the first command that writes. It is
    /// reported as <c>Exists = false</c> and <c>doctor</c> says so.
    /// </remarks>
    public static DataRootState ProbeDataRoot(GamePolishPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!Directory.Exists(paths.Root))
        {
            return new DataRootState(Exists: false, Writable: false);
        }

        try
        {
            string probe = Path.Combine(paths.Root, $".game-polish-probe-{Guid.NewGuid():N}");

            using FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);

            return new DataRootState(Exists: true, Writable: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DataRootState(Exists: true, Writable: false);
        }
    }

    /// <summary>
    /// Returns the tracked game, or refuses with <c>3</c> when it is untracked or its folder has gone missing.
    /// </summary>
    /// <param name="games">The tracked-games registry.</param>
    /// <param name="gameId">The game the command names.</param>
    /// <exception cref="CliEnvironmentException">The game is not tracked, or its install folder is gone.</exception>
    public static TrackedGame RequireTrackedGame(IGamesRegistry games, string gameId)
    {
        ArgumentNullException.ThrowIfNull(games);

        TrackedGame game = games.Get(gameId)
            ?? throw new CliEnvironmentException(
                OperationErrorCode.GameNotFound,
                $"'{gameId}' is not tracked yet. Add it first: GamePolish.Cli add-game {gameId} --path <install dir>");

        if (!Directory.Exists(game.InstallPath))
        {
            throw new CliEnvironmentException(
                OperationErrorCode.GameInstallPathMissing,
                $"{game.Name}'s install folder '{game.InstallPath}' does not exist. Plug the drive back in, or re-add the game with its new path.");
        }

        return game;
    }

    /// <summary>Returns the game's descriptor, or refuses with <c>3</c> when this build does not support the game.</summary>
    /// <param name="descriptors">The descriptor loader.</param>
    /// <param name="gameId">The game the command names.</param>
    /// <exception cref="CliEnvironmentException">No usable descriptor exists for the id.</exception>
    public static GameDescriptor RequireDescriptor(IGameDescriptorLoader descriptors, string gameId)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        GameDescriptor? descriptor;

        try
        {
            descriptor = descriptors.LoadById(gameId);
        }
        catch (GameDescriptorValidationException exception)
        {
            throw new CliEnvironmentException(OperationErrorCode.DescriptorInvalid, exception.Message, exception);
        }

        return descriptor ?? throw new CliEnvironmentException(
            OperationErrorCode.DescriptorInvalid,
            $"'{gameId}' is not a supported game: no {gameId}.yaml was found in {string.Join(", ", descriptors.DescriptorDirectories)}.");
    }

    /// <summary>Refuses with <c>3</c> when git is not usable, which is a fact about the machine, not the mod.</summary>
    /// <param name="git">The shared git runner.</param>
    /// <exception cref="CliEnvironmentException">git is not on <c>PATH</c>.</exception>
    public static void RequireGit(GitRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);

        if (!git.IsAvailable())
        {
            throw new CliEnvironmentException(OperationErrorCode.GitNotFound, GitUnavailableException.UserMessage);
        }
    }
}
