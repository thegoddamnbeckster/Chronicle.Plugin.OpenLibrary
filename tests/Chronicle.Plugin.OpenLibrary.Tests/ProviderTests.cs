using System.Net;
using Chronicle.Plugin.OpenLibrary;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public class ProviderTests
{
    private static OpenLibraryMetadataProvider Provider(FakeOpenLibrary fake, string language = "eng") => new(fake.Client(), language);

    private static MediaSearchContext Book(string name, string? author, string? series = "The Expanse", int? year = null, string type = "books") =>
        new(name, Year: year, ParentName: series, GrandparentName: author, HierarchyLevel: 2, MediaTypeName: type, IsRealHierarchyPosition: true);

    // ══ books ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ABookInASeries_IsFoundByTitleAndTheAuthorAbove_InOneRequest()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(Book("Leviathan Wakes", "James S. A. Corey", year: 2011));

        var best = result[0];
        Assert.Equal("OL16114008W", best.Metadata.ExternalId);
        Assert.Equal("openlibrary", best.Metadata.Source);
        Assert.True(best.Score >= 86, $"score {best.Score}");
        Assert.False(best.IdentifierMatch);
        Assert.Equal(1, fake.Requests);
        Assert.Contains("author=James S. A. Corey", fake.Urls[0]);
    }

    [Fact]
    public async Task AudiobooksAreSearchedTheSameWay()
    {
        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(Book("Oathbringer", "Brandon Sanderson", type: "audiobooks"));

        Assert.Equal("OL17834026W", result[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task ATrailingYear_IsNotSearchedFor()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(Book("Oathbringer (2017)", "Brandon Sanderson", "The Cosmere"));

        Assert.Equal("OL17834026W", result[0].Metadata.ExternalId);
        Assert.StartsWith("/search.json?title=Oathbringer&author=", fake.Urls[0]);
    }

    [Fact]
    public async Task ALongSubtitledTitle_IsSearchedByItsMainTitle_AndTheRealBookOutranksTheWorkbook()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(Book(
            "Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones (2018)", "James Clear", null, 2018));

        Assert.StartsWith("/search.json?title=Atomic habits&author=James Clear", fake.Urls[0]);
        Assert.Equal("OL17930368W", result[0].Metadata.ExternalId);
        Assert.Equal(1, fake.Requests);
        Assert.True(result[0].Score >= 80);
        var workbook = result.FirstOrDefault(r => r.Metadata.Title == "Atomic Habits Workbook");
        Assert.True(workbook is null || workbook.Score < result[0].Score - 30);
    }

    [Fact]
    public async Task TheLibrarysOwnWording_IsOfferedAsAnAlias_OfAConfidentMatch()
    {
        // Chronicle accepts a candidate under any alternate name, and Open Library calls this book just "Atomic Habits".
        const string wording = "Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones";

        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(Book(wording + " (2018)", "James Clear", null, 2018));

        Assert.Equal("Atomic Habits", result[0].Metadata.Title);
        Assert.Contains(wording, result[0].Metadata.AlternateNames);
        Assert.DoesNotContain(wording + " (2018)", result[0].Metadata.AlternateNames);   // without the year
    }

    [Fact]
    public async Task TheAlias_IsNotOffered_ForAMatchTooWeakToBeAccepted()
    {
        const string wording = "Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones";
        var noAuthor = new MediaSearchContext(wording, HierarchyLevel: 2, MediaTypeName: "books", IsRealHierarchyPosition: true);   // capped below the gate

        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(noAuthor);

        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.DoesNotContain(wording, r.Metadata.AlternateNames));
    }

    [Fact]
    public async Task TheLibrarysSpellingOfAnAuthor_IsOfferedAsAnAlias_WithoutDuplicates()
    {
        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(AuthorContext("James S.A. Corey"));

        var names = result[0].Metadata.AlternateNames;
        Assert.Contains("James S.A. Corey", names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(result[0].Metadata.Title, names);
    }

    [Fact]
    public async Task APositionMarker_IsLeftOutOfTheSearch()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(Book("A Dance with Dragons, Part 1: Dreams and Dust", "George R.R. Martin", "A Song of Ice and Fire"));

        Assert.StartsWith("/search.json?title=A Dance with Dragons&author=George R.R. Martin", fake.Urls[0]);
        Assert.Equal("OL1955906W", result[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task ABookByAnotherAuthor_IsNotAccepted()
    {
        // Open Library has "The Outpost" by Mike Resnick; the library's is by Rick Partlow: nothing under the author, nothing is returned.
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(Book("The Outpost", "Rick Partlow", "Birthright"));

        Assert.Empty(result);
        Assert.Equal(1, fake.Requests);   // no second guess without the author
    }

    [Fact]
    public async Task ABookWhoseAuthorIsNotKnown_NeverReachesChroniclesGate()
    {
        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(
            new MediaSearchContext("The Outpost", HierarchyLevel: 2, MediaTypeName: "books", IsRealHierarchyPosition: true));

        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.True(r.Score <= 45, $"{r.Metadata.Title} scored {r.Score}"));
    }

    [Fact]
    public async Task ASeriesWithBooksUnderIt_IsNotLookedUp_AndMakesNoRequests()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var context = new MediaSearchContext("The Expanse", ParentName: "James S. A. Corey", HierarchyLevel: 1, ChildCount: 9,
            MediaTypeName: "books", IsRealHierarchyPosition: true);

        var result = await Provider(fake).SearchAsync(context);

        Assert.Empty(result);
        Assert.Equal(0, fake.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task ALevelOneItemWithNoBooksUnderIt_IsAStandaloneBook(int? childCount)
    {
        var fake = FakeOpenLibrary.Catalogue();
        var context = new MediaSearchContext("Leviathan Wakes (2011)", Year: 2011, ParentName: "James S. A. Corey", HierarchyLevel: 1,
            ChildCount: childCount, MediaTypeName: "books", IsRealHierarchyPosition: true);

        var result = await Provider(fake).SearchAsync(context);

        Assert.Equal("OL16114008W", result[0].Metadata.ExternalId);
        Assert.Contains("author=James S. A. Corey", fake.Urls[0]);
    }

    [Fact]
    public async Task AnAlternateTitle_IsTriedWhenTheFirstFindsNothing()
    {
        var fake = FakeOpenLibrary.Catalogue().RouteBody(u => u.StartsWith("/search.json?title=Les 5 langages"), FakeOpenLibrary.EmptySearch);
        var context = new MediaSearchContext("Les 5 langages", GrandparentName: "James S. A. Corey", HierarchyLevel: 2, MediaTypeName: "books",
            AltTitles: ["Les 5 langages", "Leviathan Wakes"]);

        var result = await Provider(fake).SearchAsync(context);

        Assert.Equal("OL16114008W", result[0].Metadata.ExternalId);
        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task ASecondTitleIsNotTried_OnceTheFirstMatchedConfidently()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var context = new MediaSearchContext("Leviathan Wakes", GrandparentName: "James S. A. Corey", HierarchyLevel: 2, MediaTypeName: "books",
            AltTitles: ["Leviathan Wakes", "Oathbringer"]);

        await Provider(fake).SearchAsync(context);

        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task ASearchThatFindsNothing_StaysWithinTheRequestBudget()
    {
        var fake = new FakeOpenLibrary();
        var context = new MediaSearchContext("Foo: Bar", PreciseName: "Baz: Qux", GrandparentName: "Nobody", HierarchyLevel: 2, MediaTypeName: "books",
            AltTitles: ["Foo: Bar", "Another: Name", "A third: Name"]);

        var result = await Provider(fake).SearchAsync(context);

        Assert.Empty(result);
        Assert.InRange(fake.Requests, 1, 4);   // Chronicle gives a plugin 25 seconds; each request costs about a second
    }

    [Fact]
    public async Task EveryCandidateIsAnOpenLibraryWork_AndAtMostFiveAreReturned()
    {
        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(Book("Atomic habits", null, null));

        Assert.InRange(result.Count, 1, 5);
        Assert.All(result, r => Assert.StartsWith("OL", r.Metadata.ExternalId));
        Assert.Equal(result.OrderByDescending(r => r.Score).Select(r => r.Score), result.Select(r => r.Score));
    }

    [Fact]
    public async Task ThePreferredLanguage_ChangesTheRanking_NotTheSet()
    {
        var english = (await Provider(FakeOpenLibrary.Catalogue(), "eng").SearchAsync(Book("Leviathan Wakes", "James S. A. Corey")))[0];
        var italian = (await Provider(FakeOpenLibrary.Catalogue(), "ita").SearchAsync(Book("Leviathan Wakes", "James S. A. Corey")))[0];

        Assert.Equal(english.Metadata.ExternalId, italian.Metadata.ExternalId);   // the same book is found either way
        Assert.True(italian.Score >= english.Score);                                // listed in Italian too, so not penalised there
    }

    // ══ authors ═══════════════════════════════════════════════════════════════

    private static MediaSearchContext AuthorContext(string name, int? year = null, string[]? children = null) =>
        new(name, Year: year, HierarchyLevel: 0, MediaTypeName: "books", IsRealHierarchyPosition: true, ChildNames: children);

    [Fact]
    public async Task AnAuthor_IsFoundByName()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(AuthorContext("James S. A. Corey"));

        Assert.Equal("OL6982995A", result[0].Metadata.ExternalId);
        Assert.True(result[0].Score >= 60);
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task SeveralPeopleSharingAName_AreToldApartByTheBooksTheLibraryHolds()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(AuthorContext("John Green", children: ["The Fault in Our Stars", "Paper Towns"]));

        // Of the six John Greens, the novelist has 32 works and another has 381: the library's titles decide.
        Assert.Equal("OL5046634A", result[0].Metadata.ExternalId);
        Assert.InRange(fake.Requests, 2, 4);
        Assert.Contains(fake.Urls, u => u.StartsWith("/search.json?author_key=OL5046634A"));
    }

    [Fact]
    public async Task WithNothingToTellThemApart_TheAuthorWithTheMostWorksWins_WithoutExtraRequests()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(AuthorContext("John Green"));

        Assert.Equal("OL265902A", result[0].Metadata.ExternalId);   // 381 works
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task TheBirthYear_TellsSameNamedAuthorsApart()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(AuthorContext("John Green", year: 1977));

        Assert.Equal("OL5046634A", result[0].Metadata.ExternalId);
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task ChildTitles_AreOnlyAskedAboutWhenTheAuthorsAreCloseAndTheItemIsReal()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var notReal = new MediaSearchContext("John Green", HierarchyLevel: 0, MediaTypeName: "books", ParentName: "someone", IsRealHierarchyPosition: false,
            ChildNames: ["Paper Towns"]);

        await Provider(fake).SearchAsync(notReal);

        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task AnAuthorUnknownToOpenLibrary_TriesTheOtherSpellingOnce_ThenGivesUp()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(AuthorContext("R.A. Williamson"));

        Assert.Empty(result);
        Assert.Equal(2, fake.Requests);
        Assert.Contains("/search/authors.json?q=R. A. Williamson&limit=6", fake.Urls);
    }

    [Fact]
    public async Task AnAuthorWrittenLastNameFirst_IsFoundUnderTheOtherSpelling()
    {
        var fake = FakeOpenLibrary.Catalogue().Route(u => u.StartsWith("/search/authors.json?q=James S.A. Corey"), "authors_corey.json");

        var result = await Provider(fake).SearchAsync(AuthorContext("Corey, James S.A."));

        Assert.Equal("OL6982995A", result[0].Metadata.ExternalId);
        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task AnAuthorNameWithAnyOtherName_IsNotAccepted()
    {
        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(AuthorContext("John Greene"));

        Assert.Empty(result);
    }

    // ══ free text (Add Media) ═════════════════════════════════════════════════

    [Fact]
    public async Task AFreeSearch_OffersBooksAndAuthors_BestFirst()
    {
        var fake = FakeOpenLibrary.Catalogue().Route(u => u.StartsWith("/search/authors.json?q=Leviathan Wakes"), "authors_corey.json");
        var context = new MediaSearchContext("Leviathan Wakes");   // what Add Media sends: no parent, not a real position

        var result = await Provider(fake).SearchAsync(context);

        Assert.Equal(2, fake.Requests);
        Assert.Equal("OL16114008W", result[0].Metadata.ExternalId);   // the book
        Assert.Equal(result.OrderByDescending(r => r.Score).Select(r => r.Metadata.ExternalId), result.Select(r => r.Metadata.ExternalId));
    }

    [Fact]
    public async Task AFreeSearchForAnAuthor_FindsTheAuthor()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(new MediaSearchContext("James S. A. Corey"));

        Assert.Contains(result, r => r.Metadata.ExternalId == "OL6982995A");
    }

    [Fact]
    public async Task AFreeSearchIsNotHeldBelowTheGate_WhenNoAuthorIsGiven()
    {
        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(new MediaSearchContext("Leviathan Wakes"));

        Assert.True(result.Max(r => r.Score) >= 60);
    }

    // ══ identifiers the item already holds ════════════════════════════════════

    [Fact]
    public async Task AKnownOpenLibraryId_IsAnIdentifierMatch_WithoutASearch()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var context = Book("whatever the library calls it", "Someone Else");
        context = context with { KnownExternalIds = new Dictionary<string, string> { ["openlibrary"] = "OL16114008W" } };

        var result = await Provider(fake).SearchAsync(context);

        var only = Assert.Single(result);
        Assert.True(only.IdentifierMatch);
        Assert.Equal(100, only.Score);
        Assert.Equal("Leviathan Wakes", only.Metadata.Title);
        Assert.DoesNotContain(fake.Urls, u => u.StartsWith("/search.json?title="));
    }

    [Fact]
    public async Task AKnownAuthorId_IsUsedOnlyForAnAuthor_AndAWorkIdOnlyForABook()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var authorWithWorkId = AuthorContext("James S. A. Corey") with { KnownExternalIds = new Dictionary<string, string> { ["openlibrary"] = "OL16114008W" } };
        var bookWithAuthorId = Book("Leviathan Wakes", "James S. A. Corey") with { KnownExternalIds = new Dictionary<string, string> { ["openlibrary"] = "OL6982995A" } };

        var viaName = await Provider(fake).SearchAsync(authorWithWorkId);
        var viaTitle = await Provider(fake).SearchAsync(bookWithAuthorId);

        Assert.False(viaName[0].IdentifierMatch);   // fell through to the name search
        Assert.False(viaTitle[0].IdentifierMatch);  // fell through to the title search
        Assert.Equal("OL6982995A", viaName[0].Metadata.ExternalId);
        Assert.Equal("OL16114008W", viaTitle[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task AKnownAuthorId_IsAnIdentifierMatchForAnAuthor()
    {
        var context = AuthorContext("anything") with { KnownExternalIds = new Dictionary<string, string> { ["openlibrary"] = "OL6982995A" } };

        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(context);

        Assert.True(result[0].IdentifierMatch);
        Assert.Equal("James S. A. Corey", result[0].Metadata.Title);
    }

    [Fact]
    public async Task AKnownIdOpenLibraryDoesNotHave_FallsBackToTheSearch()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var context = Book("Leviathan Wakes", "James S. A. Corey") with { KnownExternalIds = new Dictionary<string, string> { ["openlibrary"] = "OL5W" } };

        var result = await Provider(fake).SearchAsync(context);

        Assert.Equal("OL16114008W", result[0].Metadata.ExternalId);
        Assert.False(result[0].IdentifierMatch);
    }

    [Fact]
    public async Task AKnownGoodreadsId_FindsTheBook()
    {
        var context = Book("Whatever", "Someone") with { KnownExternalIds = new Dictionary<string, string> { ["goodreads"] = "35182525" } };

        var result = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(context);

        Assert.True(result[0].IdentifierMatch);
        Assert.Equal("OL16114008W", result[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task AGoodreadsIdIsNotUsedForAnAuthor_OrWhenItIsNotAnId()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var author = AuthorContext("James S. A. Corey") with { KnownExternalIds = new Dictionary<string, string> { ["goodreads"] = "35182525" } };
        var junk = Book("Leviathan Wakes", "James S. A. Corey") with { KnownExternalIds = new Dictionary<string, string> { ["goodreads"] = "not-an-id" } };

        Assert.False((await Provider(fake).SearchAsync(author))[0].IdentifierMatch);
        Assert.False((await Provider(fake).SearchAsync(junk))[0].IdentifierMatch);
    }

    // ══ fetch by id ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ABook_IsFetchedWithItsDescriptionSeriesAndSubjects()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var m = await Provider(fake).GetByIdAsync("OL16114008W");

        Assert.Equal("Leviathan Wakes", m.Title);
        Assert.StartsWith("When Captain Jim Holden", m.Overview);
        Assert.Contains("Science Fiction", m.Genres);
        Assert.Equal("The Expanse", m.ExtendedData!.Value.GetProperty("series").GetProperty("name").GetString());
        Assert.Equal("1", m.ExtendedData!.Value.GetProperty("series").GetProperty("position").GetString());
        Assert.Equal(3, fake.Requests);   // the search document, the work record, the series name
    }

    [Fact]
    public async Task AFetchIsRememberedBriefly_SoAskingAgainCostsNothing()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var provider = Provider(fake);

        await provider.GetByIdAsync("OL16114008W");
        var before = fake.Requests;
        var again = await provider.GetByIdAsync("openlibrary:OL16114008W");

        Assert.Equal(before, fake.Requests);
        Assert.Equal("Leviathan Wakes", again.Title);
    }

    [Fact]
    public async Task ASearchThatJustFoundTheBook_NeedsOnlyTheWorkRecordToFillItIn()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var provider = Provider(fake);

        var hit = (await provider.SearchAsync(Book("Leviathan Wakes", "James S. A. Corey")))[0];
        var requestsAfterSearch = fake.Requests;
        await provider.GetByIdAsync(hit.Metadata.ExternalId);

        // The search document is remembered; only the work record and the series name are fetched.
        Assert.Equal(requestsAfterSearch + 2, fake.Requests);
        Assert.DoesNotContain(fake.Urls.Skip(requestsAfterSearch), u => u.StartsWith("/search.json"));
    }

    [Fact]
    public async Task AnAuthor_IsFetchedWithBiographyAndPhoto()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var m = await Provider(fake).GetByIdAsync("https://openlibrary.org/authors/OL6982995A/James_S._A._Corey");

        Assert.Equal("James S. A. Corey", m.Title);
        Assert.StartsWith("James S.A. Corey is the pen name", m.Overview);
        Assert.NotNull(m.PosterUrl);
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task AnAuthorFoundBySearch_KeepsItsWorkCountWhenFetched()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var provider = Provider(fake);
        await provider.SearchAsync(AuthorContext("James S. A. Corey"));

        var m = await provider.GetByIdAsync("OL6982995A");

        Assert.Equal(92, m.ExtendedData!.Value.GetProperty("work_count").GetInt32());
    }

    [Theory]
    [InlineData("OL25001749M")]
    [InlineData("https://openlibrary.org/books/OL25001749M/Leviathan_wakes")]
    public async Task AnEdition_LeadsToItsWork(string id)
    {
        var m = await Provider(FakeOpenLibrary.Catalogue()).GetByIdAsync(id);

        Assert.Equal("OL16114008W", m.ExternalId);
    }

    [Theory]
    [InlineData("9780316129084")]
    [InlineData("978-0-316-12908-4")]
    [InlineData("goodreads:35182525")]
    public async Task AnIsbnOrAGoodreadsId_LeadsToTheBook(string id)
    {
        var m = await Provider(FakeOpenLibrary.Catalogue()).GetByIdAsync(id);

        Assert.Equal("OL16114008W", m.ExternalId);
        Assert.NotNull(m.Overview);
    }

    [Theory]
    [InlineData("tt0903747")]
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task SomethingThatIsNotAnId_ComesBackEmpty_WithoutARequest(string id)
    {
        var fake = FakeOpenLibrary.Catalogue();

        var m = await Provider(fake).GetByIdAsync(id);

        Assert.Equal("", m.Title);
        Assert.Equal(id, m.ExternalId);
        Assert.Equal(0, fake.Requests);
    }

    [Theory]
    [InlineData("OL1W")]
    [InlineData("OL999999999A")]
    [InlineData("OL1M")]
    [InlineData("9780000000002")]
    public async Task AnIdOpenLibraryDoesNotHave_ComesBackEmpty(string id)
    {
        var m = await Provider(new FakeOpenLibrary()).GetByIdAsync(id);

        Assert.Equal("", m.Title);
        Assert.Equal("openlibrary", m.Source);
    }

    [Fact]
    public async Task AnEditionWithNoWork_ComesBackEmpty()
    {
        var fake = new FakeOpenLibrary().RouteBody(u => u.StartsWith("/books/OL2M.json"), "{\"title\":\"orphan\"}");

        Assert.Equal("", (await Provider(fake).GetByIdAsync("OL2M")).Title);
    }

    [Fact]
    public async Task ABookWhoseRecordIsMissing_IsStillReturned_FromItsSearchDocument()
    {
        var fake = new FakeOpenLibrary().Route(u => u.StartsWith("/search.json?q=key:"), "search_isbn.json");

        var m = await Provider(fake).GetByIdAsync("OL16114008W");

        Assert.Equal("Leviathan Wakes", m.Title);
        Assert.Null(m.Overview);
    }

    [Fact]
    public async Task ASeriesSubject_NamesTheSeries_WhenThereIsNoStructuredSeries()
    {
        var fake = new FakeOpenLibrary()
            .RouteBody(u => u.StartsWith("/works/OL77W.json"), "{\"key\":\"/works/OL77W\",\"title\":\"The Will of the Many\",\"subjects\":[\"Fiction\",\"series:Hierarchy\"]}");

        var m = await Provider(fake).GetByIdAsync("OL77W");

        var series = m.ExtendedData!.Value.GetProperty("series");
        Assert.Equal("Hierarchy", series.GetProperty("name").GetString());
        Assert.Equal(JsonValueKindNull, series.GetProperty("key").ValueKind);
        Assert.Equal(2, fake.Requests);   // the search document and the record: a subject needs no extra request
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task ASeriesWithNoName_IsStillRecordedByItsKey()
    {
        var fake = new FakeOpenLibrary()
            .RouteBody(u => u.StartsWith("/works/OL78W.json"), "{\"key\":\"/works/OL78W\",\"title\":\"T\",\"series\":[{\"series\":{\"key\":\"/series/OL9L\"},\"position\":2}]}")
            .RouteBody(u => u.StartsWith("/series/OL9L.json"), "{\"name\":\"  \"}");

        var m = await Provider(fake).GetByIdAsync("OL78W");

        var series = m.ExtendedData!.Value.GetProperty("series");
        Assert.Equal("OL9L", series.GetProperty("key").GetString());
        Assert.Equal("2", series.GetProperty("position").GetString());
    }

    // ══ failures ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ARateLimit_IsThrown_SoChronicleKeepsTheItemPending()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = HttpStatusCode.TooManyRequests;
        fake.RetryAfterSeconds = 60;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).SearchAsync(Book("Leviathan Wakes", "James S. A. Corey")));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task AServerError_IsAnError_NotAnEmptyResult()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = HttpStatusCode.ServiceUnavailable;

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).SearchAsync(AuthorContext("John Green")));
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).GetByIdAsync("OL16114008W"));
    }

    // ══ what it declares ══════════════════════════════════════════════════════

    [Fact]
    public async Task OtherMediaTypes_AreNotSearched()
    {
        var fake = FakeOpenLibrary.Catalogue();

        var result = await Provider(fake).SearchAsync(new MediaSearchContext("Leviathan Wakes", MediaTypeName: "movies"));

        Assert.Empty(result);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public void ItDeclaresBooksAndAudiobooks_WithTheSameHierarchyAsHardcover_AndALowerPriority()
    {
        var types = new OpenLibraryMetadataProvider().GetSupportedMediaTypes();

        Assert.Equal(["books", "audiobooks"], types.Select(t => t.MediaTypeName));
        Assert.All(types, t =>
        {
            Assert.Equal(3, t.HierarchyLevels);
            Assert.Equal(["Author", "Series", "Book"], t.HierarchyLabels);
            Assert.Equal(20, t.DefaultPriority);   // Hardcover is 10 and wins
            Assert.Null(t.LevelFields);            // level 1 also holds standalone books, so no level is declared empty
        });
        Assert.Equal("listened", types[1].InteractionVerb);
    }

    [Fact]
    public void ItAcceptsItsOwnCrossReferencePrefix_AndHasTheStandardIdentity()
    {
        var provider = new OpenLibraryMetadataProvider();

        Assert.Equal(["openlibrary:"], provider.GetAcceptedCrossRefPrefixes());
        Assert.Equal("chronicle.plugin.openlibrary", provider.PluginId);
        Assert.Equal("Open Library", provider.Name);
        Assert.False(string.IsNullOrWhiteSpace(provider.Author));
    }

    // ══ settings ══════════════════════════════════════════════════════════════

    [Fact]
    public void TheSettings_AreTheLanguageAndAnOptionalContactEmail()
    {
        var schema = new OpenLibraryMetadataProvider().GetSettingsSchema();

        Assert.Equal(["preferred_language", "contact_email"], schema.Settings.Select(s => s.Key));
        var language = schema.Settings[0];
        Assert.Equal(SettingType.Dropdown, language.Type);
        Assert.Equal("eng", language.DefaultValue);
        Assert.Equal("English", language.Options.First(o => o.Value == "eng").Label);
        Assert.Contains(language.Options, o => o.Value == "any");
        Assert.All(schema.Settings, s => Assert.False(s.Required));
        Assert.Equal(SettingType.Text, schema.Settings[1].Type);
    }

    [Fact]
    public void EnglishIsTheDefaultLanguage()
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string>());

        Assert.Equal("eng", provider.PreferredLanguage);
    }

    [Theory]
    [InlineData("fre", "fre")]
    [InlineData("FRE", "fre")]
    [InlineData(" ger ", "ger")]
    [InlineData("any", "any")]
    [InlineData("klingon", "eng")]
    [InlineData("", "eng")]
    public void ThePreferredLanguage_IsTakenFromTheSettings_OrFallsBackToEnglish(string setting, string expected)
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string> { ["preferred_language"] = setting });

        Assert.Equal(expected, provider.PreferredLanguage);
    }

    [Fact]
    public void WithoutAnEmail_RequestsAreAnonymous_AndPacedAtOnePerSecond()
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string>());

        Assert.Equal("Chronicle-OpenLibrary/1.0.2", provider.ConfiguredUserAgent);
        Assert.Equal(OpenLibraryClient.DefaultInterval, provider.ConfiguredInterval);
    }

    [Fact]
    public void WithAnEmail_RequestsIdentifyTheApplication_AndAreThreeTimesFaster()
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string> { ["contact_email"] = "  reader@example.org " });

        Assert.Equal("Chronicle-OpenLibrary/1.0.2 (reader@example.org)", provider.ConfiguredUserAgent);
        Assert.Equal(OpenLibraryClient.IdentifiedInterval, provider.ConfiguredInterval);
    }

    [Theory]
    [InlineData("not an email")]
    [InlineData("two@@example.org")]
    [InlineData("@example.org")]
    [InlineData("name@")]
    [InlineData("name@nodot")]
    [InlineData("name@example.")]
    [InlineData("")]
    public void SomethingThatIsNotAnEmail_IsIgnored_NotSentToOpenLibrary(string value)
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string> { ["contact_email"] = value });

        Assert.Equal("Chronicle-OpenLibrary/1.0.2", provider.ConfiguredUserAgent);
        Assert.Equal(OpenLibraryClient.DefaultInterval, provider.ConfiguredInterval);
    }

    [Theory]
    [InlineData("a)b@c.org")]
    [InlineData("a(b@c.org")]
    public void AnAddressThatCannotBeSentInAHeader_FallsBackToAnonymous_InsteadOfFailingToStart(string address)
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string> { ["contact_email"] = address });

        Assert.Equal("Chronicle-OpenLibrary/1.0.2", provider.ConfiguredUserAgent);
        Assert.Equal(OpenLibraryClient.DefaultInterval, provider.ConfiguredInterval);
    }

    [Theory]
    [InlineData("a@b.co", true)]
    [InlineData("first.last+tag@sub.example.org", true)]
    [InlineData(null, false)]
    [InlineData("a b@c.d", false)]
    public void EmailValidation_IsConservative(string? value, bool expected) =>
        Assert.Equal(expected, OpenLibraryMetadataProvider.IsPlausibleEmail(value));

    [Fact]
    public void ReconfiguringReplacesTheClient_WithoutLeakingTheOldOne()
    {
        using var provider = new OpenLibraryMetadataProvider();

        provider.Configure(new Dictionary<string, string>());
        provider.Configure(new Dictionary<string, string> { ["contact_email"] = "a@b.co", ["preferred_language"] = "fre" });

        Assert.Equal("fre", provider.PreferredLanguage);
        Assert.Equal(OpenLibraryClient.IdentifiedInterval, provider.ConfiguredInterval);
    }

    [Fact]
    public void ConfiguringAProviderGivenItsOwnClient_ChangesTheLanguageOnly()
    {
        var provider = new OpenLibraryMetadataProvider(FakeOpenLibrary.Catalogue().Client());

        provider.Configure(new Dictionary<string, string> { ["preferred_language"] = "ger", ["contact_email"] = "a@b.co" });

        Assert.Equal("ger", provider.PreferredLanguage);
        Assert.Null(provider.ConfiguredUserAgent);   // the supplied client is left as it is
    }

    // ══ lifecycle ═════════════════════════════════════════════════════════════

    [Fact]
    public async Task NotConfigured_IsAClearError()
    {
        using var provider = new OpenLibraryMetadataProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(new MediaSearchContext("x")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetByIdAsync("OL1W"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetImageAsync("https://covers.openlibrary.org/b/id/1-L.jpg"));
        Assert.False(await provider.HealthCheckAsync());
    }

    [Fact]
    public async Task AfterDispose_TheProviderIsNotConfigured()
    {
        var provider = new OpenLibraryMetadataProvider();
        provider.Configure(new Dictionary<string, string>());
        provider.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(new MediaSearchContext("x")));
    }

    [Theory]
    [InlineData("http://covers.openlibrary.org/b/id/1-L.jpg")]
    [InlineData("https://evil.example.org/b/id/1-L.jpg")]
    [InlineData("https://notopenlibrary.org/b/id/1-L.jpg")]
    [InlineData("https://openlibrary.org.evil.example/b/id/1-L.jpg")]
    [InlineData("file:///c:/secrets.txt")]
    [InlineData("covers.openlibrary.org/b/id/1-L.jpg")]
    [InlineData("")]
    public async Task OnlyOpenLibrarysOwnImagesAreFetched(string url)
    {
        var fake = FakeOpenLibrary.Catalogue();

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(fake).GetImageAsync(url));

        Assert.Equal(0, fake.Requests);
    }

    [Theory]
    [InlineData("https://covers.openlibrary.org/b/id/7314237-L.jpg")]
    [InlineData("https://openlibrary.org/static/images/openlibrary-192x192.png")]
    public async Task OpenLibrarysImageHostsAreAllowed(string url)
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.ImageBytes = [1];

        Assert.Equal(new byte[] { 1 }, await Provider(fake).GetImageAsync(url));
    }

    [Fact]
    public async Task ASeriesWithBooksUnderIt_MakesNoRequest_EvenWhenItCarriesIds()
    {
        var fake = FakeOpenLibrary.Catalogue();
        var context = new MediaSearchContext("The Expanse", ParentName: "James S. A. Corey", HierarchyLevel: 1, ChildCount: 9, MediaTypeName: "books",
            KnownExternalIds: new Dictionary<string, string> { ["openlibrary"] = "OL16114008W", ["goodreads"] = "35182525" });

        Assert.Empty(await Provider(fake).SearchAsync(context));
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task AFreeSearchYear_IsABooksYear_NotAnAuthorsBirthYear()
    {
        // The year typed in Add Media must not count against the author the search finds.
        var fake = FakeOpenLibrary.Catalogue();

        var withYear = await Provider(fake).SearchAsync(new MediaSearchContext("John Green", Year: 2012));
        var without = await Provider(FakeOpenLibrary.Catalogue()).SearchAsync(new MediaSearchContext("John Green"));

        var a = withYear.First(r => r.Metadata.ExternalId == "OL5046634A").Score;   // born 1977: would be -30 if the year were taken as a birth year
        var b = without.First(r => r.Metadata.ExternalId == "OL5046634A").Score;
        Assert.Equal(b, a);
    }

    [Fact]
    public async Task Images_AreFetchedThroughTheClient()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.ImageBytes = [5, 6];

        var bytes = await Provider(fake).GetImageAsync("https://covers.openlibrary.org/b/id/7314237-L.jpg");

        Assert.Equal(new byte[] { 5, 6 }, bytes);
    }

    [Fact]
    public async Task HealthCheck_ReflectsTheService()
    {
        Assert.True(await Provider(FakeOpenLibrary.Catalogue()).HealthCheckAsync());
        Assert.False(await Provider(new FakeOpenLibrary()).HealthCheckAsync());
    }
}
