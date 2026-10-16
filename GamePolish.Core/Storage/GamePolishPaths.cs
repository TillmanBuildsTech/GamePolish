namespace GamePolish.Core.Storage;

/// <summary>
/// The single resolution point for the Game Polish data root and for every file the app owns
/// underneath it (invariant #4: the only two roots this product touches are a game's install
/// directory and this one).
/// </summary>
/// <remarks>
/// <para>
/// Resolution precedence, highest first: an explicit constructor argument (what the CLI's
/// <c>--data-root</c> flag passes), then the <see cref="DataRootEnvironmentVariable"/> environment
/// variable (how tests and portable runs redirect the engine), then
/// <c>&lt;user profile&gt;/.game-polish</c>.
/// </para>
/// <para>
/// The root is validated at construction: it must be an absolute path, it must not be a file, and
/// it must not be a volume root (<c>C:\</c>, <c>/</c>). A relative or unqualified root throws
/// rather than being silently resolved against the process's working directory - a data root that
/// moves with the shell's cwd is exactly the kind of surprise this class exists to prevent.
/// </para>
/// <para>
/// Not checked here: whether the root overlaps a tracked game's install directory. That needs the
/// install paths recorded in <c>games.json</c>, which task <c>203</c> owns; its
/// <c>InstallPathValidator</c> holds the overlap check for both directions (a game installed inside
/// the data root, and a data root inside a game's install).
/// </para>
/// </remarks>
public sealed class GamePolishPaths
{
    /// <summary>Environment variable that overrides the default data root.</summary>
    public const string DataRootEnvironmentVariable = "GAMEPOLISH_DATA_ROOT";

    /// <summary>Directory name of the default data root, under the user profile.</summary>
    public const string DefaultDirectoryName = ".game-polish";

    /// <summary>Initializes paths rooted at <paramref name="root"/>.</summary>
    /// <param name="root">An absolute directory path. Trailing separators are removed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> is null, blank, not fully qualified, a file, or a volume root.
    /// </exception>
    public GamePolishPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException(
                $"The Game Polish data root must be an absolute path, but '{root}' is not.",
                nameof(root));
        }

        string fullPath = Path.GetFullPath(root);
        string? volumeRoot = Path.GetPathRoot(fullPath);
        if (volumeRoot is not null && TrimTrailingSeparators(volumeRoot) == TrimTrailingSeparators(fullPath))
        {
            throw new ArgumentException(
                $"The Game Polish data root must be a directory inside a volume, not the volume root itself; '{root}' is a drive root.",
                nameof(root));
        }

        if (File.Exists(fullPath))
        {
            throw new ArgumentException(
                $"The Game Polish data root must be a directory, but '{fullPath}' is an existing file.",
                nameof(root));
        }

        Root = TrimTrailingSeparators(fullPath);
    }

    /// <summary>
    /// Resolves the data root by precedence: <paramref name="overrideRoot"/>, then
    /// <see cref="DataRootEnvironmentVariable"/>, then <c>&lt;user profile&gt;/.game-polish</c>.
    /// </summary>
    /// <param name="overrideRoot">An explicit root, or <see langword="null"/> to fall through.</param>
    public static GamePolishPaths Resolve(string? overrideRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new GamePolishPaths(overrideRoot);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new GamePolishPaths(fromEnvironment);
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new GamePolishPaths(Path.Combine(profile, DefaultDirectoryName));
    }

    /// <summary>The absolute data root, with no trailing directory separator.</summary>
    public string Root { get; }

    /// <summary>The tracked-games registry, <c>&lt;Root&gt;/games.json</c>.</summary>
    public string GamesFile => Path.Combine(Root, "games.json");

    /// <summary>Non-sensitive app preferences, <c>&lt;Root&gt;/app-settings.json</c>.</summary>
    public string AppSettingsFile => Path.Combine(Root, "app-settings.json");

    /// <summary>Directory for the rolling file log, <c>&lt;Root&gt;/logs</c>.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>The per-game directory, <c>&lt;Root&gt;/&lt;game_id&gt;</c>. Not created.</summary>
    /// <param name="gameId">A single directory name, e.g. <c>octowow</c>.</param>
    public string GameDirectory(string gameId) => Path.Combine(Root, ValidateGameId(gameId));

    /// <summary>The installed-mod manifest, <c>&lt;game&gt;/manifest.json</c>. Not created.</summary>
    /// <param name="gameId">A single directory name, e.g. <c>octowow</c>.</param>
    public string ManifestFile(string gameId) => Path.Combine(GameDirectory(gameId), "manifest.json");

    /// <summary>The cached mod registry, <c>&lt;game&gt;/registry-cache.json</c>. Not created.</summary>
    /// <param name="gameId">A single directory name, e.g. <c>octowow</c>.</param>
    public string RegistryCacheFile(string gameId) => Path.Combine(GameDirectory(gameId), "registry-cache.json");

    /// <summary>Backup snapshots for a game, <c>&lt;game&gt;/backups</c>. Not created.</summary>
    /// <param name="gameId">A single directory name, e.g. <c>octowow</c>.</param>
    public string BackupsDirectory(string gameId) => Path.Combine(GameDirectory(gameId), "backups");

    /// <summary>Staging area for downloads and extractions, <c>&lt;game&gt;/staging</c>. Not created.</summary>
    /// <param name="gameId">A single directory name, e.g. <c>octowow</c>.</param>
    public string StagingDirectory(string gameId) => Path.Combine(GameDirectory(gameId), "staging");

    /// <summary>
    /// Creates the data root when it does not exist. Idempotent.
    /// </summary>
    /// <remarks>
    /// Never call this from a read-only operation: a <c>list</c> that silently creates directories
    /// is a surprise. Callers create on write.
    /// </remarks>
    public void EnsureCreated() => Directory.CreateDirectory(Root);

    /// <summary>
    /// Creates and returns <c>&lt;Root&gt;/&lt;game_id&gt;</c> when it does not exist. Idempotent.
    /// </summary>
    /// <param name="gameId">A single directory name, e.g. <c>octowow</c>.</param>
    public string EnsureGameDirectory(string gameId)
    {
        string directory = GameDirectory(gameId);

        Directory.CreateDirectory(directory);

        return directory;
    }

    /// <inheritdoc/>
    public override string ToString() => Root;

    /// <summary>
    /// Rejects anything that is not a single directory name: an id with a separator would escape the
    /// data root, and a rooted id would ignore it entirely.
    /// </summary>
    private static string ValidateGameId(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        bool isSingleName = gameId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
            && gameId is not "." and not ".."
            && !Path.IsPathRooted(gameId);

        if (!isSingleName)
        {
            throw new ArgumentException(
                $"'{gameId}' is not a usable game id: it must be a single directory name with no separators.",
                nameof(gameId));
        }

        return gameId;
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
