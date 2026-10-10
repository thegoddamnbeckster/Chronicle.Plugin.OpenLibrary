using System.Net;
using System.Text.Json;
using Serilog;

namespace Chronicle.Plugin.OpenLibrary;

/// <summary>
/// HTTP wrapper for Open Library's public API (https://openlibrary.org). No key is needed.
///
/// Usage policy (openlibrary.org/developers/api): 1 request per second by default; 3 per second when requests identify the
/// application and carry a contact email in the User-Agent. Pacing here follows that: <see cref="DefaultInterval"/> without an
/// email, <see cref="IdentifiedInterval"/> with one. Chronicle gives a plugin 25 seconds per call, so a rate-limit answer is
/// never waited out beyond <see cref="MaxRetryWait"/>: it is thrown as an HTTP 429, which Chronicle reads as "the provider is
/// rate limited" and answers by leaving the item Pending instead of marking it not found. A 404 is a normal "not found" and
/// comes back as null. HTML is never scraped, and covers are only fetched when something asks for the image.
/// </summary>
internal sealed class OpenLibraryClient : IDisposable
{
    public const string BaseUrl = "https://openlibrary.org";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1100);
    public static readonly TimeSpan IdentifiedInterval = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(8);

    // Fields asked of search.json. Not the subject list or the full ISBN set per document: they are large and are read from the
    // work record only for the one book that matters.
    private const string WorkFields =
        "key,title,subtitle,author_name,author_key,author_alternative_name,first_publish_year,cover_i,edition_count,language," +
        "ratings_average,ratings_count,isbn,number_of_pages_median,id_goodreads,id_librarything,id_amazon,id_wikidata";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public int Requests { get; private set; }

    public OpenLibraryClient(HttpClient http, TimeSpan interval, ILogger? log = null)
    {
        _http = http;
        _interval = interval;
        _log = log ?? Serilog.Core.Logger.None;
    }

    /// <summary>The User-Agent Open Library asks for: the application's name, and a contact email when there is one.</summary>
    public static string UserAgent(string version, string? contactEmail) =>
        string.IsNullOrWhiteSpace(contactEmail)
            ? $"Chronicle-OpenLibrary/{version}"
            : $"Chronicle-OpenLibrary/{version} ({contactEmail.Trim()})";

    // ── searches ──────────────────────────────────────────────────────────────

    /// <summary>Works by title and, when known, author.</summary>
    public async Task<List<WorkDoc>> SearchWorksAsync(string title, string? author, int limit, CancellationToken ct)
    {
        var url = $"/search.json?title={Uri.EscapeDataString(title)}" +
                  (string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author)}") +
                  $"&limit={limit}&fields={WorkFields}";
        return (await GetAsync<SearchResponse>(url, ct).ConfigureAwait(false))?.Docs ?? [];
    }

    /// <summary>Works by a free query: <c>key:"/works/OL...W"</c>, <c>isbn:...</c>, <c>id_goodreads:...</c>, or plain text.</summary>
    public async Task<List<WorkDoc>> SearchWorksByQueryAsync(string query, int limit, CancellationToken ct)
    {
        var url = $"/search.json?q={Uri.EscapeDataString(query)}&limit={limit}&fields={WorkFields}";
        return (await GetAsync<SearchResponse>(url, ct).ConfigureAwait(false))?.Docs ?? [];
    }

    public async Task<List<AuthorDoc>> SearchAuthorsAsync(string name, int limit, CancellationToken ct)
    {
        var url = $"/search/authors.json?q={Uri.EscapeDataString(name)}&limit={limit}";
        return (await GetAsync<AuthorSearchResponse>(url, ct).ConfigureAwait(false))?.Docs ?? [];
    }

    /// <summary>The titles of an author's best-known works (most editions first); used to tell same-named authors apart.</summary>
    public async Task<List<string>> AuthorWorkTitlesAsync(string authorOlid, int limit, CancellationToken ct)
    {
        var url = $"/search.json?author_key={Uri.EscapeDataString(authorOlid)}&sort=editions&limit={limit}&fields=key,title";
        return (await GetAsync<SearchResponse>(url, ct).ConfigureAwait(false))?.Docs.Select(d => d.Title).ToList() ?? [];
    }

    // ── records ───────────────────────────────────────────────────────────────

    public Task<WorkRecord?> GetWorkAsync(string workOlid, CancellationToken ct) => GetAsync<WorkRecord>($"/works/{workOlid}.json", ct);

    public Task<AuthorRecord?> GetAuthorAsync(string authorOlid, CancellationToken ct) => GetAsync<AuthorRecord>($"/authors/{authorOlid}.json", ct);

    public Task<SeriesRecord?> GetSeriesAsync(string seriesOlid, CancellationToken ct) => GetAsync<SeriesRecord>($"/series/{seriesOlid}.json", ct);

    public Task<EditionRecord?> GetEditionAsync(string editionOlid, CancellationToken ct) => GetAsync<EditionRecord>($"/books/{editionOlid}.json", ct);

    // ── images ────────────────────────────────────────────────────────────────

    /// <summary>Downloads one image. Covers by id are not rate limited by Open Library, so this is not paced; it is only called
    /// when the application asks for the picture.</summary>
    public Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct) => _http.GetByteArrayAsync(url, ct);

    // ── health ────────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // The Open Library work for "Leviathan Wakes" is stable and small.
            var work = await GetWorkAsync("OL16114008W", ct).ConfigureAwait(false);
            return work is { Key.Length: > 0 };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Open Library health check failed");
            return false;
        }
    }

    // ── transport ─────────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken ct) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(pathAndQuery, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response);
                if (attempt == 0 && wait <= MaxRetryWait)
                {
                    _log.Warning("Open Library: rate limited; waiting {Seconds}s before one retry", wait.TotalSeconds);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw new HttpRequestException(
                    $"Open Library is rate limiting requests (retry in about {wait.TotalSeconds:0}s)", null, HttpStatusCode.TooManyRequests);
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Open Library answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<T>(body, Json);
            }
            catch (JsonException ex)
            {
                throw new HttpRequestException("Open Library returned an answer this plugin could not read", ex, response.StatusCode);
            }
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(60);
    }

    private async Task<HttpResponseMessage> SendAsync(string pathAndQuery, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _interval - (DateTimeOffset.UtcNow - _last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
            request.Headers.Accept.ParseAdd("application/json");
            Requests++;
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            return response;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
