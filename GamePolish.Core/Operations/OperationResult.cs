using System.Text;

using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Manifest;
using GamePolish.Core.Paths;
using GamePolish.Core.Registry;
using GamePolish.Core.Sources;

namespace GamePolish.Core.Operations;

/// <summary>
/// What every Core operation returns: one status, one error code, one user-facing line, and the technical
/// trail behind it.
/// </summary>
/// <remarks>
/// <para>
/// After task 212 no Core operation throws across its public surface for an expected failure. It returns
/// this instead, so the UI has one shape to render, the CLI has one shape to serialise (task 213's
/// <c>--json</c>), and the log drawer (task 307) has one shape to expand. The two halves are split on
/// purpose: <see cref="Message"/> is a sentence the user reads at a glance and therefore never carries a
/// stack, an exception type or a credential; <see cref="Detail"/> is the technical half and may carry all
/// of those except a credential.
/// </para>
/// <para>
/// <b>Build results through the factories, not with <c>new</c>.</b> They are what clamp the message to
/// <see cref="MaxMessageLength"/>, strip line breaks, and redact both halves. The record's initialisers
/// stay public because the JSON deserialiser needs them - which also means a caller that bypasses a
/// factory bypasses the redaction, so don't.
/// </para>
/// <para>
/// <see cref="FromException"/> is the <b>only</b> mapping from the typed Core exceptions to
/// <see cref="OperationErrorCode"/>. Two places that map the same exception differently is how a UI ends up
/// showing "unknown error" for a failure it has a branch for.
/// </para>
/// </remarks>
public sealed record OperationResult
{
    /// <summary>
    /// The longest <see cref="Message"/> the UI is asked to render as a single line. Longer text is cut
    /// here (with an ellipsis) and kept whole in <see cref="Detail"/>.
    /// </summary>
    /// <remarks>
    /// 200 characters is roughly two lines of a toast on the narrowest window the app supports. The cap
    /// exists because exception messages are the wrong length for a toast - a git failure can be pages -
    /// and a view that has to clamp is a view that has to guess.
    /// </remarks>
    public const int MaxMessageLength = 200;

    /// <summary>How the operation ended.</summary>
    public required OperationStatus Status { get; init; }

    /// <summary>Why it failed, or <see cref="OperationErrorCode.None"/> when it succeeded.</summary>
    public required OperationErrorCode Code { get; init; }

    /// <summary>
    /// One line for the user: what happened, and what to do next where that is knowable. Never longer than
    /// <see cref="MaxMessageLength"/>, never containing a stack trace, an exception type name or a secret.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The technical explanation: every refused path, the descriptor's problems, the exception type and
    /// stack. Multi-line, redacted, and safe to hand to the log drawer.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The steps the operation logged, in order. Empty when the caller passed none - the log file remains the
    /// durable record, and this is the copy that travels with the result.
    /// </summary>
    public required IReadOnlyList<OperationLogEntry> Entries { get; init; }

    /// <summary>The game the operation was for, when it was about one game.</summary>
    public string? GameId { get; init; }

    /// <summary>The mod the operation was for, when it was about one mod.</summary>
    public string? ModId { get; init; }

    /// <summary>
    /// The timestamp of the backup the operation took, so the toast can offer "View backup" (task 306)
    /// without a second lookup. Null for operations that took none.
    /// </summary>
    public string? BackupTimestamp { get; init; }

    /// <summary>Builds a successful result.</summary>
    /// <param name="message">One user-facing line, e.g. <c>pfUI 3.2.1 installed.</c></param>
    /// <param name="gameId">The game, when the operation was about one.</param>
    /// <param name="modId">The mod, when the operation was about one.</param>
    /// <param name="backupTimestamp">The backup the operation took, when it took one.</param>
    /// <param name="entries">The logged steps, when the caller kept them.</param>
    /// <param name="detail">Optional technical detail; usually null on success.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty or whitespace.</exception>
    public static OperationResult Success(
        string message,
        string? gameId = null,
        string? modId = null,
        string? backupTimestamp = null,
        IReadOnlyList<OperationLogEntry>? entries = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new OperationResult
        {
            Status = OperationStatus.Succeeded,
            Code = OperationErrorCode.None,
            Message = NormalizeMessage(message),
            Detail = NormalizeDetail(detail),
            Entries = Sanitize(entries),
            GameId = gameId,
            ModId = modId,
            BackupTimestamp = backupTimestamp,
        };
    }

    /// <summary>Builds a failed result.</summary>
    /// <param name="code">Why it failed. <see cref="OperationErrorCode.None"/> is refused.</param>
    /// <param name="message">One user-facing line saying what went wrong and what to do about it.</param>
    /// <param name="detail">The technical explanation; when <paramref name="cause"/> is given, its type and stack are appended.</param>
    /// <param name="cause">The exception that stopped the operation, when there was one.</param>
    /// <param name="gameId">The game, when the operation was about one.</param>
    /// <param name="modId">The mod, when the operation was about one.</param>
    /// <param name="backupTimestamp">The backup taken before the failure, when one was taken.</param>
    /// <param name="entries">The logged steps, when the caller kept them.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="message"/> is blank, or <paramref name="code"/> is <see cref="OperationErrorCode.None"/> -
    /// a failed result with no code would render as an error with no branch in the UI.
    /// </exception>
    public static OperationResult Failure(
        OperationErrorCode code,
        string message,
        string? detail = null,
        Exception? cause = null,
        string? gameId = null,
        string? modId = null,
        string? backupTimestamp = null,
        IReadOnlyList<OperationLogEntry>? entries = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (code == OperationErrorCode.None)
        {
            throw new ArgumentException(
                "A failed result needs a code other than None; use Success for an operation that worked.",
                nameof(code));
        }

        return new OperationResult
        {
            Status = OperationStatus.Failed,
            Code = code,
            Message = NormalizeMessage(message),
            Detail = NormalizeDetail(Join(detail, Describe(cause))),
            Entries = Sanitize(entries),
            GameId = gameId,
            ModId = modId,
            BackupTimestamp = backupTimestamp,
        };
    }

    /// <summary>Builds a cancelled result.</summary>
    /// <param name="message">One user-facing line, e.g. <c>Install cancelled. Nothing was changed.</c></param>
    /// <param name="gameId">The game, when the operation was about one.</param>
    /// <param name="modId">The mod, when the operation was about one.</param>
    /// <param name="backupTimestamp">The backup taken before the cancellation, when one was taken.</param>
    /// <param name="entries">The logged steps, when the caller kept them.</param>
    /// <param name="detail">Optional technical detail.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty or whitespace.</exception>
    public static OperationResult Cancelled(
        string message,
        string? gameId = null,
        string? modId = null,
        string? backupTimestamp = null,
        IReadOnlyList<OperationLogEntry>? entries = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new OperationResult
        {
            Status = OperationStatus.Cancelled,
            Code = OperationErrorCode.Cancelled,
            Message = NormalizeMessage(message),
            Detail = NormalizeDetail(detail),
            Entries = Sanitize(entries),
            GameId = gameId,
            ModId = modId,
            BackupTimestamp = backupTimestamp,
        };
    }

    /// <summary>
    /// Turns a thrown exception into a result, using the one mapping table the whole app shares.
    /// </summary>
    /// <param name="exception">The exception to map. <see cref="OperationCanceledException"/> yields a cancelled result.</param>
    /// <param name="gameId">The game the operation was for, when it was about one.</param>
    /// <param name="modId">The mod the operation was for, when it was about one.</param>
    /// <param name="entries">The steps logged before the failure. Redacted, not discarded.</param>
    /// <returns>
    /// A result whose <see cref="Code"/> names the failure, whose <see cref="Message"/> is the exception's first
    /// line clamped to <see cref="MaxMessageLength"/>, and whose <see cref="Detail"/> holds the whole exception:
    /// its type, its full message, any structured payload (refused paths, descriptor problems) and its stack.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// A <see cref="CoreException"/> is trusted to know its own code; every other type is looked up here. That
    /// keeps the mapping in one place while letting a future typed exception declare its code where it is
    /// thrown, next to the failure it describes.
    /// </para>
    /// <para>
    /// The message is the exception's own first line, never a sentence built by this method: the exception
    /// types were written to say what to do next ("Install Git, make sure it is on PATH, then try again"),
    /// and re-wording them here would drift from what the throwing site knows. Everything that does not fit
    /// in a toast line - the rest of the message, the type, the stack - goes to <see cref="Detail"/>.
    /// </para>
    /// </remarks>
    public static OperationResult FromException(
        Exception exception,
        string? gameId,
        string? modId,
        IReadOnlyList<OperationLogEntry>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception mapped = Unwrap(exception);
        (OperationErrorCode code, OperationStatus status) = Map(mapped);

        string message = NormalizeMessage(mapped.Message, FallbackMessage(code));

        return new OperationResult
        {
            Status = status,
            Code = code,
            Message = message,
            Detail = NormalizeDetail(Describe(mapped)),
            Entries = Sanitize(entries),
            GameId = gameId,
            ModId = modId,
            BackupTimestamp = null,
        };
    }

    /// <summary>
    /// The mapping table. One row per typed exception, plus the BCL types that Core's own code can throw
    /// through an <c>IFileOps</c> seam.
    /// </summary>
    /// <remarks>
    /// Order is significant: derived types are matched before their bases, and
    /// <see cref="UnauthorizedAccessException"/> before the <see cref="IOException"/> rows even though it is
    /// not an <see cref="IOException"/> - the two are both "the filesystem said no" and would otherwise be
    /// one branch.
    /// </remarks>
    private static (OperationErrorCode Code, OperationStatus Status) Map(Exception exception) => exception switch
    {
        CoreException core => MapCore(core),

        GameDescriptorValidationException => (OperationErrorCode.DescriptorInvalid, OperationStatus.Failed),
        GameAlreadyTrackedException => (OperationErrorCode.GameAlreadyTracked, OperationStatus.Failed),
        InstallPathInvalidException => (OperationErrorCode.InstallPathInvalid, OperationStatus.Failed),
        PathValidationException => (OperationErrorCode.PathUnsafe, OperationStatus.Failed),

        BackupNotFoundException => (OperationErrorCode.BackupNotFound, OperationStatus.Failed),
        BackupFailedException backup => (
            backup.Trigger == BackupTrigger.Restore ? OperationErrorCode.RestoreFailed : OperationErrorCode.BackupFailed,
            OperationStatus.Failed),
        ManifestCorruptException => (OperationErrorCode.ManifestCorrupt, OperationStatus.Failed),

        RegistryUnavailableException => (OperationErrorCode.RegistryUnavailable, OperationStatus.Failed),
        RegistryValidationException => (OperationErrorCode.RegistryInvalid, OperationStatus.Failed),

        GitUnavailableException => (OperationErrorCode.GitNotFound, OperationStatus.Failed),
        ZipInvalidException => (OperationErrorCode.ZipInvalid, OperationStatus.Failed),
        SourceAcquisitionException source => MapSourceFailure(source.Failure),

        OperationCanceledException => (OperationErrorCode.Cancelled, OperationStatus.Cancelled),
        TimeoutException => (OperationErrorCode.Timeout, OperationStatus.Failed),
        UnauthorizedAccessException => (OperationErrorCode.PermissionDenied, OperationStatus.Failed),
        IOException io when IsDiskFull(io) => (OperationErrorCode.DiskFull, OperationStatus.Failed),
        IOException => (OperationErrorCode.IoError, OperationStatus.Failed),

        _ => (OperationErrorCode.Unknown, OperationStatus.Failed),
    };

    /// <summary>
    /// Trusts a <see cref="CoreException"/>'s own code, with two corrections: a cancelled code always means a
    /// cancelled status, and a code of <see cref="OperationErrorCode.None"/> on an exception is a contradiction
    /// that becomes <see cref="OperationErrorCode.Unknown"/> rather than a failed result claiming success.
    /// </summary>
    private static (OperationErrorCode Code, OperationStatus Status) MapCore(CoreException exception) => exception.Code switch
    {
        OperationErrorCode.None => (OperationErrorCode.Unknown, OperationStatus.Failed),
        OperationErrorCode.Cancelled => (OperationErrorCode.Cancelled, OperationStatus.Cancelled),
        OperationErrorCode code => (code, OperationStatus.Failed),
    };

    /// <summary>Maps the reasons a staged source can fail with onto the codes the install pipeline branches on.</summary>
    private static (OperationErrorCode Code, OperationStatus Status) MapSourceFailure(SourceAcquisitionFailure failure) => failure switch
    {
        SourceAcquisitionFailure.InvalidSource => (OperationErrorCode.SourceInvalid, OperationStatus.Failed),
        SourceAcquisitionFailure.GitUnavailable => (OperationErrorCode.GitNotFound, OperationStatus.Failed),
        SourceAcquisitionFailure.GitFailed => (OperationErrorCode.GitFailed, OperationStatus.Failed),
        SourceAcquisitionFailure.Timeout => (OperationErrorCode.Timeout, OperationStatus.Failed),
        SourceAcquisitionFailure.Cancelled => (OperationErrorCode.Cancelled, OperationStatus.Cancelled),
        SourceAcquisitionFailure.ZipInvalid => (OperationErrorCode.ZipInvalid, OperationStatus.Failed),

        // Unspecified means the request produced no more specific reason, and every such case in the
        // acquirer is "there was nothing at that source to stage".
        _ => (OperationErrorCode.SourceNotFound, OperationStatus.Failed),
    };

    /// <summary>
    /// True when an <see cref="IOException"/> is the disk filling up rather than any other I/O failure.
    /// </summary>
    /// <remarks>
    /// Three checks, because no single one is portable. On Windows the HRESULT carries
    /// <c>ERROR_DISK_FULL</c> (0x27) or <c>ERROR_HANDLE_DISK_FULL</c> (0x70) in its low word; on Unix .NET
    /// surfaces <c>ENOSPC</c> as HRESULT 28; and a wrapped or rethrown exception can arrive with a plain
    /// message and no code at all. The message match is a fallback, not the primary signal - but a user who
    /// sees "disk full" instead of "I/O error" can act on it, which is the whole point of the code.
    /// </remarks>
    private static bool IsDiskFull(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;

        if (code is 0x27 or 0x70 or 28)
        {
            return true;
        }

        string message = exception.Message;

        return message.Contains("no space left", StringComparison.OrdinalIgnoreCase)
            || message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not enough space", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Peels a single-inner <see cref="AggregateException"/> down to the failure it wraps, which is what
    /// <c>await</c> and <c>Task.Wait</c> hand a caller that did not expect a task boundary.
    /// </summary>
    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }

    /// <summary>
    /// The message used when the exception has nothing to say: a BCL exception raised by a failed syscall
    /// often carries an empty or generic message, and an empty toast is worse than a plain one.
    /// </summary>
    /// <remarks>
    /// Deliberately free of the exception's type name. The type belongs in <see cref="Detail"/>; a toast that
    /// reads "NullReferenceException" tells the user nothing they can act on.
    /// </remarks>
    private static string FallbackMessage(OperationErrorCode code) => code switch
    {
        OperationErrorCode.Cancelled => "The operation was cancelled. Nothing was changed.",
        OperationErrorCode.DiskFull => "There is not enough free space to finish. Free some space, then try again.",
        OperationErrorCode.PermissionDenied => "Windows refused access to a file this operation needs. Check the folder's permissions, then try again.",
        _ => "The operation could not be completed. See the details for what failed.",
    };

    /// <summary>
    /// Builds the technical half: type, full message, the structured payload of the known exception types,
    /// and the stack.
    /// </summary>
    /// <remarks>
    /// Every section is present even when empty, and the whole thing is redacted once at the end by
    /// <see cref="NormalizeDetail"/>. The stack is included for every exception, not just unknown ones: the
    /// log drawer is where a user is asked to copy something into a bug report, and "which call threw" is
    /// the first question that answer has to survive.
    /// </remarks>
    private static string? Describe(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        StringBuilder builder = new();

        builder.Append("Type: ").Append(exception.GetType().FullName).Append('\n');

        if (exception is SourceAcquisitionException source)
        {
            builder.Append("Reason: ").Append(source.Failure).Append('\n');
        }

        if (exception is BackupFailedException backup)
        {
            builder.Append("Trigger: ").Append(backup.Trigger).Append('\n');
        }

        builder.Append("Message:").Append('\n').Append(exception.Message).Append('\n');

        if (exception is PathValidationException pathValidation)
        {
            builder.Append("Refused paths:").Append('\n');
            foreach (PathValidationFailure failure in pathValidation.Failures)
            {
                builder.Append("  - ").Append(failure).Append('\n');
            }
        }

        if (exception is GameDescriptorValidationException descriptor)
        {
            builder.Append("Descriptor: ").Append(descriptor.DescriptorPath).Append('\n');
            builder.Append("Problems:").Append('\n');
            foreach (string problem in descriptor.Problems)
            {
                builder.Append("  - ").Append(problem).Append('\n');
            }
        }

        if (exception is RegistryUnavailableException unavailable)
        {
            // 211 records the technical cause on the exception (its Message is the banner text the user sees),
            // so this is the only place it reaches the drawer.
            builder.Append("Registry: ").Append(unavailable.RegistryUrl).Append('\n');
            builder.Append("Diagnostic: ").Append(unavailable.Diagnostic).Append('\n');
        }

        if (exception is RegistryValidationException registry)
        {
            // The message names the problems too, but it is clamped to one toast line; this is the copy a
            // registry author fixes the file from, and 211 collects every problem precisely so a single edit
            // can resolve them all.
            builder.Append("Registry: ").Append(registry.RegistryUrl).Append('\n');
            builder.Append("Problems:").Append('\n');
            foreach (RegistryProblem problem in registry.Problems)
            {
                builder.Append("  - ").Append(problem).Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            builder.Append("Stack:").Append('\n').Append(exception.StackTrace).Append('\n');
        }

        if (exception.InnerException is not null)
        {
            builder.Append("Inner:").Append('\n').Append(Describe(exception.InnerException));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Joins the caller's detail and the exception's description, keeping both when the caller supplied one.
    /// </summary>
    private static string? Join(string? detail, string? description)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return description;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return detail;
        }

        return $"{detail.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{description}";
    }

    /// <summary>
    /// Turns arbitrary text into one toast line: redacted, stack frames dropped, line breaks collapsed to
    /// spaces, and cut at <see cref="MaxMessageLength"/> with an ellipsis when longer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Redaction runs before the cut so a token cannot be split in half by the truncation and survive as a
    /// fragment the pattern no longer recognises. The cut reserves a character for the ellipsis, so the
    /// result is never longer than the cap - a view that trusted the cap and clipped at it would eat the
    /// ellipsis instead.
    /// </para>
    /// <para>
    /// The stack-frame stop is not paranoia: an exception composed from a logged command line can carry a
    /// trace inside its message, and a toast that reads "at System.IO.File.Copy(...)" tells the user nothing
    /// while looking like a bug in the app rather than a failure it is reporting.
    /// </para>
    /// </remarks>
    private static string NormalizeMessage(string? text, string? fallback = null)
    {
        string singleLine = CollapseLines(WithoutStackFrames(SecretRedactor.Redact(text)));

        if (string.IsNullOrWhiteSpace(singleLine))
        {
            singleLine = CollapseLines(fallback ?? string.Empty);
        }

        if (singleLine.Length <= MaxMessageLength)
        {
            return singleLine;
        }

        return string.Concat(singleLine.AsSpan(0, MaxMessageLength - 1), "…");
    }

    /// <summary>Keeps the lines before the first stack frame and drops the frame and everything after it.</summary>
    private static string WithoutStackFrames(string text)
    {
        int start = 0;

        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end;

            ReadOnlySpan<char> line = text.AsSpan(start, end - start);
            ReadOnlySpan<char> trimmed = line.Trim();

            if (trimmed.StartsWith("at ", StringComparison.Ordinal)
                && (trimmed.Contains('(') || trimmed.Contains(" in ", StringComparison.Ordinal)))
            {
                return text[..start];
            }

            start = end + 1;
        }

        return text;
    }

    /// <summary>Redacts the technical half and drops it when it is blank. Keeps its line breaks.</summary>
    private static string? NormalizeDetail(string? detail)
    {
        string redacted = SecretRedactor.Redact(detail);

        redacted = redacted.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

        return redacted.Length == 0 ? null : redacted;
    }

    /// <summary>Replaces every run of whitespace - including line breaks - with a single space.</summary>
    private static string CollapseLines(string text)
    {
        StringBuilder builder = new(text.Length);
        bool pendingSpace = false;

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Copies the entries with both text fields redacted.
    /// </summary>
    /// <remarks>
    /// The entries were built by the operation as it ran, before this result existed, so they have not been
    /// through <see cref="SecretRedactor"/> unless the caller did it. Copying rather than trusting keeps the
    /// promise that nothing in a result can leak a credential, and it is why the entries are redacted twice
    /// overall - once here and once again as the log writes them.
    /// </remarks>
    private static IReadOnlyList<OperationLogEntry> Sanitize(IReadOnlyList<OperationLogEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        return
        [
            .. entries.Select(entry => entry with
            {
                Message = SecretRedactor.Redact(entry.Message),
                Detail = string.IsNullOrEmpty(entry.Detail) ? entry.Detail : SecretRedactor.Redact(entry.Detail),
            }),
        ];
    }
}
