# Chronicle.Plugin.OpenLibrary

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.OpenLibrary?label=Chronicle.Plugin.OpenLibrary&color=1b6f45)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.OpenLibrary/releases/latest)

Book and author metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches book and author metadata — descriptions, covers, authors with biographies and photos, genres, subjects, ratings, series where Open Library has it, and cross-reference ids — from [Open Library](https://openlibrary.org/). A second source beside Hardcover for books and audiobooks, with no API key and no quota.

**Plugin ID:** `chronicle.plugin.openlibrary`
**Version:** 1.0.0
**Auth:** None (no API key or account required)

---

## Supported Media Types

| Media Type | Hierarchy | Fields |
|------------|-----------|--------|
| `books` | Author / Series / Book | title, overview, year, poster_url, genres, tags, cast (authors), rating |
| `audiobooks` | Author / Series / Book | title, overview, year, poster_url, genres, tags, cast (authors), rating |

Audiobooks use the same lookup as books: Open Library has one record per book whatever its format, and it does not record
narrators or running time, so those stay blank. Priority is 20, so **Hardcover (10) wins by default** and Open Library fills
what Hardcover does not have. Reorder it per field in Metadata Assignment.

---

## External ID Format

Stored under source `openlibrary` as bare Open Library ids, whose ending says what they are:

| ID | Meaning |
|----|---------|
| `OL16114008W` | A work (a book) |
| `OL6982995A` | An author |

**Fix match** accepts any of: an Open Library id, an openlibrary.org URL (work, author or edition), an ISBN-10 or ISBN-13,
or `goodreads:` followed by a Goodreads book id.

Cross-reference ids published for other plugins (`ids`): Goodreads, LibraryThing, Amazon ASIN and ISBN-13. Open Library's
MusicBrainz, BookBrainz, Wikidata, VIAF and ISNI values are kept in the item's extended data but deliberately not offered as
lookups, because a `musicbrainz` value here names a book and not a recording.

---

## Features

### How a book is found

Each item is searched by its **main title** and its author: the text before a `:` or ` - `, with a trailing year (`(2016)`),
and a position marker (`, Part 2`, `Book Three`, `#59`, `Vol. 4`) removed. Long subtitles break Open Library's search, so
the whole title is only tried when the main title finds nothing. Results are scored 0 to 99:

* the title matches (whole title, or main title, or subtitle form) and the author matches (any listed name or alternate name);
* a book by a **different author** is never accepted, however alike the title ("The Outpost" by Mike Resnick is not by Rick Partlow);
* a different volume or issue number is a different book ("Invincible #59" is not "Invincible, Vol. 1");
* study guides, "Summary of ...", workbooks, box sets, omnibuses and anthologies rank below the book itself;
* a result that exists in your preferred language ranks above one that does not (nothing is hidden);
* a close publication year and a large edition count help a little.

A book searched with no author known can never reach Chronicle's confidence gate: it stays "not found" for you to fix
with Fix match instead of attaching someone else's book.

### How an author is found

An author is matched by name (an alternate name counts; a merely similar name does not). When several people share a name the
plugin tells them apart by the work count, by the birth year when the library has one, and by whether their works include the
titles your library already holds under that author. (The novelist John Green has 32 works in Open Library; a different John
Green has 381.)

### Series

Open Library's series data is sparse (about one series in twenty in a sample of a real library), so a series with books
under it is **not looked up** and returns nothing without any network call. Where a *book* has series information (a
structured series, or a `series:` subject) it is recorded on that book. A level-1 item with nothing under it is a standalone
book and is searched as one.

### Descriptions and biographies

Open Library writes Markdown (`**bold**`, `[text][1]`, `----------`, Windows line endings). The words and paragraphs are
kept and the markup is removed. Genres come from Open Library's `Fiction, science fiction, general` style headings;
other subjects become tags, minus Open Library's own bookkeeping tags. The full original subject list is kept in the extended data.

### Rate limiting

Open Library allows **1 request per second**, or **3 per second** when requests carry a User-Agent with a contact email.
Requests are paced to match. Chronicle gives a plugin 25 seconds per call, so a rate-limit answer is never waited out: a wait
of 8 seconds or less is retried once, anything longer is reported as an HTTP 429, and Chronicle then leaves the item **Pending**
for the next run instead of marking it not found.

A first pass over a large library is slow by design: measured on a real library, about **20 items a minute at 1 request/second**,
so roughly 4 hours for 750 authors and 3,900 books, or about a third of that with a contact email. Entries just fetched are remembered for ten minutes. Open Library's data is
used through its API only: no HTML is scraped and covers are never downloaded in bulk (Chronicle receives the image URL).

---

## Setup

No account or API key is needed.

1. In Chronicle → **Plugins**, install this plugin from the catalog.
2. Open its settings and choose your preferred language (English by default).
3. Optionally enter a contact email for faster lookups (see Configuration).
4. Enable it. Use **Health check** to confirm Chronicle can reach Open Library.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `preferred_language` | | `eng` (English) | The language of your books, as Open Library's ISO 639-2 code. Results that do not exist in this language rank lower; nothing is hidden, and a title in another alphabet is not penalised. "No preference" turns the ranking off. |
| `contact_email` | | empty | Optional. Put in the User-Agent header so Open Library allows 3 requests a second instead of 1 and can contact you if something goes wrong. Sent only to openlibrary.org; never stored anywhere else. Leave empty to stay anonymous. |

---

## Background Tasks

| Task | Default schedule | Description |
|------|------------------|-------------|
| Fetch Missing Book Metadata | Daily 05:30, on | Looks up Open Library metadata for books, audiobooks and authors not yet enriched by Open Library |
| Re-sync All Book Metadata | Weekly Sunday 03:40, off | Re-fetches everything to pick up corrected descriptions, covers and ratings. Slow on a large library |

---

## Repository Structure

```
Chronicle.Plugin.OpenLibrary/
├── Chronicle.Plugin.OpenLibrary.csproj
├── manifest.json
├── OpenLibraryMetadataProvider.cs   # IMetadataProvider: routing by hierarchy level, search, fetch by id, settings
├── Matchers.cs                      # scoring of books and authors
├── TextRules.cs                     # normalising, title forms, markdown cleaning, subjects, image URLs
├── MetadataMapper.cs                # Open Library records -> Chronicle metadata
├── OpenLibraryClient.cs             # paced HTTP client, User-Agent, rate-limit contract
├── OlRef.cs                         # understands ids, URLs, ISBNs
├── TtlCache.cs                      # ten-minute memory of fetched records
├── OpenLibraryModels.cs             # JSON models
└── tests/Chronicle.Plugin.OpenLibrary.Tests/   # real recorded responses in Fixtures/
```

---

## Building

```powershell
dotnet build -c Release
```

Copy output to your Chronicle plugin directory:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.openlibrary"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.OpenLibrary.dll"       $pluginDir
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.OpenLibrary.deps.json" $pluginDir
Copy-Item "manifest.json"                                             $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it, and Serilog too
> (referenced at the exact version the host loads, without bundling).

Tests:

```powershell
dotnet test tests/Chronicle.Plugin.OpenLibrary.Tests                                  # recorded responses, no network
$env:OPENLIBRARY_LIVE = "1"; dotnet test tests/Chronicle.Plugin.OpenLibrary.Tests     # also the real Open Library (1 request/second)
dotnet test tests/Chronicle.Plugin.OpenLibrary.Tests --collect:"XPlat Code Coverage"  # coverage
```

---

## Development

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

Both repositories must be cloned as siblings for this to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.OpenLibrary\
```

The design, with the measurements it rests on, is in the Chronicle repository: `docs/plans/2026-10-10-openlibrary-plugin-design.md`.

---

## License

MIT. Open Library's data is available under open licences; see [openlibrary.org](https://openlibrary.org/).
