namespace GamePolish.Core.Operations;

/// <summary>
/// Base for the typed exceptions Core throws, carrying the <see cref="OperationErrorCode"/> it maps to.
/// </summary>
/// <remarks>
/// <para>
/// An exception type exists because a <b>catch site</b> needs to tell one failure from another; a code
/// exists because the <b>view</b> needs to branch on it. Those are the same distinction, so a typed
/// exception declares its code next to the throw that raises it and
/// <see cref="OperationResult.FromException"/> never has to grow another row when a new failure appears.
/// Every subclass keeps its own message, properties and constructor; this base only adds the code.
/// </para>
/// <para>
/// The types written before this class existed - <c>GameDescriptorValidationException</c>,
/// <c>PathValidationException</c>, the backup and source families - still derive from
/// <see cref="Exception"/> and are mapped by type instead. Converting them is a mechanical follow-up, not a
/// prerequisite: <see cref="OperationResult.FromException"/> checks this base <b>first</b>, so a type
/// migrated later starts using its own code with no other change.
/// </para>
/// <para>
/// Two rules for a subclass. The <b>message is user-facing</b>: one sentence, no stack, no data-root path,
/// no credential - it becomes the toast's text. And the code must not be
/// <see cref="OperationErrorCode.None"/>: an exception that claims success is mapped to
/// <see cref="OperationErrorCode.Unknown"/>, which is a silent downgrade into "something went wrong".
/// </para>
/// </remarks>
public abstract class CoreException : Exception
{
    /// <summary>Initializes the exception with the code it maps to and a user-facing message.</summary>
    /// <param name="code">The code the operation result reports. Must not be <see cref="OperationErrorCode.None"/>.</param>
    /// <param name="message">One sentence the user can read. No stack, no secret, no absolute path.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty or whitespace.</exception>
    protected CoreException(OperationErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
    }

    /// <summary>Initializes the exception with the code it maps to and no message of its own.</summary>
    /// <param name="code">The code the operation result reports. Must not be <see cref="OperationErrorCode.None"/>.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    /// <remarks>
    /// The message is then <see cref="Exception.Message"/>'s default, which names the type and is a poor
    /// toast. Use this constructor only when a wrapper's own type name is genuinely the whole story, and
    /// prefer the one that takes a sentence.
    /// </remarks>
    protected CoreException(OperationErrorCode code, Exception? innerException)
        : base(null, innerException)
    {
        Code = code;
    }

    /// <summary>The code <see cref="OperationResult.FromException"/> reports for this failure.</summary>
    public OperationErrorCode Code { get; }
}
