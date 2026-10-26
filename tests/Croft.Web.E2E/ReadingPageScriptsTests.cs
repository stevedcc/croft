using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10c item 1: a reading page (static SSR) loads only the scripts it uses. Its HTML carries
/// no reference to the editor's helpers (<c>editor.js</c>, <c>draft-backup.js</c>), to the
/// circuit's reconnect modal (<c>ReconnectModal</c>, <c>components-reconnect-modal</c>) or to
/// MudBlazor, and the browser requests none of them. The editor reached from a reading page by
/// clicking the sidebar's "New post" is a fresh interactive document that does load both editor
/// scripts, and its draft backup works. The outcomes asserted: the HTML received, the requests
/// the browser made, and the backup actually written to localStorage.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReadingPageScriptsTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string[] StaffOnly =
        ["js/editor.", "js/draft-backup.", "ReconnectModal", "components-reconnect-modal", "MudBlazor"];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;

    [Test]
    public async Task Home_LoadsNoStaffScripts() => await AssertOnlyReadingScriptsAsync("/");

    [Test]
    public async Task Post_LoadsNoStaffScripts()
    {
        await EnsureFixturesAsync();
        await AssertOnlyReadingScriptsAsync($"/post/{_postSlug}");
    }

    [Test]
    public async Task Category_LoadsNoStaffScripts()
    {
        await EnsureFixturesAsync();
        await AssertOnlyReadingScriptsAsync($"/category/{_categorySlug}");
    }

    [Test]
    public async Task Tag_LoadsNoStaffScripts()
    {
        await EnsureFixturesAsync();
        await AssertOnlyReadingScriptsAsync($"/tag/{_tagSlug}");
    }

    [Test]
    public async Task Categories_LoadsNoStaffScripts()
    {
        await EnsureFixturesAsync();
        await AssertOnlyReadingScriptsAsync("/categories");
    }

    [Test]
    public async Task StaticPage_LoadsNoStaffScripts()
    {
        await EnsureFixturesAsync();
        await AssertOnlyReadingScriptsAsync($"/{_pageSlug}");
    }

    [Test]
    public async Task Search_LoadsNoStaffScripts()
    {
        await EnsureFixturesAsync();
        await AssertOnlyReadingScriptsAsync($"/search?q={Uri.EscapeDataString(RunId)}");
    }

    [TestCase("/post/missing-post", 404)]
    [TestCase("/category/missing-category", 404)]
    public async Task MissingItem_StaticNotFoundDocument_LoadsNoStaffScripts(string path, int status) =>
        await AssertOnlyReadingScriptsAsync($"{path}-{RunId}", status);

    [Test]
    public async Task NewPost_ReachedFromAReadingPage_LoadsTheEditorScripts_AndTheDraftBackupWorks()
    {
        await LoginAsync("admin@example.com", "password");
        var requests = EventLog.RequestUrls(Page);

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.That(requests.Snapshot().Where(IsStaffScript), Is.Empty, "the reading page we start from loads no staff script.");

        await Page.Locator("#sidebar-nav a[href='/admin/posts/new']").ClickAsync();
        await Page.WaitForURLAsync(url => url.EndsWith("/admin/posts/new"));
        await WaitForInteractiveAsync(Page);

        Assert.That(requests.Snapshot(), Has.Some.Matches<string>(url => url!.Contains("/js/editor.")),
            "the editor document must load editor.js.");
        Assert.That(requests.Snapshot(), Has.Some.Matches<string>(url => url!.Contains("/js/draft-backup.")),
            "the editor document must load draft-backup.js.");
        Assert.That(await Page.EvaluateAsync<string>("() => typeof window.blogDraft?.flush + '/' + typeof window.blogEditor?.insertAtCursor"),
            Is.EqualTo("function/function"), "both scripts ran and registered their helpers.");

        var typed = $"Typed body {RunId} — words that must not be lost.";
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(typed);
        await Page.WaitForFunctionAsync(
            "(typed) => (localStorage.getItem('croft-draft:new') ?? '').includes(typed)", typed);
    }

    private async Task AssertOnlyReadingScriptsAsync(string path, int status = 200)
    {
        var log = EventLog.RequestUrls(Page);

        var response = await Page.GotoAsync($"{BaseUrl}{path}");
        Assert.That(response!.Status, Is.EqualTo(status), $"{path} must answer {status}.");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        log.Stop();
        var requests = log.Snapshot();

        var html = await response.TextAsync();
        foreach (var staffOnly in StaffOnly)
        {
            Assert.That(html, Does.Not.Contain(staffOnly), $"{path}: the HTML refers to {staffOnly}, which only interactive documents use.");
        }

        Assert.That(requests.Where(IsStaffScript), Is.Empty,
            $"{path} requested staff-only scripts: {string.Join(", ", requests.Where(IsStaffScript))}.");
        Assert.That(requests, Has.Some.Matches<string>(url => url!.Contains("/js/static-glue")),
            "the page's own script did load (the log is live).");
    }

    private static bool IsStaffScript(string url) =>
        StaffOnly.Any(marker => url.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private async Task EnsureFixturesAsync()
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

            var category = await CreateCategoryAsync($"Script Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"script{RunId}";
            var post = await PublishAsync($"Script Post {RunId}", BodyWithSections(RunId, 3), [_tagSlug], category.Id);
            _postSlug = post.Slug;
            var page = await PublishAsync($"Script About {RunId}", $"Pagemarker {RunId} waits.", kind: PostKind.Page);
            _pageSlug = page.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
