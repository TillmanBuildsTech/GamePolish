using GamePolish.Core.Registry;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Registry;

/// <summary>
/// Covers what makes a registry payload unacceptable (.todo/211, acceptance criteria 8, 9 and 11): every
/// problem is reported together with a reason the author can act on, and an oversized body is refused
/// before a parser ever sees it.
/// </summary>
[Collection(StorageCollection.Name)]
public class RegistryValidationTests
{
    private const string RegistryUrl = "https://raw.githubusercontent.com/game-polish/registries/main/octowow.json";

    private readonly StorageFixture _fixture;

    public RegistryValidationTests(StorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AGameIdMismatchNamesBothIds()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(gameId: "skyrim")));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonGameIdMismatch, problem.Reason);
        Assert.Contains("skyrim", problem.Message, StringComparison.Ordinal);
        Assert.Contains("octowow", problem.Message, StringComparison.Ordinal);

        // The message, not just the problem list: this is what the log line and the details drawer show.
        Assert.Contains("skyrim", exception.Message, StringComparison.Ordinal);
        Assert.Contains("octowow", exception.Message, StringComparison.Ordinal);
        Assert.Equal(harness.GameId, exception.GameId);
        Assert.Equal(RegistryUrl, exception.RegistryUrl);
        Assert.False(File.Exists(harness.CacheFile));
    }

    [Fact]
    public async Task DuplicateIdsAMissingSourceAndABadIdAreAllReportedTogether()
    {
        using RegistryHarness harness = new(_fixture);

        string mods = string.Join(
            ",",
            Payloads.Mod(id: "pfui"),
            Payloads.Mod(id: "pfui"),
            Payloads.Mod(id: "AtlasLoot"),
            Payloads.Mod(id: "atlasloot", source: null));

        harness.Http.Respond(() => Responses.Json(Payloads.Valid(mods)));

        RegistryValidationException exception = await Reject(harness);

        // One fetch, three problems: the author fixes the file once.
        Assert.Equal(3, exception.Problems.Count);
        Assert.Equal(
            [
                RegistryProblem.ReasonDuplicateModId,
                RegistryProblem.ReasonInvalidModId,
                RegistryProblem.ReasonMissingField,
            ],
            exception.Problems.Select(problem => problem.Reason));

        Assert.Contains("mods[1] ('pfui')", exception.Problems[0].Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", exception.Problems[0].Message, StringComparison.Ordinal);

        Assert.Contains("'AtlasLoot'", exception.Problems[1].Message, StringComparison.Ordinal);
        Assert.Contains("not a valid mod id", exception.Problems[1].Message, StringComparison.Ordinal);

        Assert.Contains("mods[3] ('atlasloot')", exception.Problems[2].Message, StringComparison.Ordinal);
        Assert.Contains("source is missing", exception.Problems[2].Message, StringComparison.Ordinal);

        Assert.Equal("pfui", exception.Problems[0].ModId);
        Assert.All(exception.Problems, problem => Assert.False(string.IsNullOrWhiteSpace(problem.Message)));
        Assert.Contains("3 problem(s)", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.CacheFile));
    }

    [Fact]
    public async Task AMissingGameIdIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(gameId: string.Empty)));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMissingGameId, problem.Reason);
        Assert.Contains("game_id is missing", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingUpdatedAtIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(updatedAt: null)));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMissingUpdatedAt, problem.Reason);
        Assert.Contains("updated_at is missing", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullModsIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(
            "{\"game_id\": \"octowow\", \"updated_at\": \"2026-10-15T00:00:00Z\", \"mods\": null}"));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMissingMods, problem.Reason);
        Assert.Contains("mods is missing", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullTagsIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(Payloads.Mod(tags: "null"))));

        RegistryValidationException exception = await Reject(harness);

        // An empty array is fine; a JSON null is not, because the mod list has to be iterable without a
        // guard and "the field was forgotten" must not look like "this mod has no tags".
        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMissingTags, problem.Reason);
        Assert.Contains("tags is missing", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyModsArrayIsAValidRegistry()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(mods: string.Empty)));

        RegistryResult result = await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Offline);
        Assert.Empty(result.Registry.Mods);
        Assert.True(File.Exists(harness.CacheFile));
    }

    [Fact]
    public async Task ANonHttpsSourceIsRejected()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(Payloads.Mod(source: "http://github.com/brues-code/pfUI"))));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonNonHttpsSource, problem.Reason);
        Assert.Contains("http://", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialBearingSourceIsRejected()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(
            Payloads.Valid(Payloads.Mod(source: "https://brues:secret@github.com/brues-code/pfUI"))));

        RegistryValidationException exception = await Reject(harness);

        // The URL would be copied into registry-cache.json and then into the manifest, both plain text.
        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonCredentialInSource, problem.Reason);
        Assert.DoesNotContain("secret", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonGithubSourceIsOnlyAWarning()
    {
        using RegistryHarness harness = new(_fixture);

        // octowow.md's "Why This Game First" calls out Gitea mirrors as legitimate; 207 decides whether
        // it can clone one, so this must not stop the registry loading.
        harness.Http.Respond(() => Responses.Json(Payloads.Valid(Payloads.Mod(source: "https://gitea.example/owner/pfUI"))));

        RegistryResult result = await harness.NewSession().GetAsync(
            harness.GameId,
            RegistryUrl,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Offline);
        Assert.Single(result.Registry.Mods);
        Assert.True(File.Exists(harness.CacheFile));

        RegistryWarning warning = Assert.Single(harness.Warnings, candidate => candidate.Code == RegistryWarningCode.NonGithubSource);

        Assert.Equal("pfui", warning.ModId);
        Assert.Contains("gitea.example", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedBodyIsRejectedBeforeParsingAndWritesNothing()
    {
        using RegistryHarness harness = new(_fixture);

        // Deliberately not JSON: if the cap ran after parsing, this would report malformed-json instead.
        string body = new('x', RegistryService.MaximumPayloadBytes + 1024);
        harness.Http.Respond(() => Responses.Json(body));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonTooLarge, problem.Reason);
        Assert.Contains("A registry is a list of mods", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(exception.Problems, candidate => candidate.Reason == RegistryProblem.ReasonMalformedJson);
        Assert.False(File.Exists(harness.CacheFile));
    }

    [Fact]
    public async Task AnOversizedChunkedBodyIsRejectedWhileReading()
    {
        using RegistryHarness harness = new(_fixture);

        // No Content-Length: the cap's declared-length check cannot fire, so this exercises the streaming
        // half of it.
        harness.Http.Respond(() => Responses.JsonWithoutLength(new string('x', RegistryService.MaximumPayloadBytes + 1024)));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonTooLarge, problem.Reason);
        Assert.False(File.Exists(harness.CacheFile));
    }

    [Fact]
    public async Task AMalformedBodyIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json("{ this is not json"));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMalformedJson, problem.Reason);
        Assert.All(exception.Problems, candidate => Assert.False(string.IsNullOrWhiteSpace(candidate.Message)));
    }

    [Fact]
    public async Task AJsonNullPayloadIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json("null"));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMalformedJson, problem.Reason);
        Assert.Contains("JSON null", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModEntryThatIsNullIsNamed()
    {
        using RegistryHarness harness = new(_fixture);
        harness.Http.Respond(() => Responses.Json(Payloads.Valid("null")));

        RegistryValidationException exception = await Reject(harness);

        RegistryProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(RegistryProblem.ReasonMissingField, problem.Reason);
        Assert.Contains("mods[0] is null", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>Fetches a payload that must fail, and returns the exception.</summary>
    private static async Task<RegistryValidationException> Reject(RegistryHarness harness) =>
        await Assert.ThrowsAsync<RegistryValidationException>(
            () => harness.NewSession().GetAsync(
                harness.GameId,
                RegistryUrl,
                forceRefresh: false,
                TestContext.Current.CancellationToken));
}
