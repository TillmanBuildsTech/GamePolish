namespace GamePolish.Core.Paths;

/// <summary>Why a path from a mod source was refused.</summary>
/// <remarks>
/// Callers switch on this, so it is part of the contract rather than a label: the installer turns
/// <see cref="CaseCollision"/> into a prompt, and treats every other reason as a refused archive.
/// </remarks>
public enum UnsafePathReason
{
    /// <summary>Nothing was supplied, or the input names the destination itself.</summary>
    Empty,

    /// <summary>The input contains a NUL character, which can truncate a path inside an OS call.</summary>
    NullByte,

    /// <summary>A single segment is over 255 characters, or the composed path is over 4096.</summary>
    TooLong,

    /// <summary>A segment contains a character that is not allowed in a file name.</summary>
    InvalidCharacters,

    /// <summary>The input is absolute or rooted, so it would land outside the destination root.</summary>
    RootedPath,

    /// <summary>A <c>..</c> segment would leave the destination root.</summary>
    TraversalEscape,

    /// <summary>The input carries an NTFS alternate data stream (<c>file.toc:zone.txt</c>).</summary>
    AlternateDataStream,

    /// <summary>A segment names a reserved Windows device (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, ...).</summary>
    DeviceName,

    /// <summary>A segment ends with a dot or a space, which Windows strips, so it would alias another name.</summary>
    TrailingDotOrSpace,

    /// <summary>A directory on the way to the target is a link, or the target itself is one.</summary>
    ReparsePointEscape,

    /// <summary>Two inputs in one batch differ only in case, which is one file on Windows and macOS.</summary>
    CaseCollision,

    /// <summary>The composed path is not inside the destination root.</summary>
    OutsideDestination,

    /// <summary>The destination root is not an existing, fully-qualified directory.</summary>
    DestinationMissing,
}
