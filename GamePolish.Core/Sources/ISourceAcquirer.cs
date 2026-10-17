namespace GamePolish.Core.Sources;

/// <summary>
/// Turns a mod source into a fully populated staging directory under the data root, or into a failure
/// that has left nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface never touches a game directory.</b> The staging model decided in task <c>207</c> is
/// stage-then-swap: a clone interrupted at 90% must not leave a half-written addon inside the folder the
/// game is reading, so the download happens under the data root and the install pipeline
/// (<c>208</c>/<c>209</c>) does the renames that put it in place.
/// </para>
/// <para>
/// Every method is fail-clean: when it throws, the operation's staging directory is gone and nothing
/// outside the data root was written. A caller that never receives a <see cref="StagedSource"/> never has
/// to clean up.
/// </para>
/// </remarks>
public interface ISourceAcquirer
{
    /// <summary>Clones a repository shallowly into a staging directory and reports what it staged.</summary>
    /// <param name="request">The source, the game and the operation id. A single name, never a path.</param>
    /// <param name="cancellationToken">Cancels the clone; the process tree is killed and staging removed.</param>
    /// <returns>The staged tree, its folder name, its commit SHA and its validated file list.</returns>
    /// <exception cref="SourceAcquisitionException">
    /// The URL or branch is unusable, git is unavailable, git failed, the clone timed out, or the caller
    /// cancelled - with <see cref="SourceAcquisitionException.Failure"/> naming which.
    /// </exception>
    /// <exception cref="Paths.PathValidationException">
    /// The cloned tree contains a path (or a case collision) the path validator refuses. The repository is
    /// untrusted input even when it comes from GitHub.
    /// </exception>
    Task<StagedSource> StageGitAsync(SourceRequest request, CancellationToken cancellationToken);

    /// <summary>Extracts a supplied archive into a staging directory and reports what it staged.</summary>
    /// <param name="zip">The archive. Read once, and never disposed by this method.</param>
    /// <param name="zipFileName">The archive's original file name, for the folder name and the message.</param>
    /// <param name="request">The source, the game and the operation id.</param>
    /// <param name="cancellationToken">Checked between entries.</param>
    /// <returns>The staged tree, its folder name, its content fingerprint and its validated file list.</returns>
    /// <exception cref="SourceAcquisitionException">
    /// The archive is unusable (<see cref="ZipInvalidException"/>), or the caller cancelled.
    /// </exception>
    /// <exception cref="Paths.PathValidationException">An entry path or folder name the validator refuses.</exception>
    Task<StagedSource> StageZipAsync(Stream zip, string zipFileName, SourceRequest request, CancellationToken cancellationToken);

    /// <summary>Removes the staging directory an operation created. Tolerant: a missing directory is not an error.</summary>
    /// <param name="staged">A staged source this acquirer produced.</param>
    void Cleanup(StagedSource staged);

    /// <summary>
    /// Removes operation directories left behind by a crash or a kill - the one thing staging leaves
    /// behind that no code path can clean up itself.
    /// </summary>
    /// <param name="gameId">The game whose staging area to sweep.</param>
    /// <param name="olderThan">How old a directory must be. Never negative.</param>
    /// <param name="excludeOperationId">An operation to leave alone, typically the one running now.</param>
    /// <returns>How many directories were removed.</returns>
    int SweepStaleStaging(string gameId, TimeSpan olderThan, string? excludeOperationId);
}
