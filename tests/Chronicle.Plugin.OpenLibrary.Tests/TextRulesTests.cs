using System.Text.Json;
using Chronicle.Plugin.OpenLibrary;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public class TextRulesTests
{
    // ── comparing ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Commander-in-Chief", "Commander in Chief")]
    [InlineData("The Day Before the Revolution", "Day Before the Revolution")]
    [InlineData("Shadows of Self", "SHADOWS OF SELF")]
    [InlineData("Léviathan Wakes", "Leviathan Wakes")]
    [InlineData("A Gathering Of Widowmakers", "a gathering of widowmakers")]
    [InlineData("Don't Look Back", "Dont Look Back")]
    public void Titles_AreEqual_AfterNormalising(string a, string b) =>
        Assert.Equal(TextRules.Normalise(a), TextRules.Normalise(b));

    [Fact]
    public void Normalise_KeepsLettersOfAnyScript_AndHandlesEmpty()
    {
        Assert.NotEqual("", TextRules.Normalise("Οι τέσσερις συμφωνίες"));
        Assert.Equal("", TextRules.Normalise(null));
        Assert.Equal("", TextRules.Normalise("   "));
        Assert.Equal("a", TextRules.Normalise("A"));   // a lone article is not dropped to nothing
    }

    [Theory]
    [InlineData("Tolkien, J. R. R.", "J.R.R. Tolkien")]
    [InlineData("J.R.R. Tolkien", "JRR Tolkien")]
    [InlineData("Corey, James S.A.", "James S. A. Corey")]
    [InlineData("A. A. Milne", "A.A. Milne")]
    public void Names_AreEqual_AcrossTheCommonWaysOfWritingThem(string a, string b) =>
        Assert.Equal(TextRules.NameKey(a), TextRules.NameKey(b));

    [Fact]
    public void NameKey_NeverDropsAnInitialThatLooksLikeAnArticle() =>
        Assert.NotEqual(TextRules.NameKey("A. A. Milne"), TextRules.NameKey("Milne"));

    [Fact]
    public void NameKey_LeavesAnAwkwardCommaAlone()
    {
        Assert.Equal(TextRules.NameKey("Smith, Jones, Brown"), TextRules.NameKey("Smith Jones Brown"));   // two commas: not "Last, First"
        Assert.Equal("", TextRules.NameKey(null));
    }

    // ── titles ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ubik (1969)", "Ubik")]
    [InlineData("Pause [2017]", "Pause")]
    [InlineData("Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones (2018)", "Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones")]
    [InlineData("1984 (1949)", "1984")]
    [InlineData("Ubik", "Ubik")]
    [InlineData("Catch-22 (3)", "Catch-22 (3)")]   // not a year
    public void YearSuffix_IsRemoved_OnlyWhenItIsAYear(string input, string expected) =>
        Assert.Equal(expected, TextRules.StripYearSuffix(input));

    [Theory]
    [InlineData("Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones (2018)", "Atomic habits")]
    [InlineData("A Dance with Dragons, Part 1: Dreams and Dust", "A Dance with Dragons")]
    [InlineData("Invincible #59", "Invincible")]
    [InlineData("Invincible #33 (2012)", "Invincible")]
    [InlineData("Saga nº 07", "Saga")]
    [InlineData("Mage Tank: Book Three", "Mage Tank")]
    [InlineData("Mage Tank Book Three", "Mage Tank")]
    [InlineData("Pathos Volume 2", "Pathos")]
    [InlineData("Pathos Vol. 2", "Pathos")]
    [InlineData("Krazy Ketchup: Book 23", "Krazy Ketchup")]
    [InlineData("Y: The Last Man - The Deluxe Edition Book Two", "Y")]
    [InlineData("The Hobbit - or There and Back Again", "The Hobbit")]
    [InlineData("Oathbringer", "Oathbringer")]
    [InlineData("Part 2", "Part 2")]   // nothing left if the marker is removed: keep it
    [InlineData("Casino 5", "Casino 5")]   // "no 5" inside a word is not a marker
    [InlineData("Piano 3", "Piano 3")]
    [InlineData("Henry VI, Part II", "Henry VI")]
    [InlineData("Book (Book 3)", "Book")]
    public void MainTitle_DropsSubtitlesAndPositionMarkers(string input, string expected) =>
        Assert.Equal(expected, TextRules.MainTitle(input));

    [Theory]
    [InlineData("Invincible #59", 59)]
    [InlineData("Invincible #33 (2012)", 33)]
    [InlineData("Pathos Volume 2", 2)]
    [InlineData("Pathos Vol. 2", 2)]
    [InlineData("Mage Tank: Book Three", 3)]
    [InlineData("Henry VI, Part II", 2)]
    [InlineData("Henry VI, Part IV: The Reckoning", 4)]
    [InlineData("A Dance with Dragons, Part 1: Dreams and Dust", 1)]
    [InlineData("Saga nº 07", 7)]
    [InlineData("Saga no. 12", 12)]
    [InlineData("Invincible, Vol. 1", 1)]
    [InlineData("Krazy Ketchup: Book 23", 23)]
    [InlineData("Casino 5", null)]                 // "no 5" inside a word is not a number marker
    [InlineData("Y: The Last Man - The Deluxe Edition Book Two", 2)]
    [InlineData("Oathbringer", null)]
    [InlineData("Catch-22", null)]
    [InlineData("Book 99999", null)]
    [InlineData("Part", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PositionNumber_IsTheVolumeOrIssueANamedTitleEndsWith(string? title, int? expected) =>
        Assert.Equal(expected, TextRules.PositionNumber(title));

    [Fact]
    public void MainTitle_OfNothing_IsNothing()
    {
        Assert.Equal("", TextRules.MainTitle(null));
        Assert.Equal("", TextRules.MainTitle("  "));
    }

    [Fact]
    public void TitleForms_AreTheWholeTitleAndItsMainTitle()
    {
        var forms = TextRules.TitleForms("Atomic habits: An Easy & Proven Way (2018)");

        Assert.Contains(TextRules.Normalise("Atomic habits"), forms);
        Assert.Contains(TextRules.Normalise("Atomic habits: An Easy & Proven Way"), forms);
        Assert.Equal(2, forms.Count);
        Assert.Single(TextRules.TitleForms("Oathbringer"));
        Assert.Empty(TextRules.TitleForms(""));
    }

    [Fact]
    public void DocTitleForms_IncludeTheSubtitleWhenThereIsOne()
    {
        var forms = TextRules.DocTitleForms("Atomic Habits", "An Easy & Proven Way");

        Assert.Contains(TextRules.Normalise("Atomic Habits: An Easy & Proven Way"), forms);
        Assert.DoesNotContain(TextRules.Normalise("x"), TextRules.DocTitleForms("Atomic Habits", "  "));
    }

    [Theory]
    [InlineData("Summary of Atomic Habits by James Clear", true)]
    [InlineData("Atomic Habits Workbook", true)]
    [InlineData("Expanse Hardcover Boxed Set : Leviathan Wakes", true)]
    [InlineData("Game of Thrones Collection George R.R. Martin 6 Books Set", true)]
    [InlineData("Study Guide for Hamlet", true)]
    [InlineData("Cosmic Powers: The Saga Anthology of Far-Away Galaxies", true)]
    [InlineData("The Hobbit Companion", true)]
    [InlineData("SparkNotes: 1984", true)]
    [InlineData("Leviathan Wakes", false)]
    [InlineData("Oathbringer", false)]
    [InlineData("The Summer I Turned Pretty", false)]   // "summer" is not "summary"
    [InlineData("", false)]
    public void Junk_IsRecognised(string title, bool junk) => Assert.Equal(junk, TextRules.LooksLikeJunk(title));

    [Fact]
    public void NonLatinLetters_AreDetected()
    {
        Assert.True(TextRules.HasNonLatinLetters("Οι τέσσερις συμφωνίες"));
        Assert.True(TextRules.HasNonLatinLetters("Джеймс"));
        Assert.False(TextRules.HasNonLatinLetters("Les 5 langages pour trouver l'amour : Le secret"));
        Assert.False(TextRules.HasNonLatinLetters(null));
    }

    // ── author names ──────────────────────────────────────────────────────────

    [Fact]
    public void TidyName_CollapsesSpaces() => Assert.Equal("Andrew Yang", TextRules.TidyName("  Andrew   Yang "));

    [Theory]
    [InlineData("Tolkien, J.R.R.", "J.R.R. Tolkien")]
    [InlineData("R.A. Williamson", "R. A. Williamson")]
    [InlineData("Brandon Sanderson", null)]
    [InlineData("J. R. R. Tolkien", null)]
    public void NameVariant_OffersTheOtherWayToWriteTheName(string name, string? expected) =>
        Assert.Equal(expected, TextRules.NameVariant(name));

    [Theory]
    [InlineData("16 June 1972", 1972)]
    [InlineData("1628", 1628)]
    [InlineData("2 January 1920", 1920)]
    [InlineData("c. 1450-1520", 1450)]
    [InlineData("born in the 1900s", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void YearOf_FindsTheFirstFourDigitYear(string? date, int? expected) => Assert.Equal(expected, TextRules.YearOf(date));

    // ── descriptions and biographies ──────────────────────────────────────────

    [Fact]
    public void TextOf_ReadsAStringOrAnObjectWithAValue()
    {
        Assert.Equal("plain", TextRules.TextOf(FakeOpenLibrary.Parse("\"plain\"")));
        Assert.Equal("wrapped", TextRules.TextOf(FakeOpenLibrary.Parse("{\"type\":\"/type/text\",\"value\":\"wrapped\"}")));
        Assert.Null(TextRules.TextOf(FakeOpenLibrary.Parse("42")));
        Assert.Null(TextRules.TextOf(FakeOpenLibrary.Parse("{\"type\":\"/type/text\"}")));
        Assert.Null(TextRules.TextOf(null));
    }

    [Fact]
    public void Markdown_IsStripped_ButTheWordsAndParagraphsStay()
    {
        var text = "**The eagerly awaited sequel** to the #1 *New York Times* bestselling *Words of Radiance*\r\n\r\n" +
                   "In *Oathbringer*, the third volume...\r\n\r\nSee [the author](https://example.org) and [Brandon][1].\r\n\r\n" +
                   "----------\r\n[1]: https://example.org/brandon\r\n";

        var clean = TextRules.CleanMarkdown(text)!;

        Assert.StartsWith("The eagerly awaited sequel to the #1 New York Times bestselling Words of Radiance", clean);
        Assert.Contains("In Oathbringer, the third volume...", clean);
        Assert.Contains("See the author and Brandon.", clean);
        Assert.DoesNotContain("*", clean);
        Assert.DoesNotContain("[1]", clean);
        Assert.DoesNotContain("----", clean);
        Assert.DoesNotContain("\r", clean);
        Assert.Contains("\n\n", clean);   // paragraphs kept
    }

    [Fact]
    public void Markdown_RealOathbringerDescription_IsCleanedWithoutLosingText()
    {
        var work = JsonSerializer.Deserialize<WorkRecord>(FakeOpenLibrary.Fixture("work_oathbringer_markdown.json"))!;

        var clean = TextRules.CleanMarkdown(TextRules.TextOf(work.Description))!;

        Assert.StartsWith("The eagerly awaited sequel to the #1 New York Times bestselling Words of Radiance", clean);
        Assert.Contains("Dalinar Kholin", clean);
        Assert.DoesNotContain("**", clean);
    }

    [Fact]
    public void Markdown_RealBiography_DropsFootnoteReferencesAndSourceNotes()
    {
        var author = JsonSerializer.Deserialize<AuthorRecord>(FakeOpenLibrary.Fixture("author_corey.json"))!;

        var clean = TextRules.CleanMarkdown(TextRules.TextOf(author.Bio))!;

        Assert.StartsWith("James S.A. Corey is the pen name used by collaborators Daniel Abraham and Ty Franck.", clean);
        Assert.DoesNotContain("][", clean);
        Assert.DoesNotContain("[1]", clean);
    }

    [Fact]
    public void Markdown_HtmlAndEntities_AreHandled()
    {
        Assert.Equal("one\ntwo & three", TextRules.CleanMarkdown("one<br>two &amp; <b>three</b>"));
        Assert.Equal("two lines\nhere", TextRules.CleanMarkdown("two lines  \r\nhere"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("----------")]
    public void Markdown_OfNothing_IsNull(string? text) => Assert.Null(TextRules.CleanMarkdown(text));

    // ── subjects ──────────────────────────────────────────────────────────────

    [Fact]
    public void Subjects_BisacHeadings_BecomeGenres_AndTheRestTags()
    {
        var (genres, tags) = TextRules.SplitSubjects(
        [
            "Space warfare", "Fiction", "Fiction, science fiction, general", "Fiction, fantasy, epic", "Interplanetary voyages",
            "Nonfiction, history, general", "Juvenile fiction, animals, general",
        ]);

        Assert.Equal(["Fiction", "Science Fiction", "Fantasy", "Nonfiction", "History", "Juvenile Fiction", "Animals"], genres);
        Assert.Equal(["Space warfare", "Interplanetary voyages"], tags);
    }

    [Fact]
    public void Subjects_MachineTagsAndBookkeeping_AreNotTags()
    {
        var (genres, tags) = TextRules.SplitSubjects(
        [
            "series:The Expanse", "nyt:hardcover-fiction=2011-07-10", "award:hugo_award=novel", "Accessible book", "Protected DAISY",
            "In library", "Overdrive", "Lending library", "Internet Archive Wishlist", "open_syllabus_project", "Conspiracies", "conspiracies", "  ", "",
        ]);

        Assert.Empty(genres);
        Assert.Equal(["Conspiracies"], tags);   // de-duplicated ignoring case
    }

    [Fact]
    public void Subjects_Null_IsEmpty()
    {
        var (genres, tags) = TextRules.SplitSubjects(null);

        Assert.Empty(genres);
        Assert.Empty(tags);
    }

    // ── images ────────────────────────────────────────────────────────────────

    [Fact]
    public void ImageUrls_AreBuiltFromIds()
    {
        Assert.Equal("https://covers.openlibrary.org/b/id/7314237-L.jpg", TextRules.CoverUrl(7314237));
        Assert.Equal("https://covers.openlibrary.org/b/id/7314237-M.jpg", TextRules.CoverUrl(7314237, 'M'));
        Assert.Equal("https://covers.openlibrary.org/a/id/7425151-L.jpg", TextRules.PhotoUrl(7425151));
    }
}

public class OlRefTests
{
    [Theory]
    [InlineData("OL16114008W", "Work", "OL16114008W")]
    [InlineData("ol16114008w", "Work", "OL16114008W")]
    [InlineData("openlibrary:OL16114008W", "Work", "OL16114008W")]
    [InlineData("/works/OL16114008W", "Work", "OL16114008W")]
    [InlineData("works/OL16114008W.json", "Work", "OL16114008W")]
    [InlineData("https://openlibrary.org/works/OL16114008W/Leviathan_Wakes", "Work", "OL16114008W")]
    [InlineData("OL6982995A", "Author", "OL6982995A")]
    [InlineData("https://openlibrary.org/authors/OL6982995A/James_S._A._Corey", "Author", "OL6982995A")]
    [InlineData("OL25001749M", "Edition", "OL25001749M")]
    [InlineData("https://openlibrary.org/books/OL25001749M/Leviathan_wakes", "Edition", "OL25001749M")]
    [InlineData("9780316129084", "Isbn", "9780316129084")]
    [InlineData("978-0-316-12908-4", "Isbn", "9780316129084")]
    [InlineData("0316129089", "Isbn", "0316129089")]
    [InlineData("080442957X", "Isbn", "080442957X")]
    [InlineData("080442957x", "Isbn", "080442957X")]
    [InlineData("https://openlibrary.org/isbn/9780316129084", "Isbn", "9780316129084")]
    [InlineData("goodreads:35182525", "Goodreads", "35182525")]
    [InlineData("  OL16114008W  ", "Work", "OL16114008W")]
    public void Identifiers_AreUnderstood(string text, string kind, string value)
    {
        var parsed = OlRef.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(Enum.Parse<OlRefKind>(kind), parsed!.Kind);
        Assert.Equal(value, parsed.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tt0903747")]
    [InlineData("show:16498")]
    [InlineData("OL123")]
    [InlineData("OL123Z")]
    [InlineData("goodreads:abc")]
    [InlineData("goodreads:")]
    [InlineData("12345")]
    [InlineData("97803161290841")]
    [InlineData("abcdefghij")]
    public void SomethingElse_IsRefused(string? text) => Assert.Null(OlRef.Parse(text));
}
