using GamePolish.Cli.CommandLine;

namespace GamePolish.Cli.Commands;

/// <summary>
/// Routes a parsed command to the class that maps it onto Core.
/// </summary>
/// <remarks>
/// A switch, not a framework: the catalog is sixteen commands and this is the only place that knows which class
/// handles which. Every branch is one call, so the mapping from the work order's command list to the Core method
/// behind it is readable in one screen - and a command whose behaviour is not one call is a sign that behaviour
/// belongs in Core.
/// </remarks>
internal static class CommandDispatcher
{
    /// <summary>Runs <paramref name="command"/> against <paramref name="composition"/>.</summary>
    /// <param name="command">The parsed command.</param>
    /// <param name="composition">The constructed Core graph.</param>
    /// <param name="reporter">Where progress and the result go.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl-C; every pipeline observes it.</param>
    internal static Task<CommandOutcome> RunAsync(
        ParsedCommand command,
        Composition composition,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Spec.Name switch
        {
            "add-game" => GameCommands.AddAsync(command, composition, reporter, cancellationToken),
            "list-games" => GameCommands.ListAsync(command, composition, reporter, cancellationToken),
            "remove-game" => GameCommands.RemoveAsync(command, composition, reporter, cancellationToken),

            "descriptors" => DescriptorCommands.ListAsync(command, composition, reporter, cancellationToken),

            "list" => ModCommands.ListAsync(command, composition, reporter, cancellationToken),
            "install" => ModCommands.InstallAsync(command, composition, reporter, cancellationToken),
            "update" => ModCommands.UpdateAsync(command, composition, reporter, cancellationToken),
            "check" => ModCommands.CheckAsync(command, composition, reporter, cancellationToken),
            "uninstall" => ModCommands.UninstallAsync(command, composition, reporter, cancellationToken),
            "enable" => ModCommands.SetEnabledAsync(command, composition, reporter, enabled: true, cancellationToken),
            "disable" => ModCommands.SetEnabledAsync(command, composition, reporter, enabled: false, cancellationToken),

            "backup list" => BackupCommands.ListAsync(command, composition, reporter, cancellationToken),
            "backup restore" => BackupCommands.RestoreAsync(command, composition, reporter, cancellationToken),

            "registry refresh" => RegistryCommands.RefreshAsync(command, composition, reporter, cancellationToken),

            "doctor" => DoctorCommands.RunAsync(command, composition, reporter, cancellationToken),

            // The catalog and this switch are two halves of one table: a command advertised by --help but not wired
            // here is a bug in this file, and refusing it is better than a silent no-op that exits 0.
            _ => throw new CliUsageException($"'{command.Spec.Name}' is advertised by --help but has no handler."),
        };
    }
}
