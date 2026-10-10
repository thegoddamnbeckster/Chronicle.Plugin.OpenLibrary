using System.Text.Json;
using Chronicle.Plugin.OpenLibrary;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public class MapperTests
{
    private static WorkDoc Doc(string fixture, string olid) =>
        JsonSerializer.Deserialize<SearchResponse>(FakeOpenLibrary.Fixture(fixture))!.Docs.First(d => d.Olid == olid);

    private static T Read<T>(string fixture) => JsonSerializer.Deserialize<T>(FakeOpenLibrary.Fixture(fixture))!;

    private static JsonElement Ext(MediaMetadata m) => m.ExtendedData!.Value;

    // ── a full book ───────────────────────────────────────────────────────────

    [Fact]
    public void ABook_CarriesEverythingOpenLibraryGave()
    {
        var doc = Doc("search_leviathan.json", "OL16114008W");
        var record = Read<WorkRecord>("work_leviathan.json");

        var m = MetadataMapper.Work(doc, record, new SeriesInfo("The Expanse", "OL334747L", "1"), "OL16114008W");

        Assert.Equal("OL16114008W", m.ExternalId);
        Assert.Equal("openlibrary", m.Source);
        Assert.Equal("Leviathan Wakes", m.Title);
        Assert.StartsWith("When Captain Jim Holden", m.Overview);
        Assert.Equal(2009, m.Year);
        Assert.Equal("https://covers.openlibrary.org/b/id/7314237-L.jpg", m.PosterUrl);
        Assert.Equal(4.26, m.Rating);
        Assert.Equal(["Fiction", "Science Fiction"], m.Genres);
        Assert.Contains("Space warfare", m.Tags);
        Assert.DoesNotContain(m.Tags, t => t.StartsWith("series:"));
        Assert.DoesNotContain("Fiction", m.Tags);
        var author = Assert.Single(m.Cast);
        Assert.Equal(("James S. A. Corey", "Author", "OL6982995A"), (author.Name, author.Role, author.ExternalPersonId));
        Assert.True(m.AdditionalImages.Count >= 3);
        Assert.All(m.AdditionalImages, i => Assert.Equal("cover", i.Type));
    }

    [Fact]
    public void ABooksExtendedData_KeepsWhatHasNoFieldOfItsOwn()
    {
        var doc = Doc("search_leviathan.json", "OL16114008W");
        var record = Read<WorkRecord>("work_leviathan.json");

        var ext = Ext(MetadataMapper.Work(doc, record, new SeriesInfo("The Expanse", "OL334747L", "1"), "OL16114008W"));

        Assert.Equal("35182525", ext.GetProperty("ids").GetProperty("goodreads").GetString());
        Assert.Equal("10519662", ext.GetProperty("ids").GetProperty("librarything").GetString());
        Assert.Equal("B073HBQXMT", ext.GetProperty("ids").GetProperty("amazon").GetString());
        Assert.Equal(13, ext.GetProperty("ids").GetProperty("isbn13").GetString()!.Length);
        Assert.Equal("The Expanse", ext.GetProperty("series").GetProperty("name").GetString());
        Assert.Equal("1", ext.GetProperty("series").GetProperty("position").GetString());
        Assert.Equal(592, ext.GetProperty("pages").GetInt32());
        Assert.Equal(29, ext.GetProperty("edition_count").GetInt32());
        Assert.Equal(168, ext.GetProperty("ratings_count").GetInt32());
        Assert.Equal("/works/OL16114008W", ext.GetProperty("open_library_key").GetString());
        Assert.Contains("Space warfare", ext.GetProperty("subjects").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("series:The Expanse", ext.GetProperty("subjects").EnumerateArray().Select(e => e.GetString()));   // the full list is kept
        Assert.Equal("OL6982995A", ext.GetProperty("authors")[0].GetProperty("olid").GetString());
    }

    [Fact]
    public void OpenLibrarysOtherIdentifiers_AreNotOfferedToOtherPlugins()
    {
        var doc = Doc("search_leviathan.json", "OL16114008W");
        var record = Read<WorkRecord>("work_leviathan.json");

        var ext = Ext(MetadataMapper.Work(doc, record, null, "OL16114008W"));

        // "ids" is turned into lookups by whichever plugin accepts each key: a "musicbrainz" value here names a book.
        var ids = ext.GetProperty("ids").EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain("musicbrainz", ids);
        Assert.DoesNotContain("wikidata", ids);
        Assert.DoesNotContain("bookbrainz", ids);
        var external = ext.GetProperty("external_ids");
        Assert.True(external.TryGetProperty("musicbrainz", out _));
        Assert.True(external.TryGetProperty("wikidata", out _));
    }

    [Fact]
    public void TheIsbnsStored_AreCapped_AndTheTrueCountKept()
    {
        var doc = Doc("search_isbn.json", "OL16114008W");
        Assert.True(doc.Isbn!.Count > MetadataMapper.MaxStoredIsbns);

        var ext = Ext(MetadataMapper.Work(doc, null, null, "OL16114008W"));

        Assert.Equal(MetadataMapper.MaxStoredIsbns, ext.GetProperty("isbn").GetArrayLength());
        Assert.Equal(doc.Isbn.Count, ext.GetProperty("isbn_count").GetInt32());
    }

    [Fact]
    public void ASubtitle_BecomesAnAlternateName_SoALibraryTitleWithOneIsAccepted()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Atomic Habits", Subtitle = "An Easy & Proven Way to Build Good Habits" };

        var m = MetadataMapper.Work(doc, null, null, "OL1W");

        Assert.Contains("Atomic Habits: An Easy & Proven Way to Build Good Habits", m.AlternateNames);
        Assert.DoesNotContain("Atomic Habits", m.AlternateNames);
    }

    [Fact]
    public void WithoutASearchDocument_TheBookIsStillMapped_FromItsRecord()
    {
        var record = Read<WorkRecord>("work_leviathan.json");

        var m = MetadataMapper.Work(null, record, null, "OL16114008W");

        Assert.Equal("Leviathan Wakes", m.Title);
        Assert.NotNull(m.Overview);
        Assert.Equal("https://covers.openlibrary.org/b/id/7314237-L.jpg", m.PosterUrl);   // from the record's covers
        Assert.Null(m.Rating);
        Assert.Empty(m.Cast);
        Assert.Equal(["Fiction", "Science Fiction"], m.Genres);
    }

    [Fact]
    public void WithoutARecord_TheBookIsStillMapped_FromItsSearchDocument()
    {
        var doc = Doc("search_leviathan.json", "OL16114008W");

        var m = MetadataMapper.Work(doc, null, null, "OL16114008W");

        Assert.Equal("Leviathan Wakes", m.Title);
        Assert.Null(m.Overview);
        Assert.Empty(m.AdditionalImages);
        Assert.Empty(m.Genres);
    }

    [Fact]
    public void ARatingNeedsRatings()
    {
        var unrated = new WorkDoc { Key = "/works/OL1W", Title = "T", RatingsAverage = 0, RatingsCount = 0 };
        var rated = new WorkDoc { Key = "/works/OL1W", Title = "T", RatingsAverage = 3.14159, RatingsCount = 12 };

        Assert.Null(MetadataMapper.Work(unrated, null, null, "OL1W").Rating);
        Assert.Equal(3.14, MetadataMapper.Work(rated, null, null, "OL1W").Rating);
    }

    [Fact]
    public void ASeries_IsRecordedWhenKnown_AndOmittedWhenNot()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "T" };

        Assert.False(Ext(MetadataMapper.Work(doc, null, null, "OL1W")).TryGetProperty("series", out _));
        Assert.True(Ext(MetadataMapper.Work(doc, null, new SeriesInfo("The Expanse", null, null), "OL1W")).TryGetProperty("series", out _));
        Assert.True(Ext(MetadataMapper.Work(doc, null, new SeriesInfo(null, "OL1L", "2"), "OL1W")).TryGetProperty("series", out _));
    }

    [Fact]
    public void AFirstPublishDateFromTheRecord_ProvidesTheYear_WhenTheSearchHasNone()
    {
        var record = new WorkRecord { Key = "/works/OL1W", Title = "T", FirstPublishDate = "March 3, 1999" };

        Assert.Equal(1999, MetadataMapper.Work(null, record, null, "OL1W").Year);
    }

    [Fact]
    public void ACoverFromTheRecord_IsUsed_WhenTheSearchHasNoCoverId()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "T" };
        var record = new WorkRecord { Key = "/works/OL1W", Title = "T", Covers = [-1, 555] };

        Assert.Equal("https://covers.openlibrary.org/b/id/555-L.jpg", MetadataMapper.Work(doc, record, null, "OL1W").PosterUrl);
    }

    [Fact]
    public void AmazonLinksAndWorkLinks_AreKept()
    {
        var record = new WorkRecord { Key = "/works/OL1W", Title = "T", Links = [new LinkRecord { Title = "Official", Url = "https://example.org" }, new LinkRecord { Title = "Empty" }] };

        var ext = Ext(MetadataMapper.Work(null, record, null, "OL1W"));

        Assert.Equal(1, ext.GetProperty("links").GetArrayLength());
    }

    // ── a search result ───────────────────────────────────────────────────────

    [Fact]
    public void ASearchResult_HasWhatTheSearchReturned()
    {
        var doc = Doc("search_leviathan.json", "OL16114008W");

        var m = MetadataMapper.WorkFromSearch(doc);

        Assert.Equal("OL16114008W", m.ExternalId);
        Assert.Equal("Leviathan Wakes", m.Title);
        Assert.Equal(2009, m.Year);
        Assert.NotNull(m.PosterUrl);
        Assert.Single(m.Cast);
        Assert.Null(m.Overview);
        Assert.Equal(29, Ext(m).GetProperty("edition_count").GetInt32());
    }

    [Fact]
    public void ACoverlessSearchResult_HasNoPoster()
    {
        var m = MetadataMapper.WorkFromSearch(new WorkDoc { Key = "/works/OL1W", Title = "T" });

        Assert.Null(m.PosterUrl);
        Assert.Empty(m.Cast);
    }

    // ── authors ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnAuthor_HasABiographyAPhotoAndTheirOtherNames()
    {
        var record = Read<AuthorRecord>("author_corey.json");

        var m = MetadataMapper.Author(record, null, "OL6982995A");

        Assert.Equal("OL6982995A", m.ExternalId);
        Assert.Equal("James S. A. Corey", m.Title);
        Assert.StartsWith("James S.A. Corey is the pen name used by collaborators Daniel Abraham and Ty Franck.", m.Overview);
        Assert.Equal("https://covers.openlibrary.org/a/id/14853100-L.jpg", m.PosterUrl);
        Assert.Contains("James Corey", m.AlternateNames);
        Assert.DoesNotContain("James S. A. Corey", m.AlternateNames);
        Assert.Equal(3, m.AdditionalImages.Count);
        Assert.All(m.AdditionalImages, i => Assert.Equal("photo", i.Type));
    }

    [Fact]
    public void ThePlaceholderPhoto_IsNeverUsed()
    {
        var record = Read<AuthorRecord>("author_asimov.json");
        Assert.Contains(-1L, record.Photos!);

        var m = MetadataMapper.Author(record, null, "OL34221A");

        Assert.Equal("https://covers.openlibrary.org/a/id/7425151-L.jpg", m.PosterUrl);
        Assert.Equal(4, m.AdditionalImages.Count);
        Assert.DoesNotContain(m.AdditionalImages, i => i.Url.Contains("-1-"));
    }

    [Fact]
    public void AnAuthorWithOnlyThePlaceholder_HasNoPhoto()
    {
        var record = new AuthorRecord { Key = "/authors/OL1A", Name = "X", Photos = [-1] };

        var m = MetadataMapper.Author(record, null, "OL1A");

        Assert.Null(m.PosterUrl);
        Assert.Empty(m.AdditionalImages);
    }

    [Fact]
    public void AnAuthorsIdentifiers_OnlyGoodreadsAndLibraryThingAreOfferedToOtherPlugins()
    {
        var record = Read<AuthorRecord>("author_corey.json");

        var ext = Ext(MetadataMapper.Author(record, null, "OL6982995A"));

        var ids = ext.GetProperty("ids").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(ids, id => Assert.Contains(id, new[] { "goodreads", "librarything" }));
        var external = ext.GetProperty("external_ids");
        Assert.True(external.TryGetProperty("viaf", out _));
        Assert.True(external.TryGetProperty("wikidata", out _));
        Assert.True(external.TryGetProperty("musicbrainz", out _));
    }

    [Fact]
    public void AnAuthorsDatesLinksAndWorkCounts_AreKept()
    {
        var record = Read<AuthorRecord>("author_asimov.json");
        var doc = new AuthorDoc { Key = "/authors/OL34221A", Name = "Isaac Asimov", WorkCount = 1500, TopWork = "Foundation", TopSubjects = ["Fiction"] };

        var ext = Ext(MetadataMapper.Author(record, doc, "OL34221A"));

        Assert.Equal("2 January 1920", ext.GetProperty("birth_date").GetString());
        Assert.Equal("6 April 1992", ext.GetProperty("death_date").GetString());
        Assert.Equal(1500, ext.GetProperty("work_count").GetInt32());
        Assert.Equal("Foundation", ext.GetProperty("top_work").GetString());
        Assert.True(ext.GetProperty("links").GetArrayLength() > 0);
    }

    [Fact]
    public void APersonalName_IsAnAlternateName_ButNotWhenItIsTheName()
    {
        var other = new AuthorRecord { Key = "/authors/OL1A", Name = "Mark Twain", PersonalName = "Samuel Clemens" };
        var same = new AuthorRecord { Key = "/authors/OL1A", Name = "Mark Twain", PersonalName = "mark twain", AlternateNames = ["Mark Twain", "M. Twain", "m. twain"] };

        Assert.Equal(["Samuel Clemens"], MetadataMapper.Author(other, null, "OL1A").AlternateNames);
        Assert.Equal(["M. Twain"], MetadataMapper.Author(same, null, "OL1A").AlternateNames);
    }

    [Fact]
    public void ARemoteIdThatIsNotAString_IsIgnored()
    {
        var record = JsonSerializer.Deserialize<AuthorRecord>(
            "{\"key\":\"/authors/OL1A\",\"name\":\"X\",\"remote_ids\":{\"goodreads\":\"123\",\"odd\":5,\"empty\":\"\"}}")!;

        var ext = Ext(MetadataMapper.Author(record, null, "OL1A"));

        Assert.Equal("123", ext.GetProperty("ids").GetProperty("goodreads").GetString());
        Assert.False(ext.GetProperty("external_ids").TryGetProperty("odd", out _));
        Assert.False(ext.GetProperty("external_ids").TryGetProperty("empty", out _));
    }

    [Fact]
    public void AnAuthorSearchResult_HasTheirNameAndAlternateNames()
    {
        var doc = new AuthorDoc { Key = "OL6982995A", Name = "James S. A. Corey", AlternateNames = ["James Corey", "james s. a. corey"], WorkCount = 92, TopWork = "Leviathan Wakes" };

        var m = MetadataMapper.AuthorFromSearch(doc);

        Assert.Equal("OL6982995A", m.ExternalId);
        Assert.Equal(["James Corey"], m.AlternateNames);
        Assert.Equal(92, Ext(m).GetProperty("work_count").GetInt32());
    }

    [Fact]
    public void ImageIdLists_KeepOnlyRealIds_AndToleratePlaceholdersAndNulls()
    {
        Assert.Equal([7, 9], MetadataMapper.RealIds([-1, 7, null, 0, 9, 7]));
        Assert.Empty(MetadataMapper.RealIds([-1, null]));
        Assert.Empty(MetadataMapper.RealIds(null));
    }

    [Fact]
    public void ARecordWithNullImageIds_IsStillRead()
    {
        var record = JsonSerializer.Deserialize<WorkRecord>("{\"key\":\"/works/OL1W\",\"title\":\"T\",\"covers\":[null,-1,555]}")!;
        var author = JsonSerializer.Deserialize<AuthorRecord>("{\"key\":\"/authors/OL1A\",\"name\":\"X\",\"photos\":[null,321]}")!;

        Assert.Equal("https://covers.openlibrary.org/b/id/555-L.jpg", MetadataMapper.Work(null, record, null, "OL1W").PosterUrl);
        Assert.Equal("https://covers.openlibrary.org/a/id/321-L.jpg", MetadataMapper.Author(author, null, "OL1A").PosterUrl);
    }

    [Theory]
    [InlineData("/works/OL1W", "OL1W")]
    [InlineData("OL1W", "OL1W")]
    [InlineData("/authors/OL6982995A", "OL6982995A")]
    public void TheBareIdIsTakenFromAKey(string key, string expected) => Assert.Equal(expected, MetadataMapper.Olid(key));

    [Fact]
    public void AWorkSeriesPosition_CanBeAStringOrANumber()
    {
        var asString = JsonSerializer.Deserialize<WorkSeries>("{\"series\":{\"key\":\"/series/OL1L\"},\"position\":\"3\"}")!;
        var asNumber = JsonSerializer.Deserialize<WorkSeries>("{\"series\":{\"key\":\"/series/OL1L\"},\"position\":4}")!;
        var odd = JsonSerializer.Deserialize<WorkSeries>("{\"series\":{\"key\":\"/series/OL1L\"},\"position\":[1]}")!;
        var none = JsonSerializer.Deserialize<WorkSeries>("{\"series\":{\"key\":\"/series/OL1L\"}}")!;

        Assert.Equal("3", asString.PositionText);
        Assert.Equal("4", asNumber.PositionText);
        Assert.Null(odd.PositionText);
        Assert.Null(none.PositionText);
    }
}
