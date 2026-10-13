using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.OpenLibrary;

/// <summary>
/// Everything that turns Open Library's text into something comparable or presentable: name and title normalising, the title
/// forms a library title is searched under, junk detection, Markdown cleaning, subject splitting and image URLs. Pure functions,
/// no network, so every rule is tested on its own.
/// </summary>
internal static partial class TextRules
{
    // ── comparing ─────────────────────────────────────────────────────────────

    /// <summary>Folds accents, lower-cases, and keeps only letters and digits (any script).</summary>
    private static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ') sb.Append(' ');
            else if (ch is not ('\'' or '’' or '.')) sb.Append(' ');   // apostrophes and dots join, everything else separates
        }
        return WhitespaceRun().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>A title reduced for comparison: folded, a leading article dropped, spaces removed.</summary>
    public static string Normalise(string? title)
    {
        var folded = Fold(title);
        foreach (var article in new[] { "the ", "a ", "an " })
            if (folded.StartsWith(article, StringComparison.Ordinal) && folded.Length > article.Length)
            { folded = folded[article.Length..]; break; }
        return folded.Replace(" ", "");
    }

    /// <summary>A person's name reduced for comparison. "Tolkien, J. R. R." equals "J.R.R. Tolkien" equals "JRR Tolkien". An
    /// article is never dropped (the "A" in "A. A. Milne" is an initial).</summary>
    public static string NameKey(string? name)
    {
        var n = (name ?? "").Trim();
        var comma = n.Split(',');
        if (comma.Length == 2 && comma[0].Trim().Length > 0 && comma[1].Trim().Length > 0)
            n = comma[1].Trim() + " " + comma[0].Trim();
        return Fold(n).Replace(" ", "");
    }

    // ── titles ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\s*[\(\[](1\d{3}|20\d{2})[\)\]]\s*$")]
    private static partial Regex YearSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    // ", Part 2", " Book Three", "Volume 4", "Vol. 4", " #59", " nº 07", " (Book 3)"
    private static readonly Regex PositionMarker = new(
        @"(\s*[,:;-]?\s*\(?\s*\b(part|book|volume|vol\.?|episode|chapter|no\.?|tome|tomo|livre|libro|band|buch|deel|volumen)\s*(\d+|[ivxlc]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s*\)?|\s*#\s*\d+(\.\d+)?|\s+n[º°o]\.?\s*\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The title with a trailing "(YYYY)" removed. The library carries them: "Ubik (1969)".</summary>
    public static string StripYearSuffix(string? title) => YearSuffix().Replace((title ?? "").Trim(), "").Trim();

    /// <summary>
    /// The part of a title worth searching on: no year suffix, nothing after the first ":" or " - ", and no trailing
    /// series-position marker. "A Dance with Dragons, Part 1: Dreams and Dust" becomes "A Dance with Dragons";
    /// "Atomic habits: An Easy &amp; Proven Way to Build Good Habits" becomes "Atomic habits".
    /// </summary>
    public static string MainTitle(string? title)
    {
        var cut = BeforeSubtitle(StripYearSuffix(title));
        if (cut.Length == 0) return cut;
        var stripped = PositionMarker.Replace(cut, "").Trim().TrimEnd(',', ';', '-', ' ');
        return (stripped.Length > 0 ? stripped : cut).Trim();
    }

    private static string BeforeSubtitle(string t)
    {
        var cut = t;
        foreach (var sep in new[] { ":", " - ", " – ", " — " })
        {
            var i = cut.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) cut = cut[..i];
        }
        return cut;
    }

    private static readonly Regex PositionNumberPattern = new(
        @"(?:\b(?:part|book|volume|vol\.?|episode|chapter|no\.?|tome|tomo|livre|libro|band|buch|deel|volumen)\s*(?<word>\d+|[ivxlc]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)|#\s*(?<hash>\d+)|\bn[º°o]\.?\s*(?<no>\d+))\s*\)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] NumberWords =
        ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    /// <summary>
    /// The volume or issue number a title names at its end ("Invincible #59" is 59, "Pathos Volume 2" is 2, "Henry VI, Part II"
    /// is 2, "Mage Tank: Book Three" is 3), or null when it names none. Two titles with different numbers are different books
    /// however alike the rest of their titles are.
    /// </summary>
    public static int? PositionNumber(string? title)
    {
        var whole = StripYearSuffix(title);
        var m = PositionNumberPattern.Match(whole);                       // "Mage Tank: Book Three"
        if (!m.Success) m = PositionNumberPattern.Match(BeforeSubtitle(whole));   // "A Dance with Dragons, Part 1: Dreams and Dust"
        if (!m.Success) return null;
        var token = (m.Groups["word"].Success ? m.Groups["word"] : m.Groups["hash"].Success ? m.Groups["hash"] : m.Groups["no"]).Value;

        if (token.All(char.IsDigit)) return token.Length <= 4 ? int.Parse(token) : null;
        var word = Array.IndexOf(NumberWords, token.ToLowerInvariant());
        return word >= 0 ? word + 1 : RomanValue(token);
    }

    private static int? RomanValue(string roman)
    {
        var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100 };
        var total = 0;
        for (var i = 0; i < roman.Length; i++)
        {
            var value = values[char.ToLowerInvariant(roman[i])];
            total += i + 1 < roman.Length && value < values[char.ToLowerInvariant(roman[i + 1])] ? -value : value;
        }
        return total > 0 ? total : null;
    }

    /// <summary>
    /// What follows the main title ("Siren Song" in "Flashback: Siren Song"), normalised, without any "(Volume 5)"-style
    /// bracket or position marker; empty when the title has no subtitle.
    /// </summary>
    public static string SubtitleKey(string? title) => Normalise(SubtitleText(title));

    private static string SubtitleText(string? title)
    {
        var t = StripYearSuffix(title);
        foreach (var sep in new[] { ":", " - ", " – ", " — " })
        {
            var i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i <= 0) continue;
            t = t[(i + sep.Length)..];
            t = Bracketed().Replace(t, " ");
            t = PositionMarker.Replace(t, "");
            return t;
        }
        return "";
    }

    /// <summary>
    /// True when a library title and an Open Library work both carry a subtitle and the two say different things: no one
    /// contains the other and they share no word of four letters or more. "Space Team: Sentienced to Death" and "Space Team:
    /// The Time Titan of Tomorrow" share a main title and are two books of one series. A work with no subtitle of its own
    /// never conflicts (Open Library often leaves them off).
    /// </summary>
    public static bool SubtitlesConflict(string? libraryTitle, string? docTitle, string? docSubtitle)
    {
        var item = SubtitleText(libraryTitle);
        var itemKey = Normalise(item);
        if (itemKey.Length == 0) return false;

        var docTexts = new[] { SubtitleText(docTitle), docSubtitle ?? "" };
        var any = false;
        foreach (var text in docTexts)
        {
            var key = Normalise(text);
            if (key.Length == 0) continue;
            any = true;
            if (key.Contains(itemKey, StringComparison.Ordinal) || itemKey.Contains(key, StringComparison.Ordinal)) return false;
            var itemWords = Fold(item).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToHashSet();
            if (Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length >= 4 && itemWords.Contains(w))) return false;
        }
        return any;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Bracketed();

    /// <summary>The normalised forms a library title answers to: the whole title and its main title.</summary>
    public static HashSet<string> TitleForms(string? title)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal);
        var full = Normalise(StripYearSuffix(title));
        var main = Normalise(MainTitle(title));
        if (full.Length > 0) forms.Add(full);
        if (main.Length > 0) forms.Add(main);
        return forms;
    }

    /// <summary>The normalised forms of an Open Library title, with its subtitle if it has one.</summary>
    public static HashSet<string> DocTitleForms(string title, string? subtitle)
    {
        var forms = TitleForms(title);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var withSub = Normalise($"{title}: {subtitle}");
            if (withSub.Length > 0) forms.Add(withSub);
        }
        return forms;
    }

    private static readonly Regex Junk = new(
        @"\b(summary|summaries|study guide|workbook|analysis|boxed? set|box set|omnibus|sparknotes|cliffsnotes|trivia|for dummies|reader'?s guide|companion|collection|anthology|selections|criticism|interpretation|\d+\s+books?\s+(set|collection))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for the kind of work Open Library files beside a book without being the book: a study guide, a
    /// "Summary of...", a box set, an omnibus, an anthology.</summary>
    public static bool LooksLikeJunk(string? title) => !string.IsNullOrWhiteSpace(title) && Junk.IsMatch(title);

    /// <summary>True when any character is outside basic Latin: such a title is probably a translation, so the preferred
    /// language must not count against a book that is in a different one.</summary>
    public static bool HasNonLatinLetters(string? s) => (s ?? "").Any(c => char.IsLetter(c) && c > 0x24F);

    // ── author names ──────────────────────────────────────────────────────────

    /// <summary>A name tidied for searching: runs of spaces collapsed.</summary>
    public static string TidyName(string? name) => WhitespaceRun().Replace((name ?? "").Trim(), " ");

    /// <summary>Another way to write the same name, tried only when the first search finds no equal name:
    /// "Tolkien, J.R.R." to "J.R.R. Tolkien", or "R.A. Williamson" to "R. A. Williamson".</summary>
    public static string? NameVariant(string name)
    {
        var n = TidyName(name);
        var comma = n.Split(',');
        if (comma.Length == 2 && comma[0].Trim().Length > 0 && comma[1].Trim().Length > 0)
            return comma[1].Trim() + " " + comma[0].Trim();
        var spaced = Regex.Replace(n, @"\b([A-Za-z])\.(?=[A-Za-z])", "$1. ");
        return string.Equals(spaced, n, StringComparison.Ordinal) ? null : spaced;
    }

    /// <summary>The first four-digit year in a free-text date ("16 June 1972", "1628"), or null.</summary>
    public static int? YearOf(string? date)
    {
        // "1900s" is a decade, not a year.
        var m = Regex.Match(date ?? "", @"(?<!\d)(\d{4})(?![\ds])");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    // ── descriptions and biographies ──────────────────────────────────────────

    /// <summary>A text field that is either a plain string or <c>{"type": "/type/text", "value": "..."}</c>.</summary>
    public static string? TextOf(JsonElement? element)
    {
        if (element is not { } e) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Object when e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
            _ => null,
        };
    }

    [GeneratedRegex(@"^\s*\[[^\]\r\n]+\]:\s*\S+.*$", RegexOptions.Multiline)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"^\s*-{3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"\[([^\]]+)\]\[[^\]]*\]")]
    private static partial Regex ReferenceLink();

    [GeneratedRegex(@"\(\s*source\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceNote();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1", RegexOptions.Singleline)]
    private static partial Regex Strong();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)([^*\r\n]+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Emphasis();

    /// <summary>
    /// Open Library descriptions and biographies are Markdown-ish: <c>**bold**</c>, <c>*italic*</c>, <c>[text](url)</c>,
    /// <c>[text][1]</c> with <c>[1]: url</c> definitions, <c>----------</c> rules, Windows line endings. This keeps the words and
    /// paragraphs and drops the markup. Nothing but markup is removed.
    /// </summary>
    public static string? CleanMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        t = LineBreakTag().Replace(t, "\n");
        t = AnyTag().Replace(t, "");
        t = WebUtility.HtmlDecode(t);
        t = ReferenceDefinition().Replace(t, "");
        t = InlineLink().Replace(t, "$1");
        t = ReferenceLink().Replace(t, "$1");
        t = SourceNote().Replace(t, "");
        t = HorizontalRule().Replace(t, "");
        t = Strong().Replace(t, "$2");
        t = Emphasis().Replace(t, "$1");
        t = Regex.Replace(t, @"[ \t]+\n", "\n");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        t = Regex.Replace(t, @"[ \t]{2,}", " ").Trim();
        return t.Length == 0 ? null : t;
    }

    // ── subjects ──────────────────────────────────────────────────────────────

    // Open Library's own bookkeeping tags: not about the book.
    private static readonly HashSet<string> BookkeepingSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessible book", "protected daisy", "in library", "overdrive", "lending library",
        "internet archive wishlist", "open_syllabus_project",
    };

    private static readonly HashSet<string> FictionRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "fiction", "nonfiction", "juvenile fiction", "juvenile nonfiction", "young adult fiction", "young adult nonfiction",
    };

    private static string TitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>
    /// Splits Open Library's subjects. A BISAC-style heading ("Fiction, science fiction, general") yields genres ("Fiction",
    /// "Science Fiction"); machine tags ("series:Name", "nyt:...", "award:...") and Open Library's bookkeeping tags
    /// are left out of the tags (the full list is kept in the book's extended data); everything else is a tag.
    /// </summary>
    public static (List<string> Genres, List<string> Tags) SplitSubjects(IEnumerable<string>? subjects)
    {
        var genres = new List<string>();
        var tags = new List<string>();
        var seenGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in subjects ?? [])
        {
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s)) continue;
            if (BookkeepingSubjects.Contains(s)) continue;
            if (Regex.IsMatch(s, @"^[a-z_]+:", RegexOptions.IgnoreCase)) continue;   // series:, nyt:, award:, place: ...

            var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && FictionRoots.Contains(parts[0]))
            {
                foreach (var g in new[] { TitleCase(parts[0]), parts[1].Equals("general", StringComparison.OrdinalIgnoreCase) ? null : TitleCase(parts[1]) })
                    if (g is not null && seenGenres.Add(g)) genres.Add(g);
                continue;
            }
            if (FictionRoots.Contains(s)) { if (seenGenres.Add(TitleCase(s))) genres.Add(TitleCase(s)); continue; }
            if (seenTags.Add(s)) tags.Add(s);
        }
        return (genres, tags);
    }

    // ── images ────────────────────────────────────────────────────────────────

    public static string CoverUrl(long coverId, char size = 'L') => $"https://covers.openlibrary.org/b/id/{coverId}-{size}.jpg";

    public static string PhotoUrl(long photoId, char size = 'L') => $"https://covers.openlibrary.org/a/id/{photoId}-{size}.jpg";

}
