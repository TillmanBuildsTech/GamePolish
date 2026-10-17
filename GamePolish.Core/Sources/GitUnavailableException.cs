namespace GamePolish.Core.Sources;

/// <summary>Thrown when no usable <c>git</c> executable can be found.</summary>
/// <remarks>
/// The message names an installation step rather than an alternative source, because there is no
/// alternative: invariant #5 allows repositories and user-supplied ZIPs, and downloading a tarball off a
/// proprietary service to paper over a missing <c>git</c> would be exactly the fallback the product
/// promise forbids.
/// </remarks>
public sealed class GitUnavailableException : SourceAcquisitionException
{
    /// <summary>What the user is told: what is missing, and what to do about it.</summary>
    public const string UserMessage =
        "Git is required to install from a repository. Install Git, make sure it is on PATH, then try again.";

    /// <summary>Initializes the exception with the standard message.</summary>
    public GitUnavailableException()
        : this(UserMessage, null)
    {
    }

    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public GitUnavailableException(string message, Exception? innerException = null)
        : base(SourceAcquisitionFailure.GitUnavailable, message, innerException)
    {
    }
}
