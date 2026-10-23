using System.Net;
using AwesomeAssertions;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14b item 1: an anonymous reader's page comes from the Web pod's memory. The API calls a
/// page makes are counted by the stub (<see cref="CacheHost.Calls"/>); a cached page makes none.
/// </summary>
public class OutputCacheReadingPageTests
{
    public static TheoryData<string> ReadingRoutes =>
    [
        "/",
        $"/post/{CacheHost.PostSlug}",
        $"/{CacheHost.PageSlug}",
        $"/category/{CacheHost.CategorySlug}",
        $"/tag/{CacheHost.TagSlug}",
        "/categories",
    ];

    /// <summary>What the cache itself says about the copy it serves, not what the page said: its age
    /// and the framing of the body (a rendered page streams, a stored one has a length).</summary>
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Age", "Content-Length", "Transfer-Encoding",
    };

    private static async Task<(HttpStatusCode Status, string Headers, string Body)> FetchAsync(
        HttpClient client, string path, bool staff = false, string? host = null)
    {
        using var request = CacheHost.Get(path, staff, host);
        using var response = await client.SendAsync(request);
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers)
            .Where(h => !TransportHeaders.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
        return (response.StatusCode, headers, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(ReadingRoutes))]
    public async Task SecondAnonymousHit_IsServedFromTheCache_ByteIdentical_AndMakesNoApiCall(string path)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var first = await FetchAsync(client, path);
        var callsForOneRender = host.Calls;
        var second = await FetchAsync(client, path);

        first.Status.Should().Be(HttpStatusCode.OK, $"{path} renders for an anonymous reader");
        callsForOneRender.Should().BeGreaterThan(0, "the first hit renders the page, which asks the API");
        host.Calls.Should().Be(callsForOneRender, "the second hit comes from the pod's memory");
        second.Body.Should().Be(first.Body, "the cached page is the page that was rendered");
        second.Headers.Should().Be(first.Headers,
            "the headers (CSP, security headers) are replayed with the page");
        first.Headers.Should().Contain("Content-Security-Policy:", "the comparison covers the policy header");
        first.Headers.Should().NotContain("Cache-Control",
            "the reading pages send no browser or proxy cache policy: someone who signs in must not get a cached anonymous copy");
    }

    [Fact]
    public async Task EveryEntryIsTaggedContent_AndLivesForTheDuration()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        await FetchAsync(client, $"/post/{CacheHost.PostSlug}");

        host.Store.Writes.Should().ContainSingle().Which.Should().Match<(string Key, string[] Tags, TimeSpan ValidFor)>(
            w => w.Tags.SequenceEqual(new[] { "content" }) && w.ValidFor == TimeSpan.FromMinutes(1),
            "every entry carries the content tag the staff writes evict, and the default Duration is a minute");
    }

    [Fact]
    public async Task APageNumber_IsItsOwnEntry_AndOtherQueryParametersShareTheEntryOfTheirPath()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        await FetchAsync(client, "/");
        var afterHome = host.Calls;
        await FetchAsync(client, "/?page=2");
        var afterPageTwo = host.Calls;
        await FetchAsync(client, "/?utm_source=x");
        await FetchAsync(client, "/?utm_source=y&utm_medium=z");
        await FetchAsync(client, "/?page=2&utm_source=x");
        await FetchAsync(client, "/?page=2");

        afterPageTwo.Should().BeGreaterThan(afterHome, "?page=2 is a different page from /, so it renders once of its own");
        host.Calls.Should().Be(afterPageTwo,
            "a query parameter the page does not read mints no entry: ?utm_source=x is /'s entry, and ?page=2&utm_source=x is ?page=2's");
        host.Store.Writes.Should().HaveCount(2, "two pages were rendered: / and /?page=2");
    }

    [Fact]
    public async Task AnUnreadablePageNumber_IsTheFirstPage_NotAnEntryOfItsOwn()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        await FetchAsync(client, "/");
        var afterHome = host.Calls;
        await FetchAsync(client, "/?page=abc");
        await FetchAsync(client, "/?page=0");
        await FetchAsync(client, "/?page=1");

        host.Calls.Should().Be(afterHome,
            "the page reads ?page=abc, ?page=0 and ?page=1 as page 1 (PageQuery), so they are the entry of /");
    }

    [Fact]
    public async Task AnotherHostOrScheme_ForTheSamePath_IsTheSameEntry()
    {
        // No Blog:BaseUrl: the script-src origin then comes from the request's own host, the one place
        // that would show the stand-in if it reached the render or the headers.
        using var host = CacheHost.Create(new() { ["Blog:BaseUrl"] = "" });
        using var client = host.Client();

        var rendered = await FetchAsync(client, "/", host: "blog.example.com");
        var afterFirst = host.Calls;
        var loopback = await FetchAsync(client, "/", host: "127.0.0.1:8080");
        var spoofed = await FetchAsync(client, "/", host: "anything.example");

        foreach (var response in new[] { rendered, loopback, spoofed })
        {
            response.Body.Should().NotContain("reading-pages.cache.invalid", "the key's stand-in host never reaches a page");
            response.Headers.Should().NotContain("reading-pages.cache.invalid", "nor a header");
        }

        host.Calls.Should().Be(afterFirst,
            "the key is the path and the query the page reads, nothing else: the warm-up (127.0.0.1) fills the entry readers get, and a spoofed Host header mints none");
        host.Store.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task AStaffRequest_IsNeverCached_AndLeavesTheAnonymousEntryAlone()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();
        var path = $"/post/{CacheHost.PostSlug}";

        await FetchAsync(client, path);
        var afterAnonymous = host.Calls;
        var writesAfterAnonymous = host.Store.Writes.Count;

        await FetchAsync(client, path, staff: true);
        var afterStaffOnce = host.Calls;
        await FetchAsync(client, path, staff: true);
        var afterStaffTwice = host.Calls;
        await FetchAsync(client, path);

        (afterStaffOnce - afterAnonymous).Should().BeGreaterThan(0, "a signed-in request is rendered, not read from the cache");
        (afterStaffTwice - afterStaffOnce).Should().Be(afterStaffOnce - afterAnonymous,
            "twice means two sets of API calls");
        host.Store.Writes.Should().HaveCount(writesAfterAnonymous, "a staff response is never stored");
        host.Calls.Should().Be(afterStaffTwice, "the staff requests did not evict or replace the anonymous entry");
        host.Store.Evictions.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_IsNotCached()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        await FetchAsync(client, "/search?q=cache");
        var afterOne = host.Calls;
        await FetchAsync(client, "/search?q=cache");

        afterOne.Should().BeGreaterThan(0);
        host.Calls.Should().Be(2 * afterOne, "free text is cached in the API (item 3), not here");
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownSlug_Is404_AndNeverCached()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var first = await FetchAsync(client, "/post/no-such-post");
        var afterOne = host.Calls;
        var second = await FetchAsync(client, "/post/no-such-post");

        first.Status.Should().Be(HttpStatusCode.NotFound);
        second.Status.Should().Be(HttpStatusCode.NotFound);
        host.Calls.Should().Be(2 * afterOne, "the API is asked twice; its own negative cache answers it");
        host.Store.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/admin/posts", true)]
    [InlineData("/admin/posts", false)]
    [InlineData("/not-found", false)]
    [InlineData("/Error", false)]
    [InlineData("/health/ready", false)]
    public async Task AnythingThatIsNotAReadingPage_IsNeverStored(string path, bool staff)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        await FetchAsync(client, path, staff);
        await FetchAsync(client, path, staff);

        host.Store.Writes.Should().BeEmpty($"{path} is not an anonymous reading page");
    }

    [Fact]
    public async Task ADurationAfterTheFill_TheNextHitRendersAgain()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();
        var path = $"/post/{CacheHost.PostSlug}";

        await FetchAsync(client, path);
        var afterFill = host.Calls;
        host.Clock.UtcNow += TimeSpan.FromSeconds(59);
        await FetchAsync(client, path);
        var justBefore = host.Calls;
        host.Clock.UtcNow += TimeSpan.FromSeconds(2);
        await FetchAsync(client, path);

        justBefore.Should().Be(afterFill, "59 seconds in, the entry is still good");
        host.Calls.Should().BeGreaterThan(justBefore, "past the Duration the entry is gone and the page renders again");
    }

    [Fact]
    public async Task ADurationOfZero_TurnsPageCachingOff()
    {
        using var host = CacheHost.Create(new() { ["Croft:Web:OutputCache:Duration"] = "00:00:00" });
        using var client = host.Client();

        await FetchAsync(client, "/");
        var afterOne = host.Calls;
        await FetchAsync(client, "/");

        host.Calls.Should().Be(2 * afterOne);
        host.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task ACachedAnonymousResponse_CarriesNoSetCookie_EvenWhenTheFillingRequestHadAStaleSessionCookie()
    {
        using var host = CacheHost.Create(staffHeader: false);
        using var client = host.Client();
        var cookie = await AuthCookie.SignInAsync(host.Services, "a-session-the-store-never-saw", "Admin");

        using var stale = new HttpRequestMessage(HttpMethod.Get, "/");
        stale.Headers.Add("Cookie", cookie);
        using var staleResponse = await client.SendAsync(stale);
        var anonymous = await FetchAsync(client, "/");
        using var again = await client.GetAsync("/");

        staleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        staleResponse.Headers.Contains("Set-Cookie").Should().BeTrue(
            "the rejected session signs the stale cookie out: the response of that request is not a shareable one");
        anonymous.Headers.Should().NotContain("Set-Cookie", "no cached anonymous page carries a cookie");
        again.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task APageAboveTheMaximumBody_IsServedWhole_AndNotCached()
    {
        using var host = CacheHost.Create(new() { ["Croft:Web:OutputCache:MaximumBodyKilobytes"] = "1" });
        using var client = host.Client();

        var first = await FetchAsync(client, "/");
        var afterOne = host.Calls;
        var second = await FetchAsync(client, "/");

        first.Body.Length.Should().BeGreaterThan(1024, "the page is above the test's 1 KB cap");
        first.Body.Should().Contain("</html>", "a page above the cap is served whole, not truncated");
        WithoutStateComment(second.Body).Should().Be(WithoutStateComment(first.Body));
        host.Calls.Should().Be(2 * afterOne, "and it is rendered each time");
    }

    private static string WithoutStateComment(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<!--Blazor-Server-Component-State:[^>]*-->", string.Empty);

    [Theory]
    [MemberData(nameof(ReadingRoutes))]
    public async Task AnEnhancedNavigationRequest_GetsTheSameResponseAsAFullLoad(string path)
    {
        // Blazor's enhanced navigation fetches the page with these two headers. If the answer
        // differed, the cache would have to vary on them; it does not, so it does not.
        using var host = CacheHost.Create(new() { ["Croft:Web:OutputCache:Duration"] = "00:00:00" });
        using var client = host.Client();

        var full = await FetchAsync(client, path);
        using var enhanced = new HttpRequestMessage(HttpMethod.Get, path);
        enhanced.Headers.Add("blazor-enhanced-nav", "on");
        enhanced.Headers.TryAddWithoutValidation("Accept", "text/html; blazor-enhanced-nav=on");
        using var response = await client.SendAsync(enhanced);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // The one thing two renders of a page never share is the data-protected component-state
        // comment at the end (a fresh random payload each time, also between two full loads).
        WithoutStateComment(await response.Content.ReadAsStringAsync()).Should().Be(WithoutStateComment(full.Body),
            "enhanced navigation is a plain fetch of the same document: the client patches the DOM");
    }

    [Fact]
    public async Task ACachedPage_KeepsTheEnhancedNavigationHeader_ExactlyAsBlazorSentIt()
    {
        // blazor.web.js compares this header's value to "allow", case-sensitively; any other spelling
        // makes every link click a full page load. The cache's serializer returns "Allow".
        using var host = CacheHost.Create();
        using var client = host.Client();

        using var rendered = await client.GetAsync("/categories");
        using var cached = await client.GetAsync("/categories");

        rendered.Headers.GetValues("blazor-enhanced-nav").Should().Equal("allow");
        cached.Headers.GetValues("blazor-enhanced-nav").Should().Equal("allow");
        host.Store.Writes.Should().ContainSingle("the second response came from the cache");
    }
}
