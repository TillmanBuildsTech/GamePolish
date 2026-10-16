using System.Text.Json;

using GamePolish.Core.Games;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Games;

/// <summary>
/// Covers the games.json registry (.todo/203, acceptance criteria 1-8): the add/list/remove round
/// trip, duplicate adds, the verify-file check, a moved install, the two-roots rule, and the promise
/// that nothing is ever written inside a game's install folder.
/// </summary>
[Collection(StorageCollection.Name)]
public class GamesRegistryTests
{
    private const string OctoWoW = "octowow";
    private const string OtherGame = "othergame";
    private const string VerifyFile = "WoW.exe";

    private readonly StorageFixture _fixture;
    private readonly GamePolishPaths _paths;
    private readonly IReadOnlyList<string> _descriptorDirectories;

    public GamesRegistryTests(StorageFixture fixture)
    {
        _fixture = fixture;

        // Each test gets its own data root: games.json is file state, and a shared fixture root would
        // leak entries between tests.
        _paths = new GamePolishPaths(fixture.NewSubdirectory($"registry-{Guid.NewGuid():N}"));

        string descriptors = fixture.NewSubdirectory($"descriptors-{Guid.NewGuid():N}");
        File.WriteAllText(
            Path.Combine(descriptors, OtherGame + ".yaml"),
            new DescriptorText
            {
                IdLine = $"id: {OtherGame}",
                NameLine = "name: \"Other Game\"",
                FileStem = OtherGame,
            }.ToYaml());

        _descriptorDirectories =
            [Path.Combine(AppContext.BaseDirectory, GameDescriptorLoader.DescriptorDirectoryName), descriptors];
    }

    private IGamesRegistry Registry => new GamesRegistry(_paths, new GameDescriptorLoader(_descriptorDirectories));

    [Fact]
    public void AddTracksTheGameAndWritesTheFileInUtc()
    {
        string installRoot = CreateInstallDirectory("octowow-install");

        TrackedGame added = Registry.Add(OctoWoW, installRoot);

        Assert.Equal(OctoWoW, added.Id);
        Assert.Equal("OctoWoW", added.Name);
        Assert.Equal(Path.GetFullPath(installRoot), added.InstallPath);
        Assert.Equal(TimeSpan.Zero, added.AddedAt.Offset);

        Assert.StartsWith(_paths.Root, _paths.GamesFile, StringComparison.OrdinalIgnoreCase);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_paths.GamesFile));

        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());

        JsonElement entry = document.RootElement.GetProperty("games")[0];

        Assert.Equal(OctoWoW, entry.GetProperty("id").GetString());
        Assert.Equal(Path.GetFullPath(installRoot), entry.GetProperty("install_path").GetString());
        Assert.Equal(
            TimeSpan.Zero,
            DateTimeOffset.Parse(entry.GetProperty("added_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Offset);

        Assert.Equal(added, Assert.Single(Registry.List()));
    }

    [Fact]
    public void AddWithAMissingVerifyFileIsRejectedAndLeavesTheFileAlone()
    {
        Registry.Add(OctoWoW, CreateInstallDirectory("first-install"));
        byte[] before = File.ReadAllBytes(_paths.GamesFile);

        string wrongFolder = _fixture.NewInstallDirectory("not-a-game");

        InstallPathInvalidException exception =
            Assert.Throws<InstallPathInvalidException>(() => Registry.Add(OtherGame, wrongFolder));

        Assert.Equal(OtherGame, exception.GameId);
        Assert.Equal(Path.GetFullPath(wrongFolder), exception.InstallPath);
        Assert.Contains(Path.GetFullPath(wrongFolder), exception.Message, StringComparison.Ordinal);
        Assert.Contains(VerifyFile, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not look like Other Game", exception.Message, StringComparison.Ordinal);

        Assert.Equal(before, File.ReadAllBytes(_paths.GamesFile));
    }

    [Fact]
    public void AddingTheSameGameTwiceIsRefusedAndKeepsOneEntry()
    {
        Registry.Add(OctoWoW, CreateInstallDirectory("first-install"));
        string second = CreateInstallDirectory("second-install");
        TrackedGame existing = Registry.Get(OctoWoW)!;

        GameAlreadyTrackedException exception =
            Assert.Throws<GameAlreadyTrackedException>(() => Registry.Add(OctoWoW, second));

        Assert.Equal(OctoWoW, exception.GameId);
        Assert.Equal(existing.InstallPath, exception.ExistingPath);
        Assert.Equal(Path.GetFullPath(second), exception.RequestedPath);
        Assert.Contains(existing.InstallPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("change install path", exception.Message, StringComparison.Ordinal);

        Assert.Single(Registry.List());
        Assert.Equal(existing.InstallPath, Registry.Get(OctoWoW)!.InstallPath);
    }

    [Fact]
    public void AnInstallFolderInsideTheDataRootIsRejected()
    {
        string inside = Path.Combine(_paths.Root, "game-inside-data-root");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, VerifyFile), string.Empty);

        InstallPathInvalidException exception =
            Assert.Throws<InstallPathInvalidException>(() => Registry.Add(OctoWoW, inside));

        Assert.Contains(_paths.Root, exception.Message, StringComparison.Ordinal);
        Assert.Contains("data directory", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.GamesFile));
    }

    [Fact]
    public void AnInstallFolderThatContainsTheDataRootIsRejected()
    {
        InstallPathInvalidException exception =
            Assert.Throws<InstallPathInvalidException>(() => Registry.Add(OctoWoW, Path.GetTempPath()));

        Assert.Contains("contains the Game Polish data directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetInstallPathMovesTheGameAndListUnresolvedFollowsIt()
    {
        string before = CreateInstallDirectory("before-move");
        Registry.Add(OctoWoW, before);

        Assert.Empty(Registry.ListUnresolved());

        Directory.Delete(before, recursive: true);

        byte[] beforeUnresolved = File.ReadAllBytes(_paths.GamesFile);

        TrackedGame unresolved = Assert.Single(Registry.ListUnresolved());

        Assert.Equal(beforeUnresolved, File.ReadAllBytes(_paths.GamesFile));
        Assert.Equal(OctoWoW, unresolved.Id);

        string after = CreateInstallDirectory("after-move");

        TrackedGame updated = Registry.SetInstallPath(OctoWoW, after);

        Assert.Equal(Path.GetFullPath(after), updated.InstallPath);
        Assert.Equal(unresolved.AddedAt, updated.AddedAt);
        Assert.Empty(Registry.ListUnresolved());
        Assert.Equal(Path.GetFullPath(after), Assert.Single(Registry.List()).InstallPath);
    }

    [Fact]
    public void SetInstallPathOnAnUntrackedGameIsRejected()
    {
        InstallPathInvalidException exception = Assert.Throws<InstallPathInvalidException>(
            () => Registry.SetInstallPath(OctoWoW, CreateInstallDirectory("never-added")));

        Assert.Contains("not tracked yet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveReturnsTrueThenFalseAndLeavesOtherGamesAlone()
    {
        Registry.Add(OctoWoW, CreateInstallDirectory("octowow-install"));
        string other = CreateInstallDirectory("othergame-install");
        Registry.Add(OtherGame, other);

        Assert.True(Registry.Remove(OctoWoW));
        Assert.False(Registry.Remove(OctoWoW));
        Assert.Null(Registry.Get(OctoWoW));

        TrackedGame remaining = Assert.Single(Registry.List());

        Assert.Equal(OtherGame, remaining.Id);
        Assert.Equal(Path.GetFullPath(other), remaining.InstallPath);
    }

    [Fact]
    public void AddWritesNothingIntoTheInstallFolderAndNothingOutsideTheDataRoot()
    {
        string installRoot = CreateInstallDirectory("untouched");
        string[] filesBefore = Directory.GetFiles(installRoot, "*", SearchOption.AllDirectories);
        DateTime writeTimeBefore = Directory.GetLastWriteTimeUtc(installRoot);

        Registry.Add(OctoWoW, installRoot);

        Assert.Equal(filesBefore, Directory.GetFiles(installRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(writeTimeBefore, Directory.GetLastWriteTimeUtc(installRoot));

        string[] written = Directory.GetFiles(_paths.Root, "*", SearchOption.AllDirectories);

        Assert.Single(written);
        Assert.StartsWith(_paths.Root, written[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_paths.GamesFile, written[0]);
    }

    [Fact]
    public void AddSucceedsWhenTheInstallFolderIsReadOnly()
    {
        string installRoot = CreateInstallDirectory("read-only-install");

        File.SetAttributes(installRoot, FileAttributes.ReadOnly);
        try
        {
            TrackedGame added = Registry.Add(OctoWoW, installRoot);

            Assert.Equal(Path.GetFullPath(installRoot), added.InstallPath);
        }
        finally
        {
            File.SetAttributes(installRoot, FileAttributes.Directory);
        }
    }

    [Fact]
    public void AFolderAlreadyTrackedForAnotherGameIsRejected()
    {
        string shared = CreateInstallDirectory("shared-install");
        Registry.Add(OctoWoW, shared);

        InstallPathInvalidException exception =
            Assert.Throws<InstallPathInvalidException>(() => Registry.Add(OtherGame, shared));

        Assert.Contains("OctoWoW is already tracked at", exception.Message, StringComparison.Ordinal);
        Assert.Single(Registry.List());
    }

    [Fact]
    public void AnUnknownGameIdIsRejected()
    {
        InstallPathInvalidException exception = Assert.Throws<InstallPathInvalidException>(
            () => Registry.Add("skyrim", CreateInstallDirectory("skyrim-install")));

        Assert.Equal("skyrim", exception.GameId);
        Assert.Contains("not a supported game", exception.Message, StringComparison.Ordinal);
        Assert.Contains("skyrim.yaml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderThatDoesNotExistIsRejected()
    {
        string missing = Path.Combine(_fixture.NewInstallDirectory("existing-parent"), "never-created");

        InstallPathInvalidException exception =
            Assert.Throws<InstallPathInvalidException>(() => Registry.Add(OctoWoW, missing));

        Assert.Equal(Path.GetFullPath(missing), exception.InstallPath);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeFolderIsRejected()
    {
        InstallPathInvalidException exception = Assert.Throws<InstallPathInvalidException>(
            () => Registry.Add(OctoWoW, "Games/OctoWoW"));

        Assert.Contains("not an absolute path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeadingTildeExpandsToTheUserProfile()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(profile, InstallPathValidator.ExpandUserProfile("~"));
        Assert.Equal(Path.Combine(profile, "Games/OctoWoW"), InstallPathValidator.ExpandUserProfile("~/Games/OctoWoW"));
        Assert.Equal(@"C:\Games\OctoWoW", InstallPathValidator.ExpandUserProfile(@"C:\Games\OctoWoW"));
        Assert.Equal("~notahome", InstallPathValidator.ExpandUserProfile("~notahome"));
    }

    [Fact]
    public void AMalformedGamesFileIsAnErrorAndIsNotOverwritten()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.GamesFile, "{ \"games\": ");
        string before = File.ReadAllText(_paths.GamesFile);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Registry.Add(OctoWoW, CreateInstallDirectory("octowow-install")));

        Assert.Contains(_paths.GamesFile, exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
        Assert.Equal(before, File.ReadAllText(_paths.GamesFile));
    }

    [Fact]
    public void IdsAreMatchedExactly()
    {
        Registry.Add(OctoWoW, CreateInstallDirectory("octowow-install"));

        Assert.Null(Registry.Get("OCTOWOW"));
        Assert.False(Registry.Remove("OCTOWOW"));
        Assert.Single(Registry.List());

        Assert.Throws<InstallPathInvalidException>(
            () => Registry.Add("OCTOWOW", CreateInstallDirectory("wrong-case-install")));
    }

    [Fact]
    public async Task ConcurrentAddsKeepBothEntries()
    {
        string octoWoW = CreateInstallDirectory("concurrent-octowow");
        string other = CreateInstallDirectory("concurrent-othergame");

        IGamesRegistry registry = Registry;

        await Task.WhenAll(
            Task.Run(() => registry.Add(OctoWoW, octoWoW)),
            Task.Run(() => registry.Add(OtherGame, other)));

        IReadOnlyList<TrackedGame> games = registry.List();

        Assert.Equal(2, games.Count);
        Assert.Contains(games, game => game.Id == OctoWoW);
        Assert.Contains(games, game => game.Id == OtherGame);
    }

    private string CreateInstallDirectory(string name)
    {
        string directory = _fixture.NewInstallDirectory(name);
        File.WriteAllText(Path.Combine(directory, VerifyFile), string.Empty);

        return directory;
    }
}
