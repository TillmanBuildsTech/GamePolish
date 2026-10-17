using System.Globalization;
using System.Text.Json;

using GamePolish.Core.IO;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Backup;

/// <summary>
/// The backup engine: timestamped snapshots under <c>&lt;data root&gt;/&lt;game_id&gt;/backups/</c>, and
/// an index that records every one of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order inside <see cref="Create"/> is the contract, not an implementation detail.</b> The snapshot
/// directory is created first, then the files are copied, then the index is written. Two consequences
/// are deliberate: a test can assert the recorded order and fail if a copy ever precedes the snapshot
/// directory (invariant #1), and an index entry always implies a complete snapshot, because the index is
/// the last thing touched.
/// </para>
/// <para>
/// The copy list comes from a mod source, so it is validated through the path validator before anything
/// is created. A refused path throws <see cref="PathValidationException"/> and leaves no snapshot
/// directory and no index entry; a filesystem failure mid-copy removes the partial snapshot and throws
/// <see cref="BackupFailedException"/>. The two are distinct on purpose: one means "this archive is not
/// safe to install", the other means "the disk had a problem".
/// </para>
/// <para>
/// Time comes from <see cref="TimeProvider"/> so the collision suffix and the newest-first ordering are
/// testable without sleeping; production uses the system clock.
/// </para>
/// </remarks>
public sealed class BackupEngine : IBackupEngine
{
    /// <summary>File name of the index inside the per-game backups folder.</summary>
    public const string IndexFileName = "index.json";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly GamePolishPaths _paths;
    private readonly IFileOps _files;
    private readonly IPathValidator _pathValidator;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes the engine with the real filesystem and the system clock.</summary>
    /// <param name="paths">The resolved data root.</param>
    public BackupEngine(GamePolishPaths paths)
        : this(paths, new FileOps(), new PathValidator(), TimeProvider.System)
    {
    }

    /// <summary>Initializes the engine with injected collaborators.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="files">The mutating-call seam.</param>
    /// <param name="pathValidator">Validates every path that came from a mod source.</param>
    /// <param name="timeProvider">The clock, for the timestamp and the collision suffix.</param>
    public BackupEngine(
        GamePolishPaths paths,
        IFileOps files,
        IPathValidator pathValidator,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pathValidator);

        _paths = paths;
        _files = files;
        _pathValidator = pathValidator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b><paramref name="sourceRoot"/> and <paramref name="sourceRelativePaths"/>.</b> Both default to
    /// <see langword="null"/>, which is every caller that existed before them - the content is under
    /// <paramref name="destinationRoot"/> and the recorded path and the source path are the same. They exist for
    /// the one case where content belongs at a game-relative path but does not live under the game: a disabled
    /// addon parked in <c>staging/disabled/&lt;FolderName&gt;</c>. Without them the snapshot of such an addon
    /// records the right paths and copies nothing, and the backup a user is promised at uninstall time restores
    /// an empty folder.
    /// </remarks>
    public BackupEvent Create(
        string gameId,
        string destinationRoot,
        BackupTrigger trigger,
        string sourceName,
        string? sourceVersion,
        IEnumerable<string> relativePaths,
        string? sourceRoot = null,
        IReadOnlyDictionary<string, string>? sourceRelativePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(relativePaths);

        if (sourceRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot, nameof(sourceRoot));
        }

        _gate.Wait();
        try
        {
            return CreateCore(
                gameId,
                destinationRoot,
                trigger,
                sourceName,
                sourceVersion,
                relativePaths,
                sourceRoot,
                sourceRelativePaths);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<BackupEvent> List(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        _gate.Wait();
        try
        {
            BackupIndex index = ReadIndex(gameId);

            RemoveOrphanSnapshots(gameId, index);

            return [.. index.Events.OrderByDescending(entry => entry.Timestamp, StringComparer.Ordinal)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public BackupEvent? Get(string gameId, string timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        BackupIndex index = ReadIndex(gameId);

        return index.Events.FirstOrDefault(
            entry => string.Equals(entry.Timestamp, timestamp, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public void Restore(string gameId, string destinationRoot, string timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        _gate.Wait();
        try
        {
            BackupEvent restorePoint = Get(gameId, timestamp)
                ?? throw new BackupNotFoundException(gameId, timestamp);

            string snapshotDirectory = Path.Combine(_paths.BackupsDirectory(gameId), restorePoint.Timestamp);

            // Step 1: snapshot the current state of exactly the files this event names, so the restore is
            // itself reversible (backups.md § Restore Flow). This event also carries the record of any
            // file the restore is about to delete, because the file exists now.
            CreateCore(
                gameId,
                destinationRoot,
                BackupTrigger.Restore,
                sourceName: "pre-restore " + restorePoint.Timestamp,
                sourceVersion: null,
                relativePaths: restorePoint.Files.Select(file => file.RelativePath),
                sourceRoot: null,
                sourceRelativePaths: null);

            // Step 2: put the snapshotted files back.
            foreach (BackupFileEntry file in restorePoint.Files)
            {
                string resolved = _pathValidator.ResolveSafe(destinationRoot, file.RelativePath);
                string snapshotFile = Path.Combine(snapshotDirectory, ToPlatformPath(file.RelativePath));

                if (File.Exists(snapshotFile))
                {
                    _files.CopyFile(snapshotFile, resolved, overwrite: true);

                    continue;
                }

                // The snapshot does not hold this file, so it did not exist when the backup was taken - a
                // pre-install backup names the files the install is about to create. Restoring means
                // removing it again. The operation log (task 212) is where each deletion gets reported;
                // until then the pre-restore event above is the record that these files existed.
                if (File.Exists(resolved))
                {
                    _files.DeleteFile(resolved);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Delete(string gameId, string timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        _gate.Wait();
        try
        {
            BackupIndex index = ReadIndex(gameId);

            BackupEvent existing = index.Events.FirstOrDefault(
                entry => string.Equals(entry.Timestamp, timestamp, StringComparison.Ordinal))
                ?? throw new BackupNotFoundException(gameId, timestamp);

            string snapshotDirectory = Path.Combine(_paths.BackupsDirectory(gameId), existing.Timestamp);

            if (Directory.Exists(snapshotDirectory))
            {
                _files.DeleteDirectory(snapshotDirectory, recursive: true);
            }

            index.Events.Remove(existing);
            WriteIndex(gameId, index);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public long GetDiskUsageBytes(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        long total = 0;

        foreach (BackupEvent entry in ReadIndex(gameId).Events)
        {
            string snapshotDirectory = Path.Combine(_paths.BackupsDirectory(gameId), entry.Timestamp);

            if (!Directory.Exists(snapshotDirectory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(snapshotDirectory, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }
        }

        return total;
    }

    /// <summary>
    /// The body of <see cref="Create"/>, assuming the gate is already held, so a restore can take its own
    /// snapshot inside one critical section instead of deadlocking on a non-reentrant semaphore.
    /// </summary>
    private BackupEvent CreateCore(
        string gameId,
        string destinationRoot,
        BackupTrigger trigger,
        string sourceName,
        string? sourceVersion,
        IEnumerable<string> relativePaths,
        string? sourceRoot,
        IReadOnlyDictionary<string, string>? sourceRelativePaths)
    {
        BackupIndex index = ReadIndex(gameId);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string timestamp = NextTimestamp(gameId, index, now);
        string snapshotDirectory = Path.Combine(_paths.BackupsDirectory(gameId), timestamp);

        // Validate the whole copy list first. Validation writes nothing, so a refused archive still leaves
        // no snapshot directory behind - and the caller gets the reasons, not a wrapped copy failure.
        //
        // Two paths per entry, deliberately: the recorded one is resolved against the game root and is what a
        // restore writes to, while the source one is where the bytes are read from. They are the same path
        // unless the caller named a sourceRoot, which is the parked-addon case. Both go through the validator,
        // so neither a manifest path nor a caller-supplied root can make the engine read outside its roots.
        string contentRoot = sourceRoot ?? destinationRoot;
        List<(string RelativePath, string SourcePath)> targets = [];

        foreach (string relativePath in relativePaths)
        {
            string recorded = _pathValidator.ResolveSafe(destinationRoot, relativePath);

            string sourceRelativePath =
                sourceRelativePaths is not null && sourceRelativePaths.TryGetValue(ToPortablePath(relativePath), out string? mapped)
                    ? mapped
                    : relativePath;

            string source = _pathValidator.ResolveSafe(contentRoot, sourceRelativePath);

            targets.Add((ToPortablePath(Path.GetRelativePath(destinationRoot, recorded)), source));
        }

        List<BackupFileEntry> files = [];
        long totalSize = 0;

        try
        {
            // The snapshot directory is the first filesystem mutation, before a single byte of content
            // moves. This line is what invariant #1 means in practice.
            _files.CreateDirectory(snapshotDirectory);

            foreach ((string relativePath, string sourcePath) in targets)
            {
                if (!File.Exists(sourcePath))
                {
                    // Recorded, not created: the index is the proof that a backup happened, and a fresh
                    // install legitimately has nothing to copy.
                    files.Add(new BackupFileEntry(relativePath, 0, now));

                    continue;
                }

                FileInfo info = new(sourcePath);

                _files.CopyFile(sourcePath, Path.Combine(snapshotDirectory, ToPlatformPath(relativePath)), overwrite: false);

                files.Add(new BackupFileEntry(relativePath, info.Length, info.LastWriteTimeUtc));
                totalSize += info.Length;
            }
        }
        catch (Exception exception) when (exception is not BackupFailedException)
        {
            // A half-written snapshot is worse than none: it looks restorable. Remove it and leave the
            // index untouched, so nothing downstream believes a backup exists.
            TryDeleteDirectory(snapshotDirectory);

            throw new BackupFailedException(gameId, trigger, exception);
        }

        BackupEvent created = new()
        {
            Timestamp = timestamp,
            RecordedAt = now,
            Trigger = trigger,
            SourceName = sourceName,
            SourceVersion = sourceVersion,
            Files = files,
            TotalSizeBytes = totalSize,
        };

        // The index is written last, so an index entry always implies a complete snapshot.
        index.Events.Add(created);
        WriteIndex(gameId, index);

        return created;
    }

    /// <summary>
    /// Builds the snapshot directory name. Two operations in the same millisecond - or a clock that went
    /// backwards - get <c>-2</c>, <c>-3</c>, ... appended, and the result is used as the event's
    /// <c>Timestamp</c>.
    /// </summary>
    private string NextTimestamp(string gameId, BackupIndex index, DateTimeOffset now)
    {
        string baseTimestamp = now.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string candidate = baseTimestamp;

        for (int suffix = 2; Collides(gameId, index, candidate); suffix++)
        {
            candidate = $"{baseTimestamp}-{suffix}";
        }

        return candidate;
    }

    private bool Collides(string gameId, BackupIndex index, string timestamp) =>
        index.Events.Any(entry => string.Equals(entry.Timestamp, timestamp, StringComparison.Ordinal))
        || Directory.Exists(Path.Combine(_paths.BackupsDirectory(gameId), timestamp));

    /// <summary>
    /// Deletes snapshot directories the index does not know about.
    /// </summary>
    /// <remarks>
    /// A crash between the last copy and the index write leaves a directory that looks restorable but has
    /// no record of what it holds. The work order's choice - taken here - is to delete the orphan rather
    /// than adopt it: an adopted entry would have to invent the trigger, the source name and the file
    /// list, and an audit trail that invents things is worse than one that removes a partial folder. The
    /// operation log (task 212) is where naming what was removed belongs.
    /// </remarks>
    private void RemoveOrphanSnapshots(string gameId, BackupIndex index)
    {
        string backupsDirectory = _paths.BackupsDirectory(gameId);

        if (!Directory.Exists(backupsDirectory))
        {
            return;
        }

        HashSet<string> known = new(index.Events.Select(entry => entry.Timestamp), StringComparer.Ordinal);

        foreach (string directory in Directory.EnumerateDirectories(backupsDirectory))
        {
            if (!known.Contains(Path.GetFileName(directory)))
            {
                _files.DeleteDirectory(directory, recursive: true);
            }
        }
    }

    private string IndexFile(string gameId) => Path.Combine(_paths.BackupsDirectory(gameId), IndexFileName);

    private BackupIndex ReadIndex(string gameId)
    {
        string indexFile = IndexFile(gameId);

        try
        {
            return AtomicJson.Read<BackupIndex>(indexFile) ?? new BackupIndex();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{indexFile} is not valid JSON, and it is the record of every backup taken for this game. "
                + "Fix or remove it, then try again - Game Polish will not overwrite it.",
                exception);
        }
    }

    private void WriteIndex(string gameId, BackupIndex index) => AtomicJson.Write(IndexFile(gameId), index);

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                _files.DeleteDirectory(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // The original failure is what matters; a leftover directory is reported by List() as an
            // orphan and removed there.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>Snapshot-relative paths are stored with <c>/</c>, so an index reads the same everywhere.</summary>
    private static string ToPortablePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string ToPlatformPath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);
}
