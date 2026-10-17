using System.Security.Cryptography;
using System.Text;

using GamePolish.Core.IO;
using GamePolish.Core.Manifest;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;

namespace GamePolish.Core.Sources;

/// <summary>
/// The staging half of the install pipeline: a repository clone or a supplied archive becomes a validated
/// directory under <c>&lt;data root&gt;/&lt;game&gt;/staging/&lt;opId&gt;/&lt;FolderName&gt;</c>, and the
/// game directory is never opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The staging model (decided here, per task 207).</b> Nothing is ever cloned or reset inside the
/// game's addons folder. A clone interrupted at 90% - a dropped connection, a full disk, a cancel, git
/// itself crashing - would otherwise leave a partial addon in a directory the running game is reading, and
/// the loader would read whatever <c>.toc</c> happened to be there without complaining. So the download
/// happens under the data root (same volume as the backups and the manifest, and it survives a process
/// death), and the install pipeline does the renames that put it in place. The cost is one extra copy per
/// install, which for a few megabytes of addon is not worth optimising away.
/// </para>
/// <para>
/// <b>Fail-clean.</b> Every path out of <see cref="StageGitAsync"/> and <see cref="StageZipAsync"/> that
/// throws has already deleted the operation's staging directory: the caller receives no
/// <see cref="StagedSource"/>, so nothing else is in a position to clean up. The one thing this class
/// cannot clean up is its own death mid-operation, which is what
/// <see cref="SweepStaleStaging"/> exists for.
/// </para>
/// <para>
/// <b>Version reporting.</b> A clone reports its 40-character HEAD SHA. It never reports a tag, because the
/// hardened clone passes <c>--no-tags</c> - the design note's "HEAD SHA or latest git tag" is therefore
/// half-implemented on purpose: tags are not fetched, so a tag on HEAD is not knowable locally, and asking
/// the remote for the tag list would be a second network round trip on every install for a prettier label.
/// A supplied archive has no git metadata at all, so it reports a content fingerprint: the first 16 hex
/// characters of a SHA-256 over every path, size and file hash in the staged tree. That answers the only
/// question the UI asks of a ZIP - "is this the same archive I installed last time?" - without pretending
/// the archive was versioned.
/// </para>
/// </remarks>
public sealed class SourceAcquirer : ISourceAcquirer
{
    /// <summary>
    /// Directory a source is staged into before its real name is known. Both flows need this: a git clone
    /// has to happen somewhere before the <c>.toc</c> inside it can name the folder, and an archive has to
    /// be extracted before the same can be said of it.
    /// </summary>
    public const string WorkingDirectoryName = "_source";

    /// <summary>Where the per-operation git hardening files live, inside the operation directory.</summary>
    public const string HardeningDirectoryName = ".git-hardening";

    /// <summary>The empty file <c>GIT_CONFIG_GLOBAL</c> points at.</summary>
    public const string EmptyConfigFileName = "gitconfig-empty";

    /// <summary>The empty directory <c>core.hooksPath</c> points at.</summary>
    public const string HooksDirectoryName = "hooks";

    /// <summary><see cref="StagedSource.VersionKind"/> for a repository clone.</summary>
    public const string GitVersionKind = "sha";

    /// <summary><see cref="StagedSource.VersionKind"/> for a supplied archive.</summary>
    public const string ZipVersionKind = "zip";

    /// <summary>Longest branch name accepted, matching the work order's rule.</summary>
    private const int MaximumBranchLength = 101;

    private readonly GamePolishPaths _paths;
    private readonly IPathValidator _pathValidator;
    private readonly IFileOps _files;
    private readonly GitRunner _git;
    private readonly ZipExtractor _zipExtractor;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the acquirer with the real filesystem, validator and git.</summary>
    /// <param name="paths">The resolved data root: staging, like everything else, lives under it.</param>
    public SourceAcquirer(GamePolishPaths paths)
        : this(paths, new PathValidator(), new FileOps(), new GitRunner(), TimeProvider.System)
    {
    }

    /// <summary>Initializes the acquirer with injected collaborators.</summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="pathValidator">Validates every path a source supplied.</param>
    /// <param name="files">The mutating-call seam.</param>
    /// <param name="git">The git runner; its launcher is the process seam tests replace.</param>
    /// <param name="timeProvider">The clock, used only to date a sweep.</param>
    public SourceAcquirer(
        GamePolishPaths paths,
        IPathValidator pathValidator,
        IFileOps files,
        GitRunner git,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pathValidator);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(git);

        _paths = paths;
        _pathValidator = pathValidator;
        _files = files;
        _git = git;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _zipExtractor = new ZipExtractor(pathValidator, files);
    }

    /// <inheritdoc/>
    public async Task<StagedSource> StageGitAsync(SourceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Everything that can be refused is refused before a directory exists (criterion 4).
        string sourceUrl = ValidateGitSource(request);
        string? branch = ValidateBranch(request);
        TimeSpan timeout = ValidateTimeout(request);
        string operationDirectory = OperationDirectory(request);

        try
        {
            _files.CreateDirectory(operationDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            GitHardening hardening = CreateHardening(operationDirectory);
            string workingDirectory = Path.Combine(operationDirectory, WorkingDirectoryName);

            await _git.RunAsync(
                BuildCloneCommand(sourceUrl, branch, workingDirectory),
                operationDirectory,
                hardening,
                timeout,
                cancellationToken).ConfigureAwait(false);

            string sha = (await _git.RunAsync(
                ["rev-parse", "HEAD"],
                workingDirectory,
                hardening,
                timeout,
                cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

            if (!IsCommitSha(sha))
            {
                throw new SourceAcquisitionException(
                    SourceAcquisitionFailure.GitFailed,
                    $"git reported '{Truncate(sha)}' as the commit of '{sourceUrl}', which is not a commit SHA. "
                    + "An empty repository has no commit to install.");
            }

            return Finish(
                request,
                operationDirectory,
                workingDirectory,
                sourceUrl,
                ModSourceType.Github,
                hasGitDirectory: true,
                version: sha,
                versionKind: GitVersionKind);
        }
        catch (OperationCanceledException exception)
        {
            DeleteDirectory(operationDirectory);

            throw Cancelled(request, exception);
        }
        catch
        {
            DeleteDirectory(operationDirectory);

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<StagedSource> StageZipAsync(
        Stream zip,
        string zipFileName,
        SourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFileName);
        ArgumentNullException.ThrowIfNull(request);

        string operationDirectory = OperationDirectory(request);

        try
        {
            // Extraction is synchronous CPU and disk work over an archive that can hold thousands of
            // entries; it must not run on a UI thread.
            return await Task.Run(
                () => StageZipCore(zip, zipFileName, request, operationDirectory, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            DeleteDirectory(operationDirectory);

            throw Cancelled(request, exception);
        }
        catch
        {
            DeleteDirectory(operationDirectory);

            throw;
        }
    }

    /// <inheritdoc/>
    public void Cleanup(StagedSource staged)
    {
        ArgumentNullException.ThrowIfNull(staged);

        // Derived from the keys rather than from StagingDirectory, and only when they are well-formed: this
        // method deletes a tree recursively, so "which directory" must not be something a caller-supplied
        // path can answer.
        if (!IsSingleDirectoryName(staged.GameId) || !IsSingleDirectoryName(staged.OperationId))
        {
            return;
        }

        DeleteDirectory(Path.Combine(_paths.StagingDirectory(staged.GameId), staged.OperationId));
    }

    /// <inheritdoc/>
    public int SweepStaleStaging(string gameId, TimeSpan olderThan, string? excludeOperationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        if (olderThan < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(olderThan),
                olderThan,
                "A negative age would sweep every staging directory, including the one an operation is using right now.");
        }

        string stagingDirectory = _paths.StagingDirectory(gameId);

        if (!Directory.Exists(stagingDirectory))
        {
            return 0;
        }

        DateTime cutoff = (_timeProvider.GetUtcNow() - olderThan).UtcDateTime;
        int removed = 0;

        foreach (string directory in Directory.EnumerateDirectories(stagingDirectory))
        {
            string name = Path.GetFileName(directory);

            // Parked disabled addons are direct children of staging/ too, and they are not abandoned: a mod can
            // stay disabled for months, so age is not evidence that a directory is nobody's. Skipped by 210's own
            // constant rather than a string here, because two literals drift and this one's drift is data loss.
            if (Mods.StagingArea.IsReservedDirectoryName(name))
            {
                continue;
            }

            if (excludeOperationId is not null
                && string.Equals(name, excludeOperationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (Directory.GetLastWriteTimeUtc(directory) > cutoff)
            {
                continue;
            }

            if (DeleteDirectory(directory))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// The extraction itself. Runs off the caller's thread, so it can block on a slow disk without a UI
    /// thread waiting for it.
    /// </summary>
    private StagedSource StageZipCore(
        Stream zip,
        string zipFileName,
        SourceRequest request,
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        _files.CreateDirectory(operationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string workingDirectory = Path.Combine(operationDirectory, WorkingDirectoryName);

        // 204 refuses a path whose destination does not exist, and it is right to: composing a path inside a
        // directory that is not there is how content lands somewhere unexpected.
        _files.CreateDirectory(workingDirectory);

        _zipExtractor.Extract(zip, zipFileName, workingDirectory, cancellationToken);

        return Finish(
            request,
            operationDirectory,
            workingDirectory,
            zipFileName,
            ModSourceType.Zip,
            hasGitDirectory: false,
            version: Fingerprint(workingDirectory),
            versionKind: ZipVersionKind);
    }

    /// <summary>
    /// Promotes the working directory to its real name, lists and validates the tree, and describes it.
    /// </summary>
    /// <param name="request">The request that started this operation.</param>
    /// <param name="operationDirectory">The operation's staging directory.</param>
    /// <param name="workingDirectory">Where the source currently sits, <c>_source</c>.</param>
    /// <param name="fallbackName">The URL or file name, for when nothing better names the folder.</param>
    /// <param name="sourceType">Which kind of source this was.</param>
    /// <param name="hasGitDirectory">True when the tree still holds a <c>.git</c> folder.</param>
    /// <param name="version">The version to report.</param>
    /// <param name="versionKind">How to read it.</param>
    private StagedSource Finish(
        SourceRequest request,
        string operationDirectory,
        string workingDirectory,
        string fallbackName,
        ModSourceType sourceType,
        bool hasGitDirectory,
        string version,
        string versionKind)
    {
        string folderName = AddonFolderName.Resolve(
            _pathValidator,
            operationDirectory,
            workingDirectory,
            request.ExpectedFolderName,
            fallbackName);

        string stagingDirectory = Path.Combine(operationDirectory, folderName);

        // The name is a single segment inside the operation directory, so this is a rename inside one
        // volume - not the copy-plus-delete a cross-volume move would be.
        if (!PathComparer.AreEqual(workingDirectory, stagingDirectory))
        {
            _files.MoveDirectory(workingDirectory, stagingDirectory);
        }

        IReadOnlyList<(string RelativePath, long SizeBytes)> listed = ListFiles(stagingDirectory, hasGitDirectory);
        IReadOnlyList<PathValidationFailure> failures = _pathValidator.ValidateAll(
            stagingDirectory,
            listed.Select(file => file.RelativePath));

        if (failures.Count > 0)
        {
            throw new PathValidationException(failures);
        }

        return new StagedSource
        {
            GameId = request.GameId,
            OperationId = request.OperationId,
            StagingDirectory = stagingDirectory,
            FolderName = folderName,
            Version = version,
            VersionKind = versionKind,
            SourceType = sourceType,
            SourceUrl = request.SourceUrl,
            HasGitDirectory = hasGitDirectory,
            Files = [.. listed.Select(file => new InstalledFileEntry($"{folderName}/{file.RelativePath}", file.SizeBytes))],
        };
    }

    /// <summary>
    /// Every file in the staged tree, relative to the addon folder and ordinally sorted.
    /// </summary>
    /// <param name="root">The staged addon folder.</param>
    /// <param name="skipGitDirectory">True to leave repository metadata out of the list.</param>
    /// <remarks>
    /// Sorted, not enumerated: <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> returns
    /// filesystem order, which differs between the three platforms, and both the fingerprint and a test
    /// comparing two lists need one stable order. A <c>.git</c> directory is skipped at any depth because it
    /// is version-control metadata, never mod content: the game ignores it, and the file list the manifest
    /// records should describe what actually ships. Empty directories are not represented at all - the
    /// product's file list is a list of files, and an addon needs no empty folder.
    /// </remarks>
    private static IReadOnlyList<(string RelativePath, long SizeBytes)> ListFiles(string root, bool skipGitDirectory)
    {
        List<(string RelativePath, long SizeBytes)> files = [];

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, file);

            if (skipGitDirectory && IsInsideGitDirectory(relativePath))
            {
                continue;
            }

            files.Add((ToPortablePath(relativePath), new FileInfo(file).Length));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        return files;
    }

    /// <summary>True when a staged-relative path names the repository metadata git writes.</summary>
    private static bool IsInsideGitDirectory(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".git", PathComparer.PathComparison));

    /// <summary>
    /// A content fingerprint of the staged tree: paths, sizes and file hashes, over a sorted list.
    /// </summary>
    /// <remarks>
    /// Two archives with the same file names but different bytes must not share a version, which is why the
    /// file contents are hashed and not just the layout. Sixteen hex characters is short enough to display
    /// and long enough that a collision is not a practical concern for a version label.
    /// </remarks>
    private static string Fingerprint(string stagingDirectory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach ((string relativePath, long sizeBytes) in ListFiles(stagingDirectory, skipGitDirectory: false))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData([0]);

            using FileStream content = File.OpenRead(Path.Combine(stagingDirectory, ToPlatformPath(relativePath)));

            hash.AppendData(SHA256.HashData(content));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>
    /// The argv for the clone. Options first, then <c>--</c>, then the two things that are data: a URL can
    /// never be re-read as an option, and neither can the destination.
    /// </summary>
    private static IReadOnlyList<string> BuildCloneCommand(string sourceUrl, string? branch, string workingDirectory)
    {
        List<string> command =
        [
            "clone",
            "--depth=1",
            "--single-branch",
            "--no-tags",
            "--no-recurse-submodules",
            "--quiet",
        ];

        if (branch is not null)
        {
            command.Add("--branch");
            command.Add(branch);
        }

        command.Add("--");
        command.Add(sourceUrl);
        command.Add(workingDirectory);

        return command;
    }

    /// <summary>Creates the empty hooks directory and empty config file this operation's git calls use.</summary>
    private GitHardening CreateHardening(string operationDirectory)
    {
        string directory = Path.Combine(operationDirectory, HardeningDirectoryName);
        string hooksDirectory = Path.Combine(directory, HooksDirectoryName);
        string globalConfigFile = Path.Combine(directory, EmptyConfigFileName);

        _files.CreateDirectory(hooksDirectory);

        // A real, empty file: "/dev/null" is not a path on Windows, and a missing GIT_CONFIG_GLOBAL makes
        // git fall back to the user's own configuration.
        _files.WriteAllBytes(globalConfigFile, []);

        return new GitHardening(hooksDirectory, globalConfigFile);
    }

    /// <summary>
    /// The operation's staging directory. Validated before it is returned and before anything is created,
    /// so a bad id can never become a path that some later cleanup deletes.
    /// </summary>
    private string OperationDirectory(SourceRequest request)
    {
        EnsureSingleDirectoryName(request.OperationId, "operation id");
        EnsureDataRoot(request);

        // GameDirectory validates the game id, and throwing here means no directory was created.
        return Path.Combine(_paths.StagingDirectory(request.GameId), request.OperationId);
    }

    /// <summary>Refuses a request built against a different data root (see <see cref="SourceRequest"/>).</summary>
    private void EnsureDataRoot(SourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DataRoot) || !PathComparer.AreEqual(request.DataRoot, _paths.Root))
        {
            throw new ArgumentException(
                $"The request's data root '{request.DataRoot}' is not this acquirer's data root '{_paths.Root}'. "
                + "Staging has to live under the same root as the manifest and the backups, or a crashed "
                + "operation's leftovers could never be found and swept.",
                nameof(request));
        }
    }

    /// <summary>
    /// Refuses anything that is not a usable repository URL. The scheme check is the substance: the test in
    /// the work order names <c>file://</c>, <c>ssh://</c> and <c>git://</c>, and <c>http://</c> is refused
    /// with them, because an addon's Lua runs inside the game client and an unauthenticated download of it
    /// is a downgrade no feature needs.
    /// </summary>
    private static string ValidateGitSource(SourceRequest request)
    {
        string sourceUrl = request.SourceUrl;

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw Invalid(request, "no source URL was supplied.");
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            throw Invalid(request, $"'{sourceUrl}' is not a URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                request,
                $"'{sourceUrl}' is a {uri.Scheme}:// URL. Only https:// repositories can be installed: "
                + "file:// and a local path reach this machine's disk, and ssh:// and git:// are not "
                + "authenticated transports.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid(
                request,
                $"'{sourceUrl}' carries an embedded credential, which would be written to the manifest as "
                + "plain text.");
        }

        return sourceUrl;
    }

    /// <summary>
    /// Validates a branch name, or returns <see langword="null"/> for the remote's default branch.
    /// </summary>
    /// <remarks>
    /// The rule is the work order's, and its purpose is narrower than git's own ref rules: this name goes
    /// into an <c>ArgumentList</c> next to a URL, so it must not be able to look like an option. A leading
    /// <c>-</c> is the attack the rule exists to stop (<c>--upload-pack=evil</c>); git rejects the rest of
    /// what a name like <c>..</c> would mean when it resolves the ref.
    /// </remarks>
    private static string? ValidateBranch(SourceRequest request)
    {
        string? branch = request.Branch;

        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        if (!IsValidBranchName(branch))
        {
            throw Invalid(
                request,
                $"'{branch}' is not a usable branch name. A branch must start with a letter or a digit and "
                + $"contain only letters, digits, '.', '_', '/' or '-', up to {MaximumBranchLength - 1} characters.");
        }

        return branch;
    }

    /// <summary>True for a branch name that cannot be mistaken for a git option.</summary>
    private static bool IsValidBranchName(string branch)
    {
        if (branch.Length is 0 or > MaximumBranchLength || !char.IsAsciiLetterOrDigit(branch[0]))
        {
            return false;
        }

        return branch.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '/' or '-');
    }

    private static TimeSpan ValidateTimeout(SourceRequest request)
    {
        if (request.TimeoutSeconds <= 0)
        {
            throw Invalid(request, $"the timeout must be a positive number of seconds, not {request.TimeoutSeconds}.");
        }

        return TimeSpan.FromSeconds(request.TimeoutSeconds);
    }

    /// <summary>True for a 40-character hexadecimal commit id.</summary>
    private static bool IsCommitSha(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    /// <summary>Throws before any work starts, with the reason the request itself is unusable.</summary>
    private static SourceAcquisitionException Invalid(SourceRequest request, string reason) =>
        new(SourceAcquisitionFailure.InvalidSource, $"the source for '{request.GameId}' was refused: {reason}");

    /// <summary>Wraps a cancellation so the pipeline's failure mapping has one shape to handle.</summary>
    private static SourceAcquisitionException Cancelled(SourceRequest request, OperationCanceledException exception) =>
        new(
            SourceAcquisitionFailure.Cancelled,
            $"staging the source for '{request.GameId}' was cancelled. No staging directory was left behind, and the game directory was never touched.",
            exception);

    /// <summary>Validates a caller-supplied directory name, which is always a single segment.</summary>
    private static void EnsureSingleDirectoryName(string value, string what)
    {
        if (!IsSingleDirectoryName(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a usable {what}: it must be a single directory name with no separators.",
                what);
        }
    }

    /// <summary>True for a name that names exactly one directory and nothing above it.</summary>
    private static bool IsSingleDirectoryName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny(['/', '\\']) < 0
        && value is not "." and not ".."
        && !Path.IsPathRooted(value);

    /// <summary>
    /// Deletes a directory tree, tolerating anything that is not there and anything that fails.
    /// </summary>
    /// <remarks>
    /// <b>The second attempt is load-bearing, not belt and braces.</b> Measured on Windows 11 with .NET 10:
    /// <c>Directory.Delete(aFreshShallowClone, recursive: true)</c> throws
    /// <c>UnauthorizedAccessException</c> naming a <c>.git/objects/pack/*.idx</c>, because git marks its
    /// object store read-only and Windows refuses to delete a read-only file. Without the retry, every
    /// cloned staging tree would leak - the orphan sweep would report success and remove nothing - so the
    /// attribute is cleared and the delete repeated. Clearing it here rather than in <see cref="IFileOps"/>
    /// is deliberate: this is not content, it is an attribute git put on its own metadata, and the tree
    /// being removed is always one this class created.
    /// </remarks>
    private bool DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            _files.DeleteDirectory(path, recursive: true);

            return true;
        }
        catch (IOException)
        {
            // Fall through: a read-only file inside the tree looks like this on some platforms.
        }
        catch (UnauthorizedAccessException)
        {
            // The Windows case described above.
        }

        try
        {
            ClearReadOnlyAttributes(path);
            _files.DeleteDirectory(path, recursive: true);

            return true;
        }
        catch (IOException)
        {
            // The failure path is already reporting something more useful. A staging directory that still
            // could not be removed is what SweepStaleStaging is for; a file that cannot be deleted is
            // reported by the operation's own log, not by this helper.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Clears the read-only attribute on every file and directory under <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Only reached by the retry in <see cref="DeleteDirectory"/>. Best-effort per entry: a file that
    /// disappears between the enumeration and the attribute call is not a reason to abandon the delete.
    /// </remarks>
    private static void ClearReadOnlyAttributes(string path)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(entry);

                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (IOException)
            {
                // Gone, or not ours to change: the delete below reports anything that still matters.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ToPortablePath(string path) => path.Replace('\\', '/');

    private static string ToPlatformPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
