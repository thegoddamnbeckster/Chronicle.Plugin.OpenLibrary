using System.Text.RegularExpressions;

namespace Chronicle.Plugin.OpenLibrary;

internal enum OlRefKind { Work, Author, Edition, Isbn, Goodreads }

/// <summary>What a pasted or stored identifier refers to.</summary>
internal sealed record OlRef(OlRefKind Kind, string Value)
{
    private static readonly Regex Olid = new(@"^OL\d+[WAM]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlPath = new(@"openlibrary\.org/(?:works|authors|books)/(OL\d+[WAM])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsbnUrl = new(@"openlibrary\.org/isbn/([0-9Xx-]{10,17})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SlashPath = new(@"^/?(?:works|authors|books)/(OL\d+[WAM])(?:\.json)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Understands: a bare Open Library id (OL16114008W work, OL6982995A author, OL25001749M edition), the same with an
    /// "openlibrary:" prefix, a /works/... path, an openlibrary.org URL, an ISBN-10 or ISBN-13 (hyphens allowed), and
    /// "goodreads:" + a Goodreads book id. Anything else is null.</summary>
    public static OlRef? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;

        if (s.StartsWith("openlibrary:", StringComparison.OrdinalIgnoreCase)) s = s["openlibrary:".Length..].Trim();

        if (s.StartsWith("goodreads:", StringComparison.OrdinalIgnoreCase))
        {
            var id = s["goodreads:".Length..].Trim();
            return id.Length > 0 && id.All(char.IsDigit) ? new OlRef(OlRefKind.Goodreads, id) : null;
        }

        if (IsbnUrl.Match(s) is { Success: true } isbnUrl) return IsbnRef(isbnUrl.Groups[1].Value);
        if (UrlPath.Match(s) is { Success: true } url) return FromOlid(url.Groups[1].Value);
        if (SlashPath.Match(s) is { Success: true } slash) return FromOlid(slash.Groups[1].Value);
        if (Olid.IsMatch(s)) return FromOlid(s);
        return IsbnRef(s);
    }

    // Only ever called for text already matched as OL + digits + W, A or M.
    private static OlRef FromOlid(string olid)
    {
        olid = olid.ToUpperInvariant();
        return olid[^1] switch
        {
            'W' => new OlRef(OlRefKind.Work, olid),
            'A' => new OlRef(OlRefKind.Author, olid),
            _ => new OlRef(OlRefKind.Edition, olid),
        };
    }

    private static OlRef? IsbnRef(string text)
    {
        var digits = text.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (digits.Length == 13 && digits.All(char.IsDigit)) return new OlRef(OlRefKind.Isbn, digits);
        if (digits.Length == 10 && digits[..9].All(char.IsDigit) && (char.IsDigit(digits[9]) || digits[9] == 'X'))
            return new OlRef(OlRefKind.Isbn, digits);
        return null;
    }
}
