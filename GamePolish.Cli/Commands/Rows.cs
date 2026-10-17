using System.Globalization;

using GamePolish.Core.Operations;

namespace GamePolish.Cli.Commands;

/// <summary>
/// Builds the row lines a command reports, so every listing looks the same and every row is also the JSON
/// payload.
/// </summary>
/// <remarks>
/// <para>
/// A row is an <see cref="OperationLogEntry"/>, which is the one shape Core already has for "a fact with a
/// technical half". That is deliberate: a listing carries its rows in
/// <see cref="OperationResult.Entries"/>, so <c>--json</c> stays a single <c>OperationResult</c> document - as the
/// work order requires - while <c>405</c> can still read one row per entry instead of parsing a table out of a
/// message.
/// </para>
/// <para>
/// Columns are padded per table rather than per row, which is the minimum that makes a pasted transcript
/// readable, and no colour or box drawing is ever emitted: a redirected stdout must stay plain text.
/// </para>
/// </remarks>
internal static class Rows
{
    /// <summary>One fact, timestamped now, at info level.</summary>
    /// <param name="message">The line, already formatted.</param>
    /// <param name="detail">Optional technical text.</param>
    public static OperationLogEntry Line(string message, string? detail = null) =>
        new(DateTimeOffset.UtcNow, "info", message, detail);

    /// <summary>Formats a table of cells into one padded line per row.</summary>
    /// <param name="cells">The rows, each row being its columns in order.</param>
    /// <remarks>
    /// The last column is never padded, so no line carries trailing whitespace - which matters because a
    /// transcript is pasted into a pull request and a diff of trailing spaces is noise.
    /// </remarks>
    public static IReadOnlyList<OperationLogEntry> Table(IReadOnlyList<IReadOnlyList<string>> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count == 0)
        {
            return [];
        }

        int columnCount = cells.Max(row => row.Count);
        int[] widths = new int[columnCount];

        foreach (IReadOnlyList<string> row in cells)
        {
            for (int column = 0; column < row.Count; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        List<OperationLogEntry> lines = [];

        foreach (IReadOnlyList<string> row in cells)
        {
            IEnumerable<string> padded = row.Select(
                (cell, column) => column == row.Count - 1 ? cell : cell.PadRight(widths[column]));

            lines.Add(Line(string.Join("  ", padded).TrimEnd()));
        }

        return lines;
    }

    /// <summary>Formats a byte count the way a transcript should read: <c>512 B</c>, <c>3.4 KiB</c>.</summary>
    /// <param name="bytes">The size.</param>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB"),
    };
}
