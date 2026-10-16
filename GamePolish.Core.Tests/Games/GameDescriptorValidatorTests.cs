using GamePolish.Core.Games;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Games;

/// <summary>
/// One test per rejection rule (.todo/202, acceptance criteria 3 and 4). Every test asserts the
/// message - the path, the key, the offending value and the expectation - not merely that something
/// threw: a descriptor that fails with "invalid descriptor" tells its author nothing.
/// </summary>
[Collection(StorageCollection.Name)]
public class GameDescriptorValidatorTests
{
    private readonly StorageFixture _fixture;

    public GameDescriptorValidatorTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void TheShippedDescriptorIsAccepted() => Accept(new DescriptorText());

    [Fact]
    public void AMissingIdNamesTheKeyAndSaysItIsMissing()
    {
        Reject(new DescriptorText { IdLine = string.Empty }, "id", "missing");
    }

    [Fact]
    public void AnIdWithUppercaseLettersIsRejectedWithTheValue()
    {
        Reject(new DescriptorText { IdLine = "id: OctoWoW" }, "id", "'OctoWoW'", "lowercase");
    }

    [Fact]
    public void AnIdWithAnUnderscoreIsRejectedWithTheValue()
    {
        Reject(new DescriptorText { IdLine = "id: octo_wow" }, "id", "'octo_wow'", "[a-z0-9][a-z0-9-]*");
    }

    [Fact]
    public void AnIdLongerThanSixtyFourCharactersIsRejected()
    {
        Reject(new DescriptorText { IdLine = "id: " + new string('a', 65) }, "id", "at most 64");
    }

    [Fact]
    public void AnIdThatDisagreesWithTheFileNameIsRejected()
    {
        Reject(new DescriptorText { FileStem = "octowow-extra" }, "id", "octowow-extra.yaml", "id: octowow-extra");
    }

    [Fact]
    public void AMissingNameIsRejected()
    {
        Reject(new DescriptorText { NameLine = string.Empty }, "name", "missing");
    }

    [Fact]
    public void ABlankNameIsRejected()
    {
        Reject(new DescriptorText { NameLine = "name: \"   \"" }, "name", "blank");
    }

    [Fact]
    public void AMissingInstallDetectionBlockIsRejected()
    {
        Reject(new DescriptorText { InstallDetectionBlock = string.Empty }, "install_detection", "missing");
    }

    [Fact]
    public void AMissingManualOnlyIsRejected()
    {
        Reject(
            new DescriptorText { InstallDetectionBlock = "install_detection:\n  verify_file: \"WoW.exe\"" },
            "install_detection.manual_only",
            "missing");
    }

    [Fact]
    public void ManualOnlyWithNoVerifyFileIsRejectedAndNamesTheExpectedKey()
    {
        Reject(
            new DescriptorText { InstallDetectionBlock = "install_detection:\n  manual_only: true" },
            "install_detection.verify_file",
            "WoW.exe");
    }

    [Fact]
    public void AMissingModLayoutIsRejected()
    {
        Reject(new DescriptorText { ModLayoutBlock = string.Empty }, "mod_layout", "missing");
    }

    [Fact]
    public void AnUnknownLayoutTypeIsRejectedAndListsTheAcceptedValues()
    {
        Reject(
            new DescriptorText { ModLayoutBlock = "mod_layout:\n  type: steam_grid\n  destination: \"mods\"" },
            "mod_layout.type",
            "'steam_grid'",
            "addons_folder");
    }

    [Fact]
    public void AMissingDestinationIsRejected()
    {
        Reject(
            new DescriptorText { ModLayoutBlock = "mod_layout:\n  type: addons_folder" },
            "mod_layout.destination",
            "missing");
    }

    [Theory]
    [InlineData("'/mods'", "absolute")]
    [InlineData("'//server/share'", "absolute")]
    [InlineData("'../mods'", "'.' or '..' segment")]
    [InlineData("'mods/./tweaks'", "'.' or '..' segment")]
    [InlineData("'mods/'", "ends with a separator")]
    [InlineData("'mods//tweaks'", "empty segment")]
    public void AnUnsafeDestinationIsRejectedWithTheValueAndTheReason(string destination, string expectedFragment)
    {
        Reject(
            new DescriptorText { ModLayoutBlock = $"mod_layout:\n  type: addons_folder\n  destination: {destination}" },
            "mod_layout.destination",
            destination,
            expectedFragment);
    }

    [Fact]
    public void ADestinationWithWindowsSeparatorsIsRejectedBecauseItIsNotPortable()
    {
        Reject(
            new DescriptorText { ModLayoutBlock = "mod_layout:\n  type: addons_folder\n  destination: 'Interface\\AddOns'" },
            "mod_layout.destination",
            "separator");
    }

    [Fact]
    public void ARootedWindowsDestinationIsRejected()
    {
        Reject(
            new DescriptorText { ModLayoutBlock = "mod_layout:\n  type: addons_folder\n  destination: 'C:\\mods'" },
            "mod_layout.destination",
            "C:\\mods");
    }

    [Fact]
    public void AMissingRegistryUrlIsRejected()
    {
        Reject(new DescriptorText { RegistryUrlLine = string.Empty }, "registry_url", "missing");
    }

    [Fact]
    public void ANonHttpsRegistryUrlIsRejectedWithTheValue()
    {
        Reject(
            new DescriptorText { RegistryUrlLine = "registry_url: \"http://example.com/octowow.json\"" },
            "registry_url",
            "http://example.com/octowow.json",
            "https");
    }

    [Fact]
    public void AnUnknownSettingsFormatIsRejected()
    {
        Reject(
            new DescriptorText { ExtraBlock = "settings_files:\n  - path: \"WTF/Config.wtf\"\n    format: yaml" },
            "settings_files[0].format",
            "'yaml'",
            "ini, toml, json");
    }

    [Fact]
    public void ASettingsEntryWithNoPathIsRejected()
    {
        Reject(
            new DescriptorText { ExtraBlock = "settings_files:\n  - format: ini" },
            "settings_files[0].path",
            "missing");
    }

    [Fact]
    public void EveryProblemIsCollectedAndEachNamesTheDescriptorPath()
    {
        GameDescriptorValidationException exception = Reject(
            new DescriptorText
            {
                IdLine = "id: OctoWoW",
                FileStem = "OctoWoW",
                NameLine = "name: \"\"",
                InstallDetectionBlock = "install_detection:\n  manual_only: true",
                ModLayoutBlock = "mod_layout:\n  type: steam_grid\n  destination: \"mods\"",
                RegistryUrlLine = "registry_url: \"http://example.com/octowow.json\"",
            },
            "id",
            "name",
            "install_detection.verify_file",
            "mod_layout.type",
            "registry_url");

        Assert.Equal(5, exception.Problems.Count);
    }

    /// <summary>Writes the descriptor, loads it, and asserts it is accepted.</summary>
    private void Accept(DescriptorText text)
    {
        string file = Write(text);

        GameDescriptor descriptor = new GameDescriptorLoader([Path.GetDirectoryName(file)!]).LoadFromFile(file);

        Assert.Equal(text.FileStem, descriptor.Id);
    }

    /// <summary>
    /// Writes the descriptor, asserts the load fails, asserts every problem names the file, and
    /// asserts each expected fragment appears somewhere in the report.
    /// </summary>
    private GameDescriptorValidationException Reject(DescriptorText text, params string[] expectedFragments)
    {
        string file = Write(text);

        GameDescriptorValidationException exception = Assert.Throws<GameDescriptorValidationException>(
            () => new GameDescriptorLoader([Path.GetDirectoryName(file)!]).LoadFromFile(file));

        string report = string.Join('\n', exception.Problems);

        Assert.All(exception.Problems, problem => Assert.StartsWith(file, problem, StringComparison.Ordinal));

        foreach (string fragment in expectedFragments)
        {
            Assert.Contains(fragment, report, StringComparison.Ordinal);
        }

        return exception;
    }

    private string Write(DescriptorText text)
    {
        string directory = _fixture.NewSubdirectory($"descriptor-{Guid.NewGuid():N}");
        string file = Path.Combine(directory, text.FileStem + ".yaml");

        File.WriteAllText(file, text.ToYaml());

        return file;
    }
}
