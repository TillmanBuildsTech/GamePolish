using GamePolish.Core.Backup;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Backup;

/// <summary>
/// Covers the backup engine (.todo/205, acceptance criteria 1-9): the mutation order that makes
/// "snapshot before the first write" provable, the restore round trip and its reversibility, the index
/// shape, disk usage, and the failure paths that must leave nothing behind.
/// </summary>
[Collection(StorageCollection.Name)]
public class BackupEngineTests
{
    private const string GameId = "octowow";
    private const string AddonToc = "Interface/AddOns/pfUI/pfUI.toc";
    private const string TocContents = "## pfUI v5.1";

    private static readonly DateTimeOffset Start = new(2026, 10, 15, 21, 42, 3, 123, TimeSpan.Zero);

    private readonly GamePolishPaths _paths;
    private readonly string _installRoot;
    private readonly FixedTimeProvider _clock = new(Start);

    public BackupEngineTests(StorageFixture fixture)
    {
        _paths = new GamePolishPaths(fixture.NewSubdirectory($"backup-{Guid.NewGuid():N}"));
        _installRoot = fixture.NewInstallDirectory($"octowow-install-{Guid.NewGuid():N}");
    }

    [Fact]
    public void TheSnapshotDirectoryIsCreatedBeforeTheFirstCopy()
    {
        WriteGameFile(AddonToc, "## pfUI");
        RecordingFileOps files = new();

        BackupEvent created = Engine(files).Create(
            GameId,
            _installRoot,
            BackupTrigger.ModInstall,
            "pfUI",
            "v5.1",
            [AddonToc]);

        Assert.Equal($"CreateDirectory({SnapshotDirectory(created.Timestamp)})", files.Calls[0]);

        int firstCopy = files.Calls.FindIndex(call => call.StartsWith("CopyFile(", StringComparison.Ordinal));

        Assert.True(
            firstCopy > 0,
            $"a copy happened before the snapshot directory existed: {string.Join(" | ", files.Calls)}");
    }

    [Fact]
    public void ABackupWithNothingToCopyStillCreatesTheSnapshotAndAnIndexEvent()
    {
        BackupEvent created = Engine().Create(
            GameId,
            _installRoot,
            BackupTrigger.ModInstall,
            "pfUI",
            null,
            [AddonToc]);

        Assert.True(Directory.Exists(SnapshotDirectory(created.Timestamp)), "the snapshot directory is the record that a backup happened.");
        Assert.Equal(0, created.TotalSizeBytes);

        // Nothing was copied, but the file is still named in the event with a zero size: that entry is
        // what makes a later restore able to remove the file the install went on to create (criterion 5).
        BackupFileEntry entry = Assert.Single(created.Files);

        Assert.Equal(AddonToc, entry.RelativePath);
        Assert.Equal(0, entry.SizeBytes);
        Assert.Empty(Directory.GetFiles(SnapshotDirectory(created.Timestamp), "*", SearchOption.AllDirectories));

        BackupEvent stored = Assert.Single(Engine().List(GameId));

        Assert.Equal(created.Timestamp, stored.Timestamp);
        Assert.Equal(BackupTrigger.ModInstall, stored.Trigger);
        Assert.Equal("pfUI", stored.SourceName);
    }

    [Fact]
    public void TheSnapshotKeepsThePathRelativeToTheInstallRoot()
    {
        WriteGameFile(AddonToc, TocContents);

        BackupEvent created = Engine().Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, [AddonToc]);

        string snapshotFile = Path.Combine(
            SnapshotDirectory(created.Timestamp),
            AddonToc.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(snapshotFile), $"{snapshotFile} was not written.");
        Assert.Equal(TocContents, File.ReadAllText(snapshotFile));

        BackupFileEntry entry = Assert.Single(created.Files);

        Assert.Equal(AddonToc, entry.RelativePath);
        Assert.Equal(TocContents.Length, created.TotalSizeBytes);
    }

    [Fact]
    public void TheIndexIsSnakeCaseAndNamesItsTrigger()
    {
        WriteGameFile(AddonToc, TocContents);

        Engine().Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", "v5.1", [AddonToc]);

        string index = File.ReadAllText(Path.Combine(_paths.BackupsDirectory(GameId), BackupEngine.IndexFileName));

        Assert.Contains("\"schema_version\": 1", index, StringComparison.Ordinal);
        Assert.Contains("\"trigger\": \"mod_install\"", index, StringComparison.Ordinal);
        Assert.Contains("\"source_name\": \"pfUI\"", index, StringComparison.Ordinal);
        Assert.Contains($"\"relative_path\": \"{AddonToc}\"", index, StringComparison.Ordinal);
        Assert.Contains($"\"total_size_bytes\": {TocContents.Length}", index, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreReturnsThePriorStateAndRecordsItself()
    {
        WriteGameFile(AddonToc, "A");
        BackupEngine engine = Engine();

        BackupEvent backup = engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, [AddonToc]);

        WriteGameFile(AddonToc, "B");

        engine.Restore(GameId, _installRoot, backup.Timestamp);

        Assert.Equal("A", ReadGameFile(AddonToc));

        IReadOnlyList<BackupEvent> events = engine.List(GameId);

        Assert.Equal(2, events.Count);

        BackupEvent preRestore = events[0];

        Assert.Equal(BackupTrigger.Restore, preRestore.Trigger);
        Assert.Equal($"pre-restore {backup.Timestamp}", preRestore.SourceName);
        Assert.Equal(
            "B",
            File.ReadAllText(Path.Combine(SnapshotDirectory(preRestore.Timestamp), AddonToc.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void RestoreOfAPreInstallSnapshotRemovesTheInstalledFile()
    {
        BackupEngine engine = Engine();

        // The pre-install backup names what the install is about to create, so nothing is copied.
        BackupEvent preInstall = engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, [AddonToc]);

        WriteGameFile(AddonToc, "installed");
        Assert.True(File.Exists(Path.Combine(_installRoot, AddonToc.Replace('/', Path.DirectorySeparatorChar))));

        engine.Restore(GameId, _installRoot, preInstall.Timestamp);

        Assert.False(File.Exists(Path.Combine(_installRoot, AddonToc.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void TwoBackupsInTheSameMillisecondGetDistinctSnapshots()
    {
        BackupEngine engine = Engine();

        BackupEvent first = engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "first", null, [AddonToc]);
        BackupEvent second = engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "second", null, [AddonToc]);

        Assert.NotEqual(first.Timestamp, second.Timestamp);
        Assert.Equal(first.Timestamp + "-2", second.Timestamp);
        Assert.True(Directory.Exists(SnapshotDirectory(first.Timestamp)));
        Assert.True(Directory.Exists(SnapshotDirectory(second.Timestamp)));
        Assert.Equal(2, engine.List(GameId).Count);
    }

    [Fact]
    public void ACopyFailureRemovesThePartialSnapshotAndThrows()
    {
        WriteGameFile("one.toc", "1");
        WriteGameFile("two.toc", "2");
        RecordingFileOps files = new()
        {
            OnCopyFile = source => source.EndsWith("two.toc", StringComparison.Ordinal)
                ? new IOException("the disk is full")
                : null,
        };

        BackupEngine engine = Engine(files);

        BackupFailedException exception = Assert.Throws<BackupFailedException>(
            () => engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, ["one.toc", "two.toc"]));

        Assert.Equal(GameId, exception.GameId);
        Assert.Equal(BackupTrigger.ModInstall, exception.Trigger);
        Assert.IsType<IOException>(exception.InnerException);

        Assert.Empty(engine.List(GameId));
        Assert.Empty(Directory.GetDirectories(_paths.BackupsDirectory(GameId)));
        Assert.False(File.Exists(Path.Combine(_paths.BackupsDirectory(GameId), BackupEngine.IndexFileName)));
    }

    [Fact]
    public void DiskUsageEqualsTheSnapshotTree()
    {
        WriteGameFile("five.txt", "12345");
        WriteGameFile("three.txt", "abc");

        BackupEngine engine = Engine();

        engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, ["five.txt", "three.txt"]);

        Assert.Equal(8, engine.GetDiskUsageBytes(GameId));

        _clock.Advance(TimeSpan.FromMinutes(5));
        engine.Create(GameId, _installRoot, BackupTrigger.ModUpdate, "pfUI", "v1 -> v2", ["five.txt"]);

        Assert.Equal(13, engine.GetDiskUsageBytes(GameId));
    }

    [Fact]
    public void TheIndexSurvivesANewEngineInstanceAndListsNewestFirst()
    {
        BackupEngine first = Engine();

        BackupEvent oldest = first.Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, [AddonToc]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        BackupEvent newest = first.Create(GameId, _installRoot, BackupTrigger.ModUninstall, "pfUI", null, [AddonToc]);

        BackupEngine reopened = Engine();
        IReadOnlyList<BackupEvent> events = reopened.List(GameId);

        Assert.Equal([newest.Timestamp, oldest.Timestamp], events.Select(entry => entry.Timestamp).ToArray());
        Assert.Equal(BackupTrigger.ModUninstall, reopened.Get(GameId, newest.Timestamp)!.Trigger);
        Assert.Null(reopened.Get(GameId, "20200101T000000000Z"));
    }

    [Fact]
    public void DeleteRemovesTheSnapshotAndItsIndexEntry()
    {
        WriteGameFile(AddonToc, "## pfUI");

        BackupEngine engine = Engine();
        BackupEvent created = engine.Create(GameId, _installRoot, BackupTrigger.ModInstall, "pfUI", null, [AddonToc]);

        engine.Delete(GameId, created.Timestamp);

        Assert.False(Directory.Exists(SnapshotDirectory(created.Timestamp)));
        Assert.Empty(engine.List(GameId));
        Assert.Equal(0, engine.GetDiskUsageBytes(GameId));
        Assert.Throws<BackupNotFoundException>(() => engine.Restore(GameId, _installRoot, created.Timestamp));
        Assert.Throws<BackupNotFoundException>(() => engine.Delete(GameId, created.Timestamp));
    }

    [Fact]
    public void AnOrphanSnapshotDirectoryIsRemovedByList()
    {
        string orphan = SnapshotDirectory("20261015T000000000Z");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "half-copied.toc"), "half");

        Assert.Empty(Engine().List(GameId));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void ARefusedCopyListLeavesNothingBehind()
    {
        RecordingFileOps files = new();

        Assert.Throws<PathValidationException>(() => Engine(files).Create(
            GameId,
            _installRoot,
            BackupTrigger.ModInstall,
            "pfUI",
            null,
            ["../evil.txt"]));

        Assert.Empty(files.Calls);
        Assert.False(Directory.Exists(_paths.BackupsDirectory(GameId)));
    }

    [Fact]
    public void ARestoreRefusesAnIndexEntryThatEscapesTheInstallRoot()
    {
        string backups = _paths.BackupsDirectory(GameId);
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, BackupEngine.IndexFileName), """
            {
              "schema_version": 1,
              "events": [
                {
                  "timestamp": "20261015T214203123Z",
                  "recorded_at": "2026-10-15T21:42:03.123+00:00",
                  "trigger": "mod_install",
                  "source_name": "pfUI",
                  "files": [
                    { "relative_path": "../evil.txt", "size_bytes": 4, "modified_at": "2026-10-15T21:00:00+00:00" }
                  ],
                  "total_size_bytes": 4
                }
              ]
            }
            """);

        BackupEngine engine = Engine();

        Assert.Throws<PathValidationException>(() => engine.Restore(GameId, _installRoot, "20261015T214203123Z"));

        // The index is input too: a hand-edited entry cannot make the engine write outside the install root,
        // and the failed restore left no pre-restore event behind.
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_installRoot)!, "evil.txt")));
        Assert.Single(engine.List(GameId));
    }

    private BackupEngine Engine(RecordingFileOps? files = null) =>
        new(_paths, files ?? new RecordingFileOps(), new PathValidator(), _clock);

    private string SnapshotDirectory(string timestamp) => Path.Combine(_paths.BackupsDirectory(GameId), timestamp);

    private string WriteGameFile(string relativePath, string contents)
    {
        string path = Path.Combine(_installRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);

        return path;
    }

    private string ReadGameFile(string relativePath) =>
        File.ReadAllText(Path.Combine(_installRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
