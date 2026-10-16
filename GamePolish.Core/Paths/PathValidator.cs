namespace GamePolish.Core.Paths;

/// <summary>
/// The path safety validator: every write of mod content in phases 2-3 goes through it.
/// </summary>
/// <remarks>
/// <para>
/// The order of the checks is the design, and each step catches a class the previous one cannot:
/// </para>
/// <list type="number">
/// <item><description>
/// Emptiness and NUL bytes first, because a NUL can truncate a path inside an OS call.
/// </description></item>
/// <item><description>
/// Length, for the segment and for the composed path.
/// </description></item>
/// <item><description>
/// Separators are normalised to <c>/</c>, treating <c>\</c> as a separator on every platform: a ZIP
/// built on Windows uses it, and macOS must not read <c>Addon\x.toc</c> as one strange file name.
/// </description></item>
/// <item><description>
/// Pattern rejection on the raw input: rooted forms, an NTFS alternate data stream, <c>..</c>
/// segments, reserved device names, trailing dots or spaces, illegal characters. This runs before
/// <see cref="Path.GetFullPath(string)"/> on purpose - <b>GetFullPath normalises but does not
/// reject</b> <c>..</c>, and happily returns a path outside the root.
/// </description></item>
/// <item><description>
/// Composition with <see cref="Path.GetFullPath(string)"/>, then a containment check against the root
/// as the belt to step 4's braces.
/// </description></item>
/// <item><description>
/// Reparse points (symbolic links, junctions, mount points) on the way to the target, and the target
/// itself: a link is resolved and re-checked, and a link that is the target is refused outright.
/// Nothing is ever written through one.
/// </description></item>
/// </list>
/// <para>
/// This component writes nothing: no <c>Directory.CreateDirectory</c>, no temp files, no handles opened
/// for writing. That is what makes it safe to call before the backup step in the install pipeline, and
/// a test asserts that the destination directory is byte-for-byte untouched after the whole payload
/// table has run through it.
/// </para>
/// </remarks>
public sealed class PathValidator : IPathValidator
{
    /// <summary>Longest accepted single segment.</summary>
    public const int MaximumSegmentLength = 255;

    /// <summary>
    /// Longest accepted composed path. 4096 is Windows' practical limit; the same cap is applied
    /// everywhere so a descriptor behaves identically on macOS.
    /// </summary>
    public const int MaximumPathLength = 4096;

    /// <summary>Characters Windows rejects in a file name, and which alias nothing useful anywhere else.</summary>
    private static readonly char[] IllegalSegmentCharacters = ['<', '>', '"', '|', '?', '*'];

    /// <summary>
    /// Segment stems Windows reserves for devices, with or without an extension. The superscript forms
    /// are spelled out because Windows treats them as digits.
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <inheritdoc/>
    public string ResolveSafe(string destinationRoot, string untrustedRelativePath)
    {
        PathValidationFailure? failure = Inspect(
            destinationRoot,
            untrustedRelativePath,
            requireSingleSegment: false,
            out string? resolved);

        if (failure is not null)
        {
            throw new PathValidationException([failure]);
        }

        return resolved!;
    }

    /// <inheritdoc/>
    public string ResolveSafeDirectoryName(string destinationRoot, string untrustedFolderName)
    {
        PathValidationFailure? failure = Inspect(
            destinationRoot,
            untrustedFolderName,
            requireSingleSegment: true,
            out string? resolved);

        if (failure is not null)
        {
            throw new PathValidationException([failure]);
        }

        return resolved!;
    }

    /// <inheritdoc/>
    public IReadOnlyList<PathValidationFailure> ValidateAll(
        string destinationRoot,
        IEnumerable<string> untrustedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(untrustedRelativePaths);

        List<PathValidationFailure> failures = [];
        List<(string Input, string Candidate)> accepted = [];

        foreach (string? input in untrustedRelativePaths)
        {
            PathValidationFailure? failure = Inspect(destinationRoot, input, requireSingleSegment: false, out string? resolved);

            if (failure is not null)
            {
                failures.Add(failure);

                continue;
            }

            accepted.Add((input, resolved!));
        }

        failures.AddRange(FindCaseCollisions(accepted));

        return failures;
    }

    /// <summary>
    /// Validates one input. Returns <see langword="null"/> and sets <paramref name="resolved"/> when the
    /// input is safe.
    /// </summary>
    private static PathValidationFailure? Inspect(
        string destinationRoot,
        string? input,
        bool requireSingleSegment,
        out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return Failure(input, UnsafePathReason.Empty, "An empty path names nothing.");
        }

        if (input.Contains('\0'))
        {
            return Failure(
                input,
                UnsafePathReason.NullByte,
                $"'{Describe(input)}' contains a NUL character, which can truncate the path inside the operating system.");
        }

        if (input == ".")
        {
            return Failure(input, UnsafePathReason.Empty, "'.' names the destination directory itself, not a path inside it.");
        }

        string normalised = input.Replace('\\', '/');
        string[] segments = normalised.Split('/');

        foreach (string segment in segments)
        {
            if (segment.Length > MaximumSegmentLength)
            {
                return Failure(
                    input,
                    UnsafePathReason.TooLong,
                    $"'{Describe(input)}' has a segment of {segment.Length} characters; the limit is {MaximumSegmentLength}.");
            }
        }

        if (IsRootedOrAbsolute(input))
        {
            return Failure(
                input,
                UnsafePathReason.RootedPath,
                $"'{Describe(input)}' is an absolute or rooted path, so it would be written outside the destination root.");
        }

        if (normalised.IndexOf(':') > 0)
        {
            return Failure(
                input,
                UnsafePathReason.AlternateDataStream,
                $"'{Describe(input)}' carries a ':' after its first character, which is an NTFS alternate data stream: it would attach data to a file rather than create one.");
        }

        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                return Failure(
                    input,
                    UnsafePathReason.TraversalEscape,
                    $"'{Describe(input)}' contains a '..' segment, which would leave the destination root.");
            }

            if (IsReservedDeviceName(segment))
            {
                return Failure(
                    input,
                    UnsafePathReason.DeviceName,
                    $"'{segment}' is a reserved Windows device name, so writing to it would address a device rather than a file.");
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return Failure(
                    input,
                    UnsafePathReason.TrailingDotOrSpace,
                    $"'{segment}' ends with a dot or a space, which Windows strips, so it would alias another name.");
            }

            if (segment.Any(character => char.IsControl(character) || IllegalSegmentCharacters.Contains(character)))
            {
                return Failure(
                    input,
                    UnsafePathReason.InvalidCharacters,
                    $"'{segment}' contains a character that is not allowed in a file name (< > \" | ? * or a control character).");
            }
        }

        if (requireSingleSegment && segments.Length != 1)
        {
            return Failure(
                input,
                UnsafePathReason.InvalidCharacters,
                $"'{Describe(input)}' contains a path separator, but a folder name must be a single name.");
        }

        if (!Path.IsPathFullyQualified(destinationRoot) || !Directory.Exists(destinationRoot))
        {
            return Failure(
                input,
                UnsafePathReason.DestinationMissing,
                $"The destination root '{destinationRoot}' is not an existing directory, so nothing can be placed inside it.");
        }

        string candidate = Path.GetFullPath(Path.Combine(destinationRoot, normalised));

        if (candidate.Length > MaximumPathLength)
        {
            return Failure(
                input,
                UnsafePathReason.TooLong,
                $"'{Describe(input)}' composes to {candidate.Length} characters; the limit is {MaximumPathLength}.");
        }

        if (!PathComparer.IsInside(candidate, destinationRoot))
        {
            return Failure(
                input,
                UnsafePathReason.OutsideDestination,
                $"'{Describe(input)}' resolves to '{candidate}', which is not inside '{destinationRoot}'.");
        }

        PathValidationFailure? reparseFailure = InspectReparsePoints(destinationRoot, candidate, input);

        if (reparseFailure is not null)
        {
            return reparseFailure;
        }

        resolved = candidate;

        return null;
    }

    /// <summary>
    /// Walks the existing path components between the root and the target. A component that is a link is
    /// resolved and re-checked against the root; a target that is itself a link is refused.
    /// </summary>
    private static PathValidationFailure? InspectReparsePoints(string destinationRoot, string candidate, string input)
    {
        for (string? current = candidate;
            !string.IsNullOrEmpty(current) && current.Length > destinationRoot.Length;
            current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if (!File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (PathComparer.AreEqual(current, candidate))
            {
                return Failure(
                    input,
                    UnsafePathReason.ReparsePointEscape,
                    $"'{Describe(input)}' resolves to '{current}', which is a link. Game Polish never writes through a link.");
            }

            string? target = TryResolveLinkTarget(current);

            if (target is null || !PathComparer.IsSameOrInside(target, destinationRoot))
            {
                return Failure(
                    input,
                    UnsafePathReason.ReparsePointEscape,
                    $"'{current}' is a link to '{target ?? "an unreadable target"}', which is outside '{destinationRoot}'.");
            }
        }

        return null;
    }

    /// <summary>Resolves a link's final target, or returns <see langword="null"/> when it cannot be proven.</summary>
    private static string? TryResolveLinkTarget(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            // Fail closed: a link whose target cannot be read is treated as an escape.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports a collision when two accepted inputs differ only in case on a case-insensitive
    /// filesystem: one of the two files would silently overwrite the other.
    /// </summary>
    private static IEnumerable<PathValidationFailure> FindCaseCollisions(List<(string Input, string Candidate)> accepted)
    {
        if (PathComparer.PathComparison == StringComparison.Ordinal)
        {
            // On a case-sensitive filesystem there is nothing to report: two names differing in case are
            // two different files.
            return [];
        }

        Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<PathValidationFailure> collisions = [];

        foreach ((string input, string candidate) in accepted)
        {
            if (seen.TryGetValue(candidate, out string? existing))
            {
                collisions.Add(new PathValidationFailure(
                    input,
                    UnsafePathReason.CaseCollision,
                    $"'{input}' and '{existing}' differ only in case, so on this filesystem they are one file and one would silently overwrite the other."));
            }
            else
            {
                seen[candidate] = input;
            }
        }

        return collisions;
    }

    /// <summary>True for <c>C:\...</c>, <c>/...</c>, <c>\\server\share</c> and <c>\\?\...</c>.</summary>
    private static bool IsRootedOrAbsolute(string input) =>
        (input.Length >= 2 && char.IsAsciiLetter(input[0]) && input[1] == ':')
        || input[0] is '/' or '\\';

    /// <summary>True when the segment's stem (before any extension) is a reserved device name.</summary>
    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = dot >= 0 ? segment[..dot] : segment;

        return ReservedDeviceNames.Contains(stem.ToUpperInvariant(), StringComparer.Ordinal);
    }

    /// <summary>Renders an input for a message, cutting it short and never inserting a raw NUL.</summary>
    private static string Describe(string? input)
    {
        const int MaximumLength = 120;

        string text = input is null
            ? "(null)"
            : input.Replace("\0", "\\0", StringComparison.Ordinal);

        if (text.Length > MaximumLength)
        {
            text = text[..MaximumLength] + "...";
        }

        return $"'{text}'";
    }

    private static PathValidationFailure Failure(string? input, UnsafePathReason reason, string message) =>
        new(input ?? string.Empty, reason, message);
}
