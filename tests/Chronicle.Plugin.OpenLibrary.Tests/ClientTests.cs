using System.Net;
using Chronicle.Plugin.OpenLibrary;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public class ClientTests
{
    [Fact]
    public async Task ASearch_AsksForTheFieldsItNeeds_AndReadsThem()
    {
        var fake = FakeOpenLibrary.Catalogue();
        using var client = fake.Client();

        var docs = await client.SearchWorksAsync("Leviathan Wakes", "James S. A. Corey", 10, CancellationToken.None);

        Assert.Equal("/works/OL16114008W", docs[0].Key);
        Assert.Equal("OL16114008W", docs[0].Olid);
        Assert.Equal("Leviathan Wakes", docs[0].Title);
        var url = Assert.Single(fake.Urls);
        Assert.StartsWith("/search.json?title=Leviathan Wakes&author=James S. A. Corey&limit=10&fields=key,title,subtitle,author_name", url);
        Assert.DoesNotContain("subject", url);   // the heavy subject list is read from the work record only
    }

    [Fact]
    public async Task ASearchWithoutAnAuthor_LeavesTheAuthorOut()
    {
        var fake = FakeOpenLibrary.Catalogue();
        using var client = fake.Client();

        await client.SearchWorksAsync("The Outpost", null, 10, CancellationToken.None);
        await client.SearchWorksAsync("The Outpost", "  ", 10, CancellationToken.None);

        Assert.All(fake.Urls, u => Assert.DoesNotContain("author=", u));
    }

    [Fact]
    public async Task QueriesAndNamesAreEscaped()
    {
        var fake = FakeOpenLibrary.Catalogue();
        using var client = fake.Client();

        await client.SearchWorksAsync("Rock & Roll: 100% \"quoted\"?", "A&B", 5, CancellationToken.None);

        Assert.Contains("title=Rock & Roll: 100% \"quoted\"?&author=A&B&limit=5", fake.Urls[0]);   // the fake unescapes what was sent
    }

    [Fact]
    public async Task AuthorSearch_AuthorWorks_AndTheRecords_AreFetched()
    {
        var fake = FakeOpenLibrary.Catalogue();
        using var client = fake.Client();

        var authors = await client.SearchAuthorsAsync("John Green", 6, CancellationToken.None);
        var titles = await client.AuthorWorkTitlesAsync("OL6982995A", 100, CancellationToken.None);
        var author = await client.GetAuthorAsync("OL6982995A", CancellationToken.None);
        var work = await client.GetWorkAsync("OL16114008W", CancellationToken.None);
        var series = await client.GetSeriesAsync("OL334747L", CancellationToken.None);
        var edition = await client.GetEditionAsync("OL25001749M", CancellationToken.None);
        var byQuery = await client.SearchWorksByQueryAsync("isbn:9780316129084", 1, CancellationToken.None);

        Assert.Contains(authors, a => a.Key == "OL5046634A");
        Assert.Contains("Leviathan Wakes", titles);
        Assert.Equal("James S. A. Corey", author!.Name);
        Assert.Equal("Leviathan Wakes", work!.Title);
        Assert.Equal("The Expanse", series!.Name);
        Assert.Equal("/works/OL16114008W", edition!.Works![0].Key);
        Assert.Single(byQuery);
        Assert.Contains("/search/authors.json?q=John Green&limit=6", fake.Urls);
        Assert.Contains("/search.json?author_key=OL6982995A&sort=editions&limit=100&fields=key,title", fake.Urls);
    }

    [Fact]
    public async Task ANotFound_IsNull_NotAnError()
    {
        using var client = FakeOpenLibrary.Catalogue().Client();

        Assert.Null(await client.GetWorkAsync("OL1W", CancellationToken.None));
        Assert.Null(await client.GetAuthorAsync("OL999999999A", CancellationToken.None));
        Assert.Empty(await client.SearchWorksAsync("nothing here", null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task ALongRateLimit_IsReportedAsAnHttp429_NotWaitedOut()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = HttpStatusCode.TooManyRequests;
        fake.RetryAfterSeconds = 60;
        using var client = fake.Client();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWorkAsync("OL16114008W", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task ARateLimitWithNoRetryAfter_IsAlsoReported_NotWaitedOut()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = HttpStatusCode.TooManyRequests;
        using var client = fake.Client();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWorkAsync("OL16114008W", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task AShortRateLimit_IsWaitedOutOnce()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = HttpStatusCode.TooManyRequests;
        fake.RetryAfterSeconds = 0;
        fake.FailTimes = 1;
        using var client = fake.Client();

        var work = await client.GetWorkAsync("OL16114008W", CancellationToken.None);

        Assert.Equal("Leviathan Wakes", work!.Title);
        Assert.Equal(2, fake.Requests);   // the refused request, then the one that worked
    }

    [Fact]
    public async Task ARateLimitThatPersistsAfterTheRetry_IsReported()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = HttpStatusCode.TooManyRequests;
        fake.RetryAfterSeconds = 0;
        using var client = fake.Client();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWorkAsync("OL16114008W", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(2, fake.Requests);   // one retry, no more
    }

    [Fact]
    public async Task ARetryAfterGivenAsADate_IsUnderstood()
    {
        var fake = new FakeDated();
        using var client = new OpenLibraryClient(new HttpClient(fake), TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWorkAsync("OL1W", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("retry in about", error.Message);
    }

    private sealed class FakeDated : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5));
            return Task.FromResult(r);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AServerError_IsAnError_WithItsStatus(HttpStatusCode status)
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.FailWith = status;
        using var client = fake.Client();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAuthorsAsync("anyone", 6, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task AnUnreadableAnswer_IsAnError_NotAnEmptyResult()
    {
        var fake = new FakeOpenLibrary().RouteBody(_ => true, "<html>Bad gateway</html>");
        using var client = fake.Client();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWorkAsync("OL1W", CancellationToken.None));

        Assert.Contains("could not read", error.Message);
    }

    [Fact]
    public async Task Requests_AreSpacedByTheInterval()
    {
        var fake = FakeOpenLibrary.Catalogue();
        using var client = fake.Client(TimeSpan.FromMilliseconds(120));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await client.GetWorkAsync("OL16114008W", CancellationToken.None);
        await client.GetAuthorAsync("OL6982995A", CancellationToken.None);
        await client.GetSeriesAsync("OL334747L", CancellationToken.None);

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(200), $"three requests took {clock.ElapsedMilliseconds}ms");
        Assert.Equal(3, client.Requests);
    }

    [Fact]
    public async Task ACancelledCall_StopsWaiting()
    {
        var fake = FakeOpenLibrary.Catalogue();
        using var client = fake.Client(TimeSpan.FromSeconds(30));
        await client.GetWorkAsync("OL16114008W", CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAuthorAsync("OL6982995A", cts.Token));
    }

    [Fact]
    public void TheDefaultPacing_FollowsOpenLibrarysPolicy()
    {
        Assert.True(OpenLibraryClient.DefaultInterval >= TimeSpan.FromSeconds(1));       // 1 request a second
        Assert.True(OpenLibraryClient.IdentifiedInterval <= TimeSpan.FromMilliseconds(400));   // 3 requests a second
        Assert.True(OpenLibraryClient.IdentifiedInterval * 3 > TimeSpan.FromMilliseconds(900));   // but not more than 3
        Assert.True(OpenLibraryClient.MaxRetryWait < TimeSpan.FromSeconds(25));          // inside Chronicle's per-call ceiling
    }

    [Theory]
    [InlineData("1.0.0", null, "Chronicle-OpenLibrary/1.0.0")]
    [InlineData("1.0.0", "", "Chronicle-OpenLibrary/1.0.0")]
    [InlineData("1.0.0", "  ", "Chronicle-OpenLibrary/1.0.0")]
    [InlineData("1.2.3", "me@example.org", "Chronicle-OpenLibrary/1.2.3 (me@example.org)")]
    [InlineData("1.2.3", "  me@example.org ", "Chronicle-OpenLibrary/1.2.3 (me@example.org)")]
    public void TheUserAgent_NamesTheApplication_AndTheContactWhenThereIsOne(string version, string? email, string expected) =>
        Assert.Equal(expected, OpenLibraryClient.UserAgent(version, email));

    [Fact]
    public async Task Images_AreDownloadedWithoutPacing()
    {
        var fake = FakeOpenLibrary.Catalogue();
        fake.ImageBytes = [9, 8, 7];
        using var client = fake.Client(TimeSpan.FromSeconds(30));

        var bytes = await client.GetImageBytesAsync("https://covers.openlibrary.org/b/id/7314237-L.jpg", CancellationToken.None);

        Assert.Equal(new byte[] { 9, 8, 7 }, bytes);
        Assert.Equal(0, client.Requests);   // not counted against the API budget
    }

    [Fact]
    public async Task HealthCheck_IsTrue_WhenTheWorkIsThere_AndFalse_WhenItIsNotOrTheServiceFails()
    {
        using var up = FakeOpenLibrary.Catalogue().Client();
        using var missing = new FakeOpenLibrary().Client();
        var broken = FakeOpenLibrary.Catalogue();
        broken.FailWith = HttpStatusCode.InternalServerError;
        using var down = broken.Client();

        Assert.True(await up.HealthCheckAsync(CancellationToken.None));
        Assert.False(await missing.HealthCheckAsync(CancellationToken.None));
        Assert.False(await down.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthCheck_DoesNotSwallowACancellation()
    {
        using var client = FakeOpenLibrary.Catalogue().Client();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.HealthCheckAsync(cts.Token));
    }
}
