namespace Chronicle.Plugin.OpenLibrary;

/// <summary>What a book is being looked up by.</summary>
/// <param name="InSeries">The book sits under a series in the library, where one main title is shared by every volume.</param>
internal sealed record BookQuery(string Title, string? Author, int? Year, string PreferredLanguage, bool InSeries = false);

/// <summary>
/// Scoring of search results against a library item. Scores run 0 to 99 (100 is reserved for a match by identifier); Chronicle
/// accepts a match at 50 or above. The numbers come from running these rules over real library items (see
/// docs/plans/2026-10-10-openlibrary-plugin-design.md in the Chronicle repository).
/// </summary>
internal static class Matchers
{
    public const string AnyLanguage = "any";

    /// <summary>The most a book can score when it names a different volume or issue number than the one wanted: under Chronicle's gate of 50.</summary>
    public const int DifferentVolumeCap = 45;

    /// <summary>The most a book of a series can score when only its main title matches and the two carry different subtitles: under the gate.</summary>
    public const int DifferentSubtitleCap = 40;

    /// <summary>How well an Open Library work matches a library book. 0 means "not this book".</summary>
    public static int ScoreBook(WorkDoc doc, BookQuery q)
    {
        var itemFull = TextRules.Normalise(TextRules.StripYearSuffix(q.Title));
        var itemForms = TextRules.TitleForms(q.Title);
        var docForms = TextRules.DocTitleForms(doc.Title, doc.Subtitle);
        var docFull = TextRules.Normalise(doc.Title);
        if (itemFull.Length == 0 || docFull.Length == 0) return 0;

        int score;
        var mainTitleOnly = false;
        if (docFull == itemFull || docForms.Contains(itemFull)) score = 60;
        else if (itemForms.Overlaps(docForms)) { score = 50; mainTitleOnly = true; }
        // One title continuing the other ("The Dark Forest" / "The Dark Forest (The Three-Body Problem Series Book 2)"), or the
        // library title being the work's subtitle ("The Night Masquerade" / "Binti: The Night Masquerade") -- never one merely
        // appearing somewhere inside the other. A series named "Galaxy's Edge" is not "Legionnaire (Galaxy's Edge)", "Mirror Image"
        // is not "The Best of Isaac Asimov (... / Mirror-Image)", and "Muad Dib" is not "Songs of Muad'dib".
        else if ((Math.Min(itemFull.Length, docFull.Length) >= 6
                  && (itemFull.StartsWith(docFull, StringComparison.Ordinal) || docFull.StartsWith(itemFull, StringComparison.Ordinal)))
                 || (itemFull.Length >= 6 && (TextRules.SubtitleKey(doc.Title) == itemFull || TextRules.Normalise(doc.Subtitle) == itemFull)))
            score = 30;
        else return 0;

        var authorMismatch = false;
        if (!string.IsNullOrWhiteSpace(q.Author))
        {
            var wanted = TextRules.NameKey(q.Author);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in doc.AuthorName ?? []) known.Add(TextRules.NameKey(n));
            foreach (var n in doc.AuthorAlternativeName ?? []) known.Add(TextRules.NameKey(n));
            if (known.Contains(wanted)) score += 30;
            else authorMismatch = true;
        }

        if (TextRules.LooksLikeJunk(doc.Title) && !TextRules.LooksLikeJunk(q.Title)) score -= 40;

        if (!string.Equals(q.PreferredLanguage, AnyLanguage, StringComparison.OrdinalIgnoreCase)
            && doc.Language is { Count: > 0 } languages
            && !languages.Contains(q.PreferredLanguage, StringComparer.OrdinalIgnoreCase)
            && !TextRules.HasNonLatinLetters(q.Title))
            score -= 10;

        if (q.Year is { } year && doc.FirstPublishYear is { } published && Math.Abs(published - year) <= 1) score += 8;

        score += Math.Min(8, (int)Math.Log2(1 + Math.Max(0, doc.EditionCount ?? 0)));

        // "Invincible #59" and "Invincible, Vol. 1" share a main title and are different books.
        if (TextRules.PositionNumber(q.Title) is { } wantedNumber && TextRules.PositionNumber(doc.Title) is { } docNumber && wantedNumber != docNumber)
            score = Math.Min(score, DifferentVolumeCap);

        // Volumes of one series share a main title ("Space Team: Sentienced to Death" / "Space Team: The Time Titan of Tomorrow"):
        // when only the main title matched and each side has a subtitle of its own, they are different books. Outside a series
        // the same difference is usually a US and a UK edition of one book, so it is not held against a standalone title.
        if (q.InSeries && mainTitleOnly && TextRules.SubtitlesConflict(q.Title, doc.Title, doc.Subtitle))
            score = Math.Min(score, DifferentSubtitleCap);

        // A book by a different author is a different book, however alike the titles: never at or above Chronicle's gate.
        if (authorMismatch) score = Math.Min(score, 35);
        return Math.Clamp(score, 0, 99);
    }

    /// <summary>How well an Open Library author matches a library author. 0 means "not this person" (names must be equal
    /// after normalising; similar is not enough).</summary>
    public static int ScoreAuthor(AuthorDoc doc, string name, int? birthYear)
    {
        var wanted = TextRules.NameKey(name);
        if (wanted.Length == 0) return 0;
        var names = new HashSet<string>(StringComparer.Ordinal) { TextRules.NameKey(doc.Name) };
        foreach (var alt in doc.AlternateNames ?? []) names.Add(TextRules.NameKey(alt));
        if (!names.Contains(wanted)) return 0;

        // More works means the well-known one (John Green: 381 works beats 43).
        var score = 60 + Math.Min(15, (int)(Math.Log2(1 + Math.Max(0, doc.WorkCount ?? 0)) * 2));

        if (birthYear is { } wantedYear && TextRules.YearOf(doc.BirthDate) is { } born)
            score += Math.Abs(born - wantedYear) <= 1 ? 20 : -30;

        return Math.Clamp(score, 0, 99);
    }

    /// <summary>Bonus for an author whose works include the titles the library already holds under that author: 25 for the first
    /// title that matches, 5 for each further one, at most 35. Only used to tell same-named authors apart.</summary>
    public static int ChildTitleBonus(IReadOnlyList<string> libraryTitles, IReadOnlyList<string> authorWorkTitles)
    {
        var workForms = authorWorkTitles.Select(t => TextRules.DocTitleForms(t, null)).ToList();
        var matches = 0;
        foreach (var title in libraryTitles)
        {
            var forms = TextRules.TitleForms(title);
            if (forms.Count > 0 && workForms.Any(w => w.Overlaps(forms))) matches++;
        }
        return matches == 0 ? 0 : Math.Min(35, 25 + 5 * (matches - 1));
    }
}
