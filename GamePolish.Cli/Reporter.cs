using System.Text.Json;

using GamePolish.Core.Operations;
using GamePolish.Core.Storage;

namespace GamePolish.Cli;

/// <summary>
/// Everything the harness prints: the resolved data root, per-phase progress, and the result.
/// </summary>
/// <remarks>
/// <para>
/// One class owns both streams so the <c>--json</c> promise holds by construction rather than by every command
/// remembering it: in JSON mode stdout carries exactly one JSON document and every other line - the data root,
/// progress, notes - goes to stderr. A pipeline that suddenly writes a progress line therefore cannot corrupt a
/// document <c>405</c> is parsing.
/// </para>
/// <para>
/// The result is serialised with <see cref="AtomicJson.Options"/> rather than a private set of options, so
/// <c>--json</c> spells its keys the way every file the engine writes does: <c>snake_case</c>, enums by name,
/// nulls omitted. That is what makes <c>code: "git_failed"</c> the same string in a transcript, a log line and a
/// manifest.
/// </para>
/// <para>
/// Progress is written synchronously (<see cref="IProgress{T}"/> implemented directly, not
/// <see cref="Progress{T}"/>): a console app has no synchronization context, so the callback-based type would
/// queue the lines on the thread pool and let them arrive after the result they belong to.
/// </para>
/// </remarks>
public sealed class Reporter : IProgress<OperationProgress>
{
    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly bool _json;
    private readonly bool _debug;
    private readonly object _gate = new();

    /// <summary>Initializes the reporter.</summary>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error: diagnostics, progress in JSON mode, and the detail of a failure.</param>
    /// <param name="json">True when <c>--json</c> was given.</param>
    /// <param name="debug">True when <c>--log-level debug</c> was given, which prints a failure's detail.</param>
    public Reporter(TextWriter output, TextWriter error, bool json, bool debug)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _out = output;
        _error = error;
        _json = json;
        _debug = debug;
    }

    /// <summary>
    /// Prints the resolved data root, first, before anything else runs.
    /// </summary>
    /// <param name="root">The absolute root <c>GamePolishPaths.Resolve</c> produced.</param>
    /// <remarks>
    /// This line is the proof-of-root the work order asks for: a pasted transcript that does not say which root it
    /// ran against cannot be audited. Under <c>--json</c> it goes to stderr, because stdout is reserved for the
    /// result document.
    /// </remarks>
    public void WriteDataRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        WriteLine(_json ? _error : _out, $"data root: {root}");
    }

    /// <summary>Prints the operation's result: a JSON document, or the message plus its rows.</summary>
    /// <param name="result">What the command produced.</param>
    public void WriteResult(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (_json)
        {
            WriteLine(_out, JsonSerializer.Serialize(result, AtomicJson.Options));

            return;
        }

        WriteLine(_out, result.Message);

        foreach (OperationLogEntry entry in result.Entries)
        {
            WriteLine(_out, entry.Message);
        }

        // The detail is the technical half: exception type, stack, every refused path. It is printed only when
        // the run asked for debug, which is what keeps an acceptance transcript readable while still making a
        // field report complete.
        if (_debug && !string.IsNullOrWhiteSpace(result.Detail))
        {
            WriteLine(_error, result.Detail);
        }
    }

    /// <summary>Prints one progress phase as a plain line.</summary>
    /// <param name="value">The phase, its counts and the current item.</param>
    /// <remarks>
    /// No bar, no colour, no carriage returns: this is the phase list a transcript should contain, and a
    /// redirected stdout must not fill with escape sequences.
    /// </remarks>
    public void Report(OperationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string counts = value.Total > 0 ? $" {value.Completed}/{value.Total}" : string.Empty;
        string item = string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : $" {value.CurrentItem}";

        WriteLine(_json ? _error : _out, $"  [{value.Phase}]{counts}{item}");
    }

    /// <summary>Writes one line, serialised against concurrent progress reports.</summary>
    private void WriteLine(TextWriter writer, string line)
    {
        lock (_gate)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }
}
