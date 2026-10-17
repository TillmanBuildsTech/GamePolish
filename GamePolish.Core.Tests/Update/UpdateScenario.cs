using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Install;
using GamePolish.Core.Tests.Storage;
using GamePolish.Core.Update;

namespace GamePolish.Core.Tests.Update;

/// <summary>
/// One update scenario: an isolated data root, a fake game installed outside it, a tracked game with the
/// shipped OctoWoW layout, an installed mod recorded the way <c>208</c> would have recorded it, a staging
/// directory the fake acquirer hands out, and a real backup engine and manifest over the real filesystem.
/// </summary>
/// <remarks>
/// The collaborators that write into a game directory are real, because the acceptance criteria are about
/// bytes on disk and the order they were written in, not about call counts. Only the four components that
/// need a network, a process or a registry file are faked: the game registry, the descriptor loader, the
/// source acquirer and the version probe. The doubles are the ones <c>208</c> and <c>207</c> already own
/// (<see cref="FakeGamesRegistry"/>, <see cref="FakeDescriptorLoader"/>, <see cref="FakeSourceAcquirer"/>,
/// <see cref="RecordingFileOps"/>, <see cref="FixedTimeProvider"/>); this class only adds the probe and the
/// installed-mod seeding an update test needs.
/// </remarks>
internal sealed class UpdateScenario
{
    public const string GameId = "octowow";

    /// <summary>The commit the seeded install records.</summary>
    public const string OldVersion = "1111111111111111111111111111111111111111";

    /// <summary>The commit the staged update reports.</summary>
    public const string NewVersion = "2222222222222222222222222222222222222222";

    public static readonly DateTimeOffset Start = new(2026, 10, 15, 21, 42, 3, 123, TimeSpan.Zero);

    private int _operations;

    public UpdateScenario(StorageFixture fixture)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        Paths = new GamePolishPaths(fixture.NewSubdirectory($"update-{suffix}"));
        InstallRoot = fixture.NewInstallDirectory($"update-{suffix}");
        AddonRoot = Path.Combine(InstallRoot, "Interface", "AddOns");
        Directory.CreateDirectory(AddonRoot);

        Clock = new FixedTimeProvider(Start);
        Files = new RecordingFileOps();
        Validator = new PathValidator();
        Manifest = new ManifestStore(Paths);
        Games = new FakeGamesRegistry();
        Descriptors = new FakeDescriptorLoader();
        Sources = new FakeSourceAcquirer();
        Probe = new FakeVersionProbe();

        Descriptors.Descriptors.Add(InstallScenario.Descriptor());
        Games.Games.Add(new TrackedGame
        {
            Id = GameId,
            Name = "OctoWoW",
            InstallPath = InstallRoot,
            AddedAt = Start,
        });
    }

    public GamePolishPaths Paths { get; }

    public string InstallRoot { get; }

    /// <summary>The OctoWoW addons folder under <see cref="InstallRoot"/>.</summary>
    public string AddonRoot { get; }

    public string BackupsRoot => Paths.BackupsDirectory(GameId);

    public string ManifestFile => Paths.ManifestFile(GameId);

    public string StagingRoot => Paths.StagingDirectory(GameId);

    public FixedTimeProvider Clock { get; }

    public RecordingFileOps Files { get; }

    public PathValidator Validator { get; }

    public ManifestStore Manifest { get; }

    public FakeGamesRegistry Games { get; }

    public FakeDescriptorLoader Descriptors { get; }

    public FakeSourceAcquirer Sources { get; }

    public FakeVersionProbe Probe { get; }

    /// <summary>A pipeline over this scenario's real collaborators, with the given seams.</summary>
    public UpdatePipeline Pipeline(IFileOps? files = null, IVersionProbe? probe = null)
    {
        IFileOps seam = files ?? Files;

        return new UpdatePipeline(
            Paths,
            Games,
            Descriptors,
            Sources,
            new BackupEngine(Paths, seam, Validator, Clock),
            Manifest,
            Validator,
            probe ?? Probe,
            seam,
            log: null,
            Clock);
    }

    /// <summary>The addon folder as an absolute path, e.g. the installed copy of <c>pfUI</c>.</summary>
    public string Folder(string folderName = "pfUI") => Path.Combine(AddonRoot, folderName);

    /// <summary>The absolute path of one addon-relative file, e.g. <c>pfUI/pfUI.lua</c>.</summary>
    public string AddonFile(string addonRelativePath) =>
        Path.Combine(AddonRoot, addonRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every backup the engine has recorded for this game, newest first.</summary>
    public IReadOnlyList<BackupEvent> Backups() => new BackupEngine(Paths).List(GameId);

    /// <summary>The absolute path of one file inside a snapshot, given its game-root-relative path.</summary>
    public string SnapshotFile(string timestamp, string gameRelativePath) =>
        Path.Combine(BackupsRoot, timestamp, gameRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Writes a mod into the folder and records it in the manifest the way <c>208</c> does: addon-relative
    /// file paths carrying the folder name, sizes taken from the bytes on disk.
    /// </summary>
    /// <param name="folderName">The folder the mod owns.</param>
    /// <param name="version">The commit to record.</param>
    /// <param name="enabled">The flag the update must not change.</param>
    /// <param name="files">Files to write; defaults to a <c>.toc</c> and a <c>.lua</c>.</param>
    public InstalledMod Install(
        string folderName = "pfUI",
        string version = OldVersion,
        bool enabled = true,
        ModSourceType sourceType = ModSourceType.Github,
        params (string Path, string Content)[] files)
    {
        (string Path, string Content)[] contents = files.Length > 0
            ? files
            :
            [
                ($"{folderName}/{folderName}.toc", $"## Interface: 11200\n## Version: {version[..6]}\n"),
                ($"{folderName}/{folderName}.lua", $"-- {version[..6]}\nreturn 1\n"),
            ];

        List<InstalledFileEntry> entries = [];

        foreach ((string relative, string content) in contents)
        {
            string absolute = AddonFile(relative);

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, content);

            entries.Add(new InstalledFileEntry(relative, new FileInfo(absolute).Length));
        }

        InstalledMod mod = new()
        {
            Id = "pfui",
            Name = "pfUI",
            SourceUrl = sourceType == ModSourceType.Github
                ? "https://github.com/brues-code/pfUI"
                : "pfui-5.1.zip",
            SourceType = sourceType,
            FolderName = folderName,
            Version = version,
            VersionKind = sourceType == ModSourceType.Github ? "sha" : "zip",
            Enabled = enabled,
            InstalledAt = Start,
            UpdatedAt = Start,
            Files = entries,
        };

        Manifest.Upsert(GameId, mod);

        return mod;
    }

    /// <summary>
    /// Stages what the source serves now: a fresh tree under the data root, published to the fake acquirer so
    /// the pipeline's next <c>StageGitAsync</c> returns it.
    /// </summary>
    /// <param name="folderName">The folder the staged tree calls itself.</param>
    /// <param name="version">The commit the staged tree reports.</param>
    /// <param name="files">Addon-relative paths and their contents.</param>
    /// <param name="withGitDirectory">True to leave a <c>.git</c> folder in the staging tree, as a clone does.</param>
    public StagedSource StageUpdate(
        string folderName = "pfUI",
        string version = NewVersion,
        bool withGitDirectory = true,
        params (string Path, string Content)[] files)
    {
        (string Path, string Content)[] contents = files.Length > 0
            ? files
            :
            [
                ($"{folderName}/{folderName}.toc", $"## Interface: 11200\n## Version: {version[..6]}\n"),
                ($"{folderName}/{folderName}.lua", $"-- {version[..6]}\nreturn 2\n"),
                ($"{folderName}/new-in-this-release.lua", "-- added upstream\n"),
            ];

        string operationId = $"op-{++_operations}";
        string staging = Path.Combine(StagingRoot, operationId, folderName);
        Directory.CreateDirectory(staging);

        List<InstalledFileEntry> entries = [];

        foreach ((string relative, string content) in contents.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            string within = relative[(folderName.Length + 1)..];
            string absolute = Path.Combine(staging, within.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, content);

            entries.Add(new InstalledFileEntry(relative, new FileInfo(absolute).Length));
        }

        if (withGitDirectory)
        {
            string git = Path.Combine(staging, ".git");

            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main");
        }

        StagedSource staged = new()
        {
            GameId = GameId,
            OperationId = operationId,
            StagingDirectory = staging,
            FolderName = folderName,
            Version = version,
            VersionKind = "sha",
            SourceType = ModSourceType.Github,
            SourceUrl = "https://github.com/brues-code/pfUI",
            Files = entries,
            HasGitDirectory = withGitDirectory,
        };

        Sources.Next = staged;

        return staged;
    }
}

/// <summary>
/// A version probe a test drives by hand: it answers what it was told to, fails when told to, and records
/// every call so "the probe was never asked" is an assertion rather than a hope.
/// </summary>
internal sealed class FakeVersionProbe : IVersionProbe
{
    /// <summary>The answer the next call returns.</summary>
    public VersionInfo? Next { get; set; }

    /// <summary>Thrown by the next call, and every call after it.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>How many times the probe was invoked.</summary>
    public int Calls { get; private set; }

    /// <summary>Every request, in order.</summary>
    public List<(string SourceUrl, string? Branch)> Requests { get; } = [];

    public Task<VersionInfo> GetRemoteVersionAsync(string sourceUrl, string? branch, CancellationToken cancellationToken)
    {
        Calls++;
        Requests.Add((sourceUrl, branch));

        cancellationToken.ThrowIfCancellationRequested();

        if (FailWith is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(
            Next ?? throw new InvalidOperationException("No probe answer was configured for this test."));
    }
}

/// <summary>Cancels an operation from inside a progress report, which is how a user's Cancel lands mid-copy.</summary>
internal sealed class CancellingProgress : IProgress<OperationProgress>
{
    private readonly CancellationTokenSource _source;
    private readonly string _phase;
    private readonly int _after;

    public CancellingProgress(CancellationTokenSource source, string phase, int after)
    {
        _source = source;
        _phase = phase;
        _after = after;
    }

    /// <summary>Every report seen, so a test can assert the phase names.</summary>
    public List<OperationProgress> Reports { get; } = [];

    public void Report(OperationProgress value)
    {
        Reports.Add(value);

        if (string.Equals(value.Phase, _phase, StringComparison.Ordinal) && value.Completed >= _after)
        {
            _source.Cancel();
        }
    }
}
