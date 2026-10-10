using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicle.Plugin.OpenLibrary;

// The parts of Open Library's JSON this plugin reads. Fields that can be either a string or an object
// ({"type": "/type/text", "value": "..."}) are kept as JsonElement and read through TextRules.TextOf.

/// <summary>One work in a <c>search.json</c> answer.</summary>
internal sealed class WorkDoc
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("author_name")] public List<string>? AuthorName { get; set; }
    [JsonPropertyName("author_key")] public List<string>? AuthorKey { get; set; }
    [JsonPropertyName("author_alternative_name")] public List<string>? AuthorAlternativeName { get; set; }
    [JsonPropertyName("first_publish_year")] public int? FirstPublishYear { get; set; }
    [JsonPropertyName("cover_i")] public long? CoverId { get; set; }
    [JsonPropertyName("edition_count")] public int? EditionCount { get; set; }
    [JsonPropertyName("language")] public List<string>? Language { get; set; }
    [JsonPropertyName("ratings_average")] public double? RatingsAverage { get; set; }
    [JsonPropertyName("ratings_count")] public int? RatingsCount { get; set; }
    [JsonPropertyName("isbn")] public List<string>? Isbn { get; set; }
    [JsonPropertyName("number_of_pages_median")] public int? PagesMedian { get; set; }
    [JsonPropertyName("id_goodreads")] public List<string>? IdGoodreads { get; set; }
    [JsonPropertyName("id_librarything")] public List<string>? IdLibraryThing { get; set; }
    [JsonPropertyName("id_amazon")] public List<string>? IdAmazon { get; set; }
    [JsonPropertyName("id_wikidata")] public List<string>? IdWikidata { get; set; }

    /// <summary>The bare Open Library id ("OL16114008W") of this work.</summary>
    public string Olid => Key.Contains('/') ? Key[(Key.LastIndexOf('/') + 1)..] : Key;
}

internal sealed class SearchResponse
{
    [JsonPropertyName("numFound")] public int NumFound { get; set; }
    [JsonPropertyName("docs")] public List<WorkDoc> Docs { get; set; } = [];
}

/// <summary>One author in a <c>search/authors.json</c> answer.</summary>
internal sealed class AuthorDoc
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alternate_names")] public List<string>? AlternateNames { get; set; }
    [JsonPropertyName("birth_date")] public string? BirthDate { get; set; }
    [JsonPropertyName("death_date")] public string? DeathDate { get; set; }
    [JsonPropertyName("top_work")] public string? TopWork { get; set; }
    [JsonPropertyName("work_count")] public int? WorkCount { get; set; }
    [JsonPropertyName("top_subjects")] public List<string>? TopSubjects { get; set; }
}

internal sealed class AuthorSearchResponse
{
    [JsonPropertyName("docs")] public List<AuthorDoc> Docs { get; set; } = [];
}

internal sealed class WorkSeries
{
    [JsonPropertyName("series")] public KeyRef? Series { get; set; }
    [JsonPropertyName("position")] public JsonElement? Position { get; set; }

    /// <summary>The position in the series as text; Open Library sends it as a string or, occasionally, a number.</summary>
    public string? PositionText => Position is { } p
        ? p.ValueKind switch { JsonValueKind.String => p.GetString(), JsonValueKind.Number => p.GetRawText(), _ => null }
        : null;
}

internal sealed class KeyRef
{
    [JsonPropertyName("key")] public string? Key { get; set; }
}

/// <summary>A full work record (<c>/works/OL...W.json</c>).</summary>
internal sealed class WorkRecord
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("description")] public JsonElement? Description { get; set; }
    [JsonPropertyName("subjects")] public List<string>? Subjects { get; set; }
    [JsonPropertyName("covers")] public List<long?>? Covers { get; set; }
    [JsonPropertyName("series")] public List<WorkSeries>? Series { get; set; }
    [JsonPropertyName("identifiers")] public Dictionary<string, List<string>>? Identifiers { get; set; }
    [JsonPropertyName("first_publish_date")] public string? FirstPublishDate { get; set; }
    [JsonPropertyName("links")] public List<LinkRecord>? Links { get; set; }
}

internal sealed class LinkRecord
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>A full author record (<c>/authors/OL...A.json</c>).</summary>
internal sealed class AuthorRecord
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("personal_name")] public string? PersonalName { get; set; }
    [JsonPropertyName("entity_type")] public string? EntityType { get; set; }
    [JsonPropertyName("bio")] public JsonElement? Bio { get; set; }
    [JsonPropertyName("birth_date")] public string? BirthDate { get; set; }
    [JsonPropertyName("death_date")] public string? DeathDate { get; set; }
    [JsonPropertyName("alternate_names")] public List<string>? AlternateNames { get; set; }
    [JsonPropertyName("photos")] public List<long?>? Photos { get; set; }
    [JsonPropertyName("remote_ids")] public Dictionary<string, JsonElement>? RemoteIds { get; set; }
    [JsonPropertyName("links")] public List<LinkRecord>? Links { get; set; }
}

internal sealed class SeriesRecord
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public JsonElement? Description { get; set; }
}

internal sealed class EditionRecord
{
    [JsonPropertyName("works")] public List<KeyRef>? Works { get; set; }
}
