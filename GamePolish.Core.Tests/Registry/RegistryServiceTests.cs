using System.Net;

using GamePolish.Core.Registry;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

// The test namespace ends in ".Registry", so the bare name resolves to that namespace rather than to the
// payload type: composition-unit aliases are consulted after every enclosing namespace. The alias gives
// the type a name of its own.
using RegistryPayload = GamePolish.Core.Registry.Registry;

namespace GamePolish.Core.Tests.Registry;

/// <summary>
/// Covers the fetch-and-cache half of .todo/211 (acceptance criteria 1-7, 10 and 12): a cold fetch and the
/// file it writes, the once-per-session rule, force refresh, conditional requests, the three offline
/// paths, the staleness boundary, and the promise that the cache is the only thing written.
/// </summary>
/// <remarks>
/// No test here opens a socket: every request goes through <see cref="RegistryFetchDouble"/>, which counts
/// them. That count is what makes "at most once per session" an assertion rather than a hope.
/// </remarks>
[Collection(StorageCollection.Name)]
public class RegistryServiceTests
{
    private const string RegistryUrl = "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json";

    private static readonly DateTimeOffset RegistryUpdatedAt = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    private readonly StorageFixture _fixture;

    public RegistryServiceTests(StorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AColdFetchWritesTheCacheAndReturnsTheParsedRegistry()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryResult result = await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            cancellationToken);

        Assert.False(result.FromCache);
        Assert.False(result.Offline);
        Assert.False(result.IsStale);
        Assert.Equal(harness.Clock.Now, result.LastFetched);
        Assert.Equal("octowow", result.Registry.GameId);
        Assert.Equal(RegistryUpdatedAt, result.Registry.UpdatedAt);
        Assert.Equal(["pfui", "atlasloot"], result.Registry.Mods.Select(mod => mod.Id));
        Assert.Equal(["ui", "essential"], result.Registry.Mods[0].Tags);
        Assert.Equal("https://example.invalid/pfui.png", result.Registry.Mods[0].Thumbnail);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), result.Registry.Mods[0].AddedAt);

        // Omitted optional members are null, not an exception and not a made-up value.
        Assert.Null(result.Registry.Mods[1].Thumbnail);
        Assert.Null(result.Registry.Mods[1].AddedAt);
        Assert.Equal(Payloads.AtlasLootSource, result.Registry.Mods[1].Source);

        Assert.Equal(1, harness.Http.CallCount);
        RecordedRequest request = harness.Http.LastRequest;
        Assert.Equal("GET", request.Method);
        Assert.Equal(new Uri(RegistryUrl), request.Uri);
        Assert.Null(request.IfNoneMatch);
        Assert.Contains("application/json", request.Accept, StringComparison.Ordinal);
        Assert.StartsWith("GamePolish/", request.UserAgent, StringComparison.Ordinal);

        Assert.True(File.Exists(harness.CacheFile));
        RegistryCacheFile cache = harness.ReadCache();
        Assert.Equal(RegistryCacheFile.CurrentSchemaVersion, cache.SchemaVersion);
        Assert.Equal("\"v1\"", cache.ETag);
        Assert.Equal(RegistryUrl, cache.SourceUrl);
        Assert.Equal(harness.Clock.Now, cache.LastFetched);
        Assert.Equal("octowow", cache.Registry.GameId);
        Assert.Equal(["pfui", "atlasloot"], cache.Registry.Mods.Select(mod => mod.Id));
    }

    [Fact]
    public async Task ASecondCallInTheSameSessionMakesNoRequest()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        RegistryService service = harness.NewSession();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryResult first = await service.GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);
        RegistryResult second = await service.GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        Assert.Equal(1, harness.Http.CallCount);
        Assert.Same(first.Registry, second.Registry);
        Assert.False(second.FromCache);
        Assert.False(second.Offline);
        Assert.Equal(first.LastFetched, second.LastFetched);
    }

    [Fact]
    public async Task ConcurrentCallsShareOneRequest()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        RegistryService service = harness.NewSession();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Five Discover opens against one service: one request, one result.
        RegistryResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 5)
                .Select(_ => service.GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken)));

        Assert.Equal(1, harness.Http.CallCount);
        Assert.All(results, result => Assert.False(result.Offline));
        Assert.All(results, result => Assert.Equal(2, result.Registry.Mods.Count));
    }

    [Fact]
    public async Task ForceRefreshMakesOneMoreRequestAndMovesLastFetched()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        RegistryService service = harness.NewSession();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryResult first = await service.GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        harness.Clock.Now = first.LastFetched.AddHours(1);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(Payloads.Mod()), etag: "\"v2\""));

        RegistryResult refreshed = await service.GetAsync(harness.GameId, RegistryUrl, forceRefresh: true, cancellationToken);

        Assert.Equal(2, harness.Http.CallCount);

        // A refresh is still a conditional request: the cache holds a validator, so it goes out.
        Assert.Equal("\"v1\"", harness.Http.LastRequest.IfNoneMatch);

        Assert.False(refreshed.FromCache);
        Assert.False(refreshed.Offline);
        Assert.Equal(harness.Clock.Now, refreshed.LastFetched);
        Assert.Single(refreshed.Registry.Mods);

        RegistryCacheFile cache = harness.ReadCache();
        Assert.Equal("\"v2\"", cache.ETag);
        Assert.Equal(harness.Clock.Now, cache.LastFetched);
        Assert.Single(cache.Registry.Mods);
    }

    [Fact]
    public async Task ASecondSessionRevalidatesWithIfNoneMatchAndA304KeepsThePayload()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryResult fetched = await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        // A new service instance is a new session: nothing in memory, so the cache is read and its ETag
        // becomes If-None-Match.
        harness.Clock.Now = fetched.LastFetched.AddHours(2);
        harness.Http.NotModified();

        RegistryResult revalidated = await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        Assert.Equal(2, harness.Http.CallCount);
        Assert.Equal("\"v1\"", harness.Http.LastRequest.IfNoneMatch);
        Assert.True(revalidated.FromCache);
        Assert.False(revalidated.Offline);
        Assert.Equal(fetched.LastFetched.AddHours(2), revalidated.LastFetched);
        Assert.Equal(fetched.Registry.Mods.Select(mod => mod.Id), revalidated.Registry.Mods.Select(mod => mod.Id));

        RegistryCacheFile cache = harness.ReadCache();
        Assert.Equal("\"v1\"", cache.ETag);
        Assert.Equal(fetched.LastFetched.AddHours(2), cache.LastFetched);

        // Only last_fetched moved: the payload the 304 vouched for is the one still on disk.
        Assert.Equal(RegistryUpdatedAt, cache.Registry.UpdatedAt);
        Assert.Equal(2, cache.Registry.Mods.Count);
        Assert.Equal(["ui", "essential"], cache.Registry.Mods[0].Tags);
    }

    [Fact]
    public async Task OfflineWithACacheReturnsItAndKeepsTheOldTimestamp()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryResult fetched = await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        harness.Clock.Now = fetched.LastFetched.AddDays(2);
        harness.Http.Throw(new HttpRequestException("Name or service not known"));

        RegistryResult offline = await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        Assert.True(offline.Offline);
        Assert.True(offline.FromCache);
        Assert.Equal(fetched.LastFetched, offline.LastFetched);
        Assert.False(offline.IsStale);
        Assert.Equal(2, offline.Registry.Mods.Count);

        // An offline read is a read: the cache keeps the timestamp of the fetch that filled it.
        Assert.Equal(fetched.LastFetched, harness.ReadCache().LastFetched);
        Assert.Contains(harness.Warnings, warning => warning.Code == RegistryWarningCode.FetchFailed);
    }

    [Fact]
    public async Task OfflineWithoutACacheThrowsTheBannerTextAndWritesNothing()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Throw(new HttpRequestException("connection refused"));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryUnavailableException exception = await Assert.ThrowsAsync<RegistryUnavailableException>(
            () => harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken));

        // The message is what the Discover banner shows, so it has to be the banner sentence.
        Assert.StartsWith(RegistryUnavailableException.BannerText, exception.Message, StringComparison.Ordinal);
        Assert.Contains("octowow", exception.Message, StringComparison.Ordinal);
        Assert.Equal(harness.GameId, exception.GameId);
        Assert.Equal(RegistryUrl, exception.RegistryUrl);
        Assert.Contains("connection refused", exception.Diagnostic, StringComparison.Ordinal);

        Assert.False(File.Exists(harness.CacheFile));
        Assert.False(Directory.Exists(harness.Paths.GameDirectory(harness.GameId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(harness.DataRoot));
        Assert.Contains(harness.Warnings, warning => warning.Code == RegistryWarningCode.FetchFailed);
    }

    [Fact]
    public async Task AnHttpFailureWithACacheIsOfflineAndNamesTheStatus()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        harness.Http.Status(HttpStatusCode.InternalServerError);
        RegistryResult offline = await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        Assert.True(offline.Offline);
        Assert.True(offline.FromCache);
        Assert.Contains(
            harness.Warnings,
            warning => warning.Code == RegistryWarningCode.FetchFailed && warning.Message.Contains("500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnHttpFailureWithoutACacheNamesTheStatusInTheDiagnostic()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Status(HttpStatusCode.Forbidden);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RegistryUnavailableException exception = await Assert.ThrowsAsync<RegistryUnavailableException>(
            () => harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken));

        Assert.Contains("403", exception.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("403", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbandonedRequestIsTreatedAsOfflineNotAsCancellation()
    {
        using RegistryHarness harness = new(_fixture);

        // What a client-side timeout looks like from the transport's side.
        harness.Http.Throw(new TaskCanceledException("the request was abandoned"));

        RegistryUnavailableException exception = await Assert.ThrowsAsync<RegistryUnavailableException>(
            () => harness.NewSession().GetAsync(
                harness.GameId,
                RegistryUrl,
                forceRefresh: false,
                TestContext.Current.CancellationToken));

        Assert.False(string.IsNullOrWhiteSpace(exception.Diagnostic));
        Assert.Contains(harness.Warnings, warning => warning.Code == RegistryWarningCode.FetchFailed);
    }

    [Fact]
    public async Task AnInvalidPayloadKeepsTheCacheByteForByteAndWarns()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);
        byte[] before = File.ReadAllBytes(harness.CacheFile);

        harness.Http.Respond(() => Responses.Json(
            "{\"game_id\": \"octowow\", \"updated_at\": \"2026-10-15T00:00:00Z\", \"mods\": null}",
            etag: "\"v2\""));

        RegistryResult result = await harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellationToken);

        Assert.True(result.Offline);
        Assert.True(result.FromCache);
        Assert.Equal(2, result.Registry.Mods.Count);
        Assert.Equal(before, File.ReadAllBytes(harness.CacheFile));
        Assert.Contains(
            harness.Warnings,
            warning => warning.Code == RegistryWarningCode.PayloadRejected
                && warning.Message.Contains("mods is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCacheIsTheOnlyFileWrittenUnderTheDataRoot()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));

        await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.StartsWith(harness.DataRoot, harness.CacheFile, StringComparison.Ordinal);

        // Parent holds the data root and nothing else, so any second file would be a write outside it.
        string[] files = [.. Directory.EnumerateFiles(harness.Parent, "*", SearchOption.AllDirectories)];
        Assert.Equal([harness.CacheFile], files);
    }

    [Fact]
    public void TheCacheFileUsesTheDesignNotesFieldNames()
    {
        using RegistryHarness harness = new(_fixture);

        AtomicJson.Write(harness.CacheFile, new RegistryCacheFile
        {
            ETag = "\"v1\"",
            SourceUrl = RegistryUrl,
            LastFetched = harness.Clock.Now,
            Registry = new RegistryPayload
            {
                GameId = harness.GameId,
                UpdatedAt = RegistryUpdatedAt,
                Mods =
                [
                    new RegistryMod
                    {
                        Id = "pfui",
                        Name = "pfUI",
                        Author = "brues-code",
                        Description = "A complete UI replacement.",
                        Source = Payloads.PfUiSource,
                        Tags = ["ui"],
                        AddedAt = harness.Clock.Now,
                    },
                ],
            },
        });

        string raw = File.ReadAllText(harness.CacheFile);

        Assert.Contains("\"schema_version\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"etag\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"source_url\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"last_fetched\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"registry\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"game_id\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"updated_at\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"added_at\"", raw, StringComparison.Ordinal);

        // "ETag" through the shared snake_case policy would be "e_tag"; the design notes say "etag".
        Assert.DoesNotContain("\"e_tag\"", raw, StringComparison.Ordinal);

        RegistryCacheFile roundTripped = harness.ReadCache();
        Assert.Equal("\"v1\"", roundTripped.ETag);
        Assert.Equal(harness.Clock.Now, roundTripped.LastFetched);
        Assert.Equal("pfui", roundTripped.Registry.Mods[0].Id);
    }

    [Fact]
    public async Task AnUnreadableCacheIsIgnoredAndReplaced()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Paths.EnsureGameDirectory(harness.GameId);
        File.WriteAllText(harness.CacheFile, "{ this is not json");
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));

        RegistryResult result = await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Offline);

        // Nothing to revalidate against, so the request is unconditional.
        Assert.Null(harness.Http.LastRequest.IfNoneMatch);
        Assert.Contains(harness.Warnings, warning => warning.Code == RegistryWarningCode.CacheUnreadable);
        Assert.Equal(2, harness.ReadCache().Registry.Mods.Count);
    }

    [Fact]
    public async Task ACacheForAnotherGameIsIgnored()
    {
        using RegistryHarness harness = new(_fixture);
        SeedCache(harness, harness.Clock.Now, gameId: "skyrim", etag: "\"v9\"");
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));

        RegistryResult result = await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.Null(harness.Http.LastRequest.IfNoneMatch);
        Assert.False(result.Offline);
        Assert.Contains(
            harness.Warnings,
            warning => warning.Code == RegistryWarningCode.CacheUnreadable
                && warning.Message.Contains("skyrim", StringComparison.Ordinal));
        Assert.Equal(harness.GameId, harness.ReadCache().Registry.GameId);
    }

    [Fact]
    public async Task AnEtagIsNotReplayedToADifferentUrl()
    {
        using RegistryHarness harness = new(_fixture);
        SeedCache(harness, harness.Clock.Now, sourceUrl: "https://example.invalid/other-registry.json", etag: "\"v9\"");
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(), etag: "\"v1\""));

        await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        // An ETag belongs to the URL that issued it; sending it elsewhere is how a 304 shows the wrong mods.
        Assert.Null(harness.Http.LastRequest.IfNoneMatch);
        Assert.Equal(RegistryUrl, harness.ReadCache().SourceUrl);
    }

    [Theory]
    [InlineData(10020, false)] // 6d 23h
    [InlineData(10080, false)] // exactly 7 days: still fresh, the comparison is strict
    [InlineData(10081, true)]  // 7d 1m
    public async Task StalenessIsMeasuredAtTheSevenDayBoundary(int ageMinutes, bool expected)
    {
        using RegistryHarness harness = new(_fixture);
        DateTimeOffset lastFetched = harness.Clock.Now - TimeSpan.FromMinutes(ageMinutes);
        SeedCache(harness, lastFetched);
        harness.Http.Throw(new HttpRequestException("the network is down"));

        RegistryResult result = await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Offline);
        Assert.Equal(lastFetched, result.LastFetched);
        Assert.Equal(expected, result.IsStale);
    }

    [Fact]
    public async Task CancellationBeforeTheFetchMakesNoRequest()
    {
        using RegistryHarness harness = new(_fixture);
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.NewSession().GetAsync(harness.GameId, RegistryUrl, forceRefresh: false, cancellation.Token));

        Assert.Equal(0, harness.Http.CallCount);
        Assert.False(File.Exists(harness.CacheFile));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/game-polish/registries/main/octowow.json")]
    [InlineData("ftp://example.invalid/octowow.json")]
    [InlineData("registries/octowow.json")]
    public async Task ANonHttpsRegistryUrlIsRefusedBeforeAnyRequest(string registryUrl)
    {
        using RegistryHarness harness = new(_fixture);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.NewSession().GetAsync(harness.GameId, registryUrl, forceRefresh: false, TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.Http.CallCount);
        Assert.False(File.Exists(harness.CacheFile));
    }

    [Fact]
    public async Task ACredentialBearingRegistryUrlIsRefused()
    {
        using RegistryHarness harness = new(_fixture);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.NewSession().GetAsync(
                harness.GameId,
                "https://token@raw.githubusercontent.com/game-polish/registries/main/octowow.json",
                forceRefresh: false,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.Http.CallCount);
    }

    [Fact]
    public async Task AGameIdThatIsNotOneDirectoryNameIsRefused()
    {
        using RegistryHarness harness = new(_fixture);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.NewSession().GetAsync(
                "../octowow",
                RegistryUrl,
                forceRefresh: false,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.Http.CallCount);
    }

    /// <summary>Writes a cache file directly, for the tests that start from an existing copy.</summary>
    private static void SeedCache(
        RegistryHarness harness,
        DateTimeOffset lastFetched,
        string? gameId = null,
        string? sourceUrl = null,
        string? etag = "\"v1\"")
    {
        AtomicJson.Write(harness.CacheFile, new RegistryCacheFile
        {
            ETag = etag,
            SourceUrl = sourceUrl ?? RegistryUrl,
            LastFetched = lastFetched,
            Registry = new RegistryPayload
            {
                GameId = gameId ?? harness.GameId,
                UpdatedAt = RegistryUpdatedAt,
                Mods =
                [
                    new RegistryMod
                    {
                        Id = "pfui",
                        Name = "pfUI",
                        Author = "brues-code",
                        Description = "A complete UI replacement.",
                        Source = Payloads.PfUiSource,
                        Tags = ["ui", "essential"],
                    },
                ],
            },
        });
    }
}
