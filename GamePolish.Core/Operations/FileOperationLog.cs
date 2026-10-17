using System.Globalization;
using System.Text;
using System.Text.Json;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Operations;

/// <summary>
/// The rolling file log: one append-only file per UTC day, under the data root, every line redacted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it lives.</b> <c>&lt;data root&gt;/logs/game-polish-yyyyMMdd.log</c> - never a game directory,
/// never the user profile, never a temp folder (invariant #4). The directory is created on the first write and
/// not before, so a read-only session leaves no trace.
/// </para>
/// <para>
/// <b>Why a file per day and not a rotating pair.</b> A user reporting a bug is asked to attach "the log from
/// when it happened", and a dated name answers that without a viewer: the day is in the file name, and no line
/// can be lost to a rotation that happens to run between two writes. Sizing is handled by retention
/// (<see cref="PurgeOlderThan"/>) rather than by size, because a size-capped file has to drop the oldest lines
/// exactly when the interesting failure just happened.
/// </para>
/// <para>
/// <b>Line shape.</b> A fixed-width timestamp, a fixed-width level and the message:
/// <c>2026-10-15T21:42:03.123Z INFO  Source staged from https://github.com/...</c>. The level is padded to five
/// characters so a human scanning a column of lines sees the loud ones, and so a parser can split on the first
/// three spaces without a regex. Detail follows as indented continuation lines, which a parser can attribute to
/// the line above by their indentation.
/// </para>
/// <para>
/// <b>Never fails the caller.</b> Every filesystem call is wrapped: a missing, read-only or locked log
/// directory drops the line and returns. The one thing this class must never do is turn a successful install
/// into a failed one because the log could not be written - a missing log line is a diagnostic gap, a failed
/// install is a broken product.
/// </para>
/// <para>
/// <b>Level gate.</b> The minimum level comes from <c>app-settings.json</c>'s <c>LogLevel</c> (read by
/// <see cref="FromSettings"/>) or an explicit override, and the
/// <see cref="LogLevelEnvironmentVariable"/> environment variable wins over both when it is set - that is how the
/// CLI and the tests turn volume up without editing the user's settings. An unrecognised value is ignored rather
/// than throwing: a typo in an environment variable must not stop the app from starting.
/// </para>
/// </remarks>
public sealed class FileOperationLog : IOperationLog
{
    /// <summary>Environment variable that overrides the configured minimum level.</summary>
    /// <remarks>
    /// Accepted values: <c>debug</c>, <c>information</c> (or <c>info</c>), <c>warn</c> (or <c>warning</c>),
    /// <c>error</c>. Set it to <c>debug</c> to reproduce a bug with a full trail.
    /// </remarks>
    public const string LogLevelEnvironmentVariable = "GAMEPOLISH_LOG_LEVEL";

    /// <summary>How long log files are kept before <see cref="PurgeOlderThan"/> removes them.</summary>
    /// <remarks>
    /// Fourteen days: long enough that "it happened last week" is still diagnosable, short enough that a busy
    /// user's log directory stays under a few megabytes.
    /// </remarks>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

    private const string FileNamePrefix = "game-polish-";
    private const string FileNameSuffix = ".log";
    private const string DayFormat = "yyyyMMdd";
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    private const string DetailIndent = "    ";
    private const int LevelColumnWidth = 5;
    private const string InformationLevel = "information";
    private const int InformationRank = 1;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GamePolishPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly int _minimumRank;

    /// <summary>Serialises appends within this process so two threads cannot interleave half a line.</summary>
    /// <remarks>
    /// Cross-process appends are not coordinated: one process per data root is the MVP assumption (tasks 203
    /// and 206 make the same one), and an advisory lock file would introduce a stale-lock failure mode that
    /// outlives a crash - exactly the kind of thing this class must never do to an operation.
    /// </remarks>
    private readonly object _writeGate = new();

    /// <summary>Initializes a log over the data root's <c>logs</c> directory.</summary>
    /// <param name="paths">The resolved data root; the log writes only inside <see cref="GamePolishPaths.LogsDirectory"/>.</param>
    /// <param name="timeProvider">The clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logLevel">
    /// The configured minimum level, typically <see cref="AppSettings.LogLevel"/>. Null falls back to
    /// <c>information</c>. The environment variable still wins.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public FileOperationLog(GamePolishPaths paths, TimeProvider? timeProvider = null, string? logLevel = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minimumRank = TryRank(ResolveLevel(logLevel), out int rank) ? rank : InformationRank;
    }

    /// <summary>
    /// Creates a log whose level comes from the data root's <c>app-settings.json</c>, falling back to
    /// <c>information</c> when the file is absent or unreadable.
    /// </summary>
    /// <param name="paths">The resolved data root.</param>
    /// <param name="timeProvider">The clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <remarks>
    /// This is the constructor the app and the CLI use. A corrupt settings file must not cost the user their
    /// log, so the parse failure is swallowed here and the level falls back to the default - the settings
    /// file is the user's preference, not a precondition for logging.
    /// </remarks>
    public static FileOperationLog FromSettings(GamePolishPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string? configured = null;

        try
        {
            configured = new AppSettingsStore(paths).Load().LogLevel;
        }
        catch (JsonException)
        {
            // Unreadable preferences fall back to the default level rather than to no logging at all.
        }

        return new FileOperationLog(paths, timeProvider, configured);
    }

    /// <inheritdoc/>
    public string CurrentLogFilePath => PathFor(_timeProvider.GetUtcNow());

    /// <inheritdoc/>
    public void Write(string level, string message, string? detail = null) =>
        Write(new OperationLogEntry(_timeProvider.GetUtcNow(), level, message, detail));

    /// <inheritdoc/>
    public void Write(OperationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        int rank = TryRank(entry.Level, out int parsedRank) ? parsedRank : InformationRank;

        if (rank < _minimumRank)
        {
            return;
        }

        string line = ComposeLine(entry);

        try
        {
            lock (_writeGate)
            {
                Directory.CreateDirectory(_paths.LogsDirectory);

                // Append, not write: a log line is never the only content of a file, and rewriting the day's
                // file would lose everything written before this process started.
                File.AppendAllText(CurrentLogFilePath, line, Utf8WithoutBom);
            }
        }
        catch (IOException)
        {
            // A missing, locked or read-only log directory drops this line; the operation carries on. See the
            // class remarks: a log that breaks installs is worse than a missing log line.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <inheritdoc/>
    public void PurgeOlderThan(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(age), age, "The retention age cannot be negative.");
        }

        string directory = _paths.LogsDirectory;

        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - age;

        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            files = Directory.GetFiles(directory, FileNamePrefix + "*" + FileNameSuffix);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (string file in files)
        {
            // The date in the name is the file's day, not its last-write time: a restored or copied log would
            // otherwise look fresh and be kept forever. A name that does not parse is left alone - this method
            // deletes only files it is certain it wrote.
            if (!TryReadDay(file, out DateTimeOffset day) || day >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Locked by a reader, usually the log drawer: try again next startup.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    /// <summary>Builds one logical line: the header, then the detail's indented continuation lines.</summary>
    /// <remarks>
    /// Newlines are written as <c>\n</c> on every platform so a log copied from Windows to a bug report reads
    /// the same as one from macOS, and so a naive line-splitting parser is not surprised by <c>\r</c>.
    /// </remarks>
    private static string ComposeLine(OperationLogEntry entry)
    {
        StringBuilder builder = new();

        builder.Append(entry.At.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(LevelName(entry.Level).PadRight(LevelColumnWidth));
        builder.Append(' ');
        builder.Append(FoldLines(SecretRedactor.Redact(entry.Message)));
        builder.Append('\n');

        string detail = SecretRedactor.Redact(entry.Detail);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            foreach (string line in NormalizeNewlines(detail).Split('\n'))
            {
                builder.Append(DetailIndent).Append(line).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Resolves the minimum level: environment variable first, then the configured value, then information.</summary>
    private static string ResolveLevel(string? configuredLevel)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(LogLevelEnvironmentVariable);

        if (TryRank(fromEnvironment, out _))
        {
            return fromEnvironment!;
        }

        return TryRank(configuredLevel, out _) ? configuredLevel! : InformationLevel;
    }

    /// <summary>Maps a level spelling to its rank, where a higher rank is louder.</summary>
    /// <returns>False for an unknown spelling; <paramref name="rank"/> is then 1 (information).</returns>
    /// <remarks>
    /// Synonyms are accepted because the setting is typed by a person and the environment variable is typed
    /// into a shell: <c>info</c> for <c>information</c>, <c>warning</c> for <c>warn</c>. Anything unrecognised
    /// ranks as information rather than throwing or silencing the line.
    /// </remarks>
    private static bool TryRank(string? level, out int rank)
    {
        switch (level?.Trim().ToLowerInvariant())
        {
            case "debug":
            case "verbose":
                rank = 0;
                return true;

            case "information":
            case "info":
                rank = InformationRank;
                return true;

            case "warn":
            case "warning":
                rank = 2;
                return true;

            case "error":
            case "fatal":
            case "critical":
                rank = 3;
                return true;

            default:
                rank = 1;
                return false;
        }
    }

    /// <summary>The five-character column name for a level; unknown levels print as <c>INFO</c>.</summary>
    private static string LevelName(string? level) => TryRank(level, out int rank)
        ? rank switch
        {
            0 => "DEBUG",
            2 => "WARN",
            3 => "ERROR",
            _ => "INFO",
        }
        : "INFO";

    /// <summary>Collapses line breaks inside a message so one entry is always one header line.</summary>
    private static string FoldLines(string text) => NormalizeNewlines(text).Replace('\n', ' ');

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>The file for the UTC day containing <paramref name="at"/>.</summary>
    private string PathFor(DateTimeOffset at) =>
        Path.Combine(
            _paths.LogsDirectory,
            FileNamePrefix + at.UtcDateTime.ToString(DayFormat, CultureInfo.InvariantCulture) + FileNameSuffix);

    /// <summary>Reads the UTC day out of a file name, or returns false when the name is not this log's shape.</summary>
    private static bool TryReadDay(string file, out DateTimeOffset day)
    {
        day = default;

        string name = Path.GetFileNameWithoutExtension(file);

        if (!name.StartsWith(FileNamePrefix, StringComparison.Ordinal)
            || name.Length != FileNamePrefix.Length + DayFormat.Length)
        {
            return false;
        }

        string digits = name[FileNamePrefix.Length..];

        if (!DateTime.TryParseExact(
                digits,
                DayFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTime parsed))
        {
            return false;
        }

        day = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));

        return true;
    }
}
