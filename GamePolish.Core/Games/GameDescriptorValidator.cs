using System.Text.RegularExpressions;

namespace GamePolish.Core.Games;

/// <summary>
/// Shape and safety rules for a deserialized <see cref="GameDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure inspection: no file access, no writes. It runs on the record graph after YAML mapping, which
/// is why it, and not the parser, owns the error messages - the parser's options are deliberately
/// left lenient (see <see cref="GameDescriptorLoader"/>) so that a missing key becomes a named
/// problem instead of a library exception.
/// </para>
/// <para>
/// Every problem string starts with the descriptor path and names the key, the value found and what
/// was expected: a message like "invalid descriptor" tells an author nothing about which of the keys
/// is wrong.
/// </para>
/// </remarks>
public static class GameDescriptorValidator
{
    /// <summary>Layout kind where mods are one folder per mod inside a destination folder.</summary>
    public const string AddonsFolderLayout = "addons_folder";

    /// <summary>Longest accepted <c>id</c>.</summary>
    public const int MaximumIdLength = 64;

    /// <summary>Layout kinds this build understands. A new kind is added here, not special-cased.</summary>
    public static readonly IReadOnlyList<string> SupportedModLayoutTypes = [AddonsFolderLayout];

    /// <summary>Settings file formats the settings editor will support (task 601).</summary>
    public static readonly IReadOnlyList<string> SupportedSettingsFormats = ["ini", "toml", "json"];

    private const string IdPattern = "^[a-z0-9][a-z0-9-]*$";

    private static readonly Regex IdFormat = new(IdPattern, RegexOptions.CultureInvariant);

    private const string DestinationExpectation =
        "a relative path using '/' separators, with no '.' or '..' segment, no empty segment, "
        + "no leading or trailing separator and no drive or share prefix (for example 'Interface/mods')";

    /// <summary>Validates a deserialized descriptor.</summary>
    /// <param name="descriptor">The mapped descriptor, or <see langword="null"/> for an empty file.</param>
    /// <param name="descriptorPath">Absolute path of the file, quoted in every problem.</param>
    /// <param name="fileNameStem">
    /// The file name without its extension, when the descriptor came from a file: the <c>id</c> must
    /// equal it, otherwise two files could claim the same game.
    /// </param>
    /// <returns>Every problem found; empty when the descriptor is usable.</returns>
    public static IReadOnlyList<string> Validate(
        GameDescriptor? descriptor,
        string descriptorPath,
        string? fileNameStem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);

        List<string> problems = [];

        if (descriptor is null)
        {
            problems.Add(
                $"{descriptorPath}: descriptor: expected a YAML mapping with id, name, install_detection, "
                + "mod_layout and registry_url, but the file is empty.");

            return problems;
        }

        ValidateId(descriptor.Id, descriptorPath, fileNameStem, problems);
        ValidateName(descriptor.Name, descriptorPath, problems);
        ValidateInstallDetection(descriptor.InstallDetection, descriptorPath, problems);
        ValidateModLayout(descriptor.ModLayout, descriptorPath, problems);
        ValidateRegistryUrl(descriptor.RegistryUrl, descriptorPath, problems);
        ValidateSettingsFiles(descriptor.SettingsFiles, descriptorPath, problems);

        return problems;
    }

    private static void ValidateId(string? id, string descriptorPath, string? fileNameStem, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            problems.Add(
                $"{descriptorPath}: id: expected a lowercase id matching '{IdPattern}' of at most "
                + $"{MaximumIdLength} characters, but the key is missing.");
        }
        else if (id.Length > MaximumIdLength)
        {
            problems.Add(
                $"{descriptorPath}: id: expected at most {MaximumIdLength} characters, but found a "
                + $"{id.Length}-character id ('{id[..MaximumIdLength]}...').");
        }
        else if (!IdFormat.IsMatch(id))
        {
            problems.Add(
                $"{descriptorPath}: id: expected a lowercase id matching '{IdPattern}' (letters, digits "
                + $"and '-' only), but found '{id}'.");
        }

        if (fileNameStem is not null && !string.IsNullOrWhiteSpace(id) && !string.Equals(fileNameStem, id, StringComparison.Ordinal))
        {
            problems.Add(
                $"{descriptorPath}: id: the file is named '{fileNameStem}.yaml', so it must declare "
                + $"'id: {fileNameStem}', but found '{id}'.");
        }
    }

    private static void ValidateName(string? name, string descriptorPath, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{descriptorPath}: name: expected a non-empty display name, but the key is missing or blank.");
        }
    }

    private static void ValidateInstallDetection(InstallDetection? detection, string descriptorPath, List<string> problems)
    {
        if (detection is null)
        {
            problems.Add(
                $"{descriptorPath}: install_detection: expected a block with manual_only (and verify_file "
                + "when manual_only is true), but the key is missing.");

            return;
        }

        if (detection.ManualOnly is null)
        {
            problems.Add($"{descriptorPath}: install_detection.manual_only: expected true or false, but the key is missing.");

            return;
        }

        if (detection.ManualOnly == true && string.IsNullOrWhiteSpace(detection.VerifyFile))
        {
            problems.Add(
                $"{descriptorPath}: install_detection.verify_file: expected the file name to look for in the "
                + "game root (for example 'WoW.exe') when manual_only is true, but the key is missing.");
        }
    }

    private static void ValidateModLayout(ModLayout? layout, string descriptorPath, List<string> problems)
    {
        if (layout is null)
        {
            problems.Add($"{descriptorPath}: mod_layout: expected a block with type and destination, but the key is missing.");

            return;
        }

        string supported = string.Join(", ", SupportedModLayoutTypes);

        if (string.IsNullOrWhiteSpace(layout.Type))
        {
            problems.Add($"{descriptorPath}: mod_layout.type: expected one of {supported}, but the key is missing.");
        }
        else if (!SupportedModLayoutTypes.Contains(layout.Type, StringComparer.Ordinal))
        {
            problems.Add($"{descriptorPath}: mod_layout.type: expected one of {supported}, but found '{layout.Type}'.");
        }

        if (string.IsNullOrWhiteSpace(layout.Destination))
        {
            problems.Add($"{descriptorPath}: mod_layout.destination: expected {DestinationExpectation}, but the key is missing.");

            return;
        }

        string? complaint = DescribeDestinationProblem(layout.Destination);
        if (complaint is not null)
        {
            problems.Add(
                $"{descriptorPath}: mod_layout.destination: expected {DestinationExpectation}, but found "
                + $"'{layout.Destination}' ({complaint}).");
        }
    }

    private static void ValidateRegistryUrl(string? registryUrl, string descriptorPath, List<string> problems)
    {
        const string expectation = "an absolute 'https://' URL to the game's registry JSON";

        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            problems.Add($"{descriptorPath}: registry_url: expected {expectation}, but the key is missing.");

            return;
        }

        bool isAbsoluteHttps = Uri.TryCreate(registryUrl, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps;

        if (!isAbsoluteHttps)
        {
            problems.Add($"{descriptorPath}: registry_url: expected {expectation}, but found '{registryUrl}'.");
        }
    }

    private static void ValidateSettingsFiles(
        IReadOnlyList<SettingsFileDescriptor>? settingsFiles,
        string descriptorPath,
        List<string> problems)
    {
        if (settingsFiles is null)
        {
            return;
        }

        string supported = string.Join(", ", SupportedSettingsFormats);

        for (int index = 0; index < settingsFiles.Count; index++)
        {
            SettingsFileDescriptor? file = settingsFiles[index];

            if (file is null)
            {
                problems.Add($"{descriptorPath}: settings_files[{index}]: expected a block with path and format, but the entry is empty.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                problems.Add($"{descriptorPath}: settings_files[{index}].path: expected a non-empty path to the game's settings file, but the key is missing.");
            }

            if (string.IsNullOrWhiteSpace(file.Format))
            {
                problems.Add($"{descriptorPath}: settings_files[{index}].format: expected one of {supported}, but the key is missing.");
            }
            else if (!SupportedSettingsFormats.Contains(file.Format, StringComparer.Ordinal))
            {
                problems.Add($"{descriptorPath}: settings_files[{index}].format: expected one of {supported}, but found '{file.Format}'.");
            }
        }
    }

    /// <summary>
    /// Describes why <paramref name="destination"/> is not a safe relative destination, or returns
    /// <see langword="null"/> when it is one.
    /// </summary>
    /// <remarks>
    /// A destination is authored by a game descriptor, not by a mod, so this is not the traversal
    /// hardening of task 204 - it is the rule that keeps a descriptor from writing outside its own
    /// install folder. A backslash is rejected rather than normalised: a descriptor is
    /// cross-platform, and '"Interface\\mods"' is a single file name on macOS, not two segments.
    /// </remarks>
    private static string? DescribeDestinationProblem(string destination)
    {
        if (destination.Contains('\0'))
        {
            return "it contains a null character";
        }

        // Before the separator check: a drive-qualified or share-rooted destination is reported as
        // absolute, which is the more useful complaint.
        if (Path.IsPathFullyQualified(destination) || destination.StartsWith('/'))
        {
            return "it is an absolute path";
        }

        if (destination.Contains('\\', StringComparison.Ordinal))
        {
            return "it uses '\\' as a separator";
        }

        if (destination.EndsWith('/'))
        {
            return "it ends with a separator";
        }

        string[] segments = destination.Split('/');

        if (segments.Any(segment => segment is "." or ".."))
        {
            return "it contains a '.' or '..' segment";
        }

        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            return "it contains an empty segment";
        }

        return null;
    }
}
