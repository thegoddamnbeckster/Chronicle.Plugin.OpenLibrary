using System.Text.Json;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OpenLibrary;

/// <summary>The series a book belongs to, as far as Open Library knows.</summary>
internal sealed record SeriesInfo(string? Name, string? Key, string? Position);

/// <summary>
/// Turns Open Library records into Chronicle's metadata shape. Nothing is invented: no poster for a series, no narrator, no
/// runtime. Everything Open Library supplied that has no first-class field is kept in ExtendedData.
/// </summary>
internal static class MetadataMapper
{
    public const string Source = "openlibrary";
    public const int MaxStoredIsbns = 30;

    // ── books ─────────────────────────────────────────────────────────────────

    /// <summary>A search result: what the search itself returned (no description, no subjects, no series).</summary>
    public static MediaMetadata WorkFromSearch(WorkDoc doc) => new()
    {
        ExternalId = doc.Olid,
        Source = Source,
        Title = doc.Title,
        Year = doc.FirstPublishYear,
        PosterUrl = doc.CoverId is > 0 ? TextRules.CoverUrl(doc.CoverId.Value) : null,
        Rating = RatingOf(doc),
        AlternateNames = AlternateTitles(doc.Title, null, doc.Subtitle),
        Cast = AuthorsOf(doc),
        ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["edition_count"] = doc.EditionCount,
            ["authors"] = AuthorList(doc),
            ["open_library_key"] = doc.Key,
        }),
    };

    /// <summary>The full record of a book: the search document (ratings, authors, ids) plus the work record (description,
    /// subjects, covers, series). Either may be missing.</summary>
    public static MediaMetadata Work(WorkDoc? doc, WorkRecord? record, SeriesInfo? series, string olid)
    {
        var title = !string.IsNullOrWhiteSpace(record?.Title) ? record!.Title : doc?.Title ?? "";
        var subtitle = !string.IsNullOrWhiteSpace(record?.Subtitle) ? record!.Subtitle : doc?.Subtitle;
        var subjects = record?.Subjects ?? [];
        var (genres, tags) = TextRules.SplitSubjects(subjects);

        var covers = RealIds(record?.Covers);
        var poster = doc?.CoverId is > 0 ? TextRules.CoverUrl(doc.CoverId.Value)
                   : covers.Count > 0 ? TextRules.CoverUrl(covers[0]) : null;

        var ids = new Dictionary<string, string>();
        AddFirst(ids, "goodreads", doc?.IdGoodreads);
        AddFirst(ids, "librarything", doc?.IdLibraryThing);
        AddFirst(ids, "amazon", doc?.IdAmazon);
        if (doc?.Isbn?.FirstOrDefault(i => i.Length == 13 && i.All(char.IsDigit)) is { } isbn13) ids["isbn13"] = isbn13;

        // Open Library's other identifiers are kept for reference but deliberately NOT under "ids": the host turns every key of
        // "ids" into a lookup for whichever plugin accepts it, and a "musicbrainz" value here names a book, not a recording.
        var external = new Dictionary<string, List<string>>();
        foreach (var (k, v) in record?.Identifiers ?? []) if (v.Count > 0) external[k] = v;
        if (doc?.IdWikidata is { Count: > 0 } wikidata && !external.ContainsKey("wikidata")) external["wikidata"] = wikidata;

        var extra = new Dictionary<string, object?>
        {
            ["ids"] = ids,
            ["external_ids"] = external,
            ["open_library_key"] = $"/works/{olid}",
            ["authors"] = doc is null ? null : AuthorList(doc),
            ["pages"] = doc?.PagesMedian,
            ["edition_count"] = doc?.EditionCount,
            ["languages"] = doc?.Language,
            ["first_publish_year"] = doc?.FirstPublishYear ?? TextRules.YearOf(record?.FirstPublishDate),
            ["ratings_average"] = doc?.RatingsAverage,
            ["ratings_count"] = doc?.RatingsCount,
            ["isbn"] = doc?.Isbn?.Take(MaxStoredIsbns).ToList(),
            ["isbn_count"] = doc?.Isbn?.Count,
            ["subjects"] = subjects.Count > 0 ? subjects : null,
            ["links"] = record?.Links?.Where(l => !string.IsNullOrWhiteSpace(l.Url)).Select(l => new { title = l.Title, url = l.Url }).ToList(),
        };
        if (series is { Name: not null } or { Key: not null })
            extra["series"] = new { name = series!.Name, key = series.Key, position = series.Position };

        return new MediaMetadata
        {
            ExternalId = olid,
            Source = Source,
            Title = title,
            Overview = TextRules.CleanMarkdown(TextRules.TextOf(record?.Description)),
            Year = doc?.FirstPublishYear ?? TextRules.YearOf(record?.FirstPublishDate),
            PosterUrl = poster,
            Rating = doc is null ? null : RatingOf(doc),
            Genres = genres,
            Tags = tags,
            AlternateNames = AlternateTitles(title, doc?.Title, subtitle),
            Cast = doc is null ? [] : AuthorsOf(doc),
            AdditionalImages = covers.Select(c => new AdditionalImage
            {
                Url = TextRules.CoverUrl(c), Type = "cover", ThumbnailUrl = TextRules.CoverUrl(c, 'M'),
            }).ToList(),
            ExtendedData = JsonSerializer.SerializeToElement(extra),
        };
    }

    // ── authors ───────────────────────────────────────────────────────────────

    public static MediaMetadata AuthorFromSearch(AuthorDoc doc) => new()
    {
        ExternalId = Olid(doc.Key),
        Source = Source,
        Title = doc.Name,
        AlternateNames = Distinct(doc.AlternateNames, except: doc.Name),
        ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["birth_date"] = doc.BirthDate,
            ["death_date"] = doc.DeathDate,
            ["top_work"] = doc.TopWork,
            ["work_count"] = doc.WorkCount,
            ["open_library_key"] = doc.Key,
        }),
    };

    public static MediaMetadata Author(AuthorRecord record, AuthorDoc? doc, string olid)
    {
        var photos = RealIds(record.Photos);

        var ids = new Dictionary<string, string>();
        var external = new Dictionary<string, string>();
        foreach (var (key, value) in record.RemoteIds ?? [])
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) continue;
            external[key] = value.GetString()!;
            if (key is "goodreads" or "librarything") ids[key] = value.GetString()!;
        }

        var alternates = Distinct(record.AlternateNames, except: record.Name);
        if (!string.IsNullOrWhiteSpace(record.PersonalName)
            && !string.Equals(record.PersonalName, record.Name, StringComparison.OrdinalIgnoreCase)
            && !alternates.Contains(record.PersonalName, StringComparer.OrdinalIgnoreCase))
            alternates.Add(record.PersonalName);

        return new MediaMetadata
        {
            ExternalId = olid,
            Source = Source,
            Title = record.Name,
            Overview = TextRules.CleanMarkdown(TextRules.TextOf(record.Bio)),
            PosterUrl = photos.Count > 0 ? TextRules.PhotoUrl(photos[0]) : null,
            AlternateNames = alternates,
            AdditionalImages = photos.Select(p => new AdditionalImage
            {
                Url = TextRules.PhotoUrl(p), Type = "photo", ThumbnailUrl = TextRules.PhotoUrl(p, 'M'),
            }).ToList(),
            ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["ids"] = ids,
                ["external_ids"] = external,
                ["open_library_key"] = $"/authors/{olid}",
                ["birth_date"] = record.BirthDate ?? doc?.BirthDate,
                ["death_date"] = record.DeathDate ?? doc?.DeathDate,
                ["entity_type"] = record.EntityType,
                ["work_count"] = doc?.WorkCount,
                ["top_work"] = doc?.TopWork,
                ["top_subjects"] = doc?.TopSubjects,
                ["links"] = record.Links?.Where(l => !string.IsNullOrWhiteSpace(l.Url)).Select(l => new { title = l.Title, url = l.Url }).ToList(),
            }),
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>The real image ids in a list: Open Library uses -1 (and, rarely, null) for "no image".</summary>
    internal static List<long> RealIds(IEnumerable<long?>? ids) =>
        (ids ?? []).Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();

    /// <summary>The bare id of an Open Library key ("/works/OL1W" gives "OL1W").</summary>
    public static string Olid(string key) => key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;

    private static double? RatingOf(WorkDoc doc) =>
        doc is { RatingsCount: > 0, RatingsAverage: > 0 } ? Math.Round(doc.RatingsAverage!.Value, 2) : null;

    private static List<CastMember> AuthorsOf(WorkDoc doc)
    {
        var names = doc.AuthorName ?? [];
        var keys = doc.AuthorKey ?? [];
        return names.Select((n, i) => new CastMember(n, "Author", i < keys.Count ? keys[i] : null)).ToList();
    }

    private static List<object> AuthorList(WorkDoc doc)
    {
        var names = doc.AuthorName ?? [];
        var keys = doc.AuthorKey ?? [];
        return names.Select((n, i) => (object)new { name = n, olid = i < keys.Count ? keys[i] : null }).ToList();
    }

    private static void AddFirst(Dictionary<string, string> into, string key, List<string>? values)
    {
        if (values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } first) into[key] = first;
    }

    private static List<string> Distinct(IEnumerable<string>? names, string? except) =>
        (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, except, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The other names a book answers to. "Atomic Habits" with the subtitle "An Easy &amp; Proven Way..." is also
    /// "Atomic Habits: An Easy &amp; Proven Way...", which is how a library often names it, and Chronicle's name check accepts a
    /// candidate under any of its alternate names.</summary>
    private static List<string> AlternateTitles(string title, string? otherTitle, string? subtitle)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(subtitle)) names.Add($"{title}: {subtitle.Trim()}");
        if (!string.IsNullOrWhiteSpace(otherTitle) && !string.Equals(otherTitle, title, StringComparison.Ordinal)) names.Add(otherTitle);
        return Distinct(names, except: title);
    }
}
