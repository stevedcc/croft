using System.Text.Json;
using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10b item 3: the home page's first page opens with the site masthead, whatever banner the
/// newest post has. The instance runs on branding.example (name "Field Notes"), whose words are
/// read from its branding.json so the test follows the file. Outcomes measured in the browser: the
/// masthead sits above the Featured card (whose post has a banner of its own) and shows the name
/// and description; it is 170-190px tall at 1440 and 110-130px at 390; white text reads at 4.5:1
/// or better on what is really painted behind it, in both themes; page 2 and a category page have
/// none; the page has one h1, the site name.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class MastheadTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static bool _dataReady;
    private static string _categorySlug = string.Empty;

    [Test]
    public async Task Masthead_SitsAboveTheFeaturedCard_ShowingTheSitesNameAndDescription()
    {
        var (name, description) = SiteWords();
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(1440, 900, "light");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);

        var featured = page.Locator(".post-card--featured");
        await Expect(featured.Locator("img.banner-or-fallback__img")).ToBeVisibleAsync();
        var masthead = page.Locator(".masthead");
        await Expect(masthead).ToHaveCountAsync(1);
        await Expect(masthead.Locator(".masthead__name")).ToHaveTextAsync(name);
        await Expect(masthead.Locator(".masthead__description")).ToHaveTextAsync(description);

        var mastheadBox = (await masthead.BoundingBoxAsync())!;
        var featuredBox = (await featured.BoundingBoxAsync())!;
        Assert.That(mastheadBox.Y + mastheadBox.Height, Is.LessThanOrEqualTo(featuredBox.Y + 1),
            "the masthead ends above the Featured card");
        Assert.That(mastheadBox.Width, Is.EqualTo(featuredBox.Width).Within(1), "it is as wide as the card column");
    }

    [TestCase(1440, 900, 170, 190)]
    [TestCase(390, 844, 110, 130)]
    public async Task Masthead_HasTheBriefsHeight(int width, int height, int min, int max)
    {
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(width, height, "light");
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await Expect(page.Locator(".masthead")).ToHaveCountAsync(1);

        var box = (await page.Locator(".masthead").BoundingBoxAsync())!;
        Assert.That(box.Height, Is.InRange(min, max), $"the masthead is {box.Height:0.#}px tall at {width}px");
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task Masthead_TextReadsAtFourAndAHalfToOne(int width, int height, string theme)
    {
        await EnsureFixtureAsync();
        await using var context = await NewContextAsync(width, height, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        foreach (var selector in new[] { ".masthead__name", ".masthead__description" })
        {
            var element = page.Locator(selector);
            await Expect(element).ToBeVisibleAsync();
            var colour = Parse(await element.EvaluateAsync<string>("el => getComputedStyle(el).color"));

            // Paint the text away (CSSOM, so the page's CSP is untouched) and photograph what is
            // really behind it: the tiled banner, the wash and the gradient, composited by the
            // browser. The worst pixel is the lightest one; the text colour is composited over each.
            await element.EvaluateAsync("el => { el.style.color = 'transparent'; }");
            var pixels = PngPixelDecoder.Decode(await element.ScreenshotAsync(
                new LocatorScreenshotOptions { Type = ScreenshotType.Png }));

            var worst = double.MaxValue;
            for (var i = 0; i < pixels.Rgba.Length; i += 4)
            {
                var behind = new Rgba(pixels.Rgba[i], pixels.Rgba[i + 1], pixels.Rgba[i + 2], 1);
                worst = Math.Min(worst, Ratio(Composite(colour, behind), behind));
            }

            Assert.That(worst, Is.GreaterThanOrEqualTo(4.5),
                $"{selector} ({theme}, {width}px): the text contrasts {worst:0.00}:1 with the lightest pixel behind it");
        }
    }

    [Test]
    public async Task HomePage_HasOneH1_TheSiteName()
    {
        var (name, _) = SiteWords();
        await EnsureFixtureAsync();
        await Page.GotoAsync(BaseUrl);
        await Expect(Page.Locator(".masthead")).ToHaveCountAsync(1);

        await Expect(Page.Locator("h1")).ToHaveCountAsync(1);
        await Expect(Page.Locator("h1")).ToHaveTextAsync(name);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts", Level = 2 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Page2_HasNoMasthead()
    {
        await EnsureFixtureAsync();
        await Page.GotoAsync($"{BaseUrl}/?page=2");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts", Level = 1 })).ToBeVisibleAsync();
        // The page has posts (the fixture published eleven of its own): "no masthead" is then a
        // statement about a real second page, not about an empty one.
        await Expect(Page.Locator(".post-card").First).ToBeVisibleAsync();
        await Expect(Page.Locator(".masthead")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task CategoryPage_HasNoMasthead()
    {
        await EnsureFixtureAsync();
        await Page.GotoAsync($"{BaseUrl}/category/{_categorySlug}");

        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".masthead")).ToHaveCountAsync(0);
    }

    private async Task<IBrowserContext> NewContextAsync(int width, int height, string theme)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = width, Height = height },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        return context;
    }

    /// <summary>The words branding.example configures (the instance's site).</summary>
    private static (string Name, string Description) SiteWords()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.That(dir, Is.Not.Null, "the tests run from inside the repository");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "branding.example", "branding.json")));
        var root = doc.RootElement;
        var description = root.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } text
            ? text
            : root.GetProperty("tagline").GetString()!;
        return (root.GetProperty("name").GetString()!, description);
    }

    /// <summary>The newest post carries a banner of its own (so the page shows no tartan on the
    /// Featured card), and a category holds one post for the category-page case.</summary>
    private async Task EnsureFixtureAsync()
    {
        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            // Eleven posts of this run's own: page 1 holds ten, so page 2 has a post whatever other
            // fixtures published (Page2_HasNoMasthead is vacuous on a page with no posts).
            for (var i = 1; i <= 11; i++)
            {
                await PublishAsync($"Masthead filler {RunId} {i}", BodyWithSections(RunId, 1));
            }

            var category = await CreateCategoryAsync($"Masthead {RunId}");
            _categorySlug = category.Slug;
            await PublishAsync($"Masthead category post {RunId}", BodyWithSections(RunId, 1), categoryId: category.Id);
            var banner = await UploadBannerAsync($"masthead-{RunId}.png");
            await PublishAsync($"Masthead newest {RunId}", BodyWithSections(RunId, 1), bannerImageId: banner);
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
