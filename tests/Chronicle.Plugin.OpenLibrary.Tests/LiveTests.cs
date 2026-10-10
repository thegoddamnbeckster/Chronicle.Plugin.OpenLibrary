using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENLIBRARY_LIVE") != "1") Skip = "Set OPENLIBRARY_LIVE=1 to run against the real Open Library.";
    }
}

public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENLIBRARY_LIVE") != "1") Skip = "Set OPENLIBRARY_LIVE=1 to run against the real Open Library.";
    }
}

/// <summary>
/// Runs only when OPENLIBRARY_LIVE=1: talks to the real openlibrary.org, at its anonymous limit of one request a second, to
/// prove the queries, the field names and the matching work on today's data and not just on the recorded fixtures.
/// </summary>
public class LiveTests
{
    private static OpenLibraryMetadataProvider Real()
    {
        var provider = new OpenLibraryMetadataProvider();
        provider.Configure(new Dictionary<string, string>());
        return provider;
    }

    [LiveTheory]
    [InlineData("Oathbringer", "Brandon Sanderson", "The Stormlight Archive", "books")]
    [InlineData("Ubik (1969)", "Philip K. Dick", null, "books")]
    [InlineData("Pebble in the Sky", "Isaac Asimov", "Galactic Empire", "books")]
    [InlineData("The Consuming Fire", "John Scalzi", "The Interdependency", "audiobooks")]
    [InlineData("Atomic habits: An Easy & Proven Way to Build Good Habits & Break Bad Ones (2018)", "James Clear", null, "books")]
    [InlineData("A Dance with Dragons, Part 1: Dreams and Dust", "George R.R. Martin", "A Song of Ice and Fire", "books")]
    public async Task TheLibraryBooks_AreFound(string title, string author, string? series, string type)
    {
        using var provider = Real();
        var context = new MediaSearchContext(title, ParentName: series ?? author, GrandparentName: series is null ? null : author,
            HierarchyLevel: series is null ? 1 : 2, ChildCount: 0, MediaTypeName: type, IsRealHierarchyPosition: true);

        var found = await provider.SearchAsync(context);

        Assert.NotEmpty(found);
        Assert.True(found[0].Score >= 70, $"{title}: {found[0].Metadata.Title} scored {found[0].Score}");
        var full = await provider.GetByIdAsync(found[0].Metadata.ExternalId);
        Assert.False(string.IsNullOrWhiteSpace(full.Overview), $"{title} has no description");
    }

    [LiveTheory]
    [InlineData("Brandon Sanderson")]
    [InlineData("Emily Henry")]
    [InlineData("Isaac Asimov")]
    public async Task TheLibraryAuthors_AreFound_WithABiography(string name)
    {
        using var provider = Real();
        var context = new MediaSearchContext(name, HierarchyLevel: 0, MediaTypeName: "books", IsRealHierarchyPosition: true);

        var found = await provider.SearchAsync(context);

        Assert.NotEmpty(found);
        var author = await provider.GetByIdAsync(found[0].Metadata.ExternalId);
        Assert.Equal(name, author.Title);
    }

    [LiveFact]
    public async Task TheNovelistJohnGreen_IsToldFromTheOthers_ByTheBooksTheLibraryHolds()
    {
        using var provider = Real();
        var context = new MediaSearchContext("John Green", HierarchyLevel: 0, MediaTypeName: "books", IsRealHierarchyPosition: true,
            ChildNames: ["The Fault in Our Stars", "Paper Towns"]);

        var found = await provider.SearchAsync(context);

        Assert.Equal("OL5046634A", found[0].Metadata.ExternalId);
    }

    [LiveFact]
    public async Task ASeriesWithBooksUnderIt_StaysUnlooked()
    {
        using var provider = Real();

        var found = await provider.SearchAsync(new MediaSearchContext("The Expanse", ParentName: "James S. A. Corey", HierarchyLevel: 1, ChildCount: 9, MediaTypeName: "books"));

        Assert.Empty(found);
    }

    [LiveFact]
    public async Task AnIsbn_AndAGoodreadsId_LeadToTheSameBook()
    {
        using var provider = Real();

        var byIsbn = await provider.GetByIdAsync("9780316129084");
        var byGoodreads = await provider.GetByIdAsync("goodreads:35182525");

        Assert.Equal("OL16114008W", byIsbn.ExternalId);
        Assert.Equal(byIsbn.ExternalId, byGoodreads.ExternalId);
        Assert.Equal("The Expanse", byIsbn.ExtendedData!.Value.GetProperty("series").GetProperty("name").GetString());
    }

    [LiveFact]
    public async Task HealthCheck_IsTrue() => Assert.True(await Real().HealthCheckAsync());
}
