using GamePolish.Core.Operations;

namespace GamePolish.Core.Mods;

/// <summary>
/// The steps, progress reports and technical notes one mod-state operation has produced so far.
/// </summary>
/// <remarks>
/// <para>
/// Shared by uninstall and enable/disable because both need the same three things and duplicating them is how
/// two operations end up logging at different levels for the same step. The entries are what travels with the
/// result (task 212) and what the log drawer shows; the notes are the multi-line half that only reaches
/// <see cref="OperationResult.Detail"/>.
/// </para>
/// <para>
/// The level vocabulary is the install pipeline's (<c>info</c>, <c>warn</c>, <c>error</c>) so a log filtered
/// by level means the same thing for every operation in the product.
/// </para>
/// </remarks>
internal sealed class ModRun
{
    private readonly List<OperationLogEntry> _entries = [];
    private readonly List<string> _notes = [];
    private readonly IOperationLog? _log;
    private readonly TimeProvider _clock;

    /// <summary>Initializes the run, taking its start time from the injected clock.</summary>
    /// <param name="clock">The clock behind every entry's timestamp.</param>
    /// <param name="log">The rolling log, or <see langword="null"/> when nobody keeps one (tests).</param>
    public ModRun(TimeProvider clock, IOperationLog? log)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _log = log;
        StartedAt = clock.GetUtcNow();
    }

    /// <summary>When the operation started, in UTC.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>The mod id, once it is known.</summary>
    public string? ModId { get; set; }

    /// <summary>The mod's display name, which the backup's source name must carry verbatim.</summary>
    public string? Name { get; set; }

    /// <summary>The timestamp of the snapshot taken before the first byte changed, when one was taken.</summary>
    public string? BackupTimestamp { get; set; }

    /// <summary>The steps logged so far, in order.</summary>
    public IReadOnlyList<OperationLogEntry> Entries => _entries;

    /// <summary>Adds technical text that belongs in the result's detail rather than in a toast.</summary>
    /// <param name="detail">Multi-line text; blank text is ignored.</param>
    public void Note(string detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            _notes.Add(detail);
        }
    }

    /// <summary>Logs one step, to the entries and to the rolling log when there is one.</summary>
    /// <param name="level"><c>info</c>, <c>warn</c> or <c>error</c>.</param>
    /// <param name="message">One line, already free of secrets.</param>
    /// <param name="detail">Optional multi-line technical text.</param>
    public void Entry(string level, string message, string? detail = null)
    {
        OperationLogEntry entry = new(_clock.GetUtcNow(), level, message, detail);

        _entries.Add(entry);
        _log?.Write(entry);
    }

    /// <summary>Reports progress when somebody is watching.</summary>
    /// <param name="progress">The caller's sink, or <see langword="null"/>.</param>
    /// <param name="phase">The phase name, in the pipeline's own vocabulary.</param>
    /// <param name="completed">Items finished in this phase.</param>
    /// <param name="total">Items in this phase, or <c>0</c> when the total is not knowable.</param>
    /// <param name="current">The item being worked on, when naming it helps.</param>
    public void Progress(IProgress<OperationProgress>? progress, string phase, int completed, int total, string? current) =>
        progress?.Report(new OperationProgress(phase, completed, total, current));

    /// <summary>The accumulated technical notes, joined for a result's detail.</summary>
    public string? Detail() => _notes.Count == 0 ? null : string.Join("\n", _notes);
}
