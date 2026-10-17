namespace GamePolish.Core.Sources;

/// <summary>
/// Thrown when a mod source could not be staged. No staging directory is left behind when this is
/// thrown, and nothing outside the data root was written.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation is reported through this type as well, with
/// <see cref="SourceAcquisitionFailure.Cancelled"/>. That deviates from the .NET convention of letting
/// an <see cref="OperationCanceledException"/> travel up, and it is deliberate: the caller passed a
/// token into a method whose only other outcome is a <c>StagedSource</c>, so a cancelled run has to be
/// reportable with the same shape as a failed one - and the pipeline's failure mapping then has exactly
/// one place to look. The <see cref="OperationCanceledException"/> is preserved as
/// <see cref="Exception.InnerException"/> for anyone who needs to rethrow it.
/// </para>
/// </remarks>
public class SourceAcquisitionException : Exception
{
    /// <summary>Initializes the exception with an unspecified reason.</summary>
    /// <param name="message">What went wrong, in a sentence a user could read.</param>
    public SourceAcquisitionException(string message)
        : this(SourceAcquisitionFailure.Unspecified, message, null)
    {
    }

    /// <summary>Initializes the exception.</summary>
    /// <param name="failure">The reason.</param>
    /// <param name="message">What went wrong, in a sentence a user could read.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public SourceAcquisitionException(
        SourceAcquisitionFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Why the source could not be staged.</summary>
    public SourceAcquisitionFailure Failure { get; }
}
