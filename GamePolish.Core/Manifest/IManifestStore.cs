namespace GamePolish.Core.Manifest;

/// <summary>
/// The durable record of what Game Polish installed into a game.
/// </summary>
/// <remarks>
/// The query set below is the contract the Library, Discover and the Add Mod flow call; anything else
/// the UI wants is a filter over <see cref="List"/> in the view model. Ids are matched exactly, paths
/// through <c>PathComparer</c>.
/// </remarks>
public interface IManifestStore
{
    /// <summary>Every installed mod for a game, in file order.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    IReadOnlyList<InstalledMod> List(string gameId);

    /// <summary>One installed mod, or <see langword="null"/>.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The mod id.</param>
    InstalledMod? Find(string gameId, string modId);

    /// <summary>Whether this mod is installed.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The mod id.</param>
    bool IsInstalled(string gameId, string modId);

    /// <summary>The installed version of a mod, or <see langword="null"/> when it is not installed.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The mod id.</param>
    string? GetInstalledVersion(string gameId, string modId);

    /// <summary>Adds a mod, or replaces the entry with the same id.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="mod">The record to store. <c>UpdatedAt</c> comes from the caller.</param>
    void Upsert(string gameId, InstalledMod mod);

    /// <summary>Removes one entry. Does not touch files on disk.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The mod id.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    bool Remove(string gameId, string modId);

    /// <summary>Flips one mod's enabled flag.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The mod id.</param>
    /// <param name="enabled">The new state.</param>
    /// <exception cref="InvalidOperationException">The mod is not installed.</exception>
    void SetEnabled(string gameId, string modId, bool enabled);

    /// <summary>Every file a mod owns, relative to the addon root.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="modId">The mod id.</param>
    IReadOnlyList<InstalledFileEntry> GetInstalledFiles(string gameId, string modId);

    /// <summary>Which mods claim a path: the "would this install clobber something" question.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="relativePath">A path relative to the addon root.</param>
    IReadOnlyList<string> FindOwnersOfPath(string gameId, string relativePath);

    /// <summary>Which mods already own a folder: the folder-name conflict question.</summary>
    /// <param name="gameId">The game id, e.g. <c>octowow</c>.</param>
    /// <param name="folderName">A folder name under the addon root.</param>
    IReadOnlyList<string> FindFolderOwners(string gameId, string folderName);
}
