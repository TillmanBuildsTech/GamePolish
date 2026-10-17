using System.Text.Json;

using GamePolish.Cli.CommandLine;
using GamePolish.Core.Backup;
using GamePolish.Core.Games;
using GamePolish.Core.Operations;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;

namespace GamePolish.Cli.Commands;

/// <summary>
/// <c>doctor</c>: an environment report that changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is the first command to run when a user reports a problem, so it has to be safe to run on a machine
/// somebody is upset about: it creates no directory, writes no file and touches no game folder. Two consequences
/// follow from that and are worth naming, because both look like missing code:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The newest backup timestamp is read from the backup index file directly. <see cref="IBackupEngine.List"/>
/// prunes snapshot directories the index does not know about, so calling it from a report would be a write.
/// </description></item>
/// <item><description>
/// Writability is the one thing that cannot be established without writing, so the probe is a single empty
/// uniquely named file opened with <see cref="FileOptions.DeleteOnClose"/>: the OS removes it as the handle
/// closes, and a data root that does not exist is reported as such rather than created.
/// </description></item>
/// </list>
/// <para>
/// Exit <c>3</c> when anything it checks is broken, because the caller's next step is "fix the machine", not
/// "retry the operation".
/// </para>
/// </remarks>
internal static class DoctorCommands
{
    /// <summary>Reports the environment.</summary>
    /// <returns><c>0</c> healthy; <c>3</c> at least one checked fact is broken.</returns>
    internal static async Task<CommandOutcome> RunAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        List<OperationLogEntry> report = [];
        List<string> problems = [];
        OperationErrorCode? code = null;

        CheckDataRoot(composition, report, problems, ref code);
        report.Add(Rows.Line($"log file: {composition.Log.CurrentLogFilePath}"));

        if (composition.Git.IsAvailable())
        {
            report.Add(Rows.Line($"git: {await DescribeGitVersionAsync(composition.Git, cancellationToken).ConfigureAwait(false)}"));
        }
        else
        {
            report.Add(Rows.Line("git: not found on PATH"));
            problems.Add("git is not on PATH; installs and updates from a repository cannot run.");
            code ??= OperationErrorCode.GitNotFound;
        }

        CheckDescriptors(composition, report, problems, ref code);
        CheckGames(composition, report, problems, ref code);

        OperationResult result = problems.Count == 0
            ? OperationResult.Success(
                $"Environment is healthy. {composition.Games.List().Count} game(s) tracked.",
                entries: report)
            : OperationResult.Failure(
                code ?? OperationErrorCode.Unknown,
                $"{problems.Count} environment problem(s): {problems[0]}",
                detail: string.Join(Environment.NewLine, problems),
                entries: report);

        return new CommandOutcome
        {
            Result = result,
            ExitCode = problems.Count == 0 ? ExitCodes.Success : ExitCodes.Environment,
        };
    }

    /// <summary>Reports whether the data root exists and can be written.</summary>
    private static void CheckDataRoot(
        Composition composition,
        List<OperationLogEntry> report,
        List<string> problems,
        ref OperationErrorCode? code)
    {
        string root = composition.Paths.Root;
        Preflight.DataRootState state = Preflight.ProbeDataRoot(composition.Paths);

        if (!state.Exists)
        {
            report.Add(Rows.Line($"data root: {root} (not created yet)"));

            return;
        }

        if (state.Writable)
        {
            report.Add(Rows.Line($"data root: {root} (writable)"));

            return;
        }

        report.Add(Rows.Line($"data root: {root} (NOT writable)"));
        problems.Add($"The data root '{root}' is not writable.");
        code ??= OperationErrorCode.DataRootUnavailable;
    }

    /// <summary>Asks git for its version, reporting a failure instead of throwing.</summary>
    private static async Task<string> DescribeGitVersionAsync(GitRunner git, CancellationToken cancellationToken)
    {
        try
        {
            GitProcessResult result = await git
                .RunAsync(["--version"], workingDirectory: null, hardening: null, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded ? result.StandardOutput : $"(git --version exited {result.ExitCode})";
        }
        catch (CoreException exception)
        {
            // A git that answered "is available" and then failed is still worth reporting as unhealthy rather than
            // as a crash: this command's job is to describe the machine.
            return $"(version unknown: {exception.Message})";
        }
    }

    /// <summary>Reports every descriptor, its addon destination and its validation state.</summary>
    private static void CheckDescriptors(
        Composition composition,
        List<OperationLogEntry> report,
        List<string> problems,
        ref OperationErrorCode? code)
    {
        IReadOnlyList<DescriptorScan.Entry> entries = DescriptorScan.Run(composition.Descriptors);
        int invalid = entries.Count(entry => entry.Descriptor is null);

        report.Add(Rows.Line($"descriptors: {entries.Count} found, {invalid} invalid"));

        foreach (DescriptorScan.Entry entry in entries)
        {
            if (entry.Descriptor is not null)
            {
                report.Add(Rows.Line($"  {entry.Id}: ok, destination {entry.Descriptor.ModLayout.Destination}, {entry.File}"));

                continue;
            }

            report.Add(Rows.Line($"  {entry.Id}: INVALID, {entry.Problem}"));
            problems.Add($"{entry.File}: {entry.Problem}");
            code ??= OperationErrorCode.DescriptorInvalid;
        }

        if (entries.Count == 0)
        {
            problems.Add(
                $"No game descriptors were found in {string.Join(", ", composition.Descriptors.DescriptorDirectories)}.");
            code ??= OperationErrorCode.DescriptorInvalid;
        }
    }

    /// <summary>Reports each tracked game: its folder, its verify file, staging leftovers and newest backup.</summary>
    private static void CheckGames(
        Composition composition,
        List<OperationLogEntry> report,
        List<string> problems,
        ref OperationErrorCode? code)
    {
        IReadOnlyList<TrackedGame> games = composition.Games.List();

        report.Add(Rows.Line($"games: {games.Count} tracked"));

        foreach (TrackedGame game in games)
        {
            bool folderExists = Directory.Exists(game.InstallPath);
            string? verifyFile = VerifyFile(composition, game.Id);
            bool verifyExists = verifyFile is null
                || !folderExists
                || File.Exists(Path.Combine(game.InstallPath, verifyFile));

            string status = !folderExists
                ? "MISSING folder"
                : verifyExists ? "ok" : $"MISSING {verifyFile}";

            int staging = CountStaging(composition.Paths.StagingDirectory(game.Id));
            string? newest = NewestBackup(composition.Paths.BackupsDirectory(game.Id));

            report.Add(Rows.Line(
                $"  {game.Id}: {status}, {game.InstallPath}, staging {staging}, newest backup {newest ?? "none"}"));

            if (!folderExists)
            {
                problems.Add($"{game.Name}'s install folder '{game.InstallPath}' does not exist.");
                code ??= OperationErrorCode.GameInstallPathMissing;
            }
            else if (!verifyExists)
            {
                problems.Add($"{game.Name}'s install folder '{game.InstallPath}' has no {verifyFile}.");
                code ??= OperationErrorCode.GameVerifyFileMissing;
            }
        }
    }

    /// <summary>The descriptor's verify file, or <see langword="null"/> when it cannot be read.</summary>
    private static string? VerifyFile(Composition composition, string gameId)
    {
        try
        {
            return composition.Descriptors.LoadById(gameId)?.InstallDetection.VerifyFile;
        }
        catch (GameDescriptorValidationException)
        {
            // The descriptor is already reported as invalid by CheckDescriptors; a second report here would
            // double-count one problem.
            return null;
        }
    }

    /// <summary>How many entries are left under a game's staging directory.</summary>
    private static int CountStaging(string stagingDirectory) =>
        Directory.Exists(stagingDirectory) ? Directory.EnumerateDirectories(stagingDirectory).Count() : 0;

    /// <summary>
    /// Reads the newest backup timestamp out of the game's index file, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Read directly rather than through <see cref="IBackupEngine.List"/>, which prunes orphaned snapshot
    /// directories - a side effect a report must not have. A corrupt index is reported as no backups: this command
    /// describes the machine, and the index's own parse error is what <c>backup list</c> exists to surface.
    /// </remarks>
    private static string? NewestBackup(string backupsDirectory)
    {
        string indexPath = Path.Combine(backupsDirectory, BackupEngine.IndexFileName);

        try
        {
            return AtomicJson.Read<BackupIndex>(indexPath)?.Events
                .Select(entry => entry.Timestamp)
                .OrderByDescending(timestamp => timestamp, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
