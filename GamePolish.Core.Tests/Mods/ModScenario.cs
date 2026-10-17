using System.Security.Cryptography;
using System.Text;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Install;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Mods;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Mods;

/// <summary>
/// One mod-state scenario: an isolated data root, a fake game installed outside it, the shipped OctoWoW folder
/// layout, a real backup engine and manifest over the real filesystem, and both pipelines over one recording seam.
/// </summary>
/// <remarks>
/// <para>
/// Real on purpose where the acceptance criteria are about bytes: the snapshot engine, the manifest store, the
/// swap and every filesystem call are production code. Only the two collaborators that need files on disk in a
/// shape this test controls - the games registry and the descriptor loader - are faked, exactly as in
/// <c>Install/InstallScenario</c>.
/// </para>
/// <para>
/// The seam is one <see cref="IFileOps"/> shared by the snapshot and the pipelines, because the ordering criteria
/// ("the snapshot directory exists before the first byte moves") are statements about one interleaved call log.
/// </para>
/// </remarks>
internal sealed class ModScenario
{
    public const string GameId = "octowow";

    public static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Builds the scenario over the shared temporary data root.</summary>
    /// <param name="fixture">The collection fixture: where the data root and the game folder live.</param>
    /// <param name="failMove">
    /// Injects a failure into <see cref="IFileOps.MoveDirectory"/>. Used for the cross-volume fallback and for
    /// criterion 9, where a move that cannot complete must leave the game folder untouched.
    /// </param>
    /// <param name="failCopy">Injects a failure into <see cref="IFileOps.CopyFile"/>.</param>
    /// <param name="manifest">
    /// A manifest store to use instead of the real one, for the case where the record cannot be written after the
    /// files are already gone.
    /// </param>
    public ModScenario(
        StorageFixture fixture,
        Func<string, string, Exception?>? failMove = null,
        Func<string, Exception?>? failCopy = null,
        IManifestStore? manifest = null)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        Paths = new GamePolishPaths(fixture.NewSubdirectory($"mods-{suffix}"));
        InstallRoot = fixture.NewInstallDirectory($"mods-{suffix}");
        AddonRoot = Path.Combine(InstallRoot, "Interface", "AddOns");
        Directory.CreateDirectory(AddonRoot);

        Clock = new FixedTimeProvider(Start);
        Recording = new RecordingFileOps { OnCopyFile = failCopy };
        Seam = failMove is null ? Recording : new MoveFailingFileOps(Recording, failMove);
        Validator = new PathValidator();
        Staging = new StagingArea(Paths, Validator, Seam);

        RealManifest = new ManifestStore(Paths);
        Manifest = manifest ?? RealManifest;
        Games = new FakeGamesRegistry();
        Descriptors = new FakeDescriptorLoader();

        Descriptors.Descriptors.Add(Descriptor());
        Games.Games.Add(new TrackedGame
        {
            Id = GameId,
            Name = "OctoWoW",
            InstallPath = InstallRoot,
            AddedAt = Start,
        });

        Backups = new BackupEngine(Paths, Seam, Validator, Clock);

        Uninstall = new UninstallPipeline(Paths, Games, Descriptors, Backups, Manifest, Validator, Seam, log: null, Clock);
        Toggle = new EnableDisablePipeline(Paths, Games, Descriptors, Backups, Manifest, Validator, Seam, log: null, Clock);
    }

    public GamePolishPaths Paths { get; }

    public string InstallRoot { get; }

    /// <summary>The OctoWoW addons folder under <see cref="InstallRoot"/>.</summary>
    public string AddonRoot { get; }

    public string BackupsRoot => Paths.BackupsDirectory(GameId);

    /// <summary>Where disabled addons are parked, <c>&lt;data root&gt;/octowow/staging/disabled</c>.</summary>
    public string DisabledRoot => Staging.DisabledRoot(GameId);

    public FixedTimeProvider Clock { get; }

    /// <summary>The seam every mutation goes through - the recording one, or a wrapper around it.</summary>
    public IFileOps Seam { get; }

    /// <summary>The recording double underneath <see cref="Seam"/>, so a test can read one honest call log.</summary>
    public RecordingFileOps Recording { get; }

    public PathValidator Validator { get; }

    public StagingArea Staging { get; }

    public ManifestStore RealManifest { get; }

    public IManifestStore Manifest { get; }

    public BackupEngine Backups { get; }

    public FakeGamesRegistry Games { get; }

    public FakeDescriptorLoader Descriptors { get; }

    public UninstallPipeline Uninstall { get; }

    public EnableDisablePipeline Toggle { get; }

    /// <summary>Every mutating call, in order.</summary>
    public List<string> Calls => Recording.Calls;

    public static GameDescriptor Descriptor(string destination = "Interface/AddOns") => new()
    {
        Id = GameId,
        Name = "OctoWoW",
        InstallDetection = new InstallDetection { ManualOnly = true, VerifyFile = "WoW.exe" },
        ModLayout = new ModLayout { Type = "addons_folder", Destination = destination },
        RegistryUrl = "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json",
    };

    /// <summary>The folder a mod occupies when it is enabled.</summary>
    public string GameFolder(string folderName) => Path.Combine(AddonRoot, folderName);

    /// <summary>The folder a mod is parked in when it is disabled.</summary>
    public string ParkedFolder(string folderName) => Path.Combine(DisabledRoot, folderName);

    /// <summary>Writes a file under the game's addons folder.</summary>
    public void WriteGameFile(string addonRelativePath, string contents)
    {
        string absolute = Path.Combine(AddonRoot, addonRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, contents);
    }

    /// <summary>
    /// Writes a mod's files into the game folder and records it, the way a completed install would have.
    /// </summary>
    /// <param name="folderName">The folder under the addons folder.</param>
    /// <param name="modId">The manifest id.</param>
    /// <param name="enabled">Whether the record says the files are in the game.</param>
    /// <param name="files">Addon-root-relative paths; defaults to a <c>.toc</c> plus a nested file.</param>
    public InstalledMod Seed(string folderName = "pfUI", string modId = "pfui", bool enabled = true, params string[] files)
    {
        string[] relative = files.Length > 0
            ? files
            : [$"{folderName}/{folderName}.toc", $"{folderName}/core.lua", $"{folderName}/lib/deep.lua"];

        List<InstalledFileEntry> entries = [];

        foreach (string path in relative)
        {
            // Unique content per path, so a comparison can tell two files apart and a length check is not the
            // only thing standing between a copy and a corrupt addon.
            string contents = $"-- {path}\nreturn {path.Length}\n";

            WriteGameFile(path, contents);
            entries.Add(new InstalledFileEntry(path, Encoding.UTF8.GetByteCount(contents)));
        }

        InstalledMod mod = new()
        {
            Id = modId,
            Name = "pfUI",
            SourceUrl = "https://github.com/brues-code/pfUI",
            SourceType = ModSourceType.Github,
            FolderName = folderName,
            Version = "3f2a1b4c5d6e7f8091a2b3c4d5e6f708192a3b4c",
            VersionKind = "sha",
            Enabled = enabled,
            InstalledAt = Start,
            UpdatedAt = Start,
            Files = entries,
        };

        Manifest.Upsert(GameId, mod);

        return mod;
    }

    /// <summary>The index of the first call that starts with <paramref name="prefix"/>, or -1.</summary>
    public int CallIndex(string prefix) =>
        Calls.FindIndex(call => call.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The index of the first call containing <paramref name="fragment"/>, or -1.</summary>
    public int CallIndexContaining(string fragment) =>
        Calls.FindIndex(call => call.Contains(fragment, StringComparison.Ordinal));

    /// <summary>How many snapshots were taken for this game, optionally filtered to one trigger.</summary>
    public int BackupCount(BackupTrigger? trigger = null) =>
        Backups.List(GameId).Count(entry => trigger is null || entry.Trigger == trigger);

    /// <summary>Every path a recorded call touched, for the "only the two roots" assertion.</summary>
    public IEnumerable<string> PathsTouched() =>
        Calls.SelectMany(static call => PathsIn(call));

    private static IEnumerable<string> PathsIn(string call)
    {
        int open = call.IndexOf('(');

        if (open < 0)
        {
            yield break;
        }

        string inside = call[(open + 1)..].TrimEnd(')');

        foreach (string part in inside.Split("=>", StringSplitOptions.TrimEntries))
        {
            string path = part.Replace(", recursive", string.Empty, StringComparison.Ordinal).Trim();

            if (Path.IsPathFullyQualified(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Every file under a directory as <c>relative|length|sha256</c>, ordinally sorted - the "identical file set"
    /// the acceptance criteria compare. Empty when the directory does not exist.
    /// </summary>
    public static IReadOnlyList<string> FileSet(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<string> files = [];

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');

            files.Add(
                $"{relative}|{new FileInfo(file).Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}");
        }

        files.Sort(StringComparer.Ordinal);

        return files;
    }
}

/// <summary>A tracked-game registry with no file behind it.</summary>
internal sealed class FakeGamesRegistry : IGamesRegistry
{
    public List<TrackedGame> Games { get; } = [];

    public IReadOnlyList<TrackedGame> List() => Games;

    public TrackedGame? Get(string gameId) =>
        Games.FirstOrDefault(game => string.Equals(game.Id, gameId, StringComparison.Ordinal));

    public TrackedGame Add(string gameId, string installPath) => throw new NotSupportedException();

    public TrackedGame SetInstallPath(string gameId, string installPath) => throw new NotSupportedException();

    public bool Remove(string gameId) => Games.RemoveAll(game => string.Equals(game.Id, gameId, StringComparison.Ordinal)) > 0;

    public IReadOnlyList<TrackedGame> ListUnresolved() => [];
}

/// <summary>A descriptor source a test can populate, so the real YAML loader is only used where it matters.</summary>
internal sealed class FakeDescriptorLoader : IGameDescriptorLoader
{
    public List<GameDescriptor> Descriptors { get; } = [];

    public IReadOnlyList<string> DescriptorDirectories => [];

    public GameDescriptor LoadFromFile(string path) => throw new NotSupportedException();

    public GameDescriptor? LoadById(string id) =>
        Descriptors.FirstOrDefault(descriptor => string.Equals(descriptor.Id, id, StringComparison.Ordinal));

    public IReadOnlyList<GameDescriptor> LoadAll() => Descriptors;
}

/// <summary>
/// The recording seam with a hook on <c>MoveDirectory</c>, which <see cref="RecordingFileOps"/> does not have.
/// </summary>
/// <remarks>
/// Every other call is forwarded, so a test still reads one honest call log. A failed rename is recorded with a
/// <c>failed</c> marker instead of the call it would have made, which is what lets a test assert that the move was
/// attempted and that nothing after it touched the game folder.
/// </remarks>
internal sealed class MoveFailingFileOps : IFileOps
{
    private readonly RecordingFileOps _inner;
    private readonly Func<string, string, Exception?> _failMove;

    public MoveFailingFileOps(RecordingFileOps inner, Func<string, string, Exception?> failMove)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(failMove);

        _inner = inner;
        _failMove = failMove;
    }

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void CopyFile(string source, string destination, bool overwrite) =>
        _inner.CopyFile(source, destination, overwrite);

    public void MoveDirectory(string source, string destination)
    {
        if (_failMove(source, destination) is { } failure)
        {
            _inner.Calls.Add($"MoveDirectory({source} => {destination}) failed");

            throw failure;
        }

        _inner.MoveDirectory(source, destination);
    }

    public void DeleteDirectory(string path, bool recursive) => _inner.DeleteDirectory(path, recursive);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public void WriteAllBytes(string path, byte[] contents) => _inner.WriteAllBytes(path, contents);

    public Stream CreateFile(string path) => _inner.CreateFile(path);

    public void MoveFile(string source, string destination, bool overwrite) =>
        _inner.MoveFile(source, destination, overwrite);
}

/// <summary>A manifest store that delegates everywhere except the one call the test wants to fail.</summary>
/// <remarks>
/// Used for the uninstall case the work order spells out: the files are already gone when the record cannot be
/// updated, and the result has to say exactly that rather than pretend nothing happened.
/// </remarks>
internal sealed class FailingManifestStore : IManifestStore
{
    private readonly IManifestStore _inner;

    public FailingManifestStore(IManifestStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    /// <summary>Returns an exception to fail that removal, or <see langword="null"/> to let it happen.</summary>
    public required Func<string, string, Exception?> FailRemove { get; init; }

    public IReadOnlyList<InstalledMod> List(string gameId) => _inner.List(gameId);

    public InstalledMod? Find(string gameId, string modId) => _inner.Find(gameId, modId);

    public bool IsInstalled(string gameId, string modId) => _inner.IsInstalled(gameId, modId);

    public string? GetInstalledVersion(string gameId, string modId) => _inner.GetInstalledVersion(gameId, modId);

    public void Upsert(string gameId, InstalledMod mod) => _inner.Upsert(gameId, mod);

    public bool Remove(string gameId, string modId) =>
        FailRemove(gameId, modId) is { } failure ? throw failure : _inner.Remove(gameId, modId);

    public void SetEnabled(string gameId, string modId, bool enabled) => _inner.SetEnabled(gameId, modId, enabled);

    public IReadOnlyList<InstalledFileEntry> GetInstalledFiles(string gameId, string modId) =>
        _inner.GetInstalledFiles(gameId, modId);

    public IReadOnlyList<string> FindOwnersOfPath(string gameId, string relativePath) =>
        _inner.FindOwnersOfPath(gameId, relativePath);

    public IReadOnlyList<string> FindFolderOwners(string gameId, string folderName) =>
        _inner.FindFolderOwners(gameId, folderName);
}
