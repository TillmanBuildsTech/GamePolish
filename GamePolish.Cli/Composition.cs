using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Install;
using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Mods;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Registry;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;
using GamePolish.Core.Update;

namespace GamePolish.Cli;

/// <summary>
/// The composition root: the one place the real implementations are constructed and handed to each other.
/// </summary>
/// <remarks>
/// <para>
/// Everything the harness drives is built here, once, and passed in - no command constructs a component and no
/// command reaches for a static. That is what makes the tool evidence rather than a second engine: the object
/// graph below is the graph the Desktop app will build in phase 3, so a bug this tool reproduces is a bug in the
/// engine and not in a parallel path.
/// </para>
/// <para>
/// The pipelines get the same collaborators the app gives them - one <see cref="GamePolishPaths"/>, one file
/// seam, one validator, one backup engine, one manifest - because a CLI that built a second
/// <c>ManifestStore</c> per command would be exercising a configuration nobody ships.
/// </para>
/// </remarks>
public sealed class Composition
{
    private Composition(
        GamePolishPaths paths,
        IFileOps files,
        IPathValidator pathValidator,
        IGamesRegistry games,
        IGameDescriptorLoader descriptors,
        IBackupEngine backups,
        IManifestStore manifest,
        ISourceAcquirer sources,
        IRegistryService registry,
        IInstallPipeline install,
        IUpdatePipeline update,
        IModStateService mods,
        IOperationLog log,
        GitRunner git)
    {
        Paths = paths;
        Files = files;
        PathValidator = pathValidator;
        Games = games;
        Descriptors = descriptors;
        Backups = backups;
        Manifest = manifest;
        Sources = sources;
        Registry = registry;
        Install = install;
        Update = update;
        Mods = mods;
        Log = log;
        Git = git;
    }

    /// <summary>The resolved data root and every path derived from it.</summary>
    public GamePolishPaths Paths { get; }

    /// <summary>The filesystem seam the pipelines mutate through.</summary>
    public IFileOps Files { get; }

    /// <summary>The validator every mod-supplied path passes through.</summary>
    public IPathValidator PathValidator { get; }

    /// <summary>Which games are tracked, and where each is installed.</summary>
    public IGamesRegistry Games { get; }

    /// <summary>The descriptor source: the verify file, the addon destination and the registry URL.</summary>
    public IGameDescriptorLoader Descriptors { get; }

    /// <summary>The snapshot engine.</summary>
    public IBackupEngine Backups { get; }

    /// <summary>The installed-mod record.</summary>
    public IManifestStore Manifest { get; }

    /// <summary>Git and ZIP staging.</summary>
    public ISourceAcquirer Sources { get; }

    /// <summary>The registry fetch and cache.</summary>
    public IRegistryService Registry { get; }

    /// <summary>The install pipeline, entered only through <see cref="Install"/>.</summary>
    public IInstallPipeline Install { get; }

    /// <summary>The update pipeline.</summary>
    public IUpdatePipeline Update { get; }

    /// <summary>The Library facade over uninstall, enable and disable.</summary>
    public IModStateService Mods { get; }

    /// <summary>The rolling file log under the data root.</summary>
    public IOperationLog Log { get; }

    /// <summary>The git runner, shared so its "is git present" probe is paid once.</summary>
    public GitRunner Git { get; }

    /// <summary>Builds the object graph over an already-resolved data root.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="logLevel">The <c>--log-level</c> value, or <see langword="null"/> to read the settings file.</param>
    /// <remarks>
    /// Constructing this writes nothing: a data root that does not exist yet stays absent until a command that
    /// mutates something creates it. That is what lets <c>doctor</c> and <c>list</c> run against a fresh root
    /// without leaving a trace.
    /// </remarks>
    public static Composition Create(GamePolishPaths paths, string? logLevel)
    {
        ArgumentNullException.ThrowIfNull(paths);

        FileOps files = new();
        PathValidator pathValidator = new();
        GameDescriptorLoader descriptors = new(DescriptorDirectories(paths));
        GamesRegistry games = new(paths, descriptors);
        BackupEngine backups = new(paths, files, pathValidator);
        ManifestStore manifest = new(paths);
        GitRunner git = new();
        SourceAcquirer sources = new(paths, pathValidator, files, git);
        RegistryService registry = new(paths);

        FileOperationLog log = logLevel is null
            ? FileOperationLog.FromSettings(paths)
            : new FileOperationLog(paths, null, logLevel);

        return new Composition(
            paths,
            files,
            pathValidator,
            games,
            descriptors,
            backups,
            manifest,
            sources,
            registry,
            new InstallPipeline(paths, games, descriptors, sources, backups, manifest, pathValidator, files, log),
            new UpdatePipeline(paths, games, descriptors, sources, backups, manifest, pathValidator, new GitVersionProbe(), files, log),
            new ModStateService(
                manifest,
                new UninstallPipeline(paths, games, descriptors, backups, manifest, pathValidator, files, log),
                new EnableDisablePipeline(paths, games, descriptors, backups, manifest, pathValidator, files, log)),
            log,
            git);
    }

    /// <summary>
    /// The directories searched for <c>&lt;id&gt;.yaml</c>, highest precedence first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three, and the order matters only for discovery, never for correctness: the loader refuses two descriptors
    /// claiming one id, so a duplicate is a loud error rather than a silent shadowing.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>&lt;executable&gt;/games</c> - the shipping location, which is where task <c>401</c>'s descriptor
    /// lands for a published build.
    /// </description></item>
    /// <item><description>
    /// The repository's own <c>games/</c> folder, found by walking up from the executable. This project exists to
    /// be run from a build tree (<c>dotnet run --project GamePolish.Cli</c>), where nothing has copied the
    /// descriptors next to the binary yet; without this the harness could only ever see user-supplied games.
    /// </description></item>
    /// <item><description>
    /// <c>&lt;data root&gt;/games</c> - a descriptor the user dropped into their data root, and the documented way
    /// for <c>405</c> to run against a fixture game.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static IReadOnlyList<string> DescriptorDirectories(GamePolishPaths paths)
    {
        List<string> directories = [];

        Add(Path.Combine(AppContext.BaseDirectory, GameDescriptorLoader.DescriptorDirectoryName));

        string? repository = FindRepositoryDescriptorDirectory();
        if (repository is not null)
        {
            Add(repository);
        }

        Add(Path.Combine(paths.Root, GameDescriptorLoader.DescriptorDirectoryName));

        return directories;

        // Deduplicated by full path: in a published tree the first two can be the same directory, and the loader
        // would then see every descriptor twice and refuse it as a duplicate id.
        void Add(string directory)
        {
            string full = Path.GetFullPath(directory);

            if (!directories.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(full);
            }
        }
    }

    /// <summary>
    /// Walks up from the executable looking for a <c>games</c> directory that actually holds a descriptor.
    /// </summary>
    /// <remarks>
    /// Bounded to eight levels: a build tree is at most <c>GamePolish.Cli/bin/Debug/net10.0</c>, so eight is
    /// generous, and an unbounded walk from an arbitrary install location is how a tool finds the wrong
    /// repository's descriptors. A directory with no <c>*.yaml</c> is not a hit, so the empty <c>games/</c> a
    /// future project might create does not shadow the real one.
    /// </remarks>
    private static string? FindRepositoryDescriptorDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        int depth = 0;

        while (current is not null && depth < 8)
        {
            string candidate = Path.Combine(current.FullName, GameDescriptorLoader.DescriptorDirectoryName);

            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.yaml").Any())
            {
                return candidate;
            }

            current = current.Parent;
            depth++;
        }

        return null;
    }
}
