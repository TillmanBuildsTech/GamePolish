using System.Text.Json;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Manifest;
using GamePolish.Core.Operations;
using GamePolish.Core.Paths;
using GamePolish.Core.Registry;
using GamePolish.Core.Sources;

namespace GamePolish.Core.Tests.Operations;

/// <summary>
/// The <c>FromException</c> mapping table (.todo/212, acceptance criterion 1): one test per row, each asserting
/// the <see cref="OperationErrorCode"/> <b>and</b> the <see cref="OperationStatus"/> the whole app branches on.
/// </summary>
/// <remarks>
/// Every row constructs a real instance of the exception type rather than a mock: the mapping is a switch over
/// those types, so a type that stops being throwable should break this file rather than pass silently.
/// </remarks>
public class OperationResultFromExceptionTests
{
    [Fact]
    public void DescriptorValidationMapsToDescriptorInvalid()
    {
        AssertFailed(
            new GameDescriptorValidationException(
                "/fixtures/games/octowow.yaml",
                ["missing key: id", "install_detection.manual_only must be a boolean"]),
            OperationErrorCode.DescriptorInvalid,
            detail => Assert.Contains("missing key: id", detail, StringComparison.Ordinal));
    }

    [Fact]
    public void AlreadyTrackedMapsToGameAlreadyTracked()
    {
        AssertFailed(
            new GameAlreadyTrackedException("octowow", "OctoWoW", @"C:\Games\World of Warcraft", @"D:\WoW"),
            OperationErrorCode.GameAlreadyTracked);
    }

    [Fact]
    public void InstallPathInvalidMapsToInstallPathInvalid()
    {
        AssertFailed(
            new InstallPathInvalidException("octowow", @"C:\Games", "That folder is the Game Polish data root."),
            OperationErrorCode.InstallPathInvalid);
    }

    [Fact]
    public void PathValidationMapsToPathUnsafeAndListsEveryFailureInTheDetail()
    {
        AssertFailed(
            new PathValidationException(
            [
                new PathValidationFailure("../outside.toc", UnsafePathReason.TraversalEscape, "Refused '../outside.toc'."),
                new PathValidationFailure("CON", UnsafePathReason.DeviceName, "Refused 'CON'."),
            ]),
            OperationErrorCode.PathUnsafe,
            detail =>
            {
                Assert.Contains("TraversalEscape", detail, StringComparison.Ordinal);
                Assert.Contains("DeviceName", detail, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void BackupFailedMapsToBackupFailed()
    {
        AssertFailed(
            new BackupFailedException("octowow", BackupTrigger.ModInstall, new IOException("The device is not ready.")),
            OperationErrorCode.BackupFailed);
    }

    [Fact]
    public void BackupFailedDuringARestoreMapsToRestoreFailed()
    {
        // Same type, different code: the toast for a failed restore offers nothing to undo, while the toast for
        // a failed pre-install backup offers "no files were changed".
        AssertFailed(
            new BackupFailedException("octowow", BackupTrigger.Restore, new IOException("The device is not ready.")),
            OperationErrorCode.RestoreFailed,
            detail => Assert.Contains("Restore", detail, StringComparison.Ordinal));
    }

    [Fact]
    public void BackupNotFoundMapsToBackupNotFound()
    {
        AssertFailed(
            new BackupNotFoundException("octowow", "20261015T214203123Z"),
            OperationErrorCode.BackupNotFound);
    }

    [Fact]
    public void ManifestCorruptMapsToManifestCorrupt()
    {
        AssertFailed(
            new ManifestCorruptException(
                "octowow",
                @"C:\data\octowow\manifest.json",
                @"C:\data\octowow\manifest.json.corrupt-20261015T214203123Z",
                new JsonException("Unexpected end of JSON.")),
            OperationErrorCode.ManifestCorrupt);
    }

    [Fact]
    public void RegistryUnavailableMapsToRegistryUnavailable()
    {
        AssertFailed(
            new RegistryUnavailableException(
                "octowow",
                "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json",
                "HttpRequestException: connection refused"),
            OperationErrorCode.RegistryUnavailable,
            detail => Assert.Contains("connection refused", detail, StringComparison.Ordinal));
    }

    [Fact]
    public void RegistryValidationMapsToRegistryInvalidAndListsEveryProblem()
    {
        AssertFailed(
            new RegistryValidationException(
                "octowow",
                "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json",
                [
                    new RegistryProblem
                    {
                        Reason = RegistryProblem.ReasonInvalidModId,
                        Message = "mods[3] ('PFUI'): id must match ^[a-z0-9][a-z0-9-]*$",
                    },
                    new RegistryProblem
                    {
                        Reason = RegistryProblem.ReasonDuplicateModId,
                        Message = "mods[7] ('pfui'): id is already used by mods[3]",
                    },
                ]),
            OperationErrorCode.RegistryInvalid,
            detail =>
            {
                // Every problem survives into the detail, so a registry author fixes the file in one edit
                // instead of one round trip per problem.
                Assert.Contains("mods[3] ('PFUI')", detail, StringComparison.Ordinal);
                Assert.Contains("already used by mods[3]", detail, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void GitUnavailableMapsToGitNotFound()
    {
        AssertFailed(new GitUnavailableException(), OperationErrorCode.GitNotFound);
    }

    [Fact]
    public void GitFailureMapsToGitFailed()
    {
        AssertFailed(
            new SourceAcquisitionException(
                SourceAcquisitionFailure.GitFailed,
                "git exited 128 while cloning the repository."),
            OperationErrorCode.GitFailed);
    }

    [Fact]
    public void GitTimeoutMapsToTimeout()
    {
        AssertFailed(
            new SourceAcquisitionException(
                SourceAcquisitionFailure.Timeout,
                "git did not finish within 60 seconds and was stopped."),
            OperationErrorCode.Timeout);
    }

    [Fact]
    public void RefusedSourceMapsToSourceInvalid()
    {
        AssertFailed(
            new SourceAcquisitionException(
                SourceAcquisitionFailure.InvalidSource,
                "the source for 'octowow' was refused: a URL carrying a credential."),
            OperationErrorCode.SourceInvalid);
    }

    [Fact]
    public void UnspecifiedSourceFailureMapsToSourceNotFound()
    {
        AssertFailed(
            new SourceAcquisitionException(SourceAcquisitionFailure.Unspecified, "there was nothing to stage."),
            OperationErrorCode.SourceNotFound);
    }

    [Fact]
    public void InvalidZipMapsToZipInvalid()
    {
        AssertFailed(
            new ZipInvalidException("pfui.zip", "pfUI/link.toc", "symbolic links are not allowed"),
            OperationErrorCode.ZipInvalid,
            detail => Assert.Contains("symbolic links", detail, StringComparison.Ordinal));
    }

    [Fact]
    public void CancelledSourceStagingMapsToCancelled()
    {
        OperationResult result = OperationResult.FromException(
            new SourceAcquisitionException(
                SourceAcquisitionFailure.Cancelled,
                "staging was cancelled. No staging directory was left behind.",
                new OperationCanceledException()),
            "octowow",
            "pfui");

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
        Assert.Equal("octowow", result.GameId);
        Assert.Equal("pfui", result.ModId);
    }

    [Fact]
    public void OperationCanceledMapsToCancelledNotFailed()
    {
        OperationResult result = OperationResult.FromException(
            new OperationCanceledException("The operation was cancelled."),
            null,
            null);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
    }

    [Fact]
    public void FullDiskIoFailureMapsToDiskFull()
    {
        AssertFailed(
            new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)),
            OperationErrorCode.DiskFull);
    }

    [Fact]
    public void FullDiskIoFailureIsRecognisedFromItsUnixErrno()
    {
        // ENOSPC survives as the HRESULT on Linux and macOS, where a Windows code cannot be present.
        AssertFailed(new IOException("No space left on device.", 28), OperationErrorCode.DiskFull);
    }

    [Fact]
    public void AnyOtherIoFailureMapsToIoError()
    {
        AssertFailed(new IOException("The device is not ready."), OperationErrorCode.IoError);
    }

    [Fact]
    public void UnauthorizedAccessMapsToPermissionDenied()
    {
        AssertFailed(
            new UnauthorizedAccessException("Access to the path is denied."),
            OperationErrorCode.PermissionDenied);
    }

    [Fact]
    public void TimeoutExceptionMapsToTimeout()
    {
        AssertFailed(new TimeoutException("The operation timed out."), OperationErrorCode.Timeout);
    }

    [Fact]
    public void AnUnknownExceptionMapsToUnknownWithItsTypeAndStackInTheDetail()
    {
        Exception thrown = Thrown(new InvalidOperationException("the widget was already wobbled."));

        OperationResult result = OperationResult.FromException(thrown, null, null);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.Unknown, result.Code);
        Assert.Equal("the widget was already wobbled.", result.Message);
        Assert.Contains("System.InvalidOperationException", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Stack:", result.Detail, StringComparison.Ordinal);
        Assert.Contains(" at ", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACoreExceptionUsesTheCodeItDeclares()
    {
        OperationResult result = OperationResult.FromException(
            new StubCoreException(OperationErrorCode.NetworkUnavailable),
            null,
            null);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.NetworkUnavailable, result.Code);
    }

    [Fact]
    public void ACoreExceptionDeclaringCancelledBecomesACancelledResult()
    {
        OperationResult result = OperationResult.FromException(
            new StubCoreException(OperationErrorCode.Cancelled),
            null,
            null);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationErrorCode.Cancelled, result.Code);
    }

    [Fact]
    public void ACoreExceptionDeclaringNoErrorIsNotReportedAsASuccess()
    {
        OperationResult result = OperationResult.FromException(
            new StubCoreException(OperationErrorCode.None),
            null,
            null);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.Unknown, result.Code);
    }

    [Fact]
    public void ASingleInnerAggregateIsMappedThroughToTheFailureItWraps()
    {
        OperationResult result = OperationResult.FromException(
            new AggregateException(new GitUnavailableException()),
            null,
            null);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationErrorCode.GitNotFound, result.Code);
    }

    [Fact]
    public void AMultipleInnerAggregateStaysUnknown()
    {
        AssertFailed(
            new AggregateException(new IOException("one"), new IOException("two")),
            OperationErrorCode.Unknown);
    }

    [Fact]
    public void MappingCarriesTheIdsAndRedactsTheEntries()
    {
        const string Token = "ghp_QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ";

        OperationResult result = OperationResult.FromException(
            new GitUnavailableException(),
            "octowow",
            "pfui",
            [new OperationLogEntry(DateTimeOffset.UnixEpoch, "error", $"token={Token}", null)]);

        Assert.Equal("octowow", result.GameId);
        Assert.Equal("pfui", result.ModId);
        Assert.Null(result.BackupTimestamp);
        Assert.DoesNotContain(Token, Assert.Single(result.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullExceptionIsARefusedArgument()
    {
        Assert.Throws<ArgumentNullException>(() => OperationResult.FromException(null!, null, null));
    }

    /// <summary>Asserts the mapping for a failure with no more to say than its code and status.</summary>
    private static void AssertFailed(
        Exception exception,
        OperationErrorCode expectedCode,
        Action<string>? assertDetail = null)
    {
        OperationResult result = OperationResult.FromException(exception, "octowow", null);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.NotNull(result.Detail);

        assertDetail?.Invoke(result.Detail);
    }

    /// <summary>Throws and catches, so the exception has a real stack for the mapping to report.</summary>
    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    /// <summary>A typed exception whose only content is the code it declares.</summary>
    private sealed class StubCoreException : CoreException
    {
        public StubCoreException(OperationErrorCode code)
            : base(code, "The stub failed.")
        {
        }
    }
}
