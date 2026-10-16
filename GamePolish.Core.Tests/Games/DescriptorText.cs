namespace GamePolish.Core.Tests.Games;

/// <summary>
/// Descriptor YAML used by the descriptor tests: the shipped OctoWoW document plus a per-test way to
/// replace one block, so each rejection rule is stated in one line instead of a wall of YAML.
/// </summary>
internal sealed record DescriptorText
{
    /// <summary>The shipped descriptor verbatim, as a copy of the OctoWoW design note.</summary>
    public const string Shipped = """
        id: octowow
        name: "OctoWoW"
        install_detection:
          manual_only: true
          verify_file: "WoW.exe"
        mod_layout:
          type: addons_folder
          destination: "Interface/AddOns"
        registry_url: "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json"
        """;

    /// <summary>Replaced to break the <c>id</c> rules. Empty removes the key entirely.</summary>
    public string IdLine { get; init; } = "id: octowow";

    /// <summary>Replaced to break the <c>name</c> rules. Empty removes the key entirely.</summary>
    public string NameLine { get; init; } = "name: \"OctoWoW\"";

    /// <summary>Replaced to break the <c>install_detection</c> rules. Empty removes the block.</summary>
    public string InstallDetectionBlock { get; init; } = """
        install_detection:
          manual_only: true
          verify_file: "WoW.exe"
        """;

    public string ModLayoutBlock { get; init; } = """
        mod_layout:
          type: addons_folder
          destination: "Interface/AddOns"
        """;

    public string RegistryUrlLine { get; init; } = "registry_url: \"https://raw.githubusercontent.com/game-polish/registries/main/octowow.json\"";

    /// <summary>Extra lines appended verbatim, e.g. a reserved block.</summary>
    public string? ExtraBlock { get; init; }

    /// <summary>File name stem the descriptor is written to. The id must match it.</summary>
    public string FileStem { get; init; } = "octowow";

    public string ToYaml()
    {
        List<string> lines = [];

        lines.AddRange(Split(IdLine));
        lines.AddRange(Split(NameLine));
        lines.AddRange(Split(InstallDetectionBlock));
        lines.AddRange(Split(ModLayoutBlock));
        lines.AddRange(Split(RegistryUrlLine));

        if (ExtraBlock is not null)
        {
            lines.AddRange(Split(ExtraBlock));
        }

        return string.Join('\n', lines) + "\n";
    }

    private static IEnumerable<string> Split(string block) =>
        block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(line => line.Length > 0);
}
