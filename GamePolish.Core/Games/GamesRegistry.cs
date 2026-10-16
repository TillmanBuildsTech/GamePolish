using System.Text.Json;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Games;

/// <summary>A game the user tracks, and where it is installed.</summary>
public sealed record TrackedGame
{
    /// <summary>Matches <see cref="GameDescriptor.Id"/>, e.g. <c>octowow</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Snapshot of <see cref="GameDescriptor.Name"/> at add time (see <see cref="IGamesRegistry.Add"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Absolute install directory, normalised, with no trailing separator.</summary>
    public required string InstallPath { get; init; }

    /// <summary>When the game was added, in UTC.</summary>
    public required DateTimeOffset AddedAt { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Id} at {InstallPath}";
}

/// <summary>The contents of <c>&lt;data root&gt;/games.json</c>, serialized by <see cref="AtomicJson"/>.</summary>
public sealed record GamesFile
{
    /// <summary>Schema version of the file, written as <c>schema_version</c>.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Tracked games, in the order they were added.</summary>
    public List<TrackedGame> Games { get; init; } = [];
}

/// <summary>The record of which games are tracked and where each one is installed.</summary>
public interface IGamesRegistry
{
    /// <summary>Every tracked game, in file order.</summary>
    IReadOnlyList<TrackedGame> List();

    /// <summary>The tracked game with this id, or <see langword="null"/>. Ids are matched exactly.</summary>
    TrackedGame? Get(string gameId);

    /// <summary>Validates the folder, verifies it is this game, and records it.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="installPath">The user-browsed install folder; a leading <c>~</c> is expanded.</param>
    /// <exception cref="InstallPathInvalidException">The folder is unusable for this game.</exception>
    /// <exception cref="GameAlreadyTrackedException">The game is already tracked.</exception>
    TrackedGame Add(string gameId, string installPath);

    /// <summary>Re-validates a moved install and updates the entry, keeping the original add time.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="installPath">The new install folder.</param>
    /// <exception cref="InstallPathInvalidException">The game is not tracked, or the new folder is unusable.</exception>
    TrackedGame SetInstallPath(string gameId, string installPath);

    /// <summary>Untracks a game. Removes the entry only - no mods, manifests or backups are deleted.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    bool Remove(string gameId);

    /// <summary>Tracked games whose folder or verify file has gone missing since they were added.</summary>
    /// <remarks>Reporting only: this never mutates the file. Only an explicit set or remove changes state.</remarks>
    IReadOnlyList<TrackedGame> ListUnresolved();
}

/// <summary>
/// The <c>games.json</c> store: the app's record of which games the user tracks and where each one is
/// installed.
/// </summary>
/// <remarks>
/// <para>
/// Every path in an entry is validated before it is written (<see cref="InstallPathValidator"/>), and
/// a folder is never written to: this class touches the data root only, which is what invariant #4
/// asks of it.
/// </para>
/// <para>
/// A malformed file is an error, not a reset: it records where the user's games live, so
/// <see cref="InvalidDataException"/> names the file and leaves it exactly as it was found for the
/// user to fix.
/// </para>
/// <para>
/// Concurrency: a <see cref="SemaphoreSlim"/> serialises the read-modify-write of add, set and remove
/// so two concurrent adds cannot lose one entry. Cross-process safety is out of scope for the MVP -
/// two running copies of Game Polish are not expected to share a data root.
/// </para>
/// </remarks>
public sealed class GamesRegistry : IGamesRegistry
{
    private readonly GamePolishPaths _paths;
    private readonly IGameDescriptorLoader _descriptors;
    private readonly InstallPathValidator _installPaths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes the registry.</summary>
    /// <param name="paths">The resolved data root, which holds <c>games.json</c>.</param>
    /// <param name="descriptors">Descriptor source: the verify file name and the display name come from it.</param>
    public GamesRegistry(GamePolishPaths paths, IGameDescriptorLoader descriptors)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(descriptors);

        _paths = paths;
        _descriptors = descriptors;
        _installPaths = new InstallPathValidator(paths);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrackedGame> List() => Read().Games;

    /// <inheritdoc/>
    public TrackedGame? Get(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        return Read().Games.FirstOrDefault(game => Is(game.Id, gameId));
    }

    /// <inheritdoc/>
    public TrackedGame Add(string gameId, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        _gate.Wait();
        try
        {
            GamesFile file = Read();

            GameDescriptor? descriptor = _descriptors.LoadById(gameId);
            if (descriptor is null)
            {
                throw new InstallPathInvalidException(
                    gameId,
                    installPath,
                    $"'{gameId}' is not a supported game: no descriptor named {gameId}.yaml was found.");
            }

            string normalized = _installPaths.NormalizeInstallPath(gameId, installPath);

            TrackedGame? trackedElsewhere = file.Games.FirstOrDefault(
                game => !Is(game.Id, gameId) && InstallPathValidator.AreSamePath(game.InstallPath, normalized));

            if (trackedElsewhere is not null)
            {
                throw new InstallPathInvalidException(
                    gameId,
                    normalized,
                    $"{trackedElsewhere.Name} is already tracked at {trackedElsewhere.InstallPath}. Two games cannot share one install folder.");
            }

            _installPaths.VerifyInstallRoot(gameId, descriptor, normalized);

            TrackedGame? existing = file.Games.FirstOrDefault(game => Is(game.Id, gameId));
            if (existing is not null)
            {
                throw new GameAlreadyTrackedException(gameId, existing.Name, existing.InstallPath, normalized);
            }

            TrackedGame added = new()
            {
                Id = descriptor.Id,
                Name = descriptor.Name,
                InstallPath = normalized,
                AddedAt = DateTimeOffset.UtcNow,
            };

            Write(file with { Games = [.. file.Games, added] });

            return added;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public TrackedGame SetInstallPath(string gameId, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        _gate.Wait();
        try
        {
            GamesFile file = Read();

            TrackedGame existing = file.Games.FirstOrDefault(game => Is(game.Id, gameId))
                ?? throw new InstallPathInvalidException(
                    gameId,
                    installPath,
                    $"'{gameId}' is not tracked yet, so there is no install path to change. Add the game first.");

            GameDescriptor? descriptor = _descriptors.LoadById(gameId);
            if (descriptor is null)
            {
                throw new InstallPathInvalidException(
                    gameId,
                    installPath,
                    $"'{gameId}' is not a supported game: no descriptor named {gameId}.yaml was found.");
            }

            string normalized = _installPaths.NormalizeInstallPath(gameId, installPath);

            TrackedGame? trackedElsewhere = file.Games.FirstOrDefault(
                game => !Is(game.Id, gameId) && InstallPathValidator.AreSamePath(game.InstallPath, normalized));

            if (trackedElsewhere is not null)
            {
                throw new InstallPathInvalidException(
                    gameId,
                    normalized,
                    $"{trackedElsewhere.Name} is already tracked at {trackedElsewhere.InstallPath}. Two games cannot share one install folder.");
            }

            _installPaths.VerifyInstallRoot(gameId, descriptor, normalized);

            TrackedGame updated = existing with { InstallPath = normalized };

            Write(file with
            {
                Games = [.. file.Games.Select(game => Is(game.Id, gameId) ? updated : game)],
            });

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public bool Remove(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        _gate.Wait();
        try
        {
            GamesFile file = Read();

            if (file.Games.All(game => !Is(game.Id, gameId)))
            {
                return false;
            }

            Write(file with { Games = [.. file.Games.Where(game => !Is(game.Id, gameId))] });

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrackedGame> ListUnresolved()
    {
        List<TrackedGame> unresolved = [];

        foreach (TrackedGame game in Read().Games)
        {
            if (!Directory.Exists(game.InstallPath))
            {
                unresolved.Add(game);

                continue;
            }

            string? verifyFile = _descriptors.LoadById(game.Id)?.InstallDetection.VerifyFile;

            if (!string.IsNullOrWhiteSpace(verifyFile) && !File.Exists(Path.Combine(game.InstallPath, verifyFile)))
            {
                unresolved.Add(game);
            }
        }

        return unresolved;
    }

    /// <summary>
    /// Ids are matched with <see cref="StringComparison.Ordinal"/>: descriptor validation already
    /// constrains them to lowercase, so an id that differs only in case is a different, unknown game
    /// rather than the same one.
    /// </summary>
    private static bool Is(string first, string second) => string.Equals(first, second, StringComparison.Ordinal);

    /// <summary>
    /// Reads <c>games.json</c>; a missing file is an empty registry, a malformed one is an error.
    /// </summary>
    private GamesFile Read()
    {
        try
        {
            return AtomicJson.Read<GamesFile>(_paths.GamesFile) ?? new GamesFile();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{_paths.GamesFile} is not valid JSON, and it records where your games are installed. "
                + "Fix or remove it, then try again - Game Polish will not overwrite it.",
                exception);
        }
    }

    private void Write(GamesFile file)
    {
        _paths.EnsureCreated();

        AtomicJson.Write(_paths.GamesFile, file);
    }
}
