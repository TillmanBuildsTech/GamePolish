using System.Globalization;

using GamePolish.Core.Operations;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Operations;

/// <summary>
/// Covers <see cref="FileOperationLog"/> (.todo/212, acceptance criteria 3, 5, 6 and 7): one file per UTC day
/// with an ISO-8601 first line, the level gate and its environment override, retention, the promise that a
/// broken log directory cannot fail an operation, and the end-to-end proof that no token reaches disk.
/// </summary>
/// <remarks>
/// Every test builds its own data root under the shared fixture, because the fixture's root is shared with the
/// rest of the storage collection and one test's log files must not look like another's. The clock is fixed for
/// the same reason: a test that took its day from the system clock could fail exactly once a day, at midnight UTC.
/// </remarks>
[Collection(StorageCollection.Name)]
public class FileOperationLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 21, 42, 3, 123, TimeSpan.Zero);

    private readonly StorageFixture _fixture;

    public FileOperationLogTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void WritesOneFileForTheUtcDayWhoseFirstLineIsAnIsoTimestamp()
    {
        GamePolishPaths paths = NewPaths();
        FileOperationLog log = NewLog(paths);

        log.Write("info", "Source staged from https://github.com/brues-code/pfUI.");
        log.Write(new OperationLogEntry(Now.AddSeconds(1), "warn", "Descriptor marked experimental.", null));
        log.Write("error", "Copy failed.", "The device is not ready.");

        string file = Assert.Single(Directory.GetFiles(paths.LogsDirectory));

        Assert.Equal("game-polish-20261015.log", Path.GetFileName(file));
        Assert.StartsWith(paths.Root, file, StringComparison.Ordinal);

        string[] lines = File.ReadAllLines(file);

        Assert.Equal("2026-10-15T21:42:03.123Z INFO  Source staged from https://github.com/brues-code/pfUI.", lines[0]);
        Assert.Equal("2026-10-15T21:42:04.123Z WARN  Descriptor marked experimental.", lines[1]);
        Assert.Equal("2026-10-15T21:42:03.123Z ERROR Copy failed.", lines[2]);

        // The timestamp is machine-parseable and unambiguously UTC, which is what task 213's doctor reads.
        DateTimeOffset parsed = DateTimeOffset.ParseExact(
            lines[0][..24],
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        Assert.Equal(Now, parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void DetailIsWrittenAsIndentedContinuationLines()
    {
        GamePolishPaths paths = NewPaths();
        FileOperationLog log = NewLog(paths);

        log.Write("error", "Copy failed.", "first detail line\nsecond detail line");

        string[] lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(paths.LogsDirectory)));

        Assert.Equal(3, lines.Length);
        Assert.Equal("    first detail line", lines[1]);
        Assert.Equal("    second detail line", lines[2]);
    }

    [Fact]
    public void CurrentLogFilePathIsTheDaysFileUnderTheDataRoot()
    {
        GamePolishPaths paths = NewPaths();

        Assert.Equal(
            Path.Combine(paths.LogsDirectory, "game-polish-20261015.log"),
            NewLog(paths).CurrentLogFilePath);

        // Reading the path must not create anything: a view may show it before the first line exists.
        Assert.False(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void EntriesBelowTheConfiguredLevelAreDropped()
    {
        GamePolishPaths paths = NewPaths();
        FileOperationLog log = new(paths, new FixedTimeProvider(Now), logLevel: "warn");

        log.Write("debug", "not written");
        log.Write("info", "not written either");

        Assert.False(Directory.Exists(paths.LogsDirectory));

        log.Write("warn", "written");
        log.Write("error", "also written");

        string[] lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(paths.LogsDirectory)));

        Assert.Equal(2, lines.Length);
        Assert.EndsWith("WARN  written", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("ERROR also written", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnvironmentVariableRaisesTheLevelOverTheConfiguredOne()
    {
        GamePolishPaths paths = NewPaths();
        FileOperationLog log;

        using (TemporaryEnvironmentVariable.Set(FileOperationLog.LogLevelEnvironmentVariable, "debug"))
        {
            log = new FileOperationLog(paths, new FixedTimeProvider(Now), logLevel: "error");
        }

        log.Write("debug", "written because the environment says debug");

        string file = Assert.Single(Directory.GetFiles(paths.LogsDirectory));

        Assert.Contains("written because the environment says debug", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnvironmentVariableLowersTheLevelOverTheConfiguredOne()
    {
        GamePolishPaths paths = NewPaths();
        FileOperationLog log;

        using (TemporaryEnvironmentVariable.Set(FileOperationLog.LogLevelEnvironmentVariable, "error"))
        {
            log = new FileOperationLog(paths, new FixedTimeProvider(Now), logLevel: "debug");
        }

        log.Write("debug", "not written");
        log.Write("warn", "not written either");

        Assert.False(Directory.Exists(paths.LogsDirectory));

        log.Write("error", "written");
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void AnUnreadableEnvironmentValueFallsBackToTheConfiguredLevel()
    {
        GamePolishPaths paths = NewPaths();
        FileOperationLog log;

        using (TemporaryEnvironmentVariable.Set(FileOperationLog.LogLevelEnvironmentVariable, "loud"))
        {
            log = new FileOperationLog(paths, new FixedTimeProvider(Now), logLevel: "error");
        }

        log.Write("warn", "not written");

        Assert.False(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void TheStoredSettingsLevelIsUsedWhenTheEnvironmentSaysNothing()
    {
        GamePolishPaths paths = NewPaths();
        AtomicJson.Write(paths.AppSettingsFile, new AppSettings { LogLevel = "error" });

        FileOperationLog log = FileOperationLog.FromSettings(paths, new FixedTimeProvider(Now));

        log.Write("info", "not written");
        log.Write("error", "written");

        string[] lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(paths.LogsDirectory)));

        Assert.Single(lines);
        Assert.Contains("written", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptSettingsFileFallsBackToInformationRatherThanNoLogAtAll()
    {
        GamePolishPaths paths = NewPaths();

        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.AppSettingsFile, "{ not json");

        FileOperationLog log = FileOperationLog.FromSettings(paths, new FixedTimeProvider(Now));

        log.Write("info", "written");

        Assert.Single(Directory.GetFiles(paths.LogsDirectory));
    }

    [Fact]
    public void PurgeOlderThan14DaysDeletesOnlyFilesOlderThanThatAndLeavesToday()
    {
        GamePolishPaths paths = NewPaths();
        Directory.CreateDirectory(paths.LogsDirectory);

        string today = Path.Combine(paths.LogsDirectory, "game-polish-20261015.log");
        string yesterday = Path.Combine(paths.LogsDirectory, "game-polish-20261014.log");
        string thirteenDays = Path.Combine(paths.LogsDirectory, "game-polish-20261002.log");
        string fifteenDays = Path.Combine(paths.LogsDirectory, "game-polish-20260930.log");
        string longAgo = Path.Combine(paths.LogsDirectory, "game-polish-20250101.log");

        foreach (string file in new[] { today, yesterday, thirteenDays, fifteenDays, longAgo })
        {
            File.WriteAllText(file, "line\n");
        }

        NewLog(paths).PurgeOlderThan(TimeSpan.FromDays(14));

        Assert.True(File.Exists(today));
        Assert.True(File.Exists(yesterday));
        Assert.True(File.Exists(thirteenDays));

        // Fifteen days is outside the window, and so is last year.
        Assert.False(File.Exists(fifteenDays));
        Assert.False(File.Exists(longAgo));
    }

    [Fact]
    public void PurgeOlderThanLeavesFilesItDidNotWrite()
    {
        GamePolishPaths paths = NewPaths();
        Directory.CreateDirectory(paths.LogsDirectory);

        string notes = Path.Combine(paths.LogsDirectory, "notes.txt");
        string undated = Path.Combine(paths.LogsDirectory, "game-polish-notadate.log");

        File.WriteAllText(notes, "keep me");
        File.WriteAllText(undated, "keep me too");

        NewLog(paths).PurgeOlderThan(TimeSpan.FromDays(1));

        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(undated));
    }

    [Fact]
    public void PurgeOlderThanOnAMissingDirectoryDoesNothing()
    {
        GamePolishPaths paths = NewPaths();

        NewLog(paths).PurgeOlderThan(FileOperationLog.DefaultRetention);

        Assert.False(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void PurgeOlderThanRefusesANegativeAge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewLog(NewPaths()).PurgeOlderThan(TimeSpan.FromDays(-1)));
    }

    [Fact]
    public void DefaultRetentionIsFourteenDays()
    {
        Assert.Equal(TimeSpan.FromDays(14), FileOperationLog.DefaultRetention);
    }

    [Fact]
    public void AnUnwritableLogsDirectoryDoesNotFailTheOperationThatWasLogging()
    {
        GamePolishPaths paths = NewPaths();

        // A file where the log directory belongs is the portable way to make every write fail: on Windows a
        // read-only directory attribute does not stop file creation, an ACL would need elevation, and a file
        // standing in for a directory fails Directory.CreateDirectory on every platform.
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.LogsDirectory, "not a directory");

        FileOperationLog log = NewLog(paths);

        OperationResult result = OperationResult.FromException(new IOException("The device is not ready."), "octowow", "pfui");

        log.Write("error", result.Message, result.Detail);

        foreach (OperationLogEntry entry in result.Entries)
        {
            log.Write(entry);
        }

        // The operation still returns its normal result, and the file standing in for the directory is intact.
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.IoError, result.Code);
        Assert.Equal("not a directory", File.ReadAllText(paths.LogsDirectory));
    }

    [Fact]
    public void RedactionHappensAtWriteTimeEvenWhenTheEntryWasNeverPartOfAResult()
    {
        const string Token = "ghp_NNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNN";

        GamePolishPaths paths = NewPaths();
        FileOperationLog log = NewLog(paths);

        log.Write(new OperationLogEntry(Now, "error", $"clone failed for {Token}", $"token={Token}"));

        string text = File.ReadAllText(Assert.Single(Directory.GetFiles(paths.LogsDirectory)));

        Assert.DoesNotContain("ghp_", text, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSecretReachesAnyFileUnderTheLogsDirectory()
    {
        const string Token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        // The app registers what it holds; the redactor must then keep it out of every line, however it arrives.
        SecretRedactor.RegisterSecret(Token);

        GamePolishPaths paths = NewPaths();
        FileOperationLog log = NewLog(paths);

        // An operation whose failure text carries the token twice: once in a URL inside the exception message,
        // once in the detail of a step it recorded while retrying.
        SourceAcquisitionException exception = new(
            SourceAcquisitionFailure.GitFailed,
            $"fatal: unable to access 'https://x-access-token:{Token}@github.com/nope/nope/': 403");

        IReadOnlyList<OperationLogEntry> entries =
        [
            new OperationLogEntry(Now, "info", $"cloning https://x-access-token:{Token}@github.com/nope/nope", $"token={Token}"),
        ];

        OperationResult result = OperationResult.FromException(exception, "octowow", "pfui", entries);

        foreach (OperationLogEntry entry in result.Entries)
        {
            log.Write(entry);
        }

        log.Write("error", result.Message, result.Detail);

        Assert.DoesNotContain(Token, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Detail!, StringComparison.Ordinal);

        foreach (OperationLogEntry entry in result.Entries)
        {
            Assert.DoesNotContain(Token, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, entry.Detail!, StringComparison.Ordinal);
        }

        string[] files = Directory.GetFiles(paths.LogsDirectory, "*", SearchOption.AllDirectories);

        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);

            Assert.DoesNotContain("ghp_", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        }
    }

    private GamePolishPaths NewPaths() =>
        new(_fixture.NewSubdirectory($"logs-{Guid.NewGuid():N}"));

    private static FileOperationLog NewLog(GamePolishPaths paths) =>
        new(paths, new FixedTimeProvider(Now));
}
