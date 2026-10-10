using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.OpenLibrary;

/// <summary>
/// Chronicle metadata provider for Open Library (books, audiobooks and their authors). No API key is needed.
///
/// What it handles, by Chronicle's Author / Series / Book hierarchy:
///   level 0  Author  an author search, same-named authors told apart by work count, birth year and the titles the library holds
///   level 1  Series  NOT looked up: Open Library's series data is too sparse (about 1 in 20) to be worth the requests, so a
///                    series with books under it returns nothing without any network call. A level-1 item with no children is a
///                    standalone book and is searched as one.
///   level 2  Book    a search by main title and author, scored (see <see cref="Matchers"/>)
///
/// External ids (source "openlibrary") are bare Open Library ids: OL16114008W is a work, OL6982995A an author. Fix match
/// also takes an openlibrary.org URL, an ISBN, or "goodreads:" plus a Goodreads book id.
///
/// Per call the plugin makes at most about five requests (Chronicle allows 25 seconds); see <see cref="OpenLibraryClient"/> for
/// pacing and the rate-limit contract.
/// </summary>
public sealed class OpenLibraryMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => "chronicle.plugin.openlibrary";
    public string Name     => "Open Library";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    public const string KeyContactEmail = "contact_email";
    public const string KeyPreferredLanguage = "preferred_language";
    public const string DefaultLanguage = "eng";

    private static readonly ILogger Log = Serilog.Log.ForContext<OpenLibraryMetadataProvider>();

    private OpenLibraryClient? _client;
    private HttpClient? _ownedHttp;
    private bool _clientSupplied;
    private string _preferredLanguage = DefaultLanguage;

    /// <summary>What the last <see cref="Configure"/> chose (read by the tests).</summary>
    internal string PreferredLanguage => _preferredLanguage;
    internal string? ConfiguredUserAgent { get; private set; }
    internal TimeSpan? ConfiguredInterval { get; private set; }

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly TtlCache<WorkDoc> _docs = new(CacheFor);
    private readonly TtlCache<AuthorDoc> _authorDocs = new(CacheFor);
    private readonly TtlCache<List<string>> _authorTitles = new(CacheFor);
    private readonly TtlCache<WorkRecord> _works = new(CacheFor);
    private readonly TtlCache<AuthorRecord> _authors = new(CacheFor);
    private readonly TtlCache<string> _seriesNames = new(CacheFor);

    private const int SearchLimit = 10;
    private const int MaxCandidates = 5;
    private const int ConfidentScore = 70;
    private const int CloseScores = 12;   // authors scoring within this of the best are "close" and worth telling apart

    public OpenLibraryMetadataProvider() { }

    internal OpenLibraryMetadataProvider(OpenLibraryClient client, string preferredLanguage = DefaultLanguage)
    {
        _client = client;
        _clientSupplied = true;
        _preferredLanguage = preferredLanguage;
    }

    // ── capabilities ──────────────────────────────────────────────────────────

    private static readonly MediaTypeSupport[] SupportedTypes =
    [
        // Declared exactly as Hardcover declares them, so the types are created the same way whichever plugin is installed first.
        new MediaTypeSupport
        {
            MediaTypeName   = "books",
            DisplayName     = "Books",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            DefaultPriority = 20,   // lower numbers win: Hardcover is 10, so Open Library fills what Hardcover does not have
            SupportedFields = ["title", "overview", "year", "poster_url", "genres", "tags", "cast", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "audiobooks",
            DisplayName     = "Audiobooks",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            InteractionVerb = "listened",
            DefaultPriority = 20,
            SupportedFields = ["title", "overview", "year", "poster_url", "genres", "tags", "cast", "rating"],
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => SupportedTypes;

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["openlibrary:"];

    /// <summary>The languages offered for the preference, as Open Library's ISO 639-2 codes.</summary>
    internal static readonly (string Code, string Label)[] Languages =
    [
        ("eng", "English"), ("fre", "French"), ("ger", "German"), ("spa", "Spanish"), ("ita", "Italian"), ("por", "Portuguese"),
        ("dut", "Dutch"), ("swe", "Swedish"), ("nor", "Norwegian"), ("dan", "Danish"), ("fin", "Finnish"), ("pol", "Polish"),
        ("cze", "Czech"), ("hun", "Hungarian"), ("gre", "Greek"), ("rus", "Russian"), ("ukr", "Ukrainian"), ("tur", "Turkish"),
        ("heb", "Hebrew"), ("ara", "Arabic"), ("hin", "Hindi"), ("chi", "Chinese"), ("jpn", "Japanese"), ("kor", "Korean"),
        ("lat", "Latin"),
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = KeyPreferredLanguage, Label = "Preferred language", Type = SettingType.Dropdown, DefaultValue = DefaultLanguage,
                Description = "The language of the books in your library. Open Library lists many translations of a book; results " +
                              "that do not exist in this language rank lower. It never hides a result, and a title written in " +
                              "another alphabet is not penalised.",
                Options = Languages.Select(l => new SelectOption { Value = l.Code, Label = l.Label })
                    .Append(new SelectOption { Value = Matchers.AnyLanguage, Label = "No preference" }).ToList(),
            },
            new SettingDefinition
            {
                Key = KeyContactEmail, Label = "Contact email (optional)", Type = SettingType.Text,
                Description = "Open Library asks applications to identify themselves. With an email address in the request, it " +
                              "allows 3 requests a second instead of 1, which makes a large library about three times faster to " +
                              "fill in, and lets them contact you if something goes wrong. The address is sent only to " +
                              "openlibrary.org, in the User-Agent header, and is never stored anywhere else. Leave empty to stay anonymous.",
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var language = settings.TryGetValue(KeyPreferredLanguage, out var l) ? l?.Trim().ToLowerInvariant() : null;
        _preferredLanguage = language == Matchers.AnyLanguage || Languages.Any(x => x.Code == language) ? language! : DefaultLanguage;

        if (_clientSupplied) return;   // a client was supplied directly (tests)

        var email = settings.TryGetValue(KeyContactEmail, out var e) ? e?.Trim() : null;
        if (!IsPlausibleEmail(email)) email = null;

        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // An address with a character that is not allowed in a header is dropped: stay anonymous rather than fail to start.
        if (email is not null && !_ownedHttp.DefaultRequestHeaders.UserAgent.TryParseAdd(OpenLibraryClient.UserAgent(Version, email))) email = null;
        if (email is null) _ownedHttp.DefaultRequestHeaders.UserAgent.TryParseAdd(OpenLibraryClient.UserAgent(Version, null));
        ConfiguredUserAgent = OpenLibraryClient.UserAgent(Version, email);
        ConfiguredInterval = email is null ? OpenLibraryClient.DefaultInterval : OpenLibraryClient.IdentifiedInterval;
        _client = new OpenLibraryClient(_ownedHttp, ConfiguredInterval.Value, Log);
        Log.Information("Open Library configured ({Mode}, preferred language {Language})",
            email is null ? "anonymous, 1 request/second" : "identified, 3 requests/second", _preferredLanguage);
    }

    internal static bool IsPlausibleEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace) && value.Count(c => c == '@') == 1
        && value.IndexOf('@') > 0 && value.IndexOf('.', value.IndexOf('@')) > value.IndexOf('@') + 1 && !value.EndsWith('.');

    // ── search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (context.MediaTypeName is not (null or "books" or "audiobooks")) return [];

        // A series with books under it is never looked up (see the class summary): no request, whatever ids it carries.
        if (context.HierarchyLevel == 1 && context.ChildCount is > 0) return [];

        // An identifier the item already holds is the surest match.
        if (await KnownIdentifierAsync(context, ct).ConfigureAwait(false) is { } known) return [known];

        var titles = SearchTitles(context);
        if (titles.Count == 0) return [];

        // Free text (Add Media): the user typed something that may be a book or an author.
        var isFreeSearch = context.HierarchyLevel == 0 && !context.IsRealHierarchyPosition && context.ParentName is null;
        if (isFreeSearch)
        {
            var books = await SearchBooksAsync(titles, null, context.Year, authorOptional: true, ct).ConfigureAwait(false);
            // The year of a free-text search is a book's, not an author's birth year, so it is not used to judge authors.
            var authors = await SearchAuthorsAsync(titles[0], context, null, ct).ConfigureAwait(false);
            return books.Concat(authors).OrderByDescending(c => c.Score).Take(MaxCandidates).ToList();
        }

        switch (context.HierarchyLevel)
        {
            case 0:
                return await SearchAuthorsAsync(titles[0], context, context.Year, ct).ConfigureAwait(false);   // an author's "year" is their birth year

            // Level 1 is a series (returned above) unless it holds nothing, in which case it is a standalone book under its author.
            case 1:
                return await SearchBooksAsync(titles, context.ParentName, context.Year, authorOptional: false, ct).ConfigureAwait(false);

            default:
                return await SearchBooksAsync(titles, context.GrandparentName, context.Year, authorOptional: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The names an item is searched under: its own name, then at most one other, each without a "(YYYY)" suffix.</summary>
    private static List<string> SearchTitles(MediaSearchContext context)
    {
        var all = new List<string> { context.Name };
        if (!string.IsNullOrWhiteSpace(context.PreciseName)) all.Add(context.PreciseName);
        all.AddRange(context.AltTitles ?? []);
        return all.Select(TextRules.StripYearSuffix).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
    }

    // A book looked up for a library item with no author known can only be matched on its title, which many books share.
    // Such a match is kept below Chronicle's confidence gate (50) so it stays "not found" for the user to fix, instead of
    // attaching some other author's book. A free-text search (Add Media) is a person choosing from a list, so it is not capped.
    private const int NoAuthorCap = 45;

    // The wording the library uses for a book or an author that Open Library matched on (titles and names are compared with
    // punctuation, case and subtitles ignored) is another name for it. Chronicle's name check accepts a candidate under any of its
    // alternate names, so a correct match is not turned away because the library writes "Atomic habits: An Easy & Proven Way..."
    // where Open Library has "Atomic Habits". Only added for matches confident enough to be accepted at all.
    private const int AliasFromScore = 50;

    private static MediaMetadata WithAlias(MediaMetadata metadata, string wording, bool matched)
    {
        if (matched && !string.IsNullOrWhiteSpace(wording)
            && !string.Equals(wording, metadata.Title, StringComparison.OrdinalIgnoreCase)
            && !metadata.AlternateNames.Contains(wording, StringComparer.OrdinalIgnoreCase))
            metadata.AlternateNames.Add(wording);
        return metadata;
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchBooksAsync(
        List<string> titles, string? author, int? year, bool authorOptional, CancellationToken ct)
    {
        var scored = new Dictionary<string, (WorkDoc Doc, int Score, string Wording)>(StringComparer.Ordinal);

        foreach (var title in titles)
        {
            var query = new BookQuery(title, string.IsNullOrWhiteSpace(author) ? null : TextRules.TidyName(author), year, _preferredLanguage);

            // The main title first: long subtitles and "Part 2" markers break the search. Then the whole title.
            var main = TextRules.MainTitle(title);
            var attempts = new List<string> { main };
            if (!string.Equals(main, TextRules.StripYearSuffix(title), StringComparison.Ordinal)) attempts.Add(TextRules.StripYearSuffix(title));

            foreach (var attempt in attempts)
            {
                var docs = await _client!.SearchWorksAsync(attempt, query.Author, SearchLimit, ct).ConfigureAwait(false);
                var any = false;
                foreach (var doc in docs)
                {
                    var score = Matchers.ScoreBook(doc, query);
                    if (score <= 0 || string.IsNullOrEmpty(doc.Key)) continue;
                    if (query.Author is null && !authorOptional) score = Math.Min(score, NoAuthorCap);
                    any = true;
                    _docs.Set(doc.Olid, doc);
                    if (!scored.TryGetValue(doc.Olid, out var have) || score > have.Score) scored[doc.Olid] = (doc, score, TextRules.StripYearSuffix(title));
                }
                if (any) break;   // this form of the title found something: no need to try the next
            }

            if (scored.Count > 0 && scored.Values.Max(s => s.Score) >= ConfidentScore) break;   // a confident match: stop spending requests
        }

        return scored.Values.OrderByDescending(s => s.Score).ThenByDescending(s => s.Doc.EditionCount ?? 0)
            .Take(MaxCandidates)
            .Select(s => new ScoredCandidate(WithAlias(MetadataMapper.WorkFromSearch(s.Doc), s.Wording, s.Score >= AliasFromScore), s.Score, "title and author search"))
            .ToList();
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchAuthorsAsync(string name, MediaSearchContext context, int? birthYear, CancellationToken ct)
    {
        var tidy = TextRules.TidyName(name);
        var docs = await _client!.SearchAuthorsAsync(tidy, 6, ct).ConfigureAwait(false);
        var scored = ScoreAuthors(docs, tidy, birthYear);

        // No equal name: try the other way of writing it once ("Tolkien, J.R.R." / "R.A. Williamson").
        if (scored.Count == 0 && TextRules.NameVariant(tidy) is { } variant)
        {
            docs = await _client.SearchAuthorsAsync(variant, 6, ct).ConfigureAwait(false);
            scored = ScoreAuthors(docs, tidy, birthYear);
        }
        if (scored.Count == 0) return [];

        // Several people share the name and the library's own titles can tell them apart: ask the works of each close
        // candidate (at most three). Work count alone is not enough: the novelist John Green has 32 works in Open Library, a
        // different John Green has 381.
        var libraryTitles = context.ChildNames ?? [];
        if (scored.Count > 1 && context.IsRealHierarchyPosition && libraryTitles.Count > 0 && scored[0].Score - scored[1].Score <= CloseScores)
        {
            var close = scored.Where(s => scored[0].Score - s.Score <= CloseScores).Take(3).ToList();
            for (var i = 0; i < close.Count; i++)
            {
                var titlesOfAuthor = await AuthorTitlesAsync(MetadataMapper.Olid(close[i].Doc.Key), ct).ConfigureAwait(false);
                close[i] = (close[i].Doc, Math.Min(99, close[i].Score + Matchers.ChildTitleBonus(libraryTitles, titlesOfAuthor)));
            }
            scored = close.Concat(scored.Skip(close.Count)).OrderByDescending(s => s.Score).ToList();
        }

        foreach (var (doc, _) in scored) _authorDocs.Set(MetadataMapper.Olid(doc.Key), doc);
        return scored.Take(MaxCandidates)
            .Select(s => new ScoredCandidate(WithAlias(MetadataMapper.AuthorFromSearch(s.Doc), tidy, true), s.Score, "author name search"))
            .ToList();
    }

    private static List<(AuthorDoc Doc, int Score)> ScoreAuthors(List<AuthorDoc> docs, string name, int? birthYear) =>
        docs.Select(d => (Doc: d, Score: Matchers.ScoreAuthor(d, name, birthYear))).Where(s => s.Score > 0 && !string.IsNullOrEmpty(s.Doc.Key))
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Doc.WorkCount ?? 0).ToList();

    private async Task<List<string>> AuthorTitlesAsync(string authorOlid, CancellationToken ct)
    {
        if (_authorTitles.TryGet(authorOlid, out var cached)) return cached;
        var titles = await _client!.AuthorWorkTitlesAsync(authorOlid, 100, ct).ConfigureAwait(false);
        _authorTitles.Set(authorOlid, titles);
        return titles;
    }

    /// <summary>An Open Library id (or, for a book, a Goodreads id) the item already carries.</summary>
    private async Task<ScoredCandidate?> KnownIdentifierAsync(MediaSearchContext context, CancellationToken ct)
    {
        var ids = context.KnownExternalIds;
        if (ids is null) return null;

        if (ids.TryGetValue("openlibrary", out var own) && OlRef.Parse(own) is { } reference
            && IsRightKind(reference, context))
        {
            var metadata = await GetByIdAsync(own, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(metadata.Title)) return new ScoredCandidate(metadata, 100, "known Open Library id") { IdentifierMatch = true };
        }
        else if (context.HierarchyLevel != 0 && ids.TryGetValue("goodreads", out var goodreads) && OlRef.Parse("goodreads:" + goodreads) is { } gr)
        {
            var metadata = await GetByIdAsync("goodreads:" + gr.Value, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(metadata.Title)) return new ScoredCandidate(metadata, 100, "known Goodreads id") { IdentifierMatch = true };
        }
        return null;
    }

    /// <summary>A stored author id is only used for an author item, and a work or edition id only for a book.</summary>
    private static bool IsRightKind(OlRef reference, MediaSearchContext context) =>
        context.HierarchyLevel == 0 ? reference.Kind == OlRefKind.Author : reference.Kind is OlRefKind.Work or OlRefKind.Edition;

    // ── fetch by id ───────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var reference = OlRef.Parse(externalId);
        if (reference is null) return Empty(externalId);

        switch (reference.Kind)
        {
            case OlRefKind.Author:
                return await FetchAuthorAsync(reference.Value, ct).ConfigureAwait(false);

            case OlRefKind.Edition:
            {
                var edition = await _client!.GetEditionAsync(reference.Value, ct).ConfigureAwait(false);
                var workKey = edition?.Works?.FirstOrDefault()?.Key;
                return workKey is null ? Empty(externalId) : await FetchWorkAsync(MetadataMapper.Olid(workKey), null, ct).ConfigureAwait(false);
            }

            case OlRefKind.Isbn:
            case OlRefKind.Goodreads:
            {
                var query = reference.Kind == OlRefKind.Isbn ? $"isbn:{reference.Value}" : $"id_goodreads:{reference.Value}";
                var doc = (await _client!.SearchWorksByQueryAsync(query, 1, ct).ConfigureAwait(false)).FirstOrDefault();
                if (doc is null || string.IsNullOrEmpty(doc.Key)) return Empty(externalId);
                _docs.Set(doc.Olid, doc);
                return await FetchWorkAsync(doc.Olid, doc, ct).ConfigureAwait(false);
            }

            default:
                return await FetchWorkAsync(reference.Value, null, ct).ConfigureAwait(false);
        }
    }

    private async Task<MediaMetadata> FetchWorkAsync(string olid, WorkDoc? doc, CancellationToken ct)
    {
        if (doc is null && !_docs.TryGet(olid, out doc!))
        {
            doc = (await _client!.SearchWorksByQueryAsync($"key:\"/works/{olid}\"", 1, ct).ConfigureAwait(false)).FirstOrDefault();
            if (doc is not null) _docs.Set(olid, doc);
        }

        if (!_works.TryGet(olid, out var record))
        {
            record = await _client!.GetWorkAsync(olid, ct).ConfigureAwait(false);
            if (record is not null) _works.Set(olid, record);
        }
        if (record is null && doc is null) return Empty(olid);

        return MetadataMapper.Work(doc, record, await SeriesOfAsync(record, ct).ConfigureAwait(false), olid);
    }

    /// <summary>The book's series: Open Library's structured series when the work has one (one extra request for its name,
    /// remembered), otherwise a "series:Name" subject.</summary>
    private async Task<SeriesInfo?> SeriesOfAsync(WorkRecord? record, CancellationToken ct)
    {
        if (record is null) return null;

        var structured = record.Series?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Series?.Key));
        if (structured is not null)
        {
            var seriesOlid = MetadataMapper.Olid(structured.Series!.Key!);
            if (!_seriesNames.TryGet(seriesOlid, out var name))
            {
                name = (await _client!.GetSeriesAsync(seriesOlid, ct).ConfigureAwait(false))?.Name ?? "";
                _seriesNames.Set(seriesOlid, name);
            }
            return new SeriesInfo(string.IsNullOrWhiteSpace(name) ? null : name, seriesOlid, structured.PositionText);
        }

        var subject = record.Subjects?.FirstOrDefault(s => s.StartsWith("series:", StringComparison.OrdinalIgnoreCase));
        return subject is null ? null : new SeriesInfo(subject["series:".Length..].Trim(), null, null);
    }

    private async Task<MediaMetadata> FetchAuthorAsync(string olid, CancellationToken ct)
    {
        if (!_authors.TryGet(olid, out var record))
        {
            record = await _client!.GetAuthorAsync(olid, ct).ConfigureAwait(false);
            if (record is not null) _authors.Set(olid, record);
        }
        if (record is null) return Empty(olid);
        _authorDocs.TryGet(olid, out var doc);
        return MetadataMapper.Author(record, doc, olid);
    }

    private static MediaMetadata Empty(string externalId) => new() { ExternalId = externalId, Source = MetadataMapper.Source };

    // ── images and health ─────────────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        // Only Open Library's own image hosts: this plugin is asked for pictures it handed out, nothing else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host == "openlibrary.org" || uri.Host.EndsWith(".openlibrary.org", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Open Library only serves images from openlibrary.org.", nameof(url));
        return _client!.GetImageBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            Log.Warning("Open Library health check skipped: plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_clientSupplied) _client?.Dispose();
        _ownedHttp?.Dispose();
        _client = null;
        _ownedHttp = null;
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("Open Library plugin is not configured. Call Configure() first.");
    }
}
