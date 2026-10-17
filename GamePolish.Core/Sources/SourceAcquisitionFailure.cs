namespace GamePolish.Core.Sources;

/// <summary>Why a mod source could not be materialised into a staging directory.</summary>
/// <remarks>
/// <para>
/// One base exception with a reason rather than five unrelated exception types: the install pipeline
/// (<c>208</c>) catches <see cref="SourceAcquisitionException"/> once and maps this value onto its own
/// result code, so it never has to know which staging step failed.
/// </para>
/// <para>
/// <see cref="Cancelled"/> is a member on purpose. A cancelled download is reported with the same shape
/// as a failed one, because from the pipeline's point of view both mean "there is no staged source and
/// nothing was written" - see <see cref="SourceAcquisitionException"/> for why that beats letting an
/// <see cref="OperationCanceledException"/> travel.
/// </para>
/// </remarks>
public enum SourceAcquisitionFailure
{
    /// <summary>Nothing more specific applies. The message is the explanation.</summary>
    Unspecified,

    /// <summary>
    /// The request is unusable before any work starts: a source that is not an <c>https</c> URL, a URL
    /// carrying a credential, a branch name that would reach git as an option, a non-positive timeout.
    /// </summary>
    InvalidSource,

    /// <summary>No usable <c>git</c> executable is on <c>PATH</c>.</summary>
    GitUnavailable,

    /// <summary>git started and exited non-zero.</summary>
    GitFailed,

    /// <summary>git did not finish inside the request's timeout; its process tree was killed.</summary>
    Timeout,

    /// <summary>The caller cancelled. Staging is gone and the game directory was never touched.</summary>
    Cancelled,

    /// <summary>
    /// The archive is not a usable mod: unreadable, empty, a symbolic-link entry, or over one of the
    /// expansion caps. A path inside it that <c>204</c> refuses is <c>PathValidationException</c> instead,
    /// because the reasons are the point there.
    /// </summary>
    ZipInvalid,
}
