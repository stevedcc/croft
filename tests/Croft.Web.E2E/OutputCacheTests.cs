using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 14b item 1, in a real browser against a real stack: an anonymous reader's page comes from
/// the Web pod's memory, and a cached page still lets Blazor's enhanced navigation patch the next
/// page in. Runs only on instances with the output cache on (dev/run-e2e.sh --output-cache, which the
/// plain dev/run-e2e.sh also does after the main suite): the main suite runs with page caching off,
/// because its tests publish through the API behind the Web app's back and read the page at once,
/// which a cache that is up to a minute stale would turn into a lottery.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("OutputCache")]
public class OutputCacheTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [SetUp]
    public void RequireTheCacheOn()
    {
        if (Environment.GetEnvironmentVariable("E2E_OUTPUT_CACHE") != "on")
        {
            Assert.Ignore("needs instances with the output cache on: dev/run-e2e.sh --output-cache");
        }
    }

    [Test]
    public async Task ASecondFullLoadOfAPost_IsServedFromTheCache_WithNoApiRequest()
    {
        var title = $"Cached post {RunId}";
        var post = await PublishAsync(title, "## A section\n\nA body worth caching.");

        var before = ApiRequestsFor($"posts/{post.Slug}");
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title, Level = 1 })).ToBeVisibleAsync();
        var afterFirst = ApiRequestsFor($"posts/{post.Slug}");

        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title, Level = 1 })).ToBeVisibleAsync();
        var afterSecond = ApiRequestsFor($"posts/{post.Slug}");

        Assert.That(afterFirst - before, Is.EqualTo(1), "the first load renders the page, which asks the API once");
        Assert.That(afterSecond - afterFirst, Is.Zero, "the second load is the cached page: the Web app asked the API nothing");
    }

    [Test]
    public async Task AClickToACachedPage_IsStillAnEnhancedNavigation()
    {
        var title = $"Cached target {RunId}";
        var post = await PublishAsync(title, "## A section\n\nThe page a link leads to.");
        var linkerTitle = $"Cached linker {RunId}";
        var linker = await PublishAsync(linkerTitle, $"See [the target](/post/{post.Slug}).");

        // Cache the target: the first full load fills the entry, the click below is then answered from it.
        await Page.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title, Level = 1 })).ToBeVisibleAsync();

        await Page.GotoAsync($"{BaseUrl}/post/{linker.Slug}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = linkerTitle, Level = 1 })).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => { window.__navigationMarker = 'before-the-click'; }");

        var before = ApiRequestsFor($"posts/{post.Slug}");
        await Page.Locator($".markdown-body a[href$='/{post.Slug}']").ClickAsync();
        await Page.WaitForURLAsync(url => url.EndsWith($"/post/{post.Slug}"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = title, Level = 1 })).ToBeVisibleAsync();

        Assert.That(await Page.EvaluateAsync<string?>("() => window.__navigationMarker"), Is.EqualTo("before-the-click"),
            "a full document load happened: the cached page lost the header that lets Blazor patch the page in");
        Assert.That(ApiRequestsFor($"posts/{post.Slug}") - before, Is.Zero, "and the page came from the cache");
    }

    [TestCase("/feed.xml", "posts", "<feed")]
    [TestCase("/sitemap.xml", "sitemap", "<urlset")]
    public async Task TheFeedAndTheSitemap_AreBuiltOnce_AndSayHowLongTheyMayBeKept(string path, string apiPath, string root)
    {
        await PublishAsync($"Feed entry {RunId}", "## A section\n\nA body for the feed.");

        await using var first = await Page.APIRequest.GetAsync($"{BaseUrl}{path}");
        var afterFirst = ApiRequestsFor(apiPath);
        await using var second = await Page.APIRequest.GetAsync($"{BaseUrl}{path}");
        var afterSecond = ApiRequestsFor(apiPath);

        Assert.That(first.Status, Is.EqualTo(200));
        Assert.That(first.Headers["cache-control"], Is.EqualTo("public, max-age=300"));
        Assert.That(second.Headers["cache-control"], Is.EqualTo("public, max-age=300"), "the cached copy says the same");
        Assert.That(await second.TextAsync(), Does.Contain(root), "and is a real document");
        Assert.That(afterSecond - afterFirst, Is.Zero, "the second request is the cached document: the Web app asked the API nothing");
    }

    [Test]
    public async Task AStaffEditOnThisInstance_ShowsToTheNextAnonymousReaderAtOnce()
    {
        // Brief 14b: a staff write through the Web app evicts the cached pages of the pod that handled it.
        var oldTitle = $"Evict before {RunId}";
        var newTitle = $"Evict after {RunId}";
        var post = await PublishAsync(oldTitle, "## A section\n\nA body that stays.");
        var heading = (string title) => new PageGetByRoleOptions { Name = title, Level = 1 };

        await using var reader = await Browser.NewContextAsync();
        var anonymous = await reader.NewPageAsync();
        await anonymous.GotoAsync($"{BaseUrl}/post/{post.Slug}");
        await Expect(anonymous.GetByRole(AriaRole.Heading, heading(oldTitle))).ToBeVisibleAsync();
        var afterFill = ApiRequestsFor($"posts/{post.Slug}");
        await anonymous.ReloadAsync();
        Assert.That(ApiRequestsFor($"posts/{post.Slug}"), Is.EqualTo(afterFill), "the reload came from the cache, so the edit below has something to evict");

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{post.Id}/edit");
        await WaitForInteractiveAsync(Page);
        var form = Page.Locator("[data-draft-stamp]");
        await Expect(form).ToBeVisibleAsync();
        var stampBefore = await form.GetAttributeAsync("data-draft-stamp");
        var titleField = Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true });
        await titleField.FillAsync(newTitle);
        await titleField.BlurAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Expect(form).Not.ToHaveAttributeAsync("data-draft-stamp", stampBefore!,
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 15000 });

        await anonymous.ReloadAsync();
        await Expect(anonymous.GetByRole(AriaRole.Heading, heading(newTitle))).ToBeVisibleAsync();
    }

    [Test]
    public async Task APublishedPostsImage_IsFetchedFromTheApiOnce_ForAnyNumberOfReaders()
    {
        // Brief 14c item 6: the image proxy keeps a public image in the Web pod's memory.
        var banner = await UploadBannerAsync($"cached-image-{RunId}.png");
        await PublishAsync($"Cached image post {RunId}", "## A section\n\nA post whose banner is cached.", bannerImageId: banner);

        await using var first = await Page.APIRequest.GetAsync($"{BaseUrl}/api/media/{banner}");
        var afterFirst = ApiRequestsFor($"media/{banner}");
        await using var second = await Page.APIRequest.GetAsync($"{BaseUrl}/api/media/{banner}?v=2");
        await using var third = await Page.APIRequest.GetAsync($"{BaseUrl}/api/media/{banner}");

        Assert.That(first.Status, Is.EqualTo(200));
        Assert.That(first.Headers["content-type"], Is.EqualTo("image/png"));
        Assert.That(first.Headers["cache-control"], Does.Contain("public"), "the API's Cache-Control reaches the browser");
        Assert.That(afterFirst, Is.EqualTo(1), "the first request asks the API");
        Assert.That(ApiRequestsFor($"media/{banner}"), Is.EqualTo(afterFirst), "the next two (one with another query string) came from the pod's memory");
        Assert.That(await second.BodyAsync(), Is.EqualTo(await first.BodyAsync()), "and are the same image");
        Assert.That(await third.BodyAsync(), Is.EqualTo(await first.BodyAsync()));
        Assert.That(second.Headers["cache-control"], Is.EqualTo(first.Headers["cache-control"]));
    }

    private int ApiRequestsFor(string endpointPath) =>
        Regex.Count(WebLogText(),
            $@"Start processing HTTP request GET {Regex.Escape(ApiUrl)}/api/{Regex.Escape(endpointPath)}(\?.*)?$",
            RegexOptions.Multiline);
}
