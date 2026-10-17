namespace GamePolish.Cli;

/// <summary>
/// The command line could not be understood: an unknown command, a missing or duplicated argument.
/// </summary>
/// <remarks>
/// A usage error is deliberately not a <c>CoreException</c>: nothing reached Core, no game was touched, and the
/// only useful response is to print the usage text. <see cref="ExitCodes.Usage"/> is what the caller sees.
/// </remarks>
public sealed class CliUsageException : Exception
{
    /// <summary>Initializes the exception with the sentence shown to the user.</summary>
    /// <param name="message">What is wrong with the command line, in one line.</param>
    public CliUsageException(string message)
        : base(message)
    {
    }
}
