namespace GamePolish.Core.Update;

/// <summary>
/// Answers "what does this source serve right now?" without touching a game directory and without writing
/// the mod's content anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists so the update check can be tested without a network, and so task 503 can replace it with
/// the GitHub releases API later: the pipeline's contract is only that a version comes back or a typed
/// failure does.
/// </para>
/// <para>
/// <b>It must be cheap.</b> The Library row calls this per mod, and a check that clones a repository per row
/// would put a download behind a repaint. <see cref="GitVersionProbe"/> answers with one <c>git ls-remote</c>,
/// which transfers refs and no objects.
/// </para>
/// <para>
/// Failures travel as exceptions rather than as a nullable answer, so the caller - which owns the mapping
/// table in <c>OperationResult.FromException</c> - decides how to present them. A probe that swallowed a
/// failure into <see langword="null"/> would be indistinguishable from "this source has no version".
/// </para>
/// </remarks>
public interface IVersionProbe
{
    /// <summary>Reads the source's current version.</summary>
    /// <param name="sourceUrl">The mod's recorded source, e.g. <c>https://github.com/brues-c/pfUI</c>.</param>
    /// <param name="branch">The branch to read, or <see langword="null"/> for the remote's default branch.</param>
    /// <param name="cancellationToken">Cancels the network call; nothing is left behind either way.</param>
    /// <returns>The remote's version, its kind, and the commit behind it when one could be resolved.</returns>
    /// <exception cref="Sources.SourceAcquisitionException">
    /// The URL or branch is unusable, git failed or timed out, or the caller cancelled - with
    /// <see cref="Sources.SourceAcquisitionException.Failure"/> naming which.
    /// </exception>
    /// <exception cref="Sources.GitUnavailableException">No git executable is on <c>PATH</c>.</exception>
    Task<VersionInfo> GetRemoteVersionAsync(string sourceUrl, string? branch, CancellationToken cancellationToken);
}
