using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Manifest;

/// <summary>
/// The manifest store: <c>&lt;data root&gt;/&lt;game_id&gt;/manifest.json</c>, read and written
/// atomically through <see cref="AtomicJson"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency, in-process:</b> the per-game lock is a <b>static</b>
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> rather than an instance field, and that is the point -
/// two store instances over the same data root (the app builds one per view, and tests build more) must
/// not each hold their own lock, or two read-modify-write cycles would lose an entry. Cross-process
/// locking is deliberately not implemented: one process per data root is the MVP assumption (see task
/// 203), and an advisory lock file would add a stale-lock failure mode that outlives a crash.
/// </para>
/// <para>
/// <b>Corruption:</b> an unreadable manifest is quarantined - renamed to
/// <c>manifest.json.corrupt-&lt;timestamp&gt;</c> in the same directory - and then reported as
/// <see cref="ManifestCorruptException"/>. It is never deleted and never silently reset: it is the user's
/// only record of what was installed.
/// </para>
/// <para>
/// The clock comes from <see cref="TimeProvider"/> for one reason only: the quarantine file name. No
/// timestamp in a stored record is written by this class - <see cref="InstalledMod.InstalledAt"/> and
/// <see cref="InstalledMod.UpdatedAt"/> come from the caller (task 209 needs the difference between "the
/// operation succeeded at T" and "we happened to write at T").
/// </para>
/// </remarks>
public sealed class ManifestStore : IManifestStore
{
    /// <summary>Timestamp format of a quarantined manifest's name.</summary>
    private const string QuarantineTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>One gate per game id, shared by every <see cref="ManifestStore"/> in the process.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> GameGates = new(StringComparer.Ordinal);

    /// <summary>Query parameter names that would put a credential into a stored URL.</summary>
    private static readonly string[] CredentialQueryNames = ["token", "key", "api_key", "access_token"];

    private readonly GamePolishPaths _paths;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the store over a data root, with the system clock.</summary>
    /// <param name="paths">The resolved data root.</param>
    public ManifestStore(GamePolishPaths paths)
        : this(paths, TimeProvider.System)
    {
    }

    /// <summary>Initializes the store over a data root.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="timeProvider">Clock used for the quarantine file name (and nothing else).</param>
    public ManifestStore(GamePolishPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public IReadOnlyList<InstalledMod> List(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        return Read(gameId).Mods;
    }

    /// <inheritdoc/>
    public InstalledMod? Find(string gameId, string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        return Read(gameId).Mods.FirstOrDefault(mod => IsSameId(mod.Id, modId));
    }

    /// <inheritdoc/>
    public bool IsInstalled(string gameId, string modId) => Find(gameId, modId) is not null;

    /// <inheritdoc/>
    public string? GetInstalledVersion(string gameId, string modId) => Find(gameId, modId)?.Version;

    /// <inheritdoc/>
    public void Upsert(string gameId, InstalledMod mod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentException.ThrowIfNullOrWhiteSpace(mod.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(mod.FolderName);

        // Defence in depth for task 504 (the PAT editor): a credential must never reach the manifest, and
        // this is the last place before it would be written.
        EnsureNoEmbeddedCredential(mod.SourceUrl, mod.Id);

        InstalledMod stored = mod with { Files = NormaliseFiles(mod.Id, mod.Files) };

        SemaphoreSlim gate = Acquire(gameId);
        try
        {
            ManifestFile manifest = Read(gameId);
            int existing = manifest.Mods.FindIndex(candidate => IsSameId(candidate.Id, mod.Id));

            if (existing >= 0)
            {
                manifest.Mods[existing] = stored;
            }
            else
            {
                manifest.Mods.Add(stored);
            }

            Write(gameId, manifest);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public bool Remove(string gameId, string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        SemaphoreSlim gate = Acquire(gameId);
        try
        {
            ManifestFile manifest = Read(gameId);

            if (manifest.Mods.All(mod => !IsSameId(mod.Id, modId)))
            {
                return false;
            }

            manifest.Mods.RemoveAll(mod => IsSameId(mod.Id, modId));
            Write(gameId, manifest);

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public void SetEnabled(string gameId, string modId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        SemaphoreSlim gate = Acquire(gameId);
        try
        {
            ManifestFile manifest = Read(gameId);
            int index = manifest.Mods.FindIndex(mod => IsSameId(mod.Id, modId));

            if (index < 0)
            {
                // Better a loud failure than a checkbox that silently does nothing: a row that is not in the
                // manifest is either already uninstalled or a bug, and either way the UI needs to know.
                throw new InvalidOperationException(
                    $"'{modId}' is not installed for '{gameId}', so its enabled state cannot be changed.");
            }

            // UpdatedAt belongs to the caller, so flipping the flag preserves the stored timestamp: this is
            // a state change of the mod's files, not a new version.
            manifest.Mods[index] = manifest.Mods[index] with { Enabled = enabled };
            Write(gameId, manifest);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<InstalledFileEntry> GetInstalledFiles(string gameId, string modId) =>
        Find(gameId, modId)?.Files ?? [];

    /// <inheritdoc/>
    public IReadOnlyList<string> FindOwnersOfPath(string gameId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string portable = relativePath.Replace('\\', '/');

        return
        [
            .. Read(gameId).Mods
                .Where(mod => mod.Files.Any(file => string.Equals(file.RelativePath, portable, PathComparer.PathComparison)))
                .Select(mod => mod.Id),
        ];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> FindFolderOwners(string gameId, string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        return
        [
            .. Read(gameId).Mods
                .Where(mod => string.Equals(mod.FolderName, folderName, PathComparer.PathComparison))
                .Select(mod => mod.Id),
        ];
    }

    /// <summary>Ids are matched exactly, like every other id in the product.</summary>
    private static bool IsSameId(string first, string second) => string.Equals(first, second, StringComparison.Ordinal);

    /// <summary>Takes the game's gate, which every store instance in the process shares.</summary>
    private static SemaphoreSlim Acquire(string gameId)
    {
        SemaphoreSlim gate = GameGates.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();

        return gate;
    }

    /// <summary>
    /// Returns the file list with portable separators and every path proven to be relative to the addon
    /// root.
    /// </summary>
    private static IReadOnlyList<InstalledFileEntry> NormaliseFiles(string modId, IReadOnlyList<InstalledFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<InstalledFileEntry> normalised = [];

        foreach (InstalledFileEntry? file in files)
        {
            if (file is null)
            {
                throw new ArgumentException($"'{modId}' lists an empty file entry.", nameof(files));
            }

            normalised.Add(file with { RelativePath = ToPortableRelativePath(modId, file.RelativePath) });
        }

        return normalised;
    }

    /// <summary>
    /// A manifest records where files were placed <i>inside</i> the game, so only relative paths are
    /// storable: an absolute path would break the moment the user moves the install (which
    /// <c>203.SetInstallPath</c> explicitly supports).
    /// </summary>
    private static string ToPortableRelativePath(string modId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException($"'{modId}' lists a file entry with no path.", nameof(relativePath));
        }

        string portable = relativePath.Replace('\\', '/');

        bool rooted = portable.StartsWith('/')
            || (portable.Length >= 2 && char.IsAsciiLetter(portable[0]) && portable[1] == ':');

        if (rooted || portable.Split('/').Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                $"'{relativePath}' is not a path relative to the addon folder, so it cannot be recorded: "
                + "a manifest stores where files were placed inside the game.",
                nameof(relativePath));
        }

        return portable;
    }

    /// <summary>Rejects a source URL that would write a credential into a plain-text file.</summary>
    private static void EnsureNoEmbeddedCredential(string? sourceUrl, string modId)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            // A ZIP source is stored as its file name, which is not a URL and cannot carry credentials.
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string user = uri.UserInfo.Split(':')[0];

            throw new ArgumentException(
                $"{modId}'s source '{sourceUrl}' carries an embedded credential ('{user}@'), which would be "
                + "written to the manifest as plain text. Use the plain https://host/path form.",
                nameof(sourceUrl));
        }

        foreach (string name in CredentialQueryNames)
        {
            if (uri.Query.Contains(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"{modId}'s source '{sourceUrl}' carries a '{name}=' query parameter, which would be "
                    + "written to the manifest as plain text. Use the plain https://host/path form.",
                    nameof(sourceUrl));
            }
        }
    }

    private ManifestFile Read(string gameId)
    {
        string manifestFile = _paths.ManifestFile(gameId);
        ManifestFile? manifest;

        try
        {
            manifest = AtomicJson.Read<ManifestFile>(manifestFile);
        }
        catch (JsonException exception)
        {
            throw Quarantine(gameId, exception);
        }

        if (manifest is null)
        {
            // Missing file: an empty manifest, created on the first write.
            return new ManifestFile();
        }

        if (manifest.Mods.Any(mod => !IsWellShaped(mod)))
        {
            // Parsed, but not usable: treat it exactly like a parse failure rather than trusting half a
            // record. (System.Text.Json already rejects a missing required member; this covers the rest.)
            throw Quarantine(gameId, null);
        }

        return manifest;
    }

    private void Write(string gameId, ManifestFile manifest)
    {
        _paths.EnsureGameDirectory(gameId);

        AtomicJson.Write(_paths.ManifestFile(gameId), manifest);
    }

    /// <summary>Moves an unreadable manifest aside and returns the exception to throw.</summary>
    private ManifestCorruptException Quarantine(string gameId, Exception? innerException)
    {
        string manifestFile = _paths.ManifestFile(gameId);
        string quarantined = NextQuarantineName(manifestFile);

        File.Move(manifestFile, quarantined);

        return new ManifestCorruptException(gameId, manifestFile, quarantined, innerException);
    }

    /// <summary>
    /// Builds the quarantine file name, adding a suffix if two corruptions land in the same millisecond so
    /// the second one cannot overwrite the first.
    /// </summary>
    private string NextQuarantineName(string manifestFile)
    {
        string baseName = $"{manifestFile}.corrupt-{_timeProvider.GetUtcNow().UtcDateTime.ToString(QuarantineTimestampFormat, CultureInfo.InvariantCulture)}";
        string candidate = baseName;

        for (int suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = $"{baseName}-{suffix}";
        }

        return candidate;
    }

    /// <summary>Shape check for a stored entry: everything the UI reads must be present.</summary>
    private static bool IsWellShaped(InstalledMod? mod) =>
        mod is not null
        && !string.IsNullOrWhiteSpace(mod.Id)
        && !string.IsNullOrWhiteSpace(mod.FolderName)
        && mod.Files is not null
        && mod.Files.All(file => file is not null && !string.IsNullOrWhiteSpace(file.RelativePath));
}
