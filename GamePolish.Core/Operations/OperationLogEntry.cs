namespace GamePolish.Core.Operations;

/// <summary>One line of an operation's history: what happened, when, and how loudly to report it.</summary>
/// <param name="At">When the line was produced, as an absolute instant. Stored as UTC in the log file.</param>
/// <param name="Level">
/// <c>info</c>, <c>warn</c> or <c>error</c>. Lowercase because it is written verbatim into the log's
/// fixed-width level column and read back by hand when a user pastes a log into an issue. <c>debug</c> is
/// accepted too, and only ever reaches the file when <c>GAMEPOLISH_LOG_LEVEL=debug</c> raised the gate.
/// </param>
/// <param name="Message">One line, already free of secrets. The log adds its own redaction pass as well.</param>
/// <param name="Detail">Optional multi-line technical text: the paths touched, the command that failed, a stack.</param>
/// <remarks>
/// <para>
/// The entry is the unit the log drawer (task 307) renders and the unit <see cref="OperationResult.Entries"/>
/// carries, so a result can be shown as "here is every step, in order" without re-reading the log file.
/// It is deliberately a record with only text in it: it goes to disk, survives a restart, and its
/// shape is what a future log parser reads.
/// </para>
/// <para>
/// <see cref="Detail"/> may legitimately contain absolute paths - it is the technical half - but it never
/// contains a credential: <see cref="SecretRedactor"/> is applied both when the entry is put into a
/// result and again when the log writes it, because two independent passes are cheaper than one missed one.
/// </para>
/// </remarks>
public sealed record OperationLogEntry(DateTimeOffset At, string Level, string Message, string? Detail);
