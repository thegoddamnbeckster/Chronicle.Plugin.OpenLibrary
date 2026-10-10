using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugin.OpenLibrary;

namespace Chronicle.Plugin.OpenLibrary.Tests;

/// <summary>A stand-in for openlibrary.org that answers from real recorded responses, records every request, and can fail on demand.</summary>
internal sealed class FakeOpenLibrary : HttpMessageHandler
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);

    private readonly List<(Func<string, bool> Match, Func<string> Body)> _routes = [];

    /// <summary>Every request, as the unescaped path and query ("/search.json?title=Ubik&amp;limit=10...").</summary>
    public List<string> Urls { get; } = [];
    public int Requests => Urls.Count;

    public HttpStatusCode? FailWith { get; set; }
    public int? RetryAfterSeconds { get; set; }
    public int FailTimes { get; set; } = int.MaxValue;
    public string? FailBody { get; set; }
    public byte[] ImageBytes { get; set; } = [1, 2, 3];

    public FakeOpenLibrary Route(Func<string, bool> match, string fixture) { _routes.Add((match, () => Fixture(fixture))); return this; }
    public FakeOpenLibrary RouteBody(Func<string, bool> match, string body) { _routes.Add((match, () => body)); return this; }

    public OpenLibraryClient Client(TimeSpan? interval = null) => new(new HttpClient(this), interval ?? TimeSpan.Zero);

    public static string EmptySearch => "{\"numFound\":0,\"docs\":[]}";

    /// <summary>The recorded answers for the books and authors the tests look up.</summary>
    public static FakeOpenLibrary Catalogue() => new FakeOpenLibrary()
        .Route(u => u.StartsWith("/search.json?title=Leviathan"), "search_leviathan.json")
        .Route(u => u.StartsWith("/search.json?title=Oathbringer"), "search_oathbringer.json")
        .RouteBody(u => u.StartsWith("/search.json?title=The Outpost") && u.Contains("author=Rick Partlow"), EmptySearch)
        .Route(u => u.StartsWith("/search.json?title=The Outpost"), "search_outpost_title_only.json")
        .Route(u => u.StartsWith("/search.json?title=Atomic habits"), "search_atomic_habits.json")
        .Route(u => u.StartsWith("/search.json?title=A Dance with Dragons"), "search_dance_with_dragons.json")
        .Route(u => u.StartsWith("/search.json?q=isbn:") || u.StartsWith("/search.json?q=id_goodreads:") || u.StartsWith("/search.json?q=key:\"/works/OL16114008W\""), "search_isbn.json")
        .Route(u => u.StartsWith("/search.json?author_key=OL6982995A"), "author_corey_works.json")
        .RouteBody(u => u.StartsWith("/search.json?author_key=OL5046634A"), "{\"docs\":[{\"key\":\"/works/OL1W\",\"title\":\"The Fault in Our Stars\"},{\"key\":\"/works/OL2W\",\"title\":\"Paper Towns\"},{\"key\":\"/works/OL3W\",\"title\":\"Looking for Alaska\"}]}")
        .RouteBody(u => u.StartsWith("/search.json?author_key="), "{\"docs\":[{\"key\":\"/works/OL9W\",\"title\":\"Counselling in HIV infection and AIDS\"}]}")
        .Route(u => u.StartsWith("/works/OL16114008W.json"), "work_leviathan.json")
        .Route(u => u.StartsWith("/works/OL17834026W.json"), "work_oathbringer_markdown.json")
        .Route(u => u.StartsWith("/authors/OL6982995A.json"), "author_corey.json")
        .Route(u => u.StartsWith("/authors/OL34221A.json"), "author_asimov.json")
        .Route(u => u.StartsWith("/search/authors.json?q=John Green"), "authors_john_green.json")
        .Route(u => u.StartsWith("/search/authors.json?q=James S. A. Corey"), "authors_corey.json")
        .Route(u => u.StartsWith("/series/OL334747L.json"), "series_expanse.json")
        .Route(u => u.StartsWith("/books/OL25001749M.json"), "edition_leviathan.json");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        Urls.Add(url);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestUri.Host.StartsWith("covers.") || request.RequestUri.Host.StartsWith("cdn.") || request.RequestUri.AbsolutePath.StartsWith("/static/"))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes) });

        if (FailWith is { } status && FailTimes-- > 0)
        {
            var failure = new HttpResponseMessage(status) { Content = new StringContent(FailBody ?? "{}", Encoding.UTF8, "application/json") };
            if (RetryAfterSeconds is { } after) failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(after));
            return Task.FromResult(failure);
        }

        foreach (var (match, body) in _routes)
            if (match(url))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
