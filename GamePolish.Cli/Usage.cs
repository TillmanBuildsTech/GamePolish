namespace GamePolish.Cli;

/// <summary>
/// The <c>--help</c> text: every command, its arguments, and the exit-code table.
/// </summary>
/// <remarks>
/// The exit-code table is part of the contract rather than a courtesy: <c>405</c>'s acceptance script branches
/// on those five numbers, so a reader of <c>--help</c> must be able to write the same script. The text is built
/// from <see cref="CommandLine.CommandCatalog"/>, so a command that cannot be dispatched cannot be advertised
/// either.
/// </remarks>
public static class Usage
{
    /// <summary>Writes the usage text to <paramref name="writer"/>.</summary>
    /// <param name="writer">Usually stdout for <c>--help</c> and stderr for a usage error.</param>
    public static void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("GamePolish.Cli - development smoke harness for GamePolish.Core. Never shipped.");
        writer.WriteLine();
        writer.WriteLine("usage:");
        writer.WriteLine("  GamePolish.Cli [--data-root <path>] [--json] [--log-level <debug|information|warn|error>] <command> [args]");
        writer.WriteLine();
        writer.WriteLine("commands:");

        foreach (CommandLine.CommandSpec spec in CommandLine.CommandCatalog.All)
        {
            writer.WriteLine($"  {spec.Usage}");
            writer.WriteLine($"      {spec.Summary}");
        }

        writer.WriteLine();
        writer.WriteLine("exit codes:");
        writer.WriteLine($"  {ExitCodes.Success,-3} the command succeeded");
        writer.WriteLine($"  {ExitCodes.Failure,-3} the operation failed; the result's code says why");
        writer.WriteLine($"  {ExitCodes.Usage,-3} usage error: unknown command, missing, duplicate or contradictory argument");
        writer.WriteLine($"  {ExitCodes.Environment,-3} environment error before the operation started: data root not writable,");
        writer.WriteLine("      git not on PATH, game not tracked, or the game's install folder missing");
        writer.WriteLine($"  {ExitCodes.Cancelled,-3} cancelled with Ctrl-C; Core rolled back, so nothing is half-written");
        writer.WriteLine();
        writer.WriteLine("global options:");
        writer.WriteLine("  --data-root <path>  where the engine's files live. Precedence: this flag, then");
        writer.WriteLine("                      GAMEPOLISH_DATA_ROOT, then <user profile>/.game-polish. The resolved");
        writer.WriteLine("                      root is always the first line of output (stderr when --json is set).");
        writer.WriteLine("  --json              print the OperationResult as snake_case JSON on stdout and nothing");
        writer.WriteLine("                      else. Diagnostics and progress go to stderr.");
        writer.WriteLine("  --log-level <level> minimum level for the rolling log. GAMEPOLISH_LOG_LEVEL wins over it,");
        writer.WriteLine("                      as it does for the app. debug also prints a failure's detail.");
        writer.WriteLine("  --help, -h          this text.");
    }
}
