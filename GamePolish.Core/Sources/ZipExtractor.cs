using System.IO.Compression;

using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Paths;

namespace GamePolish.Core.Sources;

/// <summary>
/// Extracts a user-supplied archive into a staging directory, checking every entry before a byte is
/// written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="ZipFile.ExtractToDirectory(string, string)"/>.</b> It extracts the whole archive
/// with whatever names it finds, follows nothing, and reports a failure on the fifth entry after writing
/// the first four. The three things this product needs - refuse a traversal or an absolute path, refuse a
/// symbolic link, and refuse an archive that expands to fill the disk - are exactly the things it does not
/// do, so the extraction is hand-rolled on <see cref="ZipArchive"/> and comes here in four ordered phases:
/// read the entry list, check the caps and the entry kinds, validate every planned path through
/// <c>204</c>, and only then write.
/// </para>
/// <para>
/// <b>Nothing is written unless every entry passes.</b> That is what makes "a refused archive writes
/// nothing outside the staging directory" literal rather than best-effort: validation is a batch call
/// precisely so that two entries differing only in case are caught before either is extracted.
/// </para>
/// <para>
/// <b>Writes go through <see cref="IFileOps"/>.</b> Directory creation, the entry's own bytes and the
/// rename into place are all seam calls, so the install pipeline's failure-injection tests can stop
/// extraction at an exact file - and so the seam stays the single description of what this product may do
/// to a disk. The timestamps are the one exception (<see cref="File.SetLastWriteTimeUtc(string, DateTime)"/>):
/// they are cosmetic, nothing downstream reports on them, and an archive whose entries carry an impossible
/// date must not fail an install.
/// </para>
/// </remarks>
public sealed class ZipExtractor
{
    /// <summary>Largest single entry this will extract, uncompressed.</summary>
    public const long MaximumEntryBytes = 256L * 1024 * 1024;

    /// <summary>Largest total the whole archive may expand to.</summary>
    public const long MaximumTotalBytes = 1024L * 1024 * 1024;

    /// <summary>Largest expansion ratio (uncompressed to compressed) a single entry may have.</summary>
    public const long MaximumExpansionRatio = 200;

    /// <summary>
    /// Largest archive accepted when the caller's stream cannot be seeked, forcing it through memory
    /// first. A seekable stream (a file the user picked) is not subject to it.
    /// </summary>
    public const long MaximumUnseekableArchiveBytes = 512L * 1024 * 1024;

    /// <summary>The file attribute bit that means "reparse point" in a Windows-made archive.</summary>
    private const int WindowsReparsePointAttribute = 0x400;

    /// <summary>The Unix mode that means "symbolic link" in a Unix-made archive.</summary>
    private const int UnixFileTypeMask = 0xF000;

    /// <summary>The Unix mode a symbolic link has.</summary>
    private const int UnixSymbolicLink = 0xA000;

    private readonly IPathValidator _pathValidator;
    private readonly IFileOps _files;

    /// <summary>Initializes the extractor.</summary>
    /// <param name="pathValidator">Validates every entry path before anything is written.</param>
    /// <param name="files">The mutating-call seam.</param>
    public ZipExtractor(IPathValidator pathValidator, IFileOps files)
    {
        ArgumentNullException.ThrowIfNull(pathValidator);
        ArgumentNullException.ThrowIfNull(files);

        _pathValidator = pathValidator;
        _files = files;
    }

    /// <summary>
    /// Extracts <paramref name="zip"/> into <paramref name="destinationRoot"/>, stripping a single
    /// top-level wrapper folder when the archive is wrapped in one.
    /// </summary>
    /// <param name="zip">The archive. Not disposed here; the caller owns it.</param>
    /// <param name="zipFileName">The archive's original file name, for messages.</param>
    /// <param name="destinationRoot">An existing directory inside the data root.</param>
    /// <param name="cancellationToken">Checked between entries.</param>
    /// <returns>The file list, relative to <paramref name="destinationRoot"/>, ordinally sorted.</returns>
    /// <exception cref="ZipInvalidException">The archive is unreadable, empty, over a cap, or holds a link.</exception>
    /// <exception cref="PathValidationException">An entry path is refused; nothing was written.</exception>
    public ZipExtractionResult Extract(
        Stream zip,
        string zipFileName,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        Stream archiveStream = EnsureSeekable(zip, zipFileName);

        try
        {
            using ZipArchive archive = Open(archiveStream, zipFileName);

            List<ZipArchiveEntry> entries = [.. archive.Entries.Where(entry => !IsDirectoryEntry(entry))];

            if (entries.Count == 0)
            {
                throw new ZipInvalidException(zipFileName, null, "the archive contains no files.");
            }

            EnsureWithinCaps(zipFileName, entries);
            EnsureNoLinks(zipFileName, entries);

            string? wrapper = FindWrapperFolder(entries);

            if (wrapper is not null && !IsUsableDirectoryName(destinationRoot, wrapper))
            {
                // A first segment that could not be a folder name of ours - "C:", "CON", ".." - is not a
                // wrapper. Falling back to the unstripped names means the entry validation below reports
                // the real problem against the real entry, instead of stripping the evidence away and
                // flattening a rooted path into a harmless-looking file name.
                wrapper = null;
            }

            List<(ZipArchiveEntry Entry, string RelativePath)> planned = [];

            foreach (ZipArchiveEntry entry in entries)
            {
                string name = Normalise(entry.FullName);

                planned.Add((entry, wrapper is null ? name : name[(wrapper.Length + 1)..]));
            }

            IReadOnlyList<PathValidationFailure> failures = _pathValidator.ValidateAll(
                destinationRoot,
                planned.Select(entry => entry.RelativePath));

            if (failures.Count > 0)
            {
                throw new PathValidationException(failures);
            }

            List<InstalledFileEntry> files = [];

            foreach ((ZipArchiveEntry entry, string relativePath) in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The batch call above catches what only a whole-tree view can see; this call is what
                // produces the path that is actually written, so the two agree by construction.
                string resolved = _pathValidator.ResolveSafe(destinationRoot, relativePath);

                WriteEntry(entry, resolved);
                files.Add(new InstalledFileEntry(ToPortablePath(relativePath), entry.Length));
            }

            files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

            return new ZipExtractionResult
            {
                Files = files,
                StrippedWrapperFolder = wrapper is not null,
                WrapperFolderName = wrapper,
            };
        }
        finally
        {
            if (!ReferenceEquals(archiveStream, zip))
            {
                archiveStream.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes one entry: bytes to a uniquely named sibling, then a rename into place, so a failure halfway
    /// through a file leaves no file under the name the manifest is about to record.
    /// </summary>
    private void WriteEntry(ZipArchiveEntry entry, string resolvedPath)
    {
        string partPath = $"{resolvedPath}.part-{Guid.NewGuid():N}";

        try
        {
            using (Stream output = _files.CreateFile(partPath))
            using (Stream input = entry.Open())
            {
                input.CopyTo(output);
            }

            _files.MoveFile(partPath, resolvedPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(partPath);

            throw;
        }

        TrySetTimestamp(resolvedPath, entry);
    }

    /// <summary>
    /// Buffers a stream git and <see cref="ZipArchive"/> can both rewind, since
    /// <see cref="ZipArchive"/> in read mode needs to seek to the central directory.
    /// </summary>
    private static Stream EnsureSeekable(Stream zip, string zipFileName)
    {
        if (zip.CanSeek)
        {
            return zip;
        }

        MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = zip.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;

            if (total > MaximumUnseekableArchiveBytes)
            {
                buffer.Dispose();

                throw new ZipInvalidException(
                    zipFileName,
                    null,
                    $"the upload is larger than {MaximumUnseekableArchiveBytes / (1024 * 1024)} MiB, and a "
                    + "stream that cannot be rewound has to be held in memory to be read at all.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>Opens the archive, turning "this is not a ZIP" into a mod-shaped failure.</summary>
    private static ZipArchive Open(Stream stream, string zipFileName)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new ZipInvalidException(zipFileName, null, "the file is not a readable ZIP archive.", exception);
        }
    }

    /// <summary>True for an entry that names a folder rather than a file.</summary>
    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.Name.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>
    /// Refuses an archive that would expand beyond the caps. Checked on the entry list, before extraction:
    /// a bomb that has been half-extracted is a bomb that has already cost the disk space.
    /// </summary>
    private static void EnsureWithinCaps(string zipFileName, List<ZipArchiveEntry> entries)
    {
        long total = 0;

        foreach (ZipArchiveEntry entry in entries)
        {
            if (entry.Length > MaximumEntryBytes)
            {
                throw new ZipInvalidException(
                    zipFileName,
                    entry.FullName,
                    $"it expands to {entry.Length / (1024 * 1024)} MiB, over the {MaximumEntryBytes / (1024 * 1024)} MiB limit for one file.");
            }

            total += entry.Length;

            if (total > MaximumTotalBytes)
            {
                throw new ZipInvalidException(
                    zipFileName,
                    entry.FullName,
                    $"the archive expands to more than {MaximumTotalBytes / (1024 * 1024)} MiB in total.");
            }

            if (entry.CompressedLength > 0)
            {
                double ratio = (double)entry.Length / entry.CompressedLength;

                if (ratio > MaximumExpansionRatio)
                {
                    throw new ZipInvalidException(
                        zipFileName,
                        entry.FullName,
                        $"it expands {ratio:N0}:1, over the {MaximumExpansionRatio}:1 limit - the shape of an archive that fills the disk.");
                }
            }
        }
    }

    /// <summary>
    /// Refuses symbolic-link entries. Checked on the entry list rather than after extraction, because a
    /// link that has already been created can be followed by the very next entry.
    /// </summary>
    private static void EnsureNoLinks(string zipFileName, List<ZipArchiveEntry> entries)
    {
        foreach (ZipArchiveEntry entry in entries)
        {
            if (IsUnixSymbolicLink(entry) || IsWindowsReparsePoint(entry))
            {
                throw new ZipInvalidException(
                    zipFileName,
                    entry.FullName,
                    "it is a symbolic link, and Game Polish never writes through one.");
            }
        }
    }

    /// <summary>True for an entry whose Unix mode says "symbolic link".</summary>
    private static bool IsUnixSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;

    /// <summary>
    /// True for an entry a Windows-made archive marks as a reparse point. Only read when the Unix mode bits
    /// are absent: a Unix archive's low bits can carry permission flags that overlap this mask.
    /// </summary>
    private static bool IsWindowsReparsePoint(ZipArchiveEntry entry) =>
        (entry.ExternalAttributes >> 16) == 0
        && (entry.ExternalAttributes & WindowsReparsePointAttribute) != 0;

    /// <summary>
    /// The single folder every entry lives under, or <see langword="null"/> when the archive is not wrapped
    /// in one.
    /// </summary>
    /// <remarks>
    /// "Exactly one top-level directory and no top-level file" is the test, and both halves matter: a single
    /// entry <c>pfUI/</c> (a folder entry, already skipped) or a root-level <c>README.md</c> next to a
    /// <c>pfUI/</c> folder means the archive root is the addon, not the folder.
    /// </remarks>
    private static string? FindWrapperFolder(List<ZipArchiveEntry> entries)
    {
        string? single = null;

        foreach (ZipArchiveEntry entry in entries)
        {
            string[] segments = Normalise(entry.FullName).Split('/');

            if (segments.Length < 2 || segments[0].Length == 0 || segments[0] is "." or "..")
            {
                return null;
            }

            if (single is null)
            {
                single = segments[0];

                continue;
            }

            if (!string.Equals(single, segments[0], PathComparer.PathComparison))
            {
                return null;
            }
        }

        return single;
    }

    /// <summary>True when the validator would accept <paramref name="candidate"/> as one folder name.</summary>
    private bool IsUsableDirectoryName(string destinationRoot, string candidate)
    {
        try
        {
            _pathValidator.ResolveSafeDirectoryName(destinationRoot, candidate);

            return true;
        }
        catch (PathValidationException)
        {
            return false;
        }
    }

    /// <summary>Entries are written with <c>/</c> by every sane archiver, but a Windows-made one may not.</summary>
    private static string Normalise(string entryName) => entryName.Replace('\\', '/');

    /// <summary>Paths are stored and reported with <c>/</c>, so a staged file list reads the same everywhere.</summary>
    private static string ToPortablePath(string relativePath) => relativePath.Replace('\\', '/');

    /// <summary>
    /// Applies the entry's own timestamp. Failure is ignored: a date outside the filesystem's range is the
    /// archive's problem, not a reason to refuse an install that is otherwise fine.
    /// </summary>
    private static void TrySetTimestamp(string path, ZipArchiveEntry entry)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, entry.LastWriteTime.UtcDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (InvalidDataException)
        {
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            _files.DeleteFile(path);
        }
        catch (IOException)
        {
            // The caller is already failing; a leftover ".part" file is swept with the whole staging
            // directory by the failure path that owns this operation.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
