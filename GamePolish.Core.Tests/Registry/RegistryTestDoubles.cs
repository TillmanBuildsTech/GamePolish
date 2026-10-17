using System.Net;
using System.Net.Http.Headers;
using System.Text;

using GamePolish.Core.Registry;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Registry;

/// <summary>
/// One request as the double saw it, captured at send time.
/// </summary>
/// <remarks>
/// Captured rather than kept as a reference to the <see cref="HttpRequestMessage"/>: the service disposes
/// the request once the response is read, so asserting on the message afterwards would hit a disposed
/// object. Headers are the interesting part of these tests, and they are gone with it.
/// </remarks>
internal sealed record RecordedRequest(string Method, Uri Uri, string? IfNoneMatch, string Accept, string UserAgent);

/// <summary>
/// The HTTP seam: records every request and answers with whatever the test queued, so no test in this
/// folder ever opens a socket.
/// </summary>
internal sealed class RegistryFetchDouble : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _responder = _ => Responses.Json(Payloads.Valid());

    /// <summary>How many requests reached the transport. The assertions on criteria 2, 3 and 4 read this.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>Every request, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The most recent request. Throws when there is none, which is the point of a call-count assert.</summary>
    public RecordedRequest LastRequest
    {
        get
        {
            lock (_gate)
            {
                return _requests[^1];
            }
        }
    }

    /// <summary>Answers every request with the same response, built fresh each time.</summary>
    public RegistryFetchDouble Respond(Func<HttpResponseMessage> response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return Respond(_ => response());
    }

    /// <summary>Answers every request with whatever the responder returns.</summary>
    public RegistryFetchDouble Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        _responder = responder;

        return this;
    }

    /// <summary>Fails every request the way a dead network does.</summary>
    public RegistryFetchDouble Throw(Exception exception) => Respond(_ => throw exception);

    /// <summary>Answers every request with a bare status code.</summary>
    public RegistryFetchDouble Status(HttpStatusCode status) => Respond(() => Responses.Status(status));

    /// <summary>Answers every request with a 304.</summary>
    public RegistryFetchDouble NotModified() => Respond(Responses.NotModified);

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RecordedRequest recorded = new(
            request.Method.Method,
            request.RequestUri!,
            Header(request, "If-None-Match"),
            request.Headers.Accept.ToString(),
            request.Headers.UserAgent.ToString());

        Func<HttpRequestMessage, HttpResponseMessage> responder;
        lock (_gate)
        {
            _requests.Add(recorded);
            responder = _responder;
        }

        return Task.FromResult(responder(request));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
}

/// <summary>Canned responses, none of which needs a network.</summary>
internal static class Responses
{
    public static HttpResponseMessage Json(string body, string? etag = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    /// <summary>
    /// A response whose body declares no <c>Content-Length</c>, like a chunked transfer. This is the only
    /// way to exercise the cap's streaming half: with a declared length the service rejects the body
    /// before reading it.
    /// </summary>
    public static HttpResponseMessage JsonWithoutLength(string body) => new(HttpStatusCode.OK)
    {
        Content = new UnmeasuredContent(body),
    };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    public static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);
}

/// <summary>An <see cref="HttpContent"/> that refuses to state its length, so it arrives chunked.</summary>
internal sealed class UnmeasuredContent : HttpContent
{
    private readonly byte[] _bytes;

    public UnmeasuredContent(string body) => _bytes = Encoding.UTF8.GetBytes(body);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(_bytes, 0, _bytes.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }
}

/// <summary>
/// Registry payloads as strings, with the fields a test wants to break left out or nulled.
/// </summary>
internal static class Payloads
{
    public const string PfUiSource = "https://github.com/brues-code/pfUI";

    public const string AtlasLootSource = "https://github.com/Otari98/AtlasLoot";

    /// <summary>A registry with the pfUI and AtlasLoot seed rows from octowow.md.</summary>
    public static string Valid(string? mods = null, string gameId = "octowow", string? updatedAt = "2026-10-15T00:00:00Z")
    {
        string body = "{" + $"\"game_id\": {Quote(gameId)}";

        if (updatedAt is not null)
        {
            body += $", \"updated_at\": {Quote(updatedAt)}";
        }

        body += $", \"mods\": [{mods ?? DefaultMods}]}}";

        return body;
    }

    /// <summary>Two well-formed mods, one with the optional fields and one without.</summary>
    public static string DefaultMods =>
        Mod(id: "pfui", thumbnail: "https://example.invalid/pfui.png", addedAt: "2026-10-01T00:00:00Z")
        + ","
        + Mod(
            id: "atlasloot",
            name: "AtlasLoot",
            author: "Otari98",
            description: "A loot browser.",
            source: AtlasLootSource,
            tags: "[\"items\"]");

    /// <summary>
    /// One mod entry. Pass <see langword="null"/> for a field to leave it out of the JSON entirely; pass
    /// the string <c>"null"</c> for tags to write an explicit JSON null.
    /// </summary>
    public static string Mod(
        string? id = "pfui",
        string? name = "pfUI",
        string? author = "brues-code",
        string? description = "A complete UI replacement for vanilla WoW.",
        string? source = PfUiSource,
        string? tags = "[\"ui\", \"essential\"]",
        string? thumbnail = null,
        string? addedAt = null)
    {
        List<string> fields = [];

        Add("id", id is null ? null : Quote(id));
        Add("name", name is null ? null : Quote(name));
        Add("author", author is null ? null : Quote(author));
        Add("description", description is null ? null : Quote(description));
        Add("source", source is null ? null : Quote(source));
        Add("tags", tags);
        Add("thumbnail", thumbnail is null ? null : Quote(thumbnail));
        Add("added_at", addedAt is null ? null : Quote(addedAt));

        return "{ " + string.Join(", ", fields) + " }";

        void Add(string field, string? literal)
        {
            if (literal is not null)
            {
                fields.Add($"\"{field}\": {literal}");
            }
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";
}

/// <summary>A clock a test can move forward, so <c>last_fetched</c> can be observed changing.</summary>
internal sealed class AdjustableTimeProvider : TimeProvider
{
    public AdjustableTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Everything a registry test needs: an isolated data root under the shared fixture, the HTTP double, a
/// clock, and the warnings the service produced.
/// </summary>
/// <remarks>
/// The data root is a child of <see cref="Parent"/>, and nothing else is created under
/// <see cref="Parent"/>. That two-level layout is what makes acceptance criterion 12 checkable: a fetch
/// may add files under <see cref="DataRoot"/> and nowhere else, so the only file under
/// <see cref="Parent"/> must be the cache.
/// </remarks>
internal sealed class RegistryHarness : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public RegistryHarness(StorageFixture fixture, string? gameId = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        GameId = gameId ?? "octowow";
        Parent = fixture.NewSubdirectory($"registry-{Guid.NewGuid():N}");
        DataRoot = Path.Combine(Parent, "data");

        Directory.CreateDirectory(DataRoot);

        Paths = new GamePolishPaths(DataRoot);
        Clock = new AdjustableTimeProvider(Start);

        // The client's own timeout is disabled: the service enforces its own 15 s through a token, and a
        // second timeout firing first would test the wrong thing.
        Client = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string GameId { get; }

    /// <summary>The directory that holds the data root. Nothing else may appear in it.</summary>
    public string Parent { get; }

    /// <summary>The data root the service is constructed over.</summary>
    public string DataRoot { get; }

    public GamePolishPaths Paths { get; }

    public RegistryFetchDouble Http { get; } = new();

    public AdjustableTimeProvider Clock { get; }

    public HttpClient Client { get; }

    /// <summary>Every warning the service raised, in order.</summary>
    public List<RegistryWarning> Warnings { get; } = [];

    public string CacheFile => Paths.RegistryCacheFile(GameId);

    /// <summary>
    /// A new <see cref="RegistryService"/>, i.e. a new session: it has no in-memory results and will read
    /// the cache and send a conditional request.
    /// </summary>
    public RegistryService NewSession() => new(Paths, Client, Clock, Warnings.Add);

    /// <summary>The cache file, parsed. Fails loudly when it was not written.</summary>
    public RegistryCacheFile ReadCache() => AtomicJson.Read<RegistryCacheFile>(CacheFile)!;

    public void Dispose() => Client.Dispose();
}
