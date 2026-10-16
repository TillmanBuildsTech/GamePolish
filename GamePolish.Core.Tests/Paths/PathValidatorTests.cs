using GamePolish.Core.Paths;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Paths;

/// <summary>
/// The payload table from .todo/204: one test per row, each asserting the <b>reason</b> rather than
/// merely that something threw. A validator that refuses for the wrong reason is a bug in every write
/// path in the product, so "it threw" is not evidence.
/// </summary>
[Collection(StorageCollection.Name)]
public class PathValidatorTests
{
    /// <summary>
    /// The specification's table, extended with the rows that its rules imply but do not enumerate
    /// (illegal characters, over-length inputs). The row label is carried so a failure points at the
    /// line it came from.
    /// </summary>
    private static readonly (string Row, string Input, UnsafePathReason Reason)[] UnsafePayloads =
    [
        ("1", "../evil.txt", UnsafePathReason.TraversalEscape),
        ("2", @"..\..\evil.txt", UnsafePathReason.TraversalEscape),
        ("3", "a/../../evil", UnsafePathReason.TraversalEscape),
        ("4", "a/b/../../../evil", UnsafePathReason.TraversalEscape),
        ("5", "/etc/passwd", UnsafePathReason.RootedPath),
        ("6", @"C:\Windows\System32\evil.dll", UnsafePathReason.RootedPath),
        ("7", @"\\server\share\x", UnsafePathReason.RootedPath),
        ("8", @"\\?\C:\x", UnsafePathReason.RootedPath),
        ("9", "addon.toc:zone.txt", UnsafePathReason.AlternateDataStream),
        ("10", "file.txt::$DATA", UnsafePathReason.AlternateDataStream),
        ("11", "dir/file.toc:secret", UnsafePathReason.AlternateDataStream),
        ("12", "CON", UnsafePathReason.DeviceName),
        ("12", "nul.toc", UnsafePathReason.DeviceName),
        ("12", "LPT1.lua", UnsafePathReason.DeviceName),
        ("13", "Addon.", UnsafePathReason.TrailingDotOrSpace),
        ("13", "Addon ", UnsafePathReason.TrailingDotOrSpace),
        ("14", "a\0b", UnsafePathReason.NullByte),
        ("15", "", UnsafePathReason.Empty),
        ("15", "   ", UnsafePathReason.Empty),
        ("15", ".", UnsafePathReason.Empty),
        ("15", "..", UnsafePathReason.TraversalEscape),
        ("rule 4", "addon<1>.toc", UnsafePathReason.InvalidCharacters),
        ("rule 4", "queue?.toc", UnsafePathReason.InvalidCharacters),
        ("rule 4", "say\"hi.toc", UnsafePathReason.InvalidCharacters),
        ("rule 4", "tab\tname.toc", UnsafePathReason.InvalidCharacters),
        ("rule 2", new string('a', PathValidator.MaximumSegmentLength + 1), UnsafePathReason.TooLong),
    ];

    private readonly string _addonRoot;

    public PathValidatorTests(StorageFixture fixture)
    {
        _addonRoot = fixture.NewSubdirectory($"addon-root-{Guid.NewGuid():N}");
    }

    public static TheoryData<string, UnsafePathReason> UnsafePayloadTable()
    {
        TheoryData<string, UnsafePathReason> data = new();

        foreach ((_, string input, UnsafePathReason reason) in UnsafePayloads)
        {
            data.Add(input, reason);
        }

        return data;
    }

    private static IPathValidator Validator => new PathValidator();

    [Theory]
    [MemberData(nameof(UnsafePayloadTable))]
    public void AnUnsafePathIsRefusedWithItsReason(string input, UnsafePathReason expected)
    {
        PathValidationException exception =
            Assert.Throws<PathValidationException>(() => Validator.ResolveSafe(_addonRoot, input));

        PathValidationFailure failure = Assert.Single(exception.Failures);

        Assert.Equal(expected, failure.Reason);
        Assert.Equal(input, failure.Input);
        Assert.NotEmpty(failure.Message);
    }

    [Fact]
    public void AValidPathResolvesInsideTheRoot()
    {
        string resolved = Validator.ResolveSafe(_addonRoot, "pfUI/pfUI.toc");

        Assert.Equal(Path.GetFullPath(Path.Combine(_addonRoot, "pfUI/pfUI.toc")), resolved);
        Assert.StartsWith(Path.GetFullPath(_addonRoot), resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchReportsEveryRejectedRowAndWritesNothing()
    {
        string[] inputs = [.. UnsafePayloads.Select(payload => payload.Input)];
        int filesBefore = Directory.GetFiles(_addonRoot, "*", SearchOption.AllDirectories).Length;
        DateTime writeTimeBefore = Directory.GetLastWriteTimeUtc(_addonRoot);

        IReadOnlyList<PathValidationFailure> failures = Validator.ValidateAll(_addonRoot, inputs);

        Assert.Equal(inputs.Length, failures.Count);

        foreach ((string row, string input, UnsafePathReason reason) in UnsafePayloads)
        {
            Assert.True(
                failures.Any(failure => failure.Input == input && failure.Reason == reason),
                $"row {row} ('{input}') was not reported as {reason}.");
        }

        Assert.Equal(filesBefore, Directory.GetFiles(_addonRoot, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(writeTimeBefore, Directory.GetLastWriteTimeUtc(_addonRoot));
    }

    [Fact]
    public void APercentEncodedTraversalIsRefusedOnlyAfterItIsDecoded()
    {
        const string Encoded = "%2e%2e%2fevil";

        // The validator never decodes: passed as-is this is a literal file name, and writing it literally
        // is acceptable. What must not happen is decoding it and *then* accepting the result.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_addonRoot, Encoded)),
            Validator.ResolveSafe(_addonRoot, Encoded));

        string decoded = Uri.UnescapeDataString(Encoded);

        PathValidationException exception =
            Assert.Throws<PathValidationException>(() => Validator.ResolveSafe(_addonRoot, decoded));

        Assert.Equal(UnsafePathReason.TraversalEscape, Assert.Single(exception.Failures).Reason);
    }

    [Fact]
    public void TwoEntriesThatDifferOnlyInCaseAreRefusedTogether()
    {
        if (PathComparer.PathComparison == StringComparison.Ordinal)
        {
            Assert.Skip("this filesystem is case-sensitive, so nothing collides and there is nothing to report");
        }

        IReadOnlyList<PathValidationFailure> failures =
            Validator.ValidateAll(_addonRoot, ["Addon/a.txt", "Addon/A.TXT"]);

        PathValidationFailure collision = Assert.Single(failures);

        Assert.Equal(UnsafePathReason.CaseCollision, collision.Reason);
        Assert.Contains("Addon/a.txt", collision.Message, StringComparison.Ordinal);
        Assert.Contains("Addon/A.TXT", collision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryThatLinksOutsideTheRootIsRefused()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"game-polish-outside-{Guid.NewGuid():N}");
        string link = Path.Combine(_addonRoot, "linked-addon");

        Directory.CreateDirectory(outside);
        try
        {
            if (!TryCreateDirectoryLink(link, outside) && !TryCreateJunction(link, outside))
            {
                Assert.Skip("this host does not allow creating a symbolic link (Windows needs Developer Mode or elevation)");
            }

            PathValidationException exception =
                Assert.Throws<PathValidationException>(() => Validator.ResolveSafe(_addonRoot, "linked-addon/evil.txt"));

            Assert.Equal(UnsafePathReason.ReparsePointEscape, Assert.Single(exception.Failures).Reason);
        }
        finally
        {
            TryDelete(link);
            TryDelete(outside);
        }
    }

    [Fact]
    public void AMissingDestinationRootIsRefused()
    {
        string missing = Path.Combine(_addonRoot, "not-there");

        PathValidationException exception =
            Assert.Throws<PathValidationException>(() => Validator.ResolveSafe(missing, "pfUI/pfUI.toc"));

        PathValidationFailure failure = Assert.Single(exception.Failures);

        Assert.Equal(UnsafePathReason.DestinationMissing, failure.Reason);
        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderNameIsAcceptedAndMustBeASingleName()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_addonRoot, "pfUI")),
            Validator.ResolveSafeDirectoryName(_addonRoot, "pfUI"));

        PathValidationException nested = Assert.Throws<PathValidationException>(
            () => Validator.ResolveSafeDirectoryName(_addonRoot, "pfUI/pfUI"));

        Assert.Equal(UnsafePathReason.InvalidCharacters, Assert.Single(nested.Failures).Reason);

        PathValidationException traversal = Assert.Throws<PathValidationException>(
            () => Validator.ResolveSafeDirectoryName(_addonRoot, "../evil"));

        Assert.Equal(UnsafePathReason.TraversalEscape, Assert.Single(traversal.Failures).Reason);
    }

    [Fact]
    public void TheExceptionListsEveryFailureItWasGiven()
    {
        IReadOnlyList<PathValidationFailure> failures =
            Validator.ValidateAll(_addonRoot, ["../evil", "CON", "/etc/passwd"]);

        Assert.Equal(3, failures.Count);

        PathValidationException exception = new(failures);

        Assert.Equal(3, exception.Failures.Count);

        foreach (PathValidationFailure failure in failures)
        {
            Assert.Contains(failure.Message, exception.Message, StringComparison.Ordinal);
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Last resort for Windows without Developer Mode: a directory junction is a reparse point that does
    /// not need <c>SeCreateSymbolicLinkPrivilege</c>, so the reparse-point branch still gets exercised on
    /// a host that cannot create a symbolic link. Test-only; the product never shells out.
    /// </summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(30_000);

            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup only.
        }
    }
}
