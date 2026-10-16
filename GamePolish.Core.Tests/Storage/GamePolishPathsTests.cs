using GamePolish.Core.Storage;

namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Covers the data-root precedence and validation rules, and the rule that every path the engine
/// derives from a root stays inside that root (.todo/201, acceptance criteria 1 and 2).
/// </summary>
[Collection(StorageCollection.Name)]
public class GamePolishPathsTests
{
    private readonly StorageFixture _fixture;

    public GamePolishPathsTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void EnvironmentVariableRedirectsTheRootAndEveryDerivedPath()
    {
        GamePolishPaths paths = _fixture.Paths;

        paths.EnsureCreated();
        string gameDirectory = paths.EnsureGameDirectory("octowow");

        string[] produced =
        [
            paths.Root,
            paths.GamesFile,
            paths.AppSettingsFile,
            paths.LogsDirectory,
            gameDirectory,
            paths.ManifestFile("octowow"),
            paths.RegistryCacheFile("octowow"),
            paths.BackupsDirectory("octowow"),
            paths.StagingDirectory("octowow"),
        ];

        Assert.Equal(Path.GetFullPath(_fixture.Root), paths.Root);

        foreach (string path in produced)
        {
            Assert.StartsWith(_fixture.Root, path, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(Directory.Exists(gameDirectory), $"{gameDirectory} was not created under the data root.");

        // The temp tree is the whole world for this test: nothing exists beside it that the engine did
        // not ask for. Asserted on a root this test owns, because the fixture root also holds the
        // other tests' isolated directories.
        string isolatedRoot = _fixture.NewSubdirectory(nameof(EnvironmentVariableRedirectsTheRootAndEveryDerivedPath));
        GamePolishPaths isolated = new(isolatedRoot);

        isolated.EnsureCreated();
        string isolatedGameDirectory = isolated.EnsureGameDirectory("octowow");

        Assert.Equal(
            [isolatedGameDirectory],
            Directory.GetDirectories(isolatedRoot).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Empty(Directory.GetFiles(isolatedRoot));
    }

    [Fact]
    public void AnExplicitRootWinsOverTheEnvironmentVariable()
    {
        string explicitRoot = _fixture.NewSubdirectory("explicit-root");

        GamePolishPaths paths = GamePolishPaths.Resolve(explicitRoot);

        Assert.Equal(Path.GetFullPath(explicitRoot), paths.Root);
        Assert.NotEqual(_fixture.Root, paths.Root);
    }

    [Fact]
    public void DefaultResolutionFallsBackToTheUserProfile()
    {
        // Safe to clear the variable only because this collection never runs concurrently (see
        // StorageFixture). Nothing is created here: the profile directory must stay untouched.
        using TemporaryEnvironmentVariable scope =
            TemporaryEnvironmentVariable.Set(GamePolishPaths.DataRootEnvironmentVariable, null);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expected = Path.Combine(Path.GetFullPath(profile), GamePolishPaths.DefaultDirectoryName);

        GamePolishPaths paths = GamePolishPaths.Resolve();

        Assert.Equal(expected, paths.Root);
        Assert.Equal(Path.Combine(expected, "app-settings.json"), paths.AppSettingsFile);
    }

    [Fact]
    public void EnsureCreatedIsIdempotent()
    {
        GamePolishPaths paths = new(Path.Combine(_fixture.NewSubdirectory("ensure-created"), "data-root"));

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Root), $"{paths.Root} does not exist after EnsureCreated() ran twice.");
    }

    [Fact]
    public void TrailingSeparatorsAreRemovedFromTheRoot()
    {
        string withSeparator = _fixture.Root + Path.DirectorySeparatorChar;

        GamePolishPaths paths = new(withSeparator);

        Assert.Equal(_fixture.Root, paths.Root);
    }

    [Fact]
    public void ARelativeRootIsRejected()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new GamePolishPaths("game-polish-data"));

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVolumeRootIsRejected()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(_fixture.Root))!;

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new GamePolishPaths(volumeRoot));

        Assert.Contains("volume root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileIsRejectedAsTheRoot()
    {
        string file = Path.Combine(_fixture.NewSubdirectory("file-root"), "not-a-directory");

        File.WriteAllText(file, "{}");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new GamePolishPaths(file));

        Assert.Contains("file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../octowow")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    public void AGameIdThatIsNotASingleDirectoryNameIsRejected(string gameId)
    {
        GamePolishPaths paths = _fixture.Paths;

        Assert.Throws<ArgumentException>(() => paths.GameDirectory(gameId));
    }

    [Fact]
    public void GamePathsLiveInsideTheGameDirectory()
    {
        GamePolishPaths paths = _fixture.Paths;
        string gameDirectory = paths.GameDirectory("octowow");

        Assert.Equal(Path.Combine(paths.Root, "octowow"), gameDirectory);
        Assert.Equal(Path.Combine(gameDirectory, "manifest.json"), paths.ManifestFile("octowow"));
        Assert.Equal(Path.Combine(gameDirectory, "registry-cache.json"), paths.RegistryCacheFile("octowow"));
        Assert.Equal(Path.Combine(gameDirectory, "backups"), paths.BackupsDirectory("octowow"));
        Assert.Equal(Path.Combine(gameDirectory, "staging"), paths.StagingDirectory("octowow"));
    }
}
