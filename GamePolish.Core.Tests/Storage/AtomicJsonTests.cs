using System.Text.Json;

using GamePolish.Core.Storage;

namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Covers the single JSON reader and writer: atomic replacement, snake_case on disk, absent-file
/// semantics, and the deliberate decision to let <see cref="JsonException"/> reach the caller.
/// </summary>
[Collection(StorageCollection.Name)]
public class AtomicJsonTests
{
    private readonly StorageFixture _fixture;

    public AtomicJsonTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ReadReturnsDefaultForAMissingFile()
    {
        string path = Path.Combine(_fixture.NewSubdirectory(nameof(ReadReturnsDefaultForAMissingFile)), "missing.json");

        Assert.Null(AtomicJson.Read<AppSettings>(path));
        Assert.False(File.Exists(path), $"{path} must not be created by a read.");
    }

    [Fact]
    public void WriteThenReadRoundTripsAndWritesSnakeCaseNames()
    {
        string path = Path.Combine(_fixture.NewSubdirectory(nameof(WriteThenReadRoundTripsAndWritesSnakeCaseNames)), "app-settings.json");
        AppSettings settings = new() { Theme = "dark", Accent = "purple" };

        AtomicJson.Write(path, settings);

        Assert.Equal(settings, AtomicJson.Read<AppSettings>(path));

        string json = File.ReadAllText(path);
        Assert.Contains("\"schema_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"update_check_on_start\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTwiceLeavesOneFileAndNoTemporaryFiles()
    {
        string directory = _fixture.NewSubdirectory(nameof(WriteTwiceLeavesOneFileAndNoTemporaryFiles));
        string path = Path.Combine(directory, "app-settings.json");

        AtomicJson.Write(path, new AppSettings { Theme = "light" });
        AtomicJson.Write(path, new AppSettings { Theme = "dark" });

        Assert.Single(Directory.GetFiles(directory));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal("dark", AtomicJson.Read<AppSettings>(path)!.Theme);
    }

    [Fact]
    public void WriteCreatesTheContainingDirectory()
    {
        string path = Path.Combine(_fixture.Root, "created-by-write", "app-settings.json");
        string directory = Path.GetDirectoryName(path)!;

        Assert.False(Directory.Exists(directory));

        AtomicJson.Write(path, new AppSettings());

        Assert.True(File.Exists(path), $"{path} was not written.");
    }

    [Fact]
    public void ReadLetsJsonExceptionsPropagateWithoutTouchingTheFile()
    {
        string path = Path.Combine(_fixture.NewSubdirectory(nameof(ReadLetsJsonExceptionsPropagateWithoutTouchingTheFile)), "corrupt.json");
        File.WriteAllText(path, "{ this is not json");
        string before = File.ReadAllText(path);

        Assert.Throws<JsonException>(() => AtomicJson.Read<AppSettings>(path));

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void WriteOmitsNullProperties()
    {
        string path = Path.Combine(_fixture.NewSubdirectory(nameof(WriteOmitsNullProperties)), "app-settings.json");

        AtomicJson.Write(path, new AppSettings());

        Assert.DoesNotContain("backup_location_override", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
