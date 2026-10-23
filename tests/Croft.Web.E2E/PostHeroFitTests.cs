using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// The post hero must fit long titles. With a fixed height and absolutely positioned content a
/// very long title runs off the hero's top at 2560px and over the banner's edge on a phone: the
/// hero never grows, so the text leaves its box. Outcomes asserted at 390px and 2560px, measured
/// in the browser (rects and scroll boxes): the category chip, title, subtitle and meta line all
/// lie inside the hero's box — the hero grows to fit its content, its height a minimum — with no
/// text hidden or clamped (every laid-out line fits its element's box), and a 60-character
/// unbroken word wraps onto more lines rather than overflowing its wrap container or the page
/// horizontally. Break-it-once: restoring the fixed height (360px / 240px) leaves the title's
/// box outside the hero and fails the containment assertions.
///
/// Two fixture posts, both run-keyed unique and published through the admin UI once per run
/// (the suite's rule):
/// - the main one carries a title of about 200 characters containing a 60-character unbroken
///   word, a subtitle carrying a second one, a category (the chip) and a short body — every
///   element the hero can show;
/// - the second (additive) one carries the very-long shape whose captures showed the text off
///   the hero's top at 2560px: a 300-character title and a 300-character subtitle. The ~200-
///   character title fits the fixed 360px hero even un-grown at 2560px (four lines, 219px, in
///   the 342px a bottom-anchored hero leaves), so the growth at that width needs this longer
///   shape to be exercised at all.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class PostHeroFitTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Exactly 60 characters: the unbroken word that must wrap rather than overflow.</summary>
    private static readonly string UnbrokenWord = "floccinaucinihilipilificationfloccinaucinihilipilificationzz";

    /// <summary>About 200 characters (204): the long-title shape the hero must fit.</summary>
    private static readonly string Title =
        $"Hero fit {RunId}: a post title of about two hundred characters carrying one very " +
        $"long unbroken word {UnbrokenWord} at its end, shown at every screen size";

    /// <summary>The subtitle wraps the same way, so it carries a second unbroken word.</summary>
    private static readonly string Subtitle =
        $"A subtitle carrying a second {UnbrokenWord} so both hero lines wrap rather than overflow.";

    private static readonly string CategoryName = $"herofit{RunId}";

    /// <summary>Exactly 300 characters: the very-long shape that leaves the fixed hero at 2560px.</summary>
    private static readonly string LongTitle = Padded($"Hero fit long {RunId} ");

    /// <summary>Exactly 300 characters: the very-long shape's subtitle.</summary>
    private static readonly string LongSubtitle = Padded($"Subtitle {RunId} ");

    private static bool _dataReady;
    private static string _slug = string.Empty;
    private static string _longSlug = string.Empty;

    [TestCase(390, 844)]
    [TestCase(2560, 1440)]
    public async Task HeroElements_LieInsideTheHeroAndNothingIsHidden(int width, int height)
    {
        await EnsureFixtureAsync();
        var g = await MeasureAsync(_slug, width, height);

        Assert.That(g.ContentChildren, Has.Count.EqualTo(4),
            "the fixture hero should show its four shapes: chip, title, subtitle and meta.");
        AssertInsideHero(g, width);
    }

    [TestCase(390, 844)]
    [TestCase(2560, 1440)]
    public async Task LongWords_WrapRatherThanOverflow(int width, int height)
    {
        await EnsureFixtureAsync();
        var g = await MeasureAsync(_slug, width, height);

        Assert.That(g.TitleWidth, Is.LessThanOrEqualTo(g.ContentWidth + 1),
            $"the title's box at {width}px is {g.TitleWidth:0.##}px wide inside the hero's {g.ContentWidth:0.##}px content box; " +
            $"the {UnbrokenWord.Length}-character unbroken word must wrap, not push the box wider.");
        Assert.That(g.SubtitleWidth, Is.LessThanOrEqualTo(g.ContentWidth + 1),
            $"the subtitle's box at {width}px is {g.SubtitleWidth:0.##}px wide inside the hero's {g.ContentWidth:0.##}px content box.");
        Assert.That(g.TitleScrollWidth, Is.LessThanOrEqualTo(g.TitleClientWidth + 1),
            $"the title's text at {width}px is {g.TitleScrollWidth:0.##}px wide inside a {g.TitleClientWidth:0.##}px box; " +
            "its long word must wrap within the box.");
        Assert.That(g.SubtitleScrollWidth, Is.LessThanOrEqualTo(g.SubtitleClientWidth + 1),
            $"the subtitle's text at {width}px is {g.SubtitleScrollWidth:0.##}px wide inside a {g.SubtitleClientWidth:0.##}px box.");
        Assert.That(g.DocumentScrollWidth, Is.LessThanOrEqualTo(g.ViewportWidth + 1),
            $"the page at {width}px holds {g.DocumentScrollWidth:0.##}px of content in a {g.ViewportWidth:0.##}px viewport; " +
            "nothing may overflow horizontally.");
    }

    /// <summary>The very-long shape (300-character title and subtitle) on the hero's box: its
    /// text is the one that ran off the hero's top at 2560px and past the banner's edge on a
    /// phone, so this is the case that shows the hero growing at both widths.</summary>
    [TestCase(390, 844)]
    [TestCase(2560, 1440)]
    public async Task VeryLongTitle_LiesInsideTheHero(int width, int height)
    {
        await EnsureFixtureAsync();
        var g = await MeasureAsync(_longSlug, width, height);

        Assert.That(g.ContentChildren, Has.Count.EqualTo(3),
            "the very-long fixture hero should show title, subtitle and meta.");
        AssertInsideHero(g, width);
    }

    /// <summary>Every content child (chip, title, subtitle, meta) lies inside the hero's box and
    /// every laid-out line fits its element's box — nothing hidden or clamped.</summary>
    private static void AssertInsideHero(HeroFitGeometry g, int width)
    {
        foreach (var child in g.ContentChildren)
        {
            Assert.That(child.Top, Is.GreaterThanOrEqualTo(g.Hero.Top - 1),
                $"the hero's {child.Name} at {width}px starts {g.Hero.Top - child.Top:0.##}px above the hero's top; " +
                "the hero must grow so every piece of its content lies inside its box.");
            Assert.That(child.Bottom, Is.LessThanOrEqualTo(g.Hero.Bottom + 1),
                $"the hero's {child.Name} at {width}px ends {child.Bottom - g.Hero.Bottom:0.##}px below the hero's bottom.");
            Assert.That(child.Left, Is.GreaterThanOrEqualTo(g.Hero.Left - 1),
                $"the hero's {child.Name} at {width}px starts {g.Hero.Left - child.Left:0.##}px left of the hero's left edge.");
            Assert.That(child.Right, Is.LessThanOrEqualTo(g.Hero.Right + 1),
                $"the hero's {child.Name} at {width}px ends {child.Right - g.Hero.Right:0.##}px past the hero's right edge.");
        }

        Assert.That(g.TitleLines * g.TitleLineHeight, Is.LessThanOrEqualTo(g.TitleClientHeight + 1),
            $"the title lays out {g.TitleLines} lines of {g.TitleLineHeight:0.##}px in a {g.TitleClientHeight:0.##}px box; " +
            "no title line may be hidden or clamped.");
        Assert.That(g.SubtitleLines * g.SubtitleLineHeight, Is.LessThanOrEqualTo(g.SubtitleClientHeight + 1),
            $"the subtitle lays out {g.SubtitleLines} lines of {g.SubtitleLineHeight:0.##}px in a {g.SubtitleClientHeight:0.##}px box; " +
            "no subtitle line may be hidden or clamped.");
    }

    /// <summary>One element's box in the page.</summary>
    private sealed record Box(string Name, double Top, double Right, double Bottom, double Left);

    /// <summary>One page's hero geometry: the hero's box, the hero content's wrap width, every
    /// content child's box (chip, title, subtitle, meta), the title's and subtitle's box widths,
    /// laid-out line counts and line heights (clamping) and text widths (overflow), and the
    /// page's horizontal extent.</summary>
    private sealed record HeroFitGeometry(
        Box Hero,
        double ContentWidth,
        IReadOnlyList<Box> ContentChildren,
        double TitleWidth,
        double SubtitleWidth,
        int TitleLines,
        double TitleLineHeight,
        double TitleClientHeight,
        int SubtitleLines,
        double SubtitleLineHeight,
        double SubtitleClientHeight,
        double TitleScrollWidth,
        double TitleClientWidth,
        double SubtitleScrollWidth,
        double SubtitleClientWidth,
        double DocumentScrollWidth,
        double ViewportWidth);

    /// <summary>Measures one post page at one width. Callers await EnsureFixtureAsync() first:
    /// the slug argument is evaluated before this method runs, so reading the fixture's static
    /// slug from a caller that has not run the fixture would navigate to /post/.</summary>
    private async Task<HeroFitGeometry> MeasureAsync(string slug, int width, int height)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-hero__title")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-hero__subtitle")).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-meta")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const hero = document.querySelector('.post-hero');
                const content = document.querySelector('.post-hero__content');
                const title = document.querySelector('.post-hero__title');
                const sub = document.querySelector('.post-hero__subtitle');
                const rect = el => {
                    const r = el.getBoundingClientRect();
                    return { top: r.top, right: r.right, bottom: r.bottom, left: r.left, width: r.width };
                };
                const lines = el => {
                    const range = document.createRange();
                    range.selectNodeContents(el);
                    return range.getClientRects().length;
                };
                const children = [...content.children].map(el => ({
                    name: el.tagName.toLowerCase() + (el.classList.length ? '.' + el.classList[0] : ''),
                    ...rect(el)
                }));

                return JSON.stringify({
                    hero: rect(hero),
                    contentWidth: content.getBoundingClientRect().width,
                    children,
                    titleWidth: title.getBoundingClientRect().width,
                    subWidth: sub.getBoundingClientRect().width,
                    titleLines: lines(title),
                    titleLineHeight: parseFloat(getComputedStyle(title).lineHeight),
                    titleClientH: title.clientHeight,
                    subLines: lines(sub),
                    subLineHeight: parseFloat(getComputedStyle(sub).lineHeight),
                    subClientH: sub.clientHeight,
                    titleScrollW: title.scrollWidth,
                    titleClientW: title.clientWidth,
                    subScrollW: sub.scrollWidth,
                    subClientW: sub.clientWidth,
                    docScrollW: document.documentElement.scrollWidth,
                    viewportW: window.innerWidth
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;

        static Box ReadBox(JsonElement e) => new(
            e.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text ? text : "(hero)",
            e.GetProperty("top").GetDouble(),
            e.GetProperty("right").GetDouble(),
            e.GetProperty("bottom").GetDouble(),
            e.GetProperty("left").GetDouble());

        return new HeroFitGeometry(
            ReadBox(r.GetProperty("hero").Clone()),
            r.GetProperty("contentWidth").GetDouble(),
            [.. r.GetProperty("children").EnumerateArray().Select(ReadBox)],
            r.GetProperty("titleWidth").GetDouble(),
            r.GetProperty("subWidth").GetDouble(),
            r.GetProperty("titleLines").GetInt32(),
            r.GetProperty("titleLineHeight").GetDouble(),
            r.GetProperty("titleClientH").GetDouble(),
            r.GetProperty("subLines").GetInt32(),
            r.GetProperty("subLineHeight").GetDouble(),
            r.GetProperty("subClientH").GetDouble(),
            r.GetProperty("titleScrollW").GetDouble(),
            r.GetProperty("titleClientW").GetDouble(),
            r.GetProperty("subScrollW").GetDouble(),
            r.GetProperty("subClientW").GetDouble(),
            r.GetProperty("docScrollW").GetDouble(),
            r.GetProperty("viewportW").GetDouble());
    }

    private static string Padded(string prefix)
    {
        var text = new System.Text.StringBuilder(prefix);
        while (text.Length < 300)
        {
            text.Append("long hero words ");
        }

        return text.ToString(0, 300);
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

            await LoginAsync("admin@example.com", "password");

            // The chip is one of the hero's four shapes, so the fixture needs a category to pick.
            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await Page.GetByLabel("New category").FillAsync(CategoryName);
            await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
                .Locator("button").First
                .ClickAsync();
            await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {CategoryName}" }))
                .ToBeVisibleAsync();

            await OpenNewPostEditorAsync();

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(Title);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Subtitle").FillAsync(Subtitle);
            await Page.GetByLabel("Subtitle").BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync($"Hero fit fixture {RunId}, a short body.");
            await Page.GetByLabel("Markdown").BlurAsync();

            // Select the category through the picker's search (PostPageTests' pattern).
            var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
            await category.ClickAsync();
            await category.FillAsync(CategoryName);
            await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = CategoryName }).ClickAsync();

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 30000 });
            _slug = await SlugForPostAsync(PostIdFromEditorUrl());

            // The very-long shape: title and subtitle of exactly 300 characters, no category.
            await OpenNewPostEditorAsync();
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(LongTitle);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Subtitle").FillAsync(LongSubtitle);
            await Page.GetByLabel("Subtitle").BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync($"Hero fit long fixture {RunId}, a short body.");
            await Page.GetByLabel("Markdown").BlurAsync();
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 30000 });
            _longSlug = await SlugForPostAsync(PostIdFromEditorUrl());

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
