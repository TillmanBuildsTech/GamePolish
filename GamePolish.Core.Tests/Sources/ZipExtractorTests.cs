using GamePolish.Core.IO;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Tests.Backup;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// Covers archive extraction (.todo/207, acceptance criterion 5): the wrapper folder, the hostile names a
/// supplied ZIP can carry, the entry kinds and expansion caps that are refused, and the property that a
/// refused archive writes nothing at all.
/// </summary>
[Collection(StorageCollection.Name)]
public class ZipExtractorTests
{
    private readonly ZipExtractor _extractor = new(new PathValidator(), new FileOps());
    private readonly string _root;

    public ZipExtractorTests(StorageFixture fixture)
    {
        _root = fixture.NewSubdirectory($"zip-{Guid.NewGuid():N}");
    }

    [Fact]
    public void ASingleTopLevelFolderIsStripped()
    {
        using MemoryStream archive = ZipBuilder.Build(
            ("pfUI/", string.Empty),
            ("pfUI/pfUI.toc", "## Interface: 11200"),
            ("pfUI/libs/x.lua", "-- x"));

        string destination = NewDestination();
        ZipExtractionResult result = _extractor.Extract(archive, "pfUI.zip", destination, CancellationToken.None);

        Assert.True(result.StrippedWrapperFolder);
        Assert.Equal("pfUI", result.WrapperFolderName);
        Assert.Equal(["libs/x.lua", "pfUI.toc"], result.Files.Select(file => file.RelativePath));
        Assert.True(File.Exists(Path.Combine(destination, "pfUI.toc")));
        Assert.False(Directory.Exists(Path.Combine(destination, "pfUI")), "the wrapper folder itself must not be recreated.");
    }

    [Fact]
    public void ATopLevelFileMeansTheArchiveRootIsTheAddon()
    {
        using MemoryStream archive = ZipBuilder.Build(
            ("README.md", "# pfUI"),
            ("pfUI.toc", "## Interface: 11200"));

        string destination = NewDestination();
        ZipExtractionResult result = _extractor.Extract(archive, "pfUI.zip", destination, CancellationToken.None);

        Assert.False(result.StrippedWrapperFolder);
        Assert.Null(result.WrapperFolderName);
        Assert.Equal(["README.md", "pfUI.toc"], result.Files.Select(file => file.RelativePath));
    }

    [Theory]
    [InlineData("../escape.txt", UnsafePathReason.TraversalEscape)]
    [InlineData("pfUI/../../escape.txt", UnsafePathReason.TraversalEscape)]
    [InlineData("C:\\abs.txt", UnsafePathReason.RootedPath)]
    [InlineData("/etc/passwd", UnsafePathReason.RootedPath)]
    [InlineData("a.txt:ads", UnsafePathReason.AlternateDataStream)]
    [InlineData("CON", UnsafePathReason.DeviceName)]
    [InlineData("pfUI.toc.", UnsafePathReason.TrailingDotOrSpace)]
    public void AHostileEntryIsRefusedAndNothingIsWritten(string name, UnsafePathReason expected)
    {
        using MemoryStream archive = ZipBuilder.Build(("pfUI.toc", "## pfUI"), (name, "payload"));

        string destination = NewDestination();
        PathValidationException failure = Assert.Throws<PathValidationException>(
            () => _extractor.Extract(archive, "evil.zip", destination, CancellationToken.None));

        Assert.Contains(expected, failure.Failures.Select(found => found.Reason));
        Assert.Empty(Directory.GetFileSystemEntries(destination));

        // Not just "the destination is empty": nothing escaped to the staging area either, which is where a
        // traversal payload would have landed.
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void TwoEntriesDifferingOnlyInCaseAreRefusedWhereThatIsOneFile()
    {
        using MemoryStream archive = ZipBuilder.Build(("Foo.toc", "## a"), ("foo.toc", "## b"));

        string destination = NewDestination();

        if (PathComparer.PathComparison == StringComparison.Ordinal)
        {
            // On a case-sensitive filesystem these are two files and there is nothing to report.
            ZipExtractionResult result = _extractor.Extract(archive, "case.zip", destination, CancellationToken.None);

            Assert.Equal(2, result.Files.Count);

            return;
        }

        PathValidationException failure = Assert.Throws<PathValidationException>(
            () => _extractor.Extract(archive, "case.zip", destination, CancellationToken.None));

        Assert.Contains(UnsafePathReason.CaseCollision, failure.Failures.Select(found => found.Reason));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public void ASymbolicLinkEntryIsRefused()
    {
        using MemoryStream archive = ZipBuilder.BuildWithUnixMode("pfUI/pfUI.toc", ZipBuilder.UnixSymbolicLinkMode);

        ZipInvalidException failure = Assert.Throws<ZipInvalidException>(
            () => _extractor.Extract(archive, "link.zip", NewDestination(), CancellationToken.None));

        Assert.Equal("pfUI/pfUI.toc", failure.EntryName);
        Assert.Contains("symbolic link", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowsReparsePointEntryIsRefused()
    {
        using MemoryStream archive = ZipBuilder.BuildWithWindowsReparsePoint("pfUI/junction.toc");

        Assert.Throws<ZipInvalidException>(
            () => _extractor.Extract(archive, "link.zip", NewDestination(), CancellationToken.None));
    }

    [Fact]
    public void AnEntryThatExpandsTooFarIsRefused()
    {
        // 4 MiB of zeros compresses to a few hundred bytes: far past the 200:1 cap, and small enough that
        // the test is instant.
        using MemoryStream archive = ZipBuilder.BuildHighlyCompressible("pfUI/bomb.lua", 4 * 1024 * 1024);

        ZipInvalidException failure = Assert.Throws<ZipInvalidException>(
            () => _extractor.Extract(archive, "bomb.zip", NewDestination(), CancellationToken.None));

        Assert.Equal("pfUI/bomb.lua", failure.EntryName);
        Assert.Contains("limit", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyArchiveIsRefused()
    {
        using MemoryStream archive = ZipBuilder.Empty();

        ZipInvalidException failure = Assert.Throws<ZipInvalidException>(
            () => _extractor.Extract(archive, "empty.zip", NewDestination(), CancellationToken.None));

        Assert.Null(failure.EntryName);
        Assert.Contains("no files", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAnArchiveIsRefused()
    {
        using MemoryStream archive = new("this is not a zip file"u8.ToArray());

        ZipInvalidException failure = Assert.Throws<ZipInvalidException>(
            () => _extractor.Extract(archive, "notes.txt", NewDestination(), CancellationToken.None));

        Assert.Contains("ZIP", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamThatCannotSeekIsBufferedAndStillExtracted()
    {
        using MemoryStream archive = ZipBuilder.Build(("pfUI.toc", "## Interface: 11200"));
        using Stream unseekable = ZipBuilder.NonSeekable(archive);

        string destination = NewDestination();
        ZipExtractionResult result = _extractor.Extract(unseekable, "pfUI.zip", destination, CancellationToken.None);

        Assert.Equal(["pfUI.toc"], result.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public void EveryFileIsWrittenThroughTheSeamAndRenamedIntoPlace()
    {
        RecordingFileOps files = new();
        ZipExtractor extractor = new(new PathValidator(), files);
        string destination = NewDestination();
        using MemoryStream archive = ZipBuilder.Build(("pfUI.toc", "## Interface: 11200"), ("pfUI.lua", "-- pfUI"));

        extractor.Extract(archive, "pfUI.zip", destination, CancellationToken.None);

        string[] creates = [.. files.Calls.Where(call => call.StartsWith("CreateFile(", StringComparison.Ordinal))];
        string[] moves = [.. files.Calls.Where(call => call.StartsWith("MoveFile(", StringComparison.Ordinal))];

        // Content lands in a uniquely named sibling first, so a file that is being written never appears
        // under the name the manifest is about to record.
        Assert.Equal(2, creates.Length);
        Assert.All(creates, call => Assert.Contains(".part-", call, StringComparison.Ordinal));
        Assert.Equal(2, moves.Length);
        Assert.Equal(
            [Path.Combine(destination, "pfUI.lua"), Path.Combine(destination, "pfUI.toc")],
            moves.Select(move => move[(move.IndexOf("=> ", StringComparison.Ordinal) + 3)..^1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnEntryRefusedByNameWritesNothingThroughTheSeam()
    {
        RecordingFileOps files = new();
        ZipExtractor extractor = new(new PathValidator(), files);
        using MemoryStream archive = ZipBuilder.Build(("pfUI/pfUI.toc", "## pfUI"), ("pfUI/../escape.txt", "payload"));

        Assert.Throws<PathValidationException>(
            () => extractor.Extract(archive, "evil.zip", NewDestination(), CancellationToken.None));

        Assert.Empty(files.Calls);
    }

    private string NewDestination()
    {
        string destination = Path.Combine(_root, Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(destination);

        return destination;
    }
}
