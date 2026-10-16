namespace GamePolish.Core.Backup;

/// <summary>One recorded backup: the snapshot directory's name, what caused it, and what is in it.</summary>
public sealed record BackupEvent
{
    /// <summary>
    /// The snapshot directory's name, and the key of this event, e.g. <c>20261015T214203123Z</c>.
    /// <c>yyyyMMddTHHmmssfffZ</c>, so lexicographic order is chronological order; a collision inside one
    /// millisecond is disambiguated with a <c>-2</c>, <c>-3</c>, ... suffix.
    /// </summary>
    public required string Timestamp { get; init; }

    /// <summary>Real time the backup was taken, UTC.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>What caused the backup.</summary>
    public required BackupTrigger Trigger { get; init; }

    /// <summary>The mod name, or the settings file name, this backup protects.</summary>
    public required string SourceName { get; init; }

    /// <summary>Version transition for an update, e.g. <c>v5.1 -> v5.2</c>.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Every file the operation would touch, whether or not it existed at backup time.</summary>
    public required IReadOnlyList<BackupFileEntry> Files { get; init; }

    /// <summary>Sum of <see cref="BackupFileEntry.SizeBytes"/>: what the snapshot holds.</summary>
    public required long TotalSizeBytes { get; init; }

    /// <summary>Free-text note, e.g. <c>pre-restore of 20261015T214203123Z</c>.</summary>
    public string? Note { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Timestamp} {Trigger} {SourceName} ({Files.Count} file(s))";
}
