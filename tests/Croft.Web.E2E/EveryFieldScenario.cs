using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// The owner's editor scenario shared by the draft-backup and session-loss tests: a new post
/// with every field filled (title, subtitle, description, Markdown, a category, two tags, a banner
/// chosen from the media library, a publish date), then checks of what the editor shows and what
/// the saved post holds. The publish time is not set: MudBlazor's clock is only reachable through
/// W-073's pointer workaround (SchedulingTests); the unit round trip covers the time.
/// </summary>
internal sealed class EveryFieldScenario(IPage page, string baseUrl, string apiUrl, string runId)
{
    private readonly string _categoryName = $"Scenario Cat {runId}";
    private readonly string _imageName = $"{runId}-banner.png";
    private readonly string _tagOne = $"scenario-one-{runId}";
    private readonly string _tagTwo = $"scenario-two-{runId}";
    private string? _bannerSrc;
    private string _dateValue = string.Empty;

    public string Title { get; } = $"Every field {runId}";

    public string Subtitle { get; } = $"A subtitle {runId}";

    public string Description { get; } = $"A search and link-preview description {runId}.";

    public string Body { get; } = $"Every field body {runId} with **markdown**.";

    private const string BannerAlt = "A banner from the library";

    private IPage Page => page;

    private ILocator Field(string label) => Page.GetByLabel(label, new PageGetByLabelOptions { Exact = true });

    private ILocator Markdown => Field("Markdown");

    /// <summary>The category through the API and the image through the media library page (signed in).</summary>
    public async Task SeedAsync()
    {
        using (var client = MockIdpApi.CreateClient())
        {
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            (await MockIdpApi.PostJsonAsync(client, apiUrl, "/api/categories", JsonSerializer.Serialize(_categoryName), token)).Dispose();
        }

        var imagePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, _imageName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "test-image.png"), imagePath, overwrite: true);
        await Page.GotoAsync($"{baseUrl}/admin/media");
        await WebTest.WaitForInteractiveAsync(Page);
        await Page.GetByLabel("Upload images").SetInputFilesAsync(imagePath);
        var tile = Page.Locator(".media-tile", new PageLocatorOptions { HasText = _imageName });
        await Assertions.Expect(tile).ToBeVisibleAsync();
        await tile.GetByLabel("Alt text").FillAsync(BannerAlt);
        await tile.GetByLabel("Alt text").BlurAsync();
        _bannerSrc = await tile.Locator("img").GetAttributeAsync("src");
        Assert.That(_bannerSrc, Does.Match("^/api/media/[0-9a-fA-F-]{36}$"));
    }

    /// <summary>Opens a new post and fills every field the editor has.</summary>
    public async Task FillNewPostAsync()
    {
        await Page.GotoAsync($"{baseUrl}/admin/posts/new");
        await WebTest.WaitForInteractiveAsync(Page);
        await Field("Title").FillAsync(Title);
        await Field("Subtitle").FillAsync(Subtitle);
        await Page.GetByLabel("Search & link-preview description").FillAsync(Description);
        await Markdown.FillAsync(Body);
        await Markdown.BlurAsync();
        foreach (var name in new[] { _tagOne, _tagTwo })
        {
            await Page.GetByLabel("Tags").FillAsync(name);
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add tag" }).ClickAsync();
        }

        var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
        await category.ClickAsync();
        await category.FillAsync(_categoryName);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = _categoryName }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Choose from library" }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await dialog.Locator($".media-tile:has-text('{_imageName}') .media-tile__pick").ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        await Assertions.Expect(Page.Locator(".banner-picker__thumb")).ToHaveAttributeAsync("src", _bannerSrc!);
        await Page.GetByLabel("Publish date (UTC)").ClickAsync();
        var picker = Page.Locator(".mud-picker-open, .mud-popover-open").First;
        await picker.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await picker.Locator(".mud-picker-calendar-day:has-text('15')").Last.ClickAsync();
        await Page.Locator(".mud-picker-open, .mud-popover-open").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
        _dateValue = await Page.GetByLabel("Publish date (UTC)").InputValueAsync();
        Assert.That(_dateValue, Is.Not.Empty, "the day click must set the date");
    }

    /// <summary>The backup under "croft-draft:new" holds every field (not just the texts).</summary>
    public async Task WaitForBackupOfEveryFieldAsync(string? body = null)
    {
        await Page.WaitForFunctionAsync(
            """
            args => { const raw = localStorage.getItem('croft-draft:new'); if (!raw) return false;
                const r = JSON.parse(raw);
                return r.version === 2 && r.title === args.title && r.subtitle === args.subtitle
                    && r.excerpt === args.description && r.body === args.body
                    && r.category && r.category.name === args.category
                    && JSON.stringify(r.tags) === JSON.stringify(args.tags)
                    && r.banner && r.banner.url === args.banner && !!r.publishedDate; }
            """,
            new
            {
                title = Title, subtitle = Subtitle, description = Description, body = body ?? Body,
                category = _categoryName, tags = new[] { _tagOne, _tagTwo }, banner = _bannerSrc,
            },
            new PageWaitForFunctionOptions { Timeout = 30000 });
    }

    /// <summary>Every field's value is shown, the banner thumbnail has the library image's src and loads.</summary>
    public async Task AssertEveryFieldShownAsync(string? body = null)
    {
        await Assertions.Expect(Field("Title")).ToHaveValueAsync(Title);
        await Assertions.Expect(Field("Subtitle")).ToHaveValueAsync(Subtitle);
        await Assertions.Expect(Page.GetByLabel("Search & link-preview description")).ToHaveValueAsync(Description);
        await Assertions.Expect(Markdown).ToHaveValueAsync(body ?? Body);
        await Assertions.Expect(Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" })).ToHaveValueAsync(_categoryName);
        await Assertions.Expect(Page.Locator(".post-form__tag")).ToHaveCountAsync(2);
        await Assertions.Expect(Page.Locator(".post-form__tag").Nth(0)).ToContainTextAsync(_tagOne);
        await Assertions.Expect(Page.Locator(".post-form__tag").Nth(1)).ToContainTextAsync(_tagTwo);
        await Assertions.Expect(Page.Locator(".banner-picker__thumb")).ToHaveAttributeAsync("src", _bannerSrc!);
        var thumb = Page.Locator(".banner-picker__thumb");
        Assert.That(await thumb.EvaluateAsync<bool>("el => el.complete && el.naturalWidth > 0"), Is.True,
            "the banner thumbnail must actually have loaded");
        await Assertions.Expect(Page.GetByLabel("Publish date (UTC)")).ToHaveValueAsync(_dateValue);
    }

    /// <summary>The saved post, read from the API as the admin, holds every value.</summary>
    public async Task AssertSavedAsync(Guid id, string? body = null)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/api/posts/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = saved.RootElement;
        Assert.That(root.GetProperty("title").GetString(), Is.EqualTo(Title));
        Assert.That(root.GetProperty("subtitle").GetString(), Is.EqualTo(Subtitle));
        Assert.That(root.GetProperty("excerpt").GetString(), Is.EqualTo(Description));
        Assert.That(root.GetProperty("markdownContent").GetString(), Is.EqualTo(body ?? Body));
        Assert.That(root.GetProperty("category").GetProperty("name").GetString(), Is.EqualTo(_categoryName));
        Assert.That(root.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()),
            Is.EquivalentTo((string[])[_tagOne, _tagTwo]));
        Assert.That(root.GetProperty("bannerImage").GetProperty("url").GetString(), Is.EqualTo(_bannerSrc));
    }
}
