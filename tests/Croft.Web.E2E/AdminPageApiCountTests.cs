using System.Text.RegularExpressions;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 13e item 2: a full load of an admin page (prerender, then the circuit's own run) fetches
/// each piece of data once, not twice. The counts come from this instance's Web log, which logs
/// every API request the Web app sends ("Start processing HTTP request GET ..."); a fixture owns
/// its instance while it runs (WebTest.StackUrls), so the log delta is this test's.
/// Break-it-once (recorded in the commit): [PersistentState] removed from TagsPage (its lists load
/// twice, so the tags-page test fails) and from PostEditPage (the post, its feedback and the tag
/// suggestions load twice, so the edit-page test fails).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class AdminPageApiCountTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static bool _dataReady;
    private static Published _post = null!;

    [Test]
    public async Task PostsList_FullLoad_FetchesThePostsOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync("/admin/posts", () => Expect(Row(PostTitle)).ToBeVisibleAsync());

        Assert.That(Requests(@"/api/posts\?\*"), Is.EqualTo(1));
    }

    [Test]
    public async Task PagesList_FullLoad_FetchesThePagesOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync("/admin/pages", () => Expect(Row(PageTitle)).ToBeVisibleAsync());

        Assert.That(Requests(@"/api/posts\?\*"), Is.EqualTo(1));
    }

    [Test]
    public async Task TagsPage_FullLoad_FetchesTagsAndCategoriesOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync("/admin/tags", () => Expect(Page.GetByLabel("New tag")).ToBeVisibleAsync());

        Assert.That(Requests(@"/api/tags\?\*"), Is.EqualTo(1));
        // The shell's sidebar read (top categories) is the other one.
        Assert.That(Requests(@"/api/categories\?\*"), Is.EqualTo(2), "the categories table and the sidebar, once each");
    }

    [Test]
    public async Task MediaPage_FullLoad_FetchesTheLibraryOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync("/admin/media", () => Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Media library" })).ToBeVisibleAsync());

        Assert.That(Requests(@"/api/media\?\*"), Is.EqualTo(1));
    }

    [Test]
    public async Task PostEditPage_FullLoad_FetchesThePostItsFeedbackAndTheTagSuggestionsOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync($"/admin/posts/{_post.Id}/edit",
            () => Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(PostTitle));

        Assert.That(Requests($@"/api/posts/{_post.Id}$"), Is.EqualTo(1));
        Assert.That(Requests($@"/api/posts/{_post.Id}/feedback$"), Is.EqualTo(1));
        Assert.That(Requests(@"/api/tags\?\*"), Is.EqualTo(1));
    }

    [Test]
    public async Task PostAnalyticsPage_FullLoad_FetchesTheSummaryOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync($"/admin/posts/{_post.Id}/analytics",
            () => Expect(Page.Locator(".analytics__total")).ToBeVisibleAsync());

        Assert.That(Requests(@"/api/analytics\?\*"), Is.EqualTo(1));
    }

    [Test]
    public async Task DraftsPage_FullLoad_FetchesEachStatusOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync("/editor", () => Expect(Row(DraftTitle)).ToBeVisibleAsync());

        // The queries are redacted in the log; the page makes one read per status.
        Assert.That(Requests(@"/api/posts\?\*"), Is.EqualTo(2), "drafts and scheduled, once each");
    }

    [Test]
    public async Task DraftReviewPage_FullLoad_FetchesThePostItsRelatedPostsAndItsFeedbackOnce()
    {
        await EnsureFixtureAsync();
        await LoadAsync($"/editor/posts/{_post.Slug}", () => Expect(Page.GetByText(PostTitle).First).ToBeVisibleAsync());

        Assert.That(Requests($@"/api/posts/{_post.Slug}$"), Is.EqualTo(1));
        Assert.That(Requests($@"/api/posts/{_post.Slug}/related\?\*"), Is.EqualTo(1));
        Assert.That(Requests($@"/api/posts/{_post.Id}/feedback$"), Is.EqualTo(1));
    }

    private static string PostTitle => $"Apicount Post {RunId}";
    private static string PageTitle => $"Apicount Page {RunId}";
    private static string DraftTitle => $"Apicount Draft {RunId}";

    private ILocator Row(string title) =>
        Page.Locator(".posts-grid__row").Filter(new LocatorFilterOptions { HasText = title });

    /// <summary>Signs in, opens <paramref name="path"/> as a full page load, and waits until the
    /// circuit is interactive and <paramref name="loaded"/> shows the data: a second (circuit)
    /// fetch starts before the first interactive render, so it is in the log by then.</summary>
    private async Task LoadAsync(string path, Func<Task> loaded)
    {
        await LoginAsync("admin@example.com", "password");
        _baseline = ReadLog();
        await Page.GotoAsync($"{BaseUrl}{path}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await loaded();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private string[] _baseline = [];

    private string[] ReadLog()
    {
        var log = $"/tmp/blog-e2e-{(new Uri(BaseUrl).Port - 5002) / 10 + 1}-web.log";
        Assert.That(File.Exists(log), Is.True, $"The Web log {log} is how the API requests are counted.");
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }

    /// <summary>How many API requests since the page was opened match the path-and-query
    /// pattern (the Web app logs one "Start processing" line per request).</summary>
    private int Requests(string pathAndQueryPattern)
    {
        var pattern = new Regex($@"Start processing HTTP request GET {Regex.Escape(ApiUrl)}{pathAndQueryPattern}\s*$");
        return ReadLog().Skip(_baseline.Length).Count(line => pattern.IsMatch(line));
    }

    private async Task EnsureFixtureAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            _post = await PublishAsync(PostTitle, "A post whose admin pages are loaded once each.");
            await PublishAsync(PageTitle, "A page, so the pages list has a row.", kind: PostKind.Page);
            using var client = MockIdpApi.CreateClient();
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
                new Croft.Shared.Contracts.CreatePostRequest(DraftTitle, "A draft, so the drafts list has a row."), token);
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
