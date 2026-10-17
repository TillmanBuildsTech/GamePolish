namespace GamePolish.Core.Sources;

/// <summary>Thrown when a supplied archive cannot be used as a mod.</summary>
/// <remarks>
/// Carries the offending entry so the message can name one file rather than the whole archive: a
/// 400-entry ZIP refused because of one symlink should say which entry, or the user cannot fix it.
/// A path inside the archive that <c>204</c> refuses surfaces as <c>PathValidationException</c> instead -
/// that type exists to carry the full list of reasons.
/// </remarks>
public sealed class ZipInvalidException : SourceAcquisitionException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="zipFileName">The archive's file name, for the message.</param>
    /// <param name="entryName">The offending entry, or <see langword="null"/> when the archive as a whole is the problem.</param>
    /// <param name="reason">What is wrong, in a sentence a user could read.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public ZipInvalidException(string zipFileName, string? entryName, string reason, Exception? innerException = null)
        : base(SourceAcquisitionFailure.ZipInvalid, BuildMessage(zipFileName, entryName, reason), innerException)
    {
        ZipFileName = zipFileName;
        EntryName = entryName;
        Reason = reason;
    }

    /// <summary>The archive's file name.</summary>
    public string ZipFileName { get; }

    /// <summary>The offending entry, or <see langword="null"/> when the whole archive is the problem.</summary>
    public string? EntryName { get; }

    /// <summary>What is wrong with <see cref="EntryName"/>, or with the archive.</summary>
    public string Reason { get; }

    private static string BuildMessage(string zipFileName, string? entryName, string reason) =>
        entryName is null ? $"'{zipFileName}' is not a usable mod archive: {reason}" : $"'{zipFileName}' entry '{entryName}': {reason}";
}
