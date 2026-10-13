using System.Text.Json;
using Chronicle.Plugin.OpenLibrary;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public class MatchersTests
{
    private static List<WorkDoc> Docs(string fixture) =>
        JsonSerializer.Deserialize<SearchResponse>(FakeOpenLibrary.Fixture(fixture))!.Docs;

    private static BookQuery Query(string title, string? author, int? year = null, string language = "eng") => new(title, author, year, language);

    // ── books ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRightBook_ByTheRightAuthor_ScoresHigh()
    {
        var doc = Docs("search_leviathan.json").First(d => d.Olid == "OL16114008W");

        var score = Matchers.ScoreBook(doc, Query("Leviathan Wakes", "James S. A. Corey", 2011));

        Assert.True(score >= 86, $"score was {score}");
        Assert.True(score <= 99);
    }

    [Fact]
    public void TheAuthorsAlternateName_CountsAsTheAuthor()
    {
        var doc = Docs("search_leviathan.json").First(d => d.Olid == "OL16114008W");

        var score = Matchers.ScoreBook(doc, Query("Leviathan Wakes", "James Corey"));   // listed only as an alternate name

        Assert.True(score >= 86, $"score was {score}");
    }

    [Fact]
    public void TheSameTitleByAnotherAuthor_StaysBelowChroniclesGate()
    {
        // "The Outpost" by Rick Partlow (the library) is not "The Outpost" by Mike Resnick (Open Library).
        var resnick = Docs("search_outpost_title_only.json").First(d => d.AuthorName?.Contains("Mike Resnick") == true);

        var score = Matchers.ScoreBook(resnick, Query("The Outpost", "Rick Partlow", 2001));

        Assert.InRange(score, 1, 35);
    }

    [Fact]
    public void WithoutAnAuthor_TheSameBookIsNotCapped()
    {
        var resnick = Docs("search_outpost_title_only.json").First(d => d.AuthorName?.Contains("Mike Resnick") == true);

        Assert.True(Matchers.ScoreBook(resnick, Query("The Outpost", null, 2001)) >= 60);
    }

    [Fact]
    public void ABookWithNoRecordedAuthor_IsNotTrustedWhenAnAuthorIsKnown()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Y: The Last Man - The Deluxe Edition Book Two", EditionCount = 1 };

        Assert.True(Matchers.ScoreBook(doc, Query("Y: The Last Man - The Deluxe Edition Book Two", "Pia Guerra")) <= 35);
    }

    [Fact]
    public void AWorkbookOrSummary_RanksBelowTheBookItself()
    {
        var docs = Docs("search_atomic_habits.json");
        var book = docs.First(d => d.Title == "Atomic Habits");
        var workbook = docs.First(d => d.Title == "Atomic Habits Workbook");
        var query = Query("Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones (2018)", "James Clear", 2018);

        var bookScore = Matchers.ScoreBook(book, query);
        var workbookScore = Matchers.ScoreBook(workbook, query);

        Assert.True(bookScore >= 80, $"book {bookScore}");
        Assert.True(workbookScore < bookScore - 30, $"workbook {workbookScore} vs book {bookScore}");
    }

    [Fact]
    public void JunkIsNotPenalised_WhenTheLibraryTitleIsItselfAnAnthology()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Cosmic Powers: The Saga Anthology of Far-Away Galaxies", AuthorName = ["Yoon Ha Lee"], EditionCount = 1 };

        Assert.True(Matchers.ScoreBook(doc, Query("Cosmic Powers: The Saga Anthology of Far-Away Galaxies", "Yoon Ha Lee")) >= 85);
    }

    [Fact]
    public void ABookTitledOnlyByItsMainTitle_MatchesTheFullSubtitledTitle()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "The Kingdom of Copper", Subtitle = "A Novel", AuthorName = ["S. A. Chakraborty"], EditionCount = 11 };

        var score = Matchers.ScoreBook(doc, Query("The Kingdom of Copper: A Novel (The Daevabad Trilogy)", "S. A. Chakraborty"));

        Assert.True(score >= 80, $"score was {score}");
    }

    [Fact]
    public void AWorkTitledWithAPartMarker_IsKept_ButRanksBelowTheBookItself()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Oathbringer part 1", AuthorName = ["Brandon Sanderson"], EditionCount = 5 };

        var exact = new WorkDoc { Key = "/works/OL2W", Title = "Oathbringer", AuthorName = ["Brandon Sanderson"], EditionCount = 18 };
        var query = Query("Oathbringer", "Brandon Sanderson");

        var half = Matchers.ScoreBook(doc, query);
        var whole = Matchers.ScoreBook(exact, query);

        Assert.InRange(half, 50, 98);   // kept as a candidate...
        Assert.True(whole > half);      // ...but the book itself ranks above its "part 1"
    }

    [Fact]
    public void ANearlyIdenticalTitle_IsAWeakMatch_BelowChroniclesGate()
    {
        // "City World" (the library) and "City Worlds" (a different book): one contains the other, which is only a weak signal.
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "City Worlds", AuthorName = ["Anyone"], EditionCount = 6 };

        var score = Matchers.ScoreBook(doc, Query("City World", null));

        Assert.InRange(score, 1, 49);
    }

    [Fact]
    public void ADifferentVolumeOrIssue_IsADifferentBook_BelowChroniclesGate()
    {
        // "Invincible #59" (an issue) and "Invincible, Vol. 1" (a collected volume) share a main title.
        var volume = new WorkDoc { Key = "/works/OL1W", Title = "Invincible, Vol. 1", AuthorName = ["Robert Kirkman"], EditionCount = 30 };

        Assert.InRange(Matchers.ScoreBook(volume, Query("Invincible #59", "Robert Kirkman")), 1, Matchers.DifferentVolumeCap);
        Assert.True(Matchers.ScoreBook(volume, Query("Invincible, Vol. 1", "Robert Kirkman")) >= 85);   // the same volume still matches
        Assert.True(Matchers.ScoreBook(volume, Query("Invincible", "Robert Kirkman")) >= 60);           // naming no volume is not a conflict
    }

    [Fact]
    public void TheSameVolumeWritten_Differently_IsTheSameBook()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Pathos Volume 2", AuthorName = ["Nathan Hystad"], EditionCount = 2 };

        Assert.True(Matchers.ScoreBook(doc, Query("Pathos Vol. 2", "Nathan Hystad")) >= 80);
        Assert.True(Matchers.ScoreBook(doc, Query("Pathos, Book Two", "Nathan Hystad")) >= 80);
    }

    [Fact]
    public void ATitleTooShortToCountAsContained_ScoresNothing()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "The Moon in the Cloud", AuthorName = ["Anyone"], EditionCount = 6 };

        Assert.Equal(0, Matchers.ScoreBook(doc, Query("Moon", null)));
    }

    [Fact]
    public void AnUnrelatedTitle_ScoresNothing()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Water-quality variations in Antelope Creek", AuthorName = ["Anyone"], EditionCount = 6 };

        Assert.Equal(0, Matchers.ScoreBook(doc, Query("Deadman's Run", "Jonathan Yanez")));
    }

    [Fact]
    public void EmptyTitles_ScoreNothing()
    {
        Assert.Equal(0, Matchers.ScoreBook(new WorkDoc { Title = "" }, Query("Ubik", null)));
        Assert.Equal(0, Matchers.ScoreBook(new WorkDoc { Title = "Ubik" }, Query("   ", null)));
    }

    [Fact]
    public void ABookNotInThePreferredLanguage_RanksLower_ButNotWhenTheLibraryTitleIsInAnotherAlphabet()
    {
        var english = new WorkDoc { Key = "/works/OL1W", Title = "The Four Agreements", AuthorName = ["Miguel Ruiz"], Language = ["eng", "spa"], EditionCount = 20 };
        var foreignOnly = new WorkDoc { Key = "/works/OL2W", Title = "The Four Agreements", AuthorName = ["Miguel Ruiz"], Language = ["spa"], EditionCount = 20 };

        var withEnglish = Matchers.ScoreBook(english, Query("The Four Agreements", "Miguel Ruiz"));
        var withoutEnglish = Matchers.ScoreBook(foreignOnly, Query("The Four Agreements", "Miguel Ruiz"));
        var anyLanguage = Matchers.ScoreBook(foreignOnly, Query("The Four Agreements", "Miguel Ruiz", language: Matchers.AnyLanguage));
        var preferSpanish = Matchers.ScoreBook(foreignOnly, Query("The Four Agreements", "Miguel Ruiz", language: "spa"));

        Assert.Equal(withEnglish - 10, withoutEnglish);
        Assert.Equal(withEnglish, anyLanguage);
        Assert.Equal(withEnglish, preferSpanish);
        // A title in another alphabet is not penalised for lacking the preferred language.
        var greek = new WorkDoc { Key = "/works/OL3W", Title = "Οι τέσσερις συμφωνίες", AuthorName = ["Miguel Ruiz"], Language = ["gre"], EditionCount = 20 };
        var greekEnglish = Matchers.ScoreBook(greek, Query("Οι τέσσερις συμφωνίες", "Miguel Ruiz"));
        Assert.Equal(Matchers.ScoreBook(greek, Query("Οι τέσσερις συμφωνίες", "Miguel Ruiz", language: Matchers.AnyLanguage)), greekEnglish);
    }

    [Fact]
    public void ThePublicationYear_AddsALittle_ButOnlyWhenItIsClose()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Nemesis", AuthorName = ["Isaac Asimov"], FirstPublishYear = 1989, EditionCount = 22 };

        var close = Matchers.ScoreBook(doc, Query("Nemesis", null, 1990));
        var far = Matchers.ScoreBook(doc, Query("Nemesis", null, 2004));
        var unknown = Matchers.ScoreBook(doc, Query("Nemesis", null, null));

        Assert.Equal(far + 8, close);
        Assert.Equal(far, unknown);
    }

    [Fact]
    public void TheMoreEditionsTheBetter_UpToALimit()
    {
        WorkDoc Doc(int editions) => new() { Key = "/works/OL1W", Title = "Second Foundation", AuthorName = ["Isaac Asimov"], EditionCount = editions };
        var query = Query("Second Foundation", "Isaac Asimov");

        var popular = Matchers.ScoreBook(Doc(81), query);
        var rare = Matchers.ScoreBook(Doc(2), query);
        var none = Matchers.ScoreBook(Doc(0), query);

        Assert.True(popular > rare);
        Assert.True(rare > none);
        Assert.Equal(Matchers.ScoreBook(Doc(300), query), Matchers.ScoreBook(Doc(100000), query));   // capped at +8
    }

    [Fact]
    public void AScoreNeverReachesAHundred_WhichIsKeptForIdentifierMatches()
    {
        var doc = new WorkDoc { Key = "/works/OL1W", Title = "Ubik", AuthorName = ["Philip K. Dick"], FirstPublishYear = 1969, EditionCount = 5000 };

        Assert.True(Matchers.ScoreBook(doc, Query("Ubik", "Philip K. Dick", 1969)) <= 99);
    }

    // ── authors ───────────────────────────────────────────────────────────────

    private static List<AuthorDoc> Authors(string fixture) =>
        JsonSerializer.Deserialize<AuthorSearchResponse>(FakeOpenLibrary.Fixture(fixture))!.Docs;

    [Fact]
    public void AnAuthor_NeedsAnEqualName()
    {
        var corey = Authors("authors_corey.json");

        Assert.True(Matchers.ScoreAuthor(corey.First(a => a.Key == "OL6982995A"), "James S. A. Corey", null) >= 60);
        Assert.Equal(0, Matchers.ScoreAuthor(corey.First(a => a.Key == "OL7523472A"), "Daniel Abraham", null));   // Ty Franck is not Daniel Abraham
        Assert.Equal(0, Matchers.ScoreAuthor(new AuthorDoc { Key = "/authors/OL1A", Name = "Fiona Staples" }, "Fiona Stapler", null));
        Assert.Equal(0, Matchers.ScoreAuthor(corey[0], "", null));
    }

    [Fact]
    public void AnAuthorsAlternateName_IsAnEqualName()
    {
        var corey = Authors("authors_corey.json").First(a => a.Key == "OL6982995A");

        Assert.True(Matchers.ScoreAuthor(corey, "James Corey", null) >= 60);
        Assert.True(Matchers.ScoreAuthor(corey, "Corey, James S.A.", null) >= 60);
    }

    [Fact]
    public void WithoutOtherEvidence_TheAuthorWithMoreWorksScoresHigher()
    {
        var johns = Authors("authors_john_green.json");
        var many = johns.First(a => a.Key == "OL265902A");    // 381 works
        var few = johns.First(a => a.Key == "OL4271523A");    // 43 works

        Assert.True(Matchers.ScoreAuthor(many, "John Green", null) > Matchers.ScoreAuthor(few, "John Green", null));
    }

    [Fact]
    public void TheBirthYear_ConfirmsOrContradictsAnAuthor()
    {
        var novelist = Authors("authors_john_green.json").First(a => a.Key == "OL5046634A");   // born 24 Aug 1977

        var none = Matchers.ScoreAuthor(novelist, "John Green", null);
        var agrees = Matchers.ScoreAuthor(novelist, "John Green", 1977);
        var close = Matchers.ScoreAuthor(novelist, "John Green", 1978);
        var disagrees = Matchers.ScoreAuthor(novelist, "John Green", 1948);

        Assert.Equal(none + 20, agrees);
        Assert.Equal(agrees, close);
        Assert.Equal(none - 30, disagrees);
    }

    [Fact]
    public void AnAuthorWithNoBirthDate_IsNeitherConfirmedNorContradicted()
    {
        var undated = Authors("authors_john_green.json").First(a => a.Key == "OL265902A");

        Assert.Equal(Matchers.ScoreAuthor(undated, "John Green", null), Matchers.ScoreAuthor(undated, "John Green", 1977));
    }

    [Fact]
    public void AnAuthorScoreStaysBelowAHundred()
    {
        var huge = new AuthorDoc { Key = "/authors/OL1A", Name = "Isaac Asimov", WorkCount = 1_000_000, BirthDate = "2 January 1920" };

        Assert.True(Matchers.ScoreAuthor(huge, "Isaac Asimov", 1920) <= 99);
    }

    // ── telling same-named authors apart ──────────────────────────────────────

    [Fact]
    public void TitlesTheLibraryHolds_RaiseTheAuthorWhoWroteThem()
    {
        IReadOnlyList<string> library = ["The Fault in Our Stars", "Paper Towns"];
        IReadOnlyList<string> novelistWorks = ["The Fault in Our Stars", "Paper Towns", "Looking for Alaska"];
        IReadOnlyList<string> otherWorks = ["Counselling in HIV infection and AIDS"];

        Assert.Equal(30, Matchers.ChildTitleBonus(library, novelistWorks));   // 25 + 5 for the second title
        Assert.Equal(0, Matchers.ChildTitleBonus(library, otherWorks));
        Assert.Equal(0, Matchers.ChildTitleBonus([], novelistWorks));
        Assert.Equal(0, Matchers.ChildTitleBonus(library, []));
    }

    [Fact]
    public void TheChildBonus_IsCapped_AndIgnoresYearSuffixesAndPositionMarkers()
    {
        IReadOnlyList<string> library = Enumerable.Range(1, 10).Select(i => $"Book {i}").Append("Ubik (1969)").ToList();
        IReadOnlyList<string> works = Enumerable.Range(1, 10).Select(i => $"Book {i}").Append("Ubik").ToList();

        Assert.Equal(35, Matchers.ChildTitleBonus(library, works));
        Assert.Equal(25, Matchers.ChildTitleBonus(["Ubik (1969)"], ["Ubik"]));
        Assert.Equal(25, Matchers.ChildTitleBonus(["A Dance with Dragons, Part 1: Dreams and Dust"], ["A Dance with Dragons"]));
    }

    // ── volumes of one series (2026-10-13: wrong matches found in a real library) ─────────────────────────────────────────

    private static WorkDoc Work(string title, string? subtitle, string author, int? year = 2018) =>
        new() { Key = "/works/OL1W", Title = title, Subtitle = subtitle, AuthorName = [author], FirstPublishYear = year, EditionCount = 5, Language = ["eng"] };

    private static BookQuery InSeries(string title, string author, int? year = 2018) => new(title, author, year, "eng", InSeries: true);

    [Theory]
    [InlineData("Space Team: Sentienced to Death", "Space Team: The Time Titan of Tomorrow", "Barry J. Hutchison")]
    [InlineData("The Sandman: Endless Nights (2003)", "The Sandman - Overture", "Neil Gaiman")]
    [InlineData("Flashback: Siren Song", "Flashback : The Morrigan", "James A. Hunter")]
    [InlineData("Binti: Sacred Fire", "Binti: The Night Masquerade", "Nnedi Okorafor")]
    [InlineData("The Legend of Korra: Ruins of the Empire", "Legend of Korra : The Art of the Animated Series Book Two", "Michael Dante DiMartino")]
    [InlineData("The Slaver Wars: Endgame", "The Slaver Wars: Retaliation (Volume 5)", "Raymond L. Weil")]
    public void AnotherVolumeOfTheSameSeries_IsNotTheBook(string wanted, string found, string author)
    {
        Assert.True(Matchers.ScoreBook(Work(found, null, author), InSeries(wanted, author)) < 50, found);
    }

    [Fact]
    public void ASubtitleKeptInAnotherField_IsStillCompared()
    {
        Assert.True(Matchers.ScoreBook(Work("Binti", "The Night Masquerade", "Nnedi Okorafor"), InSeries("Binti: Sacred Fire", "Nnedi Okorafor")) < 50);
        Assert.True(Matchers.ScoreBook(Work("Binti", "Sacred Fire", "Nnedi Okorafor"), InSeries("Binti: Sacred Fire", "Nnedi Okorafor")) >= 90);
    }

    [Fact]
    public void ASubtitleTheWorkLeavesOff_DoesNotCountAgainstIt()
    {
        Assert.True(Matchers.ScoreBook(Work("Flashback", null, "James A. Hunter"), InSeries("Flashback: Siren Song", "James A. Hunter")) >= 50);
        Assert.True(Matchers.ScoreBook(Work("Legion", null, "Brandon Sanderson", 2014), InSeries("Legion: Skin Deep", "Brandon Sanderson", 2014)) >= 50);
    }

    [Fact]
    public void TheSameSubtitleWordedDifferently_IsTheSameBook()
    {
        Assert.True(Matchers.ScoreBook(Work("Space Team", "The Wrath of Vajazzle", "Barry J. Hutchison"), InSeries("Space Team: Wrath of Vajazzle", "Barry J. Hutchison")) >= 90);
        Assert.True(Matchers.ScoreBook(Work("Saga", "Volume 4 (Saga #4)", "Brian K. Vaughan"), InSeries("Saga: Volume 4", "Brian K. Vaughan")) >= 50);
    }

    [Fact]
    public void AStandaloneBook_IsNotHeldToItsSubtitle()
    {
        var doc = Work("Atomic Habits", "Tiny Changes, Remarkable Results", "James Clear");
        var standalone = new BookQuery("Atomic Habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones", "James Clear", 2018, "eng");

        Assert.True(Matchers.ScoreBook(doc, standalone) >= 80);
        Assert.True(Matchers.ScoreBook(doc, standalone with { InSeries = true }) < 50);   // the same texts inside a series are two books
    }

    [Theory]
    [InlineData("Saga, Vol. 4", "SAGA - Tome 1")]
    [InlineData("Saga, Vol. 5", "SAGA - Tome 6")]
    [InlineData("The Old Guard, Book 2: Force Multiplied", "The Old Guard - Tome 03")]
    [InlineData("Los Juegos del Hambre, Vol. 1", "Los Juegos del Hambre - Libro 2")]
    public void ADifferentVolumeNumber_InAnotherLanguagesWording_IsAnotherBook(string wanted, string found)
    {
        Assert.True(Matchers.ScoreBook(Work(found, null, "Brian K. Vaughan", 2013), InSeries(wanted, "Brian K. Vaughan", 2013)) < 50, found);
    }

    [Fact]
    public void TheSameVolumeNumber_InAnotherLanguagesWording_IsNotMarkedDown()
    {
        Assert.True(Matchers.ScoreBook(Work("SAGA - Tome 4", null, "Brian K. Vaughan", 2014), InSeries("Saga, Vol. 4", "Brian K. Vaughan", 2014)) >= 50);
    }

    // ── a title that merely appears inside another (2026-10-13: ~130 series and stories matched to one of their books) ───

    [Theory]
    [InlineData("Galaxy's Edge", "Legionnaire (Galaxy's Edge)", "Jason Anspach")]
    [InlineData("Mirror Image", "The Best of Isaac Asimov 1954-1972 (Anniversary / The Billiard Ball / The Last Question / Mirror-Image)", "Isaac Asimov")]
    [InlineData("Muad Dib", "Songs of Muad'dib", "Frank Herbert")]
    [InlineData("The Divine Dungeon", "Dungeon Calamity (The Divine Dungeon)", "Dakota Krout")]
    [InlineData("The Dagger and the Coin", "The King's Blood (The Dagger and the Coin #2)", "Daniel Abraham")]
    public void AnItemNamedInsideAnotherTitle_IsNotThatBook(string wanted, string found, string author)
    {
        Assert.True(Matchers.ScoreBook(Work(found, null, author), new BookQuery(wanted, author, 2018, "eng")) < 50, found);
    }

    [Theory]
    [InlineData("The Dark Forest (2008)", "The Dark Forest (The Three-Body Problem Series Book 2)")]
    [InlineData("Atomic habits", "Atomic Habits: Tiny Changes, Remarkable Results")]
    [InlineData("The Night Masquerade", "Binti: The Night Masquerade")]
    public void AContinuedOrSubtitleTitle_IsStillTheBook(string wanted, string found)
    {
        Assert.True(Matchers.ScoreBook(Work(found, null, "A. Author"), new BookQuery(wanted, "A. Author", 2018, "eng")) >= 50, found);
    }

    [Fact]
    public void TheSubtitleOfTheWork_CanBeTheLibraryTitle()
    {
        Assert.True(Matchers.ScoreBook(Work("Binti", "The Night Masquerade", "A. Author"), new BookQuery("The Night Masquerade", "A. Author", 2018, "eng")) >= 50);
    }
}
