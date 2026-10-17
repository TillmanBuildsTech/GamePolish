using GamePolish.Core.Games;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;

namespace GamePolish.Core.Mods;

/// <summary>
/// One installed mod plus the absolute directories the three mod-state operations need: the game's install
/// root, the folder mods live in, and the folder this mod occupies.
/// </summary>
/// <remarks>
/// <para>
/// Resolved once per operation, before anything is written, so uninstall, enable and disable cannot disagree
/// about which folder they are talking about - a resolution done twice is how a disable parks one folder and
/// a manifest write records another.
/// </para>
/// <para>
/// Every failure here is one the UI has a branch for: <c>ModNotInstalled</c>, <c>GameInstallPathMissing</c>
/// and <c>GameNotFound</c> already exist in the error vocabulary, so this type adds vocabulary rather than
/// inventing a fourth answer to "where is the game?".
/// </para>
/// </remarks>
internal sealed record ModTarget
{
    /// <summary>The game id, e.g. <c>octowow</c>.</summary>
    public required string GameId { get; init; }

    /// <summary>The game's install root, the <c>destinationRoot</c> every backup for this game uses.</summary>
    public required string InstallPath { get; init; }

    /// <summary>The folder mods live in, from the descriptor's <c>mod_layout.destination</c>.</summary>
    public required string AddonRoot { get; init; }

    /// <summary>The manifest record being changed.</summary>
    public required InstalledMod Mod { get; init; }

    /// <summary>Where this mod's files belong when it is enabled.</summary>
    public string GameFolder => Path.Combine(AddonRoot, Mod.FolderName);

    /// <summary>
    /// The game-root-relative paths a snapshot of this mod must cover: its folder, every file the manifest
    /// lists, and every file actually on disk in <paramref name="folderHoldingTheFiles"/>.
    /// </summary>
    /// <param name="folderHoldingTheFiles">
    /// Where the files are now: the game folder for an enabled mod, or the parked copy under the data root for
    /// a disabled one. It may not exist - a fresh backup legitimately has nothing to copy.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Compose from the addon root; never subtract it.</b> For a disabled mod the folder is under the data
    /// root, so <see cref="Path.GetRelativePath"/> against the install root would produce <c>../../..</c> and the
    /// validator would refuse it. The addon root's own path relative to the install root <i>is</i> the
    /// descriptor's destination, so composing from it yields the same game-relative shape for both cases -
    /// which is what makes one restore path put the files back where the game expects them, enabled or not.
    /// </para>
    /// <para>
    /// The manifest's list is not trusted on its own: a file the user added by hand inside the folder is
    /// exactly the file a delete destroys with no record of it, so the tree on disk is enumerated too.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> BackupPathsFor(string folderHoldingTheFiles)
    {
        string addonRootRelative = Portable(Path.GetRelativePath(InstallPath, AddonRoot));
        string folderRelative = addonRootRelative + "/" + Mod.FolderName;

        HashSet<string> paths = new(StringComparer.Ordinal) { folderRelative };

        foreach (InstalledFileEntry file in Mod.Files)
        {
            paths.Add(addonRootRelative + "/" + file.RelativePath);
        }

        if (Directory.Exists(folderHoldingTheFiles))
        {
            foreach (string file in Directory.EnumerateFiles(folderHoldingTheFiles, "*", SearchOption.AllDirectories))
            {
                paths.Add(folderRelative + "/" + Portable(Path.GetRelativePath(folderHoldingTheFiles, file)));
            }
        }

        List<string> sorted = [.. paths];
        sorted.Sort(StringComparer.Ordinal);

        return sorted;
    }

    private static string Portable(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Maps every path <see cref="BackupPathsFor"/> records to where its bytes live under a root that stands in for
    /// the addons folder - which is what <c>staging/disabled/&lt;FolderName&gt;</c> is.
    /// </summary>
    /// <param name="folderHoldingTheFiles">Where the files are now, exactly as for <see cref="BackupPathsFor"/>.</param>
    /// <remarks>
    /// <para>
    /// This is the other half of <c>205</c>'s optional <c>sourceRoot</c>. A parked addon's tree mirrors the
    /// <i>addons folder</i>, not the game root: <c>staging/disabled/pfUI/pfUI.toc</c> corresponds to
    /// <c>pfUI/pfUI.toc</c> under the game's addons folder. Handing the snapshot a source root alone would
    /// therefore look for <c>staging/disabled/&lt;addons folder&gt;/pfUI/pfUI.toc</c> and find nothing, so the
    /// recorded path is mapped to its addons-folder-relative key and the snapshot reads the parked bytes while
    /// writing the game-relative layout.
    /// </para>
    /// <para>
    /// Every recorded path is composed from the addons folder in <see cref="BackupPathsFor"/>, so every one of them
    /// has the prefix. A path that somehow does not is left out rather than guessed at: it is then looked for at its
    /// own relative path under the source root, which is the documented default.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> BackupSourceMapFor(string folderHoldingTheFiles)
    {
        string addonRootRelative = Portable(Path.GetRelativePath(InstallPath, AddonRoot));
        string prefix = addonRootRelative + "/";

        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (string recorded in BackupPathsFor(folderHoldingTheFiles))
        {
            if (recorded.StartsWith(prefix, StringComparison.Ordinal))
            {
                map[recorded] = recorded[prefix.Length..];
            }
        }

        return map;
    }

    /// <summary>
    /// Resolves the tracked game, its descriptor and the mod's own folder, or throws with the reason.
    /// </summary>
    /// <param name="games">Which games are tracked, and where each is installed.</param>
    /// <param name="descriptors">The descriptor source: the folder mods live in.</param>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="mod">The manifest record to change.</param>
    /// <exception cref="ModStateException">
    /// The game is no longer tracked, its install folder or the mods folder is gone, or no descriptor names
    /// this game. The code says which.
    /// </exception>
    /// <remarks>
    /// A missing folder is never created. Every one of the three operations is preceded by a snapshot, and
    /// creating a directory first would be the one write in the product that happens before its own backup
    /// (invariant #1). The message names the folder the descriptor asked for, so the user can see that the
    /// game's own layout is what is missing.
    /// </remarks>
    public static ModTarget Resolve(
        IGamesRegistry games,
        IGameDescriptorLoader descriptors,
        string gameId,
        InstalledMod mod)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(mod);

        TrackedGame tracked = games.Get(gameId)
            ?? throw new ModStateException(
                OperationErrorCode.GameInstallPathMissing,
                $"'{gameId}' is not tracked any more, so there is no game folder to change. Add the game again first.");

        if (!Directory.Exists(tracked.InstallPath))
        {
            throw new ModStateException(
                OperationErrorCode.GameInstallPathMissing,
                $"{tracked.Name} is recorded at {tracked.InstallPath}, but that folder is gone. Check the game's install folder, then try again.");
        }

        GameDescriptor descriptor = descriptors.LoadById(gameId)
            ?? throw new ModStateException(
                OperationErrorCode.GameNotFound,
                $"'{gameId}' is not a supported game: no descriptor named {gameId}.yaml was found.");

        string addonRoot = descriptor.AddonRoot(tracked.InstallPath);

        if (!Directory.Exists(addonRoot))
        {
            throw new ModStateException(
                OperationErrorCode.GameInstallPathMissing,
                $"{tracked.Name} has no '{descriptor.ModLayout.Destination}' folder under {tracked.InstallPath}, so there is nowhere for its mods to be.");
        }

        return new ModTarget
        {
            GameId = gameId,
            InstallPath = tracked.InstallPath,
            AddonRoot = addonRoot,
            Mod = mod,
        };
    }
}
