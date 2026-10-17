namespace GamePolish.Cli.CommandLine;

/// <summary>
/// Every command the harness understands, declared once.
/// </summary>
/// <remarks>
/// This table is the work order's command list transcribed: one entry per line of the specification's usage
/// block, with the options that line names. It is the only place a command is described, so <c>--help</c>,
/// the parser and the dispatcher cannot drift - a command added here is reachable, and one that is not here
/// does not exist.
/// </remarks>
public static class CommandCatalog
{
    /// <summary>The commands, in the order <c>--help</c> prints them.</summary>
    public static IReadOnlyList<CommandSpec> All { get; } =
    [
        new CommandSpec
        {
            Tokens = ["add-game"],
            Positionals = ["<gameId>"],
            Options = ["path"],
            RequiredOptions = ["path"],
            Summary = "Track a game and record where it is installed (203).",
            Usage = "add-game <gameId> --path <installDir>",
        },

        new CommandSpec
        {
            Tokens = ["list-games"],
            Summary = "List tracked games, marking any whose install path no longer resolves (203).",
            Usage = "list-games",
        },

        new CommandSpec
        {
            Tokens = ["remove-game"],
            Positionals = ["<gameId>"],
            Summary = "Untrack a game. Mods, manifests and backups are left on disk (203).",
            Usage = "remove-game <gameId>",
        },

        new CommandSpec
        {
            Tokens = ["descriptors"],
            Summary = "List every loadable game descriptor, its addon destination and any validation error (202).",
            Usage = "descriptors",
        },

        new CommandSpec
        {
            Tokens = ["list"],
            Options = ["game"],
            RequiredOptions = ["game"],
            Summary = "List a game's installed mods with enabled state and version (206).",
            Usage = "list --game <gameId>",
        },

        new CommandSpec
        {
            Tokens = ["install"],
            Options = ["game", "source", "branch", "name", "mod", "zip"],
            RequiredOptions = ["game"],
            Flags = ["overwrite"],
            ExclusiveGroups = [["source", "zip"]],

            // --overwrite only means something for a git source: a ZIP install is of a folder the manifest has
            // never seen, so the parser is where "you asked for two different things" is refused.
            Summary = "Install a mod from a GitHub repository or a local ZIP (208).",
            Usage = "install --game <gameId> (--source <repoUrl> [--branch <name>] | --zip <path>) "
                + "[--name <display>] [--mod <id>] [--overwrite]",
        },

        new CommandSpec
        {
            Tokens = ["update"],
            Options = ["game", "mod"],
            RequiredOptions = ["game", "mod"],
            Summary = "Replace an installed mod with what its source serves now (209).",
            Usage = "update --game <gameId> --mod <id>",
        },

        new CommandSpec
        {
            Tokens = ["check"],
            Options = ["game", "mod"],
            RequiredOptions = ["game"],
            Summary = "Print the update state per installed mod, or for one mod, without changing anything (209).",
            Usage = "check --game <gameId> [--mod <id>]",
        },

        new CommandSpec
        {
            Tokens = ["uninstall"],
            Options = ["game", "mod"],
            RequiredOptions = ["game", "mod"],
            Summary = "Remove an installed mod and its manifest entry, after a backup (210).",
            Usage = "uninstall --game <gameId> --mod <id>",
        },

        new CommandSpec
        {
            Tokens = ["enable"],
            Options = ["game", "mod"],
            RequiredOptions = ["game", "mod"],
            Summary = "Put a disabled mod's files back into the game folder, after a backup (210).",
            Usage = "enable --game <gameId> --mod <id>",
        },

        new CommandSpec
        {
            Tokens = ["disable"],
            Options = ["game", "mod"],
            RequiredOptions = ["game", "mod"],
            Summary = "Park a mod's files under the data root, after a backup (210).",
            Usage = "disable --game <gameId> --mod <id>",
        },

        new CommandSpec
        {
            Tokens = ["backup", "list"],
            Options = ["game"],
            RequiredOptions = ["game"],
            Summary = "List a game's backups, newest first: timestamp, trigger, source, files and size (205).",
            Usage = "backup list --game <gameId>",
        },

        new CommandSpec
        {
            Tokens = ["backup", "restore"],
            Options = ["game", "timestamp"],
            RequiredOptions = ["game", "timestamp"],
            Summary = "Restore a backup, after snapshotting what is there now (205).",
            Usage = "backup restore --game <gameId> --timestamp <ts>",
        },

        new CommandSpec
        {
            Tokens = ["registry", "refresh"],
            Options = ["game"],
            RequiredOptions = ["game"],
            Summary = "Force a registry refresh and print the mod count and last fetch time (211).",
            Usage = "registry refresh --game <gameId>",
        },

        new CommandSpec
        {
            Tokens = ["doctor"],
            Summary = "Report the environment without changing it. Exits 3 when something it checks is broken.",
            Usage = "doctor",
        },
    ];
}
