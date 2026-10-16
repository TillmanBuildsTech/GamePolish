using GamePolish.Core.Games;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Games;

/// <summary>
/// Covers descriptor discovery and loading (.todo/202, acceptance criteria 1, 2, 5 and 6): the
/// shipped OctoWoW descriptor parses, unknown keys are ignored, duplicate ids across the two search
/// directories are refused, and every failure names the file.
/// </summary>
[Collection(StorageCollection.Name)]
public class GameDescriptorLoaderTests
{
    private readonly StorageFixture _fixture;

    public GameDescriptorLoaderTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>The shipping directory as it exists in the test output, copied by the test project.</summary>
    private static string ShippedDescriptorDirectory => Path.Combine(AppContext.BaseDirectory, GameDescriptorLoader.DescriptorDirectoryName);

    [Fact]
    public void LoadsTheShippedOctoWoWDescriptor()
    {
        GameDescriptorLoader loader = new([ShippedDescriptorDirectory]);

        GameDescriptor descriptor = loader.LoadById("octowow")!;

        Assert.Equal("octowow", descriptor.Id);
        Assert.Equal("OctoWoW", descriptor.Name);
        Assert.True(descriptor.InstallDetection.ManualOnly);
        Assert.Equal("WoW.exe", descriptor.InstallDetection.VerifyFile);
        Assert.Equal(GameDescriptorValidator.AddonsFolderLayout, descriptor.ModLayout.Type);
        Assert.Equal("Interface/AddOns", descriptor.ModLayout.Destination);
        Assert.Equal("https://raw.githubusercontent.com/game-polish/registries/main/octowow.json", descriptor.RegistryUrl);
        Assert.Empty(descriptor.SettingsFiles);
    }

    [Fact]
    public void LoadLoadsTheSameDescriptorFromAFilePath()
    {
        string file = Path.Combine(ShippedDescriptorDirectory, "octowow.yaml");

        GameDescriptor fromFile = new GameDescriptorLoader([ShippedDescriptorDirectory]).LoadFromFile(file);

        Assert.Equal("octowow", fromFile.Id);
        Assert.Equal("OctoWoW", fromFile.Name);
    }

    [Fact]
    public void DescriptorDirectoriesPutTheShippedDirectoryFirstAndTheDataRootSecond()
    {
        GamePolishPaths paths = _fixture.Paths;

        GameDescriptorLoader loader = new(paths);

        Assert.Equal(
            [
                Path.Combine(AppContext.BaseDirectory, "games"),
                Path.Combine(paths.Root, "games"),
            ],
            loader.DescriptorDirectories);
    }

    [Fact]
    public void UnknownTopLevelKeysAreIgnoredAndSettingsFilesAreStillParsed()
    {
        string directory = _fixture.NewSubdirectory(nameof(UnknownTopLevelKeysAreIgnoredAndSettingsFilesAreStillParsed));
        string file = Path.Combine(directory, "octowow.yaml");
        File.WriteAllText(file, new DescriptorText
        {
            ExtraBlock = """
                settings_files:
                  - path: "WTF/Config.wtf"
                    format: ini
                a_key_from_the_future: "ignore me"
                """,
        }.ToYaml());

        GameDescriptor descriptor = new GameDescriptorLoader([directory]).LoadFromFile(file);

        SettingsFileDescriptor settings = Assert.Single(descriptor.SettingsFiles);
        Assert.Equal("WTF/Config.wtf", settings.Path);
        Assert.Equal("ini", settings.Format);
    }

    [Fact]
    public void TwoFilesWithTheSameIdProduceOneErrorNamingBothFiles()
    {
        string shipped = _fixture.NewSubdirectory("duplicate-shipped");
        string user = _fixture.NewSubdirectory("duplicate-user");
        string shippedFile = Path.Combine(shipped, "octowow.yaml");
        string userFile = Path.Combine(user, "octowow.yaml");
        File.WriteAllText(shippedFile, DescriptorText.Shipped);
        File.WriteAllText(userFile, DescriptorText.Shipped);

        GameDescriptorLoader loader = new([shipped, user]);

        GameDescriptorValidationException exception = Assert.Throws<GameDescriptorValidationException>(() => loader.LoadAll());

        Assert.Single(exception.Problems);
        Assert.Contains(shippedFile, exception.Problems[0], StringComparison.Ordinal);
        Assert.Contains(userFile, exception.Problems[0], StringComparison.Ordinal);
        Assert.Contains("unique", exception.Problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadByIdReturnsNullForAnIdWithNoDescriptor()
    {
        GameDescriptorLoader loader = new([ShippedDescriptorDirectory]);

        Assert.Null(loader.LoadById("no-such-game"));
    }

    [Fact]
    public void LoadAllThrowsForTheInvalidFileAndNamesIt()
    {
        string directory = _fixture.NewSubdirectory(nameof(LoadAllThrowsForTheInvalidFileAndNamesIt));
        File.WriteAllText(Path.Combine(directory, "octowow.yaml"), DescriptorText.Shipped);
        File.WriteAllText(Path.Combine(directory, "broken.yaml"), "id: broken\nname: \"Broken\"\n");

        GameDescriptorValidationException exception =
            Assert.Throws<GameDescriptorValidationException>(() => new GameDescriptorLoader([directory]).LoadAll());

        Assert.Contains(Path.Combine(directory, "broken.yaml"), exception.DescriptorPath, StringComparison.Ordinal);
        Assert.Contains("install_detection", string.Join('\n', exception.Problems), StringComparison.Ordinal);
        Assert.Contains("mod_layout", string.Join('\n', exception.Problems), StringComparison.Ordinal);
        Assert.Contains("registry_url", string.Join('\n', exception.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFromFileThrowsWhenTheFileIsMissing()
    {
        string file = Path.Combine(_fixture.NewSubdirectory(nameof(LoadFromFileThrowsWhenTheFileIsMissing)), "octowow.yaml");

        GameDescriptorValidationException exception =
            Assert.Throws<GameDescriptorValidationException>(() => new GameDescriptorLoader([Path.GetDirectoryName(file)!]).LoadFromFile(file));

        Assert.Contains(file, exception.Problems[0], StringComparison.Ordinal);
        Assert.Contains("does not exist", exception.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedDescriptorIsRejectedBeforeParsing()
    {
        string directory = _fixture.NewSubdirectory(nameof(AnOversizedDescriptorIsRejectedBeforeParsing));
        string file = Path.Combine(directory, "large.yaml");
        File.WriteAllText(file, new string('a', (int)GameDescriptorLoader.MaximumDescriptorBytes + 1));

        GameDescriptorValidationException exception =
            Assert.Throws<GameDescriptorValidationException>(() => new GameDescriptorLoader([directory]).LoadFromFile(file));

        Assert.Contains("bytes", exception.Problems[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id: [unclosed\n", "line 1")]
    [InlineData("", "empty")]
    [InlineData("just a plain string\n", "yaml")]
    [InlineData("- a\n- b\n", "yaml")]
    [InlineData("id: one\nid: two\n", "Duplicate")]
    public void FilesThatAreNotUsableDescriptorsAreRejectedWithTheReason(string yaml, string expectedFragment)
    {
        string directory = _fixture.NewSubdirectory($"not-a-descriptor-{Guid.NewGuid():N}");
        string file = Path.Combine(directory, "octowow.yaml");
        File.WriteAllText(file, yaml);

        GameDescriptorValidationException exception =
            Assert.Throws<GameDescriptorValidationException>(() => new GameDescriptorLoader([directory]).LoadFromFile(file));

        Assert.All(exception.Problems, problem => Assert.StartsWith(exception.DescriptorPath, problem, StringComparison.Ordinal));
        Assert.Contains(expectedFragment, string.Join('\n', exception.Problems), StringComparison.OrdinalIgnoreCase);
    }
}
