using GamePolish.Core.Manifest;
using GamePolish.Core.Paths;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Manifest;

/// <summary>
/// Covers the manifest store (.todo/206, acceptance criteria 1-9): the CRUD round trip, the query set the
/// UI calls, corruption quarantine, fifty parallel writers, the credential guard and the rule that no
/// timestamp is written by the store itself.
/// </summary>
[Collection(StorageCollection.Name)]
public class ManifestStoreTests
{
    private const string GameId = "octowow";
    private const string ModId = "pfUI";

    private static readonly DateTimeOffset InstalledAt = new(2026, 10, 15, 21, 42, 3, TimeSpan.Zero);

    private readonly GamePolishPaths _paths;

    public ManifestStoreTests(StorageFixture fixture)
    {
        _paths = new GamePolishPaths(fixture.NewSubdirectory($"manifest-{Guid.NewGuid():N}"));
    }

    private ManifestStore Store => new(_paths);

    [Fact]
    public void UpsertThenListRoundTripsEveryField()
    {
        InstalledMod original = AMod(files: [("pfUI/pfUI.toc", 120), ("pfUI/README.md", 40)]);

        Store.Upsert(GameId, original);

        InstalledMod stored = Assert.Single(Store.List(GameId));

        AssertSameMod(original, stored);
        Assert.Equal(original.Files, stored.Files);

        // The same values after a fresh read of the file, not out of memory.
        Assert.Equal(original.Files, Store.GetInstalledFiles(GameId, ModId));
    }

    [Fact]
    public void QueriesAnswerForAnInstalledAndForAnUnknownMod()
    {
        Store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120)]));
        ManifestStore store = Store;

        Assert.True(store.IsInstalled(GameId, ModId));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", store.GetInstalledVersion(GameId, ModId));
        Assert.NotNull(store.Find(GameId, ModId));
        Assert.Single(store.GetInstalledFiles(GameId, ModId));

        Assert.False(store.IsInstalled(GameId, "not-installed"));
        Assert.Null(store.GetInstalledVersion(GameId, "not-installed"));
        Assert.Null(store.Find(GameId, "not-installed"));
        Assert.Empty(store.GetInstalledFiles(GameId, "not-installed"));

        // Ids are matched exactly.
        Assert.False(store.IsInstalled(GameId, "PFUI"));
    }

    [Fact]
    public void SetEnabledFlipsOnlyThatModAndSurvivesANewStore()
    {
        Store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120)]));
        Store.Upsert(GameId, AMod("ItemRack", files: [("ItemRack/ItemRack.toc", 80)]));

        Store.SetEnabled(GameId, ModId, false);

        // Durability, not cache: a new instance against the same data root reads the file.
        ManifestStore reopened = new(_paths);

        Assert.False(reopened.Find(GameId, ModId)!.Enabled);
        Assert.True(reopened.Find(GameId, "ItemRack")!.Enabled);
        Assert.Equal(InstalledAt, reopened.Find(GameId, ModId)!.UpdatedAt);
    }

    [Fact]
    public void SetEnabledOnAModThatIsNotInstalledIsAnError()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Store.SetEnabled(GameId, ModId, false));

        Assert.Contains(ModId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("not installed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveReturnsTrueThenFalseAndDeletesOnlyThatEntry()
    {
        Store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120)]));
        Store.Upsert(GameId, AMod("ItemRack", files: [("ItemRack/ItemRack.toc", 80)]));

        Assert.True(Store.Remove(GameId, ModId));
        Assert.False(Store.Remove(GameId, ModId));

        InstalledMod remaining = Assert.Single(Store.List(GameId));

        Assert.Equal("ItemRack", remaining.Id);
    }

    [Fact]
    public void AMissingManifestIsEmptyAndReadingDoesNotCreateIt()
    {
        Assert.Empty(Store.List(GameId));
        Assert.False(File.Exists(_paths.ManifestFile(GameId)));
    }

    [Fact]
    public void UpsertReplacesTheEntryWithTheSameId()
    {
        Store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120)]));
        Store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 121), ("pfUI/pfUI.lua", 9)]) with { Version = "v5.2", VersionKind = "tag" });

        InstalledMod stored = Assert.Single(Store.List(GameId));

        Assert.Equal("v5.2", stored.Version);
        Assert.Equal("tag", stored.VersionKind);
        Assert.Equal(2, stored.Files.Count);
    }

    [Fact]
    public void AManifestThatCannotBeParsedIsQuarantinedAndThenReplaced()
    {
        _paths.EnsureGameDirectory(GameId);
        const string Garbage = "{ \"mods\": [ { \"id\": ";
        File.WriteAllText(_paths.ManifestFile(GameId), Garbage);

        ManifestCorruptException exception = Assert.Throws<ManifestCorruptException>(() => Store.List(GameId));

        Assert.Equal(GameId, exception.GameId);
        Assert.False(File.Exists(_paths.ManifestFile(GameId)), "the unreadable manifest must be moved aside, not left in place.");
        Assert.True(File.Exists(exception.QuarantinedPath));
        Assert.Equal(Garbage, File.ReadAllText(exception.QuarantinedPath));
        Assert.StartsWith(_paths.ManifestFile(GameId) + ".corrupt-", exception.QuarantinedPath, StringComparison.Ordinal);
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);

        // Quarantining is not the end of the world: the next write produces a valid file.
        Store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120)]));

        Assert.Equal(ModId, Assert.Single(Store.List(GameId)).Id);
    }

    [Theory]
    [InlineData("{ \"schema_version\": 1, \"mods\": [ null ] }")]
    [InlineData("{ \"schema_version\": 1, \"mods\": [ { \"id\": \"pfUI\", \"name\": \"pfUI\", \"source_url\": \"https://github.com/brues-code/pfUI\", \"source_type\": \"github\", \"folder_name\": \"pfUI\", \"version\": \"abc\", \"version_kind\": \"sha\", \"enabled\": true, \"installed_at\": \"2026-10-15T21:42:03+00:00\", \"updated_at\": \"2026-10-15T21:42:03+00:00\" } ] }")]
    [InlineData("{ \"schema_version\": 1, \"mods\": [ { \"id\": \"pfUI\", \"files\": [ { \"size_bytes\": 1 } ] } ] }")]
    public void AManifestWhoseShapeIsWrongIsQuarantinedToo(string manifestJson)
    {
        _paths.EnsureGameDirectory(GameId);
        File.WriteAllText(_paths.ManifestFile(GameId), manifestJson);

        ManifestCorruptException exception = Assert.Throws<ManifestCorruptException>(() => Store.List(GameId));

        Assert.True(File.Exists(exception.QuarantinedPath));
        Assert.Equal(manifestJson, File.ReadAllText(exception.QuarantinedPath));
    }

    [Fact]
    public async Task FiftyParallelUpsertsLoseNothingAndLeaveNoTemporaryFiles()
    {
        ManifestStore store = Store;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(index => Task.Run(
            () => store.Upsert(GameId, AMod($"mod{index:00}", files: [($"mod{index:00}/mod.toc", index)])))));

        IReadOnlyList<InstalledMod> mods = store.List(GameId);

        Assert.Equal(50, mods.Count);
        Assert.Equal(50, mods.Select(mod => mod.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(Directory.GetFiles(_paths.GameDirectory(GameId), "*.tmp"));
    }

    [Theory]
    [InlineData("https://x-access-token:abc123@github.com/owner/repo", "x-access-token")]
    [InlineData("https://github.com/owner/repo?token=abc123", "token=")]
    [InlineData("https://github.com/owner/repo?access_token=abc123", "access_token=")]
    public void ASourceUrlWithAnEmbeddedCredentialIsRejected(string sourceUrl, string expectedComponent)
    {
        InstalledMod mod = AMod() with { SourceUrl = sourceUrl };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => Store.Upsert(GameId, mod));

        Assert.Contains(expectedComponent, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Store.List(GameId));
    }

    [Fact]
    public void AZipSourceFileNameIsAccepted()
    {
        InstalledMod mod = AMod() with { SourceUrl = "pfUI-v5.1.zip", SourceType = ModSourceType.Zip };

        Store.Upsert(GameId, mod);

        Assert.Equal("pfUI-v5.1.zip", Assert.Single(Store.List(GameId)).SourceUrl);
    }

    [Fact]
    public void FindOwnersOfPathFindsTheOwnerAndIgnoresOtherPaths()
    {
        ManifestStore store = Store;

        store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120), ("pfUI/README.md", 40)]));
        store.Upsert(GameId, AMod("ItemRack", files: [("ItemRack/ItemRack.toc", 80)]));

        Assert.Equal([ModId], store.FindOwnersOfPath(GameId, "pfUI/pfUI.toc"));
        Assert.Equal([ModId], store.FindOwnersOfPath(GameId, @"pfUI\pfUI.toc"));
        Assert.Empty(store.FindOwnersOfPath(GameId, "pfUI/other.lua"));

        if (PathComparer.PathComparison == StringComparison.Ordinal)
        {
            Assert.Empty(store.FindOwnersOfPath(GameId, "PFUI/PFUI.TOC"));

            return;
        }

        Assert.Equal([ModId], store.FindOwnersOfPath(GameId, "PFUI/PFUI.TOC"));
    }

    [Fact]
    public void FindFolderOwnersFindsTheOwner()
    {
        ManifestStore store = Store;

        store.Upsert(GameId, AMod(files: [("pfUI/pfUI.toc", 120)]));

        Assert.Equal([ModId], store.FindFolderOwners(GameId, "pfUI"));
        Assert.Empty(store.FindFolderOwners(GameId, "pfQuest"));
    }

    [Theory]
    [InlineData("/absolute/pfUI.toc")]
    [InlineData(@"C:\absolute\pfUI.toc")]
    [InlineData("../outside/pfUI.toc")]
    public void UpsertRefusesAFilePathThatIsNotRelativeToTheAddonFolder(string relativePath)
    {
        InstalledMod mod = AMod() with { Files = [new InstalledFileEntry(relativePath, 1)] };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => Store.Upsert(GameId, mod));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Store.List(GameId));
    }

    [Fact]
    public void UpsertStoresPortableSeparators()
    {
        Store.Upsert(GameId, AMod(files: [(@"plugins\pfQuest\pfQuest.toc", 10)]));

        InstalledFileEntry entry = Assert.Single(Store.GetInstalledFiles(GameId, ModId));

        Assert.Equal("plugins/pfQuest/pfQuest.toc", entry.RelativePath);
        Assert.Equal([ModId], Store.FindOwnersOfPath(GameId, "plugins/pfQuest/pfQuest.toc"));
        Assert.Contains("plugins/pfQuest/pfQuest.toc", File.ReadAllText(_paths.ManifestFile(GameId)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoreNeverStampsATimeItself()
    {
        // Acceptance criterion 9 as a test: InstalledAt and UpdatedAt come from the caller, so the only
        // clock read allowed here is the corruption-quarantine file name.
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryLayout.CoreDirectory, "Manifest", "ManifestStore.cs"));
        string source = string.Join('\n', lines);

        string[] clockReads = [.. lines.Where(line => line.Contains("UtcNow", StringComparison.Ordinal))];

        Assert.Single(clockReads);
        Assert.Contains("corrupt-", clockReads[0], StringComparison.Ordinal);

        Assert.DoesNotContain("InstalledAt =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatedAt =", source, StringComparison.Ordinal);
    }

    private static InstalledMod AMod(
        string id = ModId,
        string? folderName = null,
        params (string Path, long Size)[] files) => new()
        {
            Id = id,
            Name = id,
            SourceUrl = "https://github.com/brues-code/pfUI",
            SourceType = ModSourceType.Github,
            FolderName = folderName ?? id,
            Version = "0123456789abcdef0123456789abcdef01234567",
            VersionKind = "sha",
            Enabled = true,
            InstalledAt = InstalledAt,
            UpdatedAt = InstalledAt,
            Files = [.. files.Select(file => new InstalledFileEntry(file.Path, file.Size))],
        };

    private static void AssertSameMod(InstalledMod expected, InstalledMod actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.SourceUrl, actual.SourceUrl);
        Assert.Equal(expected.SourceType, actual.SourceType);
        Assert.Equal(expected.FolderName, actual.FolderName);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.VersionKind, actual.VersionKind);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.InstalledAt, actual.InstalledAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }
}
