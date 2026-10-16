using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Games;

/// <summary>
/// Decides whether a user-browsed folder really is a game's install directory.
/// </summary>
/// <remarks>
/// <para>
/// Three checks and no writes. The folder must be an absolute path that exists; it must be outside the
/// Game Polish data root (invariant #4 - a game installed inside the data root collapses the two roots
/// the product is allowed to touch, and would put backups inside the game); and, when the descriptor
/// names a <c>verify_file</c>, that file must be in the folder, because that is the only check the user
/// gets against picking the wrong directory.
/// </para>
/// <para>
/// Path comparison comes from <see cref="PathComparer"/>: task 204 created it, and it owns the decision
/// this class used to make for itself.
/// </para>
/// </remarks>
public sealed class InstallPathValidator
{
    private readonly GamePolishPaths _paths;

    /// <summary>Initializes the validator.</summary>
    /// <param name="paths">The resolved data root, for the two-roots check.</param>
    public InstallPathValidator(GamePolishPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    /// <summary>
    /// Expands a leading <c>~</c> to the user profile, so a path copied from a terminal or from the
    /// design notes works. A <c>~</c> that is not the whole first segment is a literal file name and is
    /// left alone.
    /// </summary>
    /// <param name="path">The user-supplied path.</param>
    public static string ExpandUserProfile(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return path;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.Length == 1)
        {
            return profile;
        }

        return path[1] is '/' or '\\' ? Path.Combine(profile, path[2..]) : path;
    }

    /// <summary>
    /// Expands <c>~</c>, requires an absolute path, requires the directory to exist, and requires the
    /// folder to be clear of the data root in both directions.
    /// </summary>
    /// <param name="gameId">The game id, carried on the exception.</param>
    /// <param name="installPath">The user-supplied folder.</param>
    /// <returns>The normalized absolute path, with no trailing separator.</returns>
    /// <exception cref="InstallPathInvalidException">The folder is not usable as an install directory.</exception>
    public string NormalizeInstallPath(string gameId, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        if (string.IsNullOrWhiteSpace(installPath))
        {
            throw new InstallPathInvalidException(
                gameId,
                string.Empty,
                "No install folder was supplied. Browse to the game's install folder.");
        }

        string expanded = ExpandUserProfile(installPath.Trim());

        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new InstallPathInvalidException(
                gameId,
                expanded,
                $"{expanded} is not an absolute path. Browse to the game's install folder instead of typing a relative one.");
        }

        string normalized = TrimTrailingSeparators(Path.GetFullPath(expanded));

        if (PathComparer.AreEqual(normalized, _paths.Root) || PathComparer.IsInside(normalized, _paths.Root))
        {
            throw new InstallPathInvalidException(
                gameId,
                normalized,
                $"{normalized} is inside the Game Polish data directory ({_paths.Root}). Game Polish keeps its own "
                + "files separate from the games it manages, so choose the game's own install folder.");
        }

        if (PathComparer.IsInside(_paths.Root, normalized))
        {
            throw new InstallPathInvalidException(
                gameId,
                normalized,
                $"{normalized} contains the Game Polish data directory ({_paths.Root}). Game Polish keeps its own "
                + "files separate from the games it manages, so choose the game's own install folder.");
        }

        if (!Directory.Exists(normalized))
        {
            throw new InstallPathInvalidException(
                gameId,
                normalized,
                $"{normalized} does not exist. Browse to the game's install folder.");
        }

        return normalized;
    }

    /// <summary>
    /// The acceptance check the user sees: does this folder look like this game? A descriptor with no
    /// <c>verify_file</c> is skipped - there is nothing to look for.
    /// </summary>
    /// <param name="gameId">The game id, carried on the exception.</param>
    /// <param name="descriptor">The game's descriptor: supplies the file name and the display name.</param>
    /// <param name="installPath">A path already normalized by <see cref="NormalizeInstallPath"/>.</param>
    /// <exception cref="InstallPathInvalidException">The verify file is not in the folder.</exception>
    public void VerifyInstallRoot(string gameId, GameDescriptor descriptor, string installPath)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        string? verifyFile = descriptor.InstallDetection.VerifyFile;

        if (string.IsNullOrWhiteSpace(verifyFile))
        {
            return;
        }

        if (!File.Exists(Path.Combine(installPath, verifyFile)))
        {
            throw new InstallPathInvalidException(
                gameId,
                installPath,
                $"{installPath} does not look like {descriptor.Name}: {verifyFile} was not found in the game root.");
        }
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
