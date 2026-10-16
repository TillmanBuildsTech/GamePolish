using System.Text.Json;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Covers <see cref="AppSettingsStore"/> (.todo/201, acceptance criteria 1 and 3-6): defaults for a
/// missing file, a faithful round trip through disk, no temporary files left behind, and a corrupt
/// file that throws without being overwritten.
/// </summary>
[Collection(StorageCollection.Name)]
public class AppSettingsStoreTests
{
    private readonly GamePolishPaths _paths;

    public AppSettingsStoreTests(StorageFixture fixture)
    {
        _paths = new GamePolishPaths(fixture.NewSubdirectory($"app-settings-store-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void LoadReturnsDefaultsAndDoesNotCreateTheFile()
    {
        AppSettings settings = new AppSettingsStore(_paths).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal("system", settings.Theme);
        Assert.Equal("blue", settings.Accent);
        Assert.Equal("session", settings.UpdateCheckOnStart);
        Assert.Equal("information", settings.LogLevel);
        Assert.Null(settings.BackupLocationOverride);
        Assert.False(File.Exists(_paths.AppSettingsFile), $"{_paths.AppSettingsFile} must not be created by a load.");
    }

    [Fact]
    public void SaveThenLoadRoundTripsThroughDisk()
    {
        AppSettings settings = new()
        {
            Theme = "dark",
            Accent = "orange",
            UpdateCheckOnStart = "never",
            LogLevel = "debug",
            BackupLocationOverride = Path.Combine(_paths.Root, "other-backups"),
        };

        new AppSettingsStore(_paths).Save(settings);

        // A second store, so the value has to come off disk rather than out of memory.
        Assert.Equal(settings, new AppSettingsStore(_paths).Load());
        Assert.Equal(1, new AppSettingsStore(_paths).Load().SchemaVersion);
    }

    [Fact]
    public void SaveTwiceLeavesExactlyOneFileAndNoTemporaryFiles()
    {
        AppSettingsStore store = new(_paths);

        store.Save(new AppSettings { Theme = "light" });
        store.Save(new AppSettings { Theme = "dark" });

        Assert.Single(Directory.GetFiles(_paths.Root));
        Assert.Empty(Directory.GetFiles(_paths.Root, "*.tmp"));
        Assert.Equal("dark", store.Load().Theme);
    }

    [Fact]
    public void LoadThrowsOnMalformedJsonAndDoesNotOverwriteTheFile()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.AppSettingsFile, "{ \"theme\": ");
        string before = File.ReadAllText(_paths.AppSettingsFile);

        Assert.Throws<JsonException>(() => new AppSettingsStore(_paths).Load());

        Assert.Equal(before, File.ReadAllText(_paths.AppSettingsFile));
    }

    [Fact]
    public void SaveCreatesTheDataRootWhenItIsMissing()
    {
        GamePolishPaths nested = new(Path.Combine(_paths.Root, "created", "on", "write"));
        AppSettingsStore store = new(nested);

        Assert.False(Directory.Exists(nested.Root));

        store.Save(new AppSettings { Theme = "dark" });

        Assert.True(File.Exists(nested.AppSettingsFile), $"{nested.AppSettingsFile} was not written.");
    }

    [Fact]
    public void SaveWritesOnlyInsideTheDataRoot()
    {
        new AppSettingsStore(_paths).Save(new AppSettings());

        string[] written = Directory.GetFiles(_paths.Root, "*", SearchOption.AllDirectories);

        Assert.Single(written);
        Assert.StartsWith(_paths.Root, written[0], StringComparison.OrdinalIgnoreCase);
    }
}
