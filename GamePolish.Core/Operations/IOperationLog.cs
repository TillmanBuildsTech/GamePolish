namespace GamePolish.Core.Operations;

/// <summary>
/// The rolling file log under the data root: every operation writes its steps here, redacted.
/// </summary>
/// <remarks>
/// <para>
/// One interface because the log has exactly one production implementation and one shape of consumer: a
/// pipeline logs as it works, and the log drawer (task 307) and <c>doctor</c> (task 213) read the files.
/// Depending on the interface rather than <see cref="FileOperationLog"/> is what lets a pipeline test assert
/// what it logged without a file, and it keeps the file layout a detail of one class.
/// </para>
/// <para>
/// <b>Writing a log line never fails the operation that is logging.</b> That is a promise of the
/// implementation, not a suggestion: an install that fails because the log directory was deleted is a worse
/// product than an install with a gap in its log. Callers may therefore write freely while unwinding a
/// failure.
/// </para>
/// </remarks>
public interface IOperationLog
{
    /// <summary>The file today's lines go to, <c>&lt;data root&gt;/logs/game-polish-yyyyMMdd.log</c>.</summary>
    /// <remarks>
    /// Reading it does not create the directory or the file, so a view can show the path before the first
    /// line exists. It is today's file at the moment of the call; a log held across midnight returns the new
    /// day's path.
    /// </remarks>
    string CurrentLogFilePath { get; }

    /// <summary>Writes <paramref name="entry"/>, redacted, to today's file. Drops it when below the level gate.</summary>
    /// <param name="entry">The step to record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    void Write(OperationLogEntry entry);

    /// <summary>Writes one line to today's file, building the entry from the clock.</summary>
    /// <param name="level"><c>debug</c>, <c>info</c>, <c>warn</c> or <c>error</c>; anything else is treated as <c>info</c>.</param>
    /// <param name="message">One line. Line breaks are folded into it rather than splitting the line.</param>
    /// <param name="detail">Optional technical text, written as indented continuation lines.</param>
    void Write(string level, string message, string? detail = null);

    /// <summary>Deletes log files whose UTC day is older than <paramref name="age"/>, leaving today's.</summary>
    /// <param name="age">Retention window, e.g. 14 days.</param>
    /// <remarks>
    /// Called at startup. Failures to delete are swallowed for the same reason failures to write are: housekeeping
    /// must not stop the app from starting, and the next launch tries again.
    /// </remarks>
    void PurgeOlderThan(TimeSpan age);
}
