using GamePolish.Core.Operations;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Operations;

/// <summary>
/// Covers the result factories and the JSON shape (.todo/212, acceptance criteria 4 and 8): the status/code
/// pairing each factory establishes, the 200-character message clamp with the remainder left in the detail,
/// redaction of the carried entries, and the round trip through <see cref="AtomicJson"/> that task 213's
/// <c>--json</c> output depends on.
/// </summary>
[Collection(StorageCollection.Name)]
public class OperationResultTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 21, 42, 3, 123, TimeSpan.Zero);

    private readonly StorageFixture _fixture;

    public OperationResultTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void SuccessCarriesNoErrorAndNoDetail()
    {
        OperationResult result = OperationResult.Success("pfUI 3.2.1 installed.");

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(OperationErrorCode.None, result.Code);
        Assert.Equal("pfUI 3.2.1 installed.", result.Message);
        Assert.Null(result.Detail);
        Assert.Empty(result.Entries);
        Assert.Null(result.GameId);
        Assert.Null(result.ModId);
        Assert.Null(result.BackupTimestamp);
    }

    [Fact]
    public void SuccessCarriesTheIdsAndTheBackupItTook()
    {
        OperationResult result = OperationResult.Success(
            "pfUI 3.2.1 installed.",
            gameId: "octowow",
            modId: "pfui",
            backupTimestamp: "20261015T214203123Z",
            entries: [AnEntry("info", "Backup created.")]);

        Assert.Equal("octowow", result.GameId);
        Assert.Equal("pfui", result.ModId);
        Assert.Equal("20261015T214203123Z", result.BackupTimestamp);
        Assert.Single(result.Entries);
    }

    [Fact]
    public void FailureKeepsTheCodeAndSetsFailedStatus()
    {
        OperationResult result = OperationResult.Failure(
            OperationErrorCode.GitFailed,
            "The repository could not be cloned.",
            "git exited 128.");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.GitFailed, result.Code);
        Assert.Equal("git exited 128.", result.Detail);
    }

    [Fact]
    public void FailureRefusesNoErrorCode()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => OperationResult.Failure(OperationErrorCode.None, "Something happened."));

        Assert.Equal("code", exception.ParamName);
    }

    [Fact]
    public void SuccessAndFailureRefuseAnEmptyMessage()
    {
        Assert.Throws<ArgumentException>(() => OperationResult.Success("  "));
        Assert.Throws<ArgumentException>(() => OperationResult.Failure(OperationErrorCode.IoError, string.Empty));
        Assert.Throws<ArgumentException>(() => OperationResult.Cancelled(string.Empty));
    }

    [Fact]
    public void CancelledIsNotAFailure()
    {
        OperationResult result = OperationResult.Cancelled("Install cancelled. Nothing was changed.", gameId: "octowow");

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
    }

    [Fact]
    public void MessageIsClampedAndTheDetailKeepsTheRemainder()
    {
        // An exception whose message is far longer than a toast line, as a git failure wrapping a long
        // remote response can be.
        string longMessage = new string('a', 9_960) + "TAIL-MARKER" + new string('b', 30);

        OperationResult result = OperationResult.FromException(new InvalidOperationException(longMessage), null, null);

        Assert.Equal(OperationResult.MaxMessageLength, result.Message.Length);
        Assert.True(result.Message.Length <= 200);
        Assert.EndsWith("…", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL-MARKER", result.Message, StringComparison.Ordinal);

        // Nothing was dropped: the whole message is in the technical half, including the part that did not fit.
        Assert.NotNull(result.Detail);
        Assert.Contains("TAIL-MARKER", result.Detail, StringComparison.Ordinal);
        Assert.Contains(longMessage, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageDropsAStackTraceEmbeddedInTheExceptionMessage()
    {
        OperationResult result = OperationResult.FromException(
            new InvalidOperationException("first line\n   at Some.Type.Method() in /src/File.cs:line 12"),
            null,
            null);

        Assert.Equal("first line", result.Message);
        Assert.DoesNotContain("Some.Type.Method", result.Message, StringComparison.Ordinal);

        // The stack is still there for the log drawer; it is the message that stays readable.
        Assert.Contains("Some.Type.Method", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageFallsBackToASentenceWhenTheExceptionSaysNothing()
    {
        OperationResult result = OperationResult.FromException(new IOException(), null, null);

        Assert.Equal(OperationErrorCode.IoError, result.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));

        // The fallback never names the exception type; the type is technical detail and lives in Detail.
        Assert.DoesNotContain(nameof(IOException), result.Message, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EntriesAreRedactedWhenTheResultIsBuilt()
    {
        const string Token = "ghp_ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ";

        OperationResult result = OperationResult.Success(
            "Installed.",
            entries:
            [
                new OperationLogEntry(At, "info", $"cloning url with {Token}", $"token={Token}"),
            ]);

        OperationLogEntry entry = Assert.Single(result.Entries);

        Assert.DoesNotContain("ghp_", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, entry.Message, StringComparison.Ordinal);
        Assert.Equal(At, entry.At);
        Assert.Equal("info", entry.Level);
    }

    [Fact]
    public void MessageAndDetailAreRedactedWhenTheResultIsBuilt()
    {
        const string Token = "ghp_YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYY";

        OperationResult result = OperationResult.Failure(
            OperationErrorCode.SourceAuthRequired,
            $"The token {Token} was refused.",
            $"Authorization: Bearer {Token}");

        Assert.DoesNotContain(Token, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsThroughAtomicJsonWithEntriesAndANullBackupTimestamp()
    {
        string directory = _fixture.NewSubdirectory($"result-{Guid.NewGuid():N}");
        string file = Path.Combine(directory, "result.json");

        OperationResult original = OperationResult.Failure(
            OperationErrorCode.GitFailed,
            "The repository could not be cloned.",
            "git exited 128.",
            gameId: "octowow",
            modId: "pfui",
            backupTimestamp: null,
            entries:
            [
                new OperationLogEntry(At, "info", "Backup created.", null),
                new OperationLogEntry(At.AddSeconds(1), "error", "git exited 128.", "fatal: repository not found"),
            ]);

        AtomicJson.Write(file, original);

        string json = File.ReadAllText(file);
        OperationResult? restored = AtomicJson.Read<OperationResult>(file);

        Assert.NotNull(restored);
        Assert.Equal(OperationStatus.Failed, restored.Status);
        Assert.Equal(OperationErrorCode.GitFailed, restored.Code);
        Assert.Equal(original.Message, restored.Message);
        Assert.Equal(original.Detail, restored.Detail);
        Assert.Equal("octowow", restored.GameId);
        Assert.Equal("pfui", restored.ModId);
        Assert.Null(restored.BackupTimestamp);

        Assert.Equal(original.Entries, restored.Entries);
        Assert.Equal("error", restored.Entries[1].Level);
        Assert.Equal("fatal: repository not found", restored.Entries[1].Detail);

        // The file is the CLI's output, so its spelling is part of the contract: snake_case names and enums by
        // name. A null backup timestamp is omitted rather than written as null.
        Assert.Contains("\"status\": \"failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"git_failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"entries\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("backup_timestamp", json, StringComparison.Ordinal);
    }

    private static OperationLogEntry AnEntry(string level, string message) => new(At, level, message, null);
}
