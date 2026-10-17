using System.Security.Cryptography;
using System.Text;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Install;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Install;

/// <summary>
/// One install scenario: an isolated data root, a fake game installed outside it (the registry refuses an
/// install path inside the data root), a tracked game with the shipped OctoWoW layout, a staging directory
/// the fake acquirer hands out, and a real backup engine and manifest over the real filesystem.
/// </summary>
/// <remarks>
/// The collaborators that write into a game directory are real on purpose - the acceptance criteria are
/// about bytes on disk, not about call counts. Only the four components that need a network, a process or a
/// registry file are faked: the game registry, the descriptor loader, the source acquirer and (in tests that
/// need a failure the recording double cannot inject) the file operations.
/// </remarks>
internal sealed class InstallScenario
{
    public const string GameId = "octowow";

    public static readonly DateTimeOffset Start = new(2026, 10, 15, 21, 42, 3, 123, TimeSpan.Zero);

    private int _operations;

    public InstallScenario(StorageFixture fixture)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        Paths = new GamePolishPaths(fixture.NewSubdirectory($"install-{suffix}"));
        InstallRoot = fixture.NewInstallDirectory($"install-{suffix}");
        AddonRoot = Path.Combine(InstallRoot, "Interface", "AddOns");
        Directory.CreateDirectory(AddonRoot);

        Clock = new FixedTimeProvider(Start);
        Files = new RecordingFileOps();
        Validator = new PathValidator();
        Manifest = new ManifestStore(Paths);
        Games = new FakeGamesRegistry();
        Descriptors = new FakeDescriptorLoader();
        Sources = new FakeSourceAcquirer();

        Descriptors.Descriptors.Add(Descriptor());
        Track(GameId, "OctoWoW");
    }

    public GamePolishPaths Paths { get; }

    public string InstallRoot { get; }

    /// <summary>The OctoWoW addons folder under <see cref="InstallRoot"/>.</summary>
    public string AddonRoot { get; }

    public string BackupsRoot => Paths.BackupsDirectory(GameId);

    public string ManifestFile => Paths.ManifestFile(GameId);

    /// <summary>Where <see cref="FakeSourceAcquirer"/> builds its trees.</summary>
    public string StagingRoot => Paths.StagingDirectory(GameId);

    public FixedTimeProvider Clock { get; }

    public RecordingFileOps Files { get; }

    public PathValidator Validator { get; }

    public ManifestStore Manifest { get; }

    public FakeGamesRegistry Games { get; }

    public FakeDescriptorLoader Descriptors { get; }

    public FakeSourceAcquirer Sources { get; }

    public static GameDescriptor Descriptor(string destination = "Interface/AddOns", string id = GameId, string name = "OctoWoW") => new()
    {
        Id = id,
        Name = name,
        InstallDetection = new InstallDetection { ManualOnly = true, VerifyFile = "WoW.exe" },
        ModLayout = new ModLayout { Type = "addons_folder", Destination = destination },
        RegistryUrl = $"https://raw.githubusercontent.com/game-polish/registries/main/{id}.json",
    };

    public void Track(string gameId, string name)
    {
        Games.Games.Add(new TrackedGame
        {
            Id = gameId,
            Name = name,
            InstallPath = InstallRoot,
            AddedAt = Start,
        });
    }

    /// <summary>A pipeline over this scenario's real collaborations, with <paramref name="files"/> as the seam.</summary>
    public InstallPipeline Pipeline(IFileOps? files = null)
    {
        IFileOps seam = files ?? Files;

        return new InstallPipeline(
            Paths,
            Games,
            Descriptors,
            Sources,
            new BackupEngine(Paths, seam, Validator, Clock),
            Manifest,
            Validator,
            seam,
            log: null,
            Clock);
    }

    public InstallRequest Request(
        bool acceptOverwrites = false,
        string? modId = null,
        string? displayName = null,
        string gameId = GameId) => new()
        {
            GameId = gameId,
            SourceType = ModSourceType.Github,
            SourceUrl = "https://github.com/brues-code/pfUI",
            ModId = modId,
            DisplayName = displayName,
            AcceptOverwrites = acceptOverwrites,
        };

    /// <summary>Stages <paramref name="files"/> files, the first of which is the addon's <c>.toc</c>.</summary>
    public StagedSource Stage(string folderName = "pfUI", int files = 10, bool withGitDirectory = false)
    {
        List<string> relativePaths = [$"{folderName}/{folderName}.toc"];

        for (int index = 2; index <= files; index++)
        {
            relativePaths.Add($"{folderName}/file{index:D2}.lua");
        }

        return Stage(folderName, relativePaths, withGitDirectory);
    }

    /// <summary>Stages an exact file list, with content unique per path so a hash can tell them apart.</summary>
    public StagedSource Stage(string folderName, IEnumerable<string> relativePaths, bool withGitDirectory = false)
    {
        string operationId = $"op-{++_operations}";
        string staging = Path.Combine(Paths.StagingDirectory(GameId), operationId, folderName);
        Directory.CreateDirectory(staging);

        List<InstalledFileEntry> entries = [];

        foreach (string relative in relativePaths.Order(StringComparer.Ordinal))
        {
            string within = relative[(folderName.Length + 1)..];
            string absolute = Path.Combine(staging, within.Replace('/', Path.DirectorySeparatorChar));
            byte[] contents = Encoding.UTF8.GetBytes($"-- {relative}\nreturn 1\n");

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllBytes(absolute, contents);

            entries.Add(new InstalledFileEntry(relative, contents.Length));
        }

        if (withGitDirectory)
        {
            string git = Path.Combine(staging, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\n");
        }

        return Publish(new StagedSource
        {
            GameId = GameId,
            OperationId = operationId,
            StagingDirectory = staging,
            FolderName = folderName,
            Version = "3f2a1b4c5d6e7f8091a2b3c4d5e6f708192a3b4c",
            VersionKind = "sha",
            SourceType = ModSourceType.Github,
            SourceUrl = "https://github.com/brues-code/pfUI",
            Files = entries,
            HasGitDirectory = withGitDirectory,
        });
    }

    /// <summary>
    /// Publishes a staged source whose file list is taken at face value - the way a hostile repository would
    /// look after a bug in <c>207</c>. No content is written: the paths are supposed to be refused before any
    /// copy happens.
    /// </summary>
    public StagedSource StageRaw(string folderName, params string[] relativePaths)
    {
        string operationId = $"op-{++_operations}";
        string staging = Path.Combine(Paths.StagingDirectory(GameId), operationId, folderName);
        Directory.CreateDirectory(staging);

        return Publish(new StagedSource
        {
            GameId = GameId,
            OperationId = operationId,
            StagingDirectory = staging,
            FolderName = folderName,
            Version = "0000000000000000000000000000000000000000",
            VersionKind = "sha",
            SourceType = ModSourceType.Github,
            SourceUrl = "https://github.com/brues-code/pfUI",
            Files = [.. relativePaths.Order(StringComparer.Ordinal).Select(path => new InstalledFileEntry(path, 0))],
            HasGitDirectory = false,
        });
    }

    /// <summary>Writes a file under the addons folder, creating directories.</summary>
    public void WriteAddonFile(string addonRelativePath, string contents)
    {
        string absolute = Path.Combine(AddonRoot, addonRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, contents);
    }

    /// <summary>Records a mod the way a previous install would have, files included.</summary>
    public void SeedInstalledMod(string modId, string folderName, params string[] addonRelativePaths)
    {
        string[] files = addonRelativePaths.Length > 0
            ? addonRelativePaths
            : [folderName + "/" + folderName + ".toc"];

        foreach (string file in files)
        {
            if (!File.Exists(Path.Combine(AddonRoot, file.Replace('/', Path.DirectorySeparatorChar))))
            {
                WriteAddonFile(file, $"-- {modId} was here\n");
            }
        }

        Manifest.Upsert(GameId, new InstalledMod
        {
            Id = modId,
            Name = modId,
            SourceUrl = "https://github.com/example/" + modId,
            SourceType = ModSourceType.Github,
            FolderName = folderName,
            Version = "1111111111111111111111111111111111111111",
            VersionKind = "sha",
            Enabled = true,
            InstalledAt = Start,
            UpdatedAt = Start,
            Files = [.. files.Select(file => new InstalledFileEntry(file, 8))],
        });
    }

    private StagedSource Publish(StagedSource staged)
    {
        Sources.Next = staged;

        return staged;
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
/// Hands out a prepared <see cref="StagedSource"/> and deletes its operation directory on
/// <see cref="Cleanup"/>, the way <c>207</c> does.
/// </summary>
internal sealed class FakeSourceAcquirer : ISourceAcquirer
{
    public StagedSource? Next { get; set; }

    /// <summary>Thrown once by the next staging call, then cleared.</summary>
    public Exception? FailWith { get; set; }

    public int StageCalls { get; private set; }

    public List<StagedSource> Cleaned { get; } = [];

    public int CleanupCount => Cleaned.Count;

    public Task<StagedSource> StageGitAsync(SourceRequest request, CancellationToken cancellationToken)
    {
        StageCalls++;

        if (FailWith is { } failure)
        {
            FailWith = null;

            throw failure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Next ?? throw new InvalidOperationException("No staged source was configured."));
    }

    public Task<StagedSource> StageZipAsync(
        Stream zip,
        string zipFileName,
        SourceRequest request,
        CancellationToken cancellationToken) => StageGitAsync(request, cancellationToken);

    public void Cleanup(StagedSource staged)
    {
        Cleaned.Add(staged);

        string? operationDirectory = Path.GetDirectoryName(staged.StagingDirectory);

        if (operationDirectory is not null && Directory.Exists(operationDirectory))
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }

    public int SweepStaleStaging(string gameId, TimeSpan olderThan, string? excludeOperationId) => 0;
}

/// <summary>
/// <c>205</c>'s recording double with a hook on <c>MoveDirectory</c>, which it does not have.
/// </summary>
/// <remarks>
/// The rename-level rollback cases need to fail a move, and <c>RecordingFileOps</c> only exposes copy,
/// create-file and move-file hooks. Rather than edit a file the work order puts out of scope, this wraps the
/// same recording double and fails the move the predicate names; every other call is forwarded unchanged, so a
/// test can still read one honest call list.
/// </remarks>
internal sealed class MoveFailingFileOps : IFileOps
{
    private readonly RecordingFileOps _inner = new();

    /// <summary>Returns an exception to fail that rename, or <see langword="null"/> to let it happen.</summary>
    public required Func<string, string, Exception?> FailMove { get; init; }

    public List<string> Calls => _inner.Calls;

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void CopyFile(string source, string destination, bool overwrite) =>
        _inner.CopyFile(source, destination, overwrite);

    public void MoveDirectory(string source, string destination)
    {
        if (FailMove(source, destination) is { } failure)
        {
            Calls.Add($"MoveDirectory({source} => {destination}) failed");

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

/// <summary>Collects progress reports so a test can assert the phase and the per-file counts.</summary>
internal sealed class RecordingProgress : IProgress<OperationProgress>
{
    public List<OperationProgress> Reports { get; } = [];

    public void Report(OperationProgress value) => Reports.Add(value);
}

/// <summary>
/// A recursive fingerprint of a directory: its shape <b>including empty directories</b>, and the bytes of
/// every file.
/// </summary>
/// <remarks>
/// Directories are in the hash on purpose. The property the acceptance criteria care about - "the addon root
/// is byte-identical, and no <c>.game-polish-*</c> sibling remains" - is false if a rolled-back install
/// leaves behind an empty <c>.game-polish-new-*</c> directory, and a file-only hash would not notice.
/// </remarks>
internal static class TreeHash
{
    public static string Of(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "<missing>";
        }

        StringBuilder builder = new();

        foreach (string entry in Directory
                     .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');

            if (Directory.Exists(entry))
            {
                builder.Append("D ").Append(relative).Append('\n');

                continue;
            }

            builder.Append("F ").Append(relative).Append(' ')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
