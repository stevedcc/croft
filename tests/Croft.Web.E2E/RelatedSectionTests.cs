using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07d item 3: "Related" leaves the rail and becomes a card section after the post body, in
/// the text column (same width as the body text). Both post surfaces are covered: the public post
/// page and the draft review page.
///
/// Outcomes are measured geometry (rule 7), at the brief's tolerances: at 1440×900 the section's
/// top sits below the body's last paragraph, its left/right edges equal the body column's ±1px,
/// and the three cards share one row with equal widths ±1px; at 390×844 the cards stack in one
/// column and — the narrow order — after the text comes Related (then Feedback on the review
/// page), with the tags living in the contents panel above the text (these fixtures have two
/// headings, so the panel carries them at rail-less widths); every card links to its post and
/// following one loads the post.
/// Break-it-once (recorded in the commit): putting Related back in the rail fails the
/// column-edges test; removing the ≥600px three-column rule fails the one-row test; dropping
/// the narrow order fails the Related-after-the-text test.
///
/// The "no related posts → no section, no heading" rule is NOT here: the API back-fills
/// zero-scoring posts (PostService.RelatedAsync), so the empty state needs an empty database and
/// cannot be created on the suite's shared instances — it is pinned as page-level bUnit tests
/// instead (tests/Croft.Web.Tests/Components/RelatedSectionTests.cs, controller ruling).
///
/// Fixtures (the suite's rule): each test's data is created through the admin UI, run-keyed and
/// shared by the class once per run — a category and a tag both run-keyed, the main post, three
/// related posts sharing that tag and category (score 3 each, so they outrank every other
/// candidate and the section holds exactly those three) and one draft for the review page.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class RelatedSectionTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string MainTitle = $"R7D3 Main {RunId}";
    private static readonly string DraftTitle = $"R7D3 Draft {RunId}";
    private static readonly string CategoryName = $"R7D3 Cat {RunId}";
    private static readonly string TagName = $"r7d3tag{RunId}";

    private static readonly string MainBody =
        $"Fixture intro {RunId} opens the post with a full sentence of prose so the body reads " +
        "naturally before its sections.\n\n" +
        "## Section one\n\n" +
        $"First section paragraph {RunId} carries enough words to make the body real.\n\n" +
        "## Section two\n\n" +
        $"Closing paragraph {RunId} ends the post body and anchors the geometry below.";

    private static bool _dataReady;
    private static string _mainSlug = string.Empty;
    private static readonly List<(string Title, string Slug)> Related = [];

    [Test]
    public async Task PostPage_WithThreeRelated_At1440_SectionSitsBelowBodyInTextColumn_AndCardsShareOneRow()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");
        await Expect(Page.Locator(".related-posts")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const body = document.querySelector('.markdown-body').getBoundingClientRect();
                const paras = [...document.querySelectorAll('.markdown-body > p')];
                const last = paras[paras.length - 1].getBoundingClientRect();
                const section = document.querySelector('.related-posts').getBoundingClientRect();
                const cards = [...document.querySelectorAll('.related-posts__item')].map(el => {
                    const r = el.getBoundingClientRect();
                    return { top: r.top, bottom: r.bottom, left: r.left, width: r.width };
                });
                return JSON.stringify({
                    bodyLeft: body.left, bodyRight: body.right,
                    lastParagraphBottom: last.bottom,
                    sectionTop: section.top, sectionLeft: section.left, sectionRight: section.right,
                    cards
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var bodyLeft = r.GetProperty("bodyLeft").GetDouble();
        var bodyRight = r.GetProperty("bodyRight").GetDouble();
        var lastParagraphBottom = r.GetProperty("lastParagraphBottom").GetDouble();
        var sectionTop = r.GetProperty("sectionTop").GetDouble();
        var sectionLeft = r.GetProperty("sectionLeft").GetDouble();
        var sectionRight = r.GetProperty("sectionRight").GetDouble();
        var cards = r.GetProperty("cards").EnumerateArray().ToList();

        Assert.That(cards, Has.Count.EqualTo(3), "the fixture's three related posts must render as three cards.");
        Assert.That(Math.Abs(sectionLeft - bodyLeft), Is.LessThanOrEqualTo(1),
            $"the section's left edge must equal the body column's (±1px): section {sectionLeft:0.#}px, body {bodyLeft:0.#}px.");
        Assert.That(Math.Abs(sectionRight - bodyRight), Is.LessThanOrEqualTo(1),
            $"the section's right edge must equal the body column's (±1px): section {sectionRight:0.#}px, body {bodyRight:0.#}px.");
        Assert.That(sectionTop, Is.GreaterThanOrEqualTo(lastParagraphBottom - 1),
            $"the Related section's top must sit below the body's last paragraph: section top {sectionTop:0.#}px, " +
            $"last paragraph bottom {lastParagraphBottom:0.#}px.");

        // "share one row": same top and bottom band; "equal widths": ±1px.
        var tops = cards.Select(c => c.GetProperty("top").GetDouble()).ToList();
        var bottoms = cards.Select(c => c.GetProperty("bottom").GetDouble()).ToList();
        var widths = cards.Select(c => c.GetProperty("width").GetDouble()).ToList();
        Assert.That(tops.Max() - tops.Min(), Is.LessThanOrEqualTo(1),
            $"the three cards must share one row (equal tops ±1px): {string.Join(", ", tops.Select(t => $"{t:0.#}"))}.");
        Assert.That(bottoms.Max() - bottoms.Min(), Is.LessThanOrEqualTo(1),
            $"the three cards must share one row (equal bottoms ±1px): {string.Join(", ", bottoms.Select(t => $"{t:0.#}"))}.");
        Assert.That(widths.Max() - widths.Min(), Is.LessThanOrEqualTo(1),
            $"the three cards must have equal widths ±1px: {string.Join(", ", widths.Select(t => $"{t:0.#}"))}.");
    }

    [Test]
    public async Task PostPage_At390_CardsStackInOneColumn_AndTagsLiveInThePanelAboveTheText()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");
        await Expect(Page.Locator(".related-posts")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const body = document.querySelector('.markdown-body');
                const paras = [...body.querySelectorAll(':scope > p')];
                const last = paras[paras.length - 1].getBoundingClientRect();
                const tags = document.querySelector('.toc-inline .tag-pills').getBoundingClientRect();
                const bodyTop = body.getBoundingClientRect().top;
                const section = document.querySelector('.related-posts').getBoundingClientRect();
                const cards = [...document.querySelectorAll('.related-posts__item')].map(el => {
                    const r = el.getBoundingClientRect();
                    return { top: r.top, bottom: r.bottom, left: r.left, width: r.width };
                });
                return JSON.stringify({
                    lastParagraphBottom: last.bottom,
                    tagsTop: tags.top, tagsBottom: tags.bottom,
                    bodyTop,
                    sectionTop: section.top,
                    cards
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var lastParagraphBottom = r.GetProperty("lastParagraphBottom").GetDouble();
        var tagsTop = r.GetProperty("tagsTop").GetDouble();
        var tagsBottom = r.GetProperty("tagsBottom").GetDouble();
        var bodyTop = r.GetProperty("bodyTop").GetDouble();
        var sectionTop = r.GetProperty("sectionTop").GetDouble();
        var cards = r.GetProperty("cards").EnumerateArray().ToList();

        Assert.That(cards, Has.Count.EqualTo(3), "the fixture's three related posts must render as three cards.");

        // One column: equal left edges and widths, each card below the previous one.
        var lefts = cards.Select(c => c.GetProperty("left").GetDouble()).ToList();
        var widths = cards.Select(c => c.GetProperty("width").GetDouble()).ToList();
        Assert.That(lefts.Max() - lefts.Min(), Is.LessThanOrEqualTo(1),
            $"at 390 the cards must stack in one column (equal left edges ±1px): {string.Join(", ", lefts.Select(t => $"{t:0.#}"))}.");
        Assert.That(widths.Max() - widths.Min(), Is.LessThanOrEqualTo(1),
            $"at 390 the cards must stack in one column (equal widths ±1px): {string.Join(", ", widths.Select(t => $"{t:0.#}"))}.");
        for (var i = 1; i < cards.Count; i++)
        {
            var above = cards[i - 1];
            var below = cards[i];
            Assert.That(below.GetProperty("top").GetDouble(), Is.GreaterThanOrEqualTo(above.GetProperty("bottom").GetDouble() - 1),
                $"at 390 the cards must stack in one column: card {i + 1} starts at {below.GetProperty("top").GetDouble():0.#}px, " +
                $"above card {i}'s bottom {above.GetProperty("bottom").GetDouble():0.#}px.");
        }

        // The narrow order: the tags live in the contents panel above the text (posts with a
        // panel carry their tags there at rail-less widths) and after the text comes Related.
        // Migrated when that rule moved the tags: the old assertions pinned "Tags after the
        // text, then Related" — disclosed in the item's commit.
        Assert.That(tagsBottom, Is.LessThanOrEqualTo(bodyTop + 1),
            $"at 390 the tags must live in the contents panel above the text: tags {tagsTop:0.#}..{tagsBottom:0.#}px, text top {bodyTop:0.#}px.");
        Assert.That(sectionTop, Is.GreaterThanOrEqualTo(lastParagraphBottom - 1),
            $"at 390 the Related section must come after the text: related top {sectionTop:0.#}px, last paragraph bottom {lastParagraphBottom:0.#}px.");
    }

    [Test]
    public async Task DraftReview_At390_AfterTheText_OrderIsRelatedThenFeedback_WithTagsInThePanel()
    {
        await EnsureFixturesAsync();
        await LogoutAsync();
        await LoginAsync("editor@example.com", "password");
        await Page.SetViewportSizeAsync(390, 844);
        await OpenReviewAsync(DraftTitle);

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const body = document.querySelector('.markdown-body');
                const paras = [...body.querySelectorAll(':scope > p')];
                const last = paras[paras.length - 1].getBoundingClientRect();
                const tags = document.querySelector('.toc-inline .tag-pills').getBoundingClientRect();
                const bodyTop = body.getBoundingClientRect().top;
                const section = document.querySelector('.related-posts').getBoundingClientRect();
                const feedback = document.querySelector('.feedback-panel').getBoundingClientRect();
                return JSON.stringify({
                    lastParagraphBottom: last.bottom,
                    tagsTop: tags.top, tagsBottom: tags.bottom,
                    bodyTop,
                    sectionTop: section.top, sectionBottom: section.bottom,
                    feedbackTop: feedback.top
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var lastParagraphBottom = r.GetProperty("lastParagraphBottom").GetDouble();
        var tagsTop = r.GetProperty("tagsTop").GetDouble();
        var tagsBottom = r.GetProperty("tagsBottom").GetDouble();
        var bodyTop = r.GetProperty("bodyTop").GetDouble();
        var sectionTop = r.GetProperty("sectionTop").GetDouble();
        var sectionBottom = r.GetProperty("sectionBottom").GetDouble();
        var feedbackTop = r.GetProperty("feedbackTop").GetDouble();

        // The narrow order on the review page: the tags live in the contents panel above the
        // text (the draft has two headings, so it has one), then after the text come Related and
        // the Feedback panel. Migrated when the tags moved into the panel — the old assertions
        // pinned "Tags, then Related, then Feedback" — disclosed in the item's commit.
        Assert.That(tagsBottom, Is.LessThanOrEqualTo(bodyTop + 1),
            $"at 390 the tags must live in the contents panel above the text: tags {tagsTop:0.#}..{tagsBottom:0.#}px, text top {bodyTop:0.#}px.");
        Assert.That(sectionTop, Is.GreaterThanOrEqualTo(lastParagraphBottom - 1),
            $"at 390 the Related section must come after the text: related top {sectionTop:0.#}px, last paragraph bottom {lastParagraphBottom:0.#}px.");
        Assert.That(feedbackTop, Is.GreaterThanOrEqualTo(sectionBottom - 1),
            $"at 390 the Feedback panel must come after the Related section: feedback top {feedbackTop:0.#}px, related bottom {sectionBottom:0.#}px.");
    }

    [Test]
    public async Task PostPage_RelatedCardsLinkToTheirPosts_FollowingOneLoadsThePost()
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");
        await Expect(Page.Locator(".related-posts")).ToBeVisibleAsync();

        // Every card links to its post: exactly the three fixtures, each under its own href.
        var links = Page.Locator(".related-posts__link");
        await Expect(links).ToHaveCountAsync(3);
        foreach (var (title, slug) in Related)
        {
            await Expect(Page.Locator($".related-posts__link[href='/post/{slug}']"))
                .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 5000 });
            await Expect(Page.Locator($".related-posts__link[href='/post/{slug}']"))
                .ToContainTextAsync(title);
        }

        // Follow one: the post loads (its own hero title renders under its URL).
        var (followedTitle, followedSlug) = Related[0];
        await Page.Locator($".related-posts__link[href='/post/{followedSlug}']").ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/post/{followedSlug}"),
            new PageWaitForURLOptions { Timeout = 10000 });
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = followedTitle, Level = 1 }))
            .ToBeVisibleAsync();
    }

    /// <summary>Brief 10b item 7: a related thumbnail showing the fallback tartan keeps its category
    /// icon (its only category cue) but in the top-left corner, 8px in from the top and the left.</summary>
    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task RelatedThumbnailWithoutABanner_HasItsIconInTheTopLeftCorner(int width, int height)
    {
        await EnsureFixturesAsync();
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{BaseUrl}/post/{_mainSlug}");

        var thumb = Page.Locator(".related-posts__thumb").First;
        await Expect(thumb.Locator(".banner-or-fallback__fallback")).ToBeVisibleAsync();
        var box = (await thumb.BoundingBoxAsync())!;
        var icon = (await thumb.Locator(".banner-or-fallback__icon svg").BoundingBoxAsync())!;

        Assert.That(icon.X - box.X, Is.EqualTo(8).Within(1), $"icon left inset at {width}px");
        Assert.That(icon.Y - box.Y, Is.EqualTo(8).Within(1), $"icon top inset at {width}px");
        Assert.That(icon.Width, Is.EqualTo(24).Within(1), "the icon keeps its 24px size");
    }

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

            await LoginAsync("admin@example.com", "password");
            await Page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(Page);
            await CreateCategoryAsync(CategoryName);

            _mainSlug = await PublishPostAsync(MainTitle, MainBody, TagName, CategoryName);
            for (var n = 1; n <= 3; n++)
            {
                var title = $"R7D3 Rel{n} {RunId}";
                var slug = await PublishPostAsync(title, $"Related fixture {n} {RunId}.", TagName, CategoryName);
                Related.Add((title, slug));
            }

            // The review page's draft shares the tag and category, so its related list is the
            // same three published posts and its Related section renders.
            await CreateDraftAsync(DraftTitle, MainBody, TagName, CategoryName);

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    private async Task CreateCategoryAsync(string name)
    {
        await WaitForInteractiveAsync(Page);
        await Page.GetByLabel("New category").FillAsync(name);
        await Page.Locator(".mud-input-control", new PageLocatorOptions { Has = Page.GetByLabel("New category") })
            .Locator("button").First
            .ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Icon for {name}" }))
            .ToBeVisibleAsync();
    }

    private async Task<string> PublishPostAsync(string title, string body, string tagName, string categoryName)
    {
        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        await AddTagsAsync(tagName);
        await SelectCategoryAsync(categoryName);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });

        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    private async Task CreateDraftAsync(string title, string body, string tagName, string categoryName)
    {
        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();
        await AddTagsAsync(tagName);
        await SelectCategoryAsync(categoryName);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
    }

    private async Task SelectCategoryAsync(string categoryName)
    {
        // Select through the picker's search: the unfiltered list is a bounded window (the first
        // page of name-ordered categories), so a freshly created category can sort outside it.
        var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
        await category.ClickAsync();
        await category.FillAsync(categoryName);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();
    }

    private async Task LogoutAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/logout");
        await Expect(Page.GetByText("Log in")).ToBeVisibleAsync();
    }

    /// <summary>Opens the draft's review page through the Editor list (EditorTests' pattern: its
    /// Review link carries the slug the public API cannot reveal).</summary>
    private async Task OpenReviewAsync(string title)
    {
        await Page.GotoAsync($"{BaseUrl}/editor");
        await WaitForInteractiveAsync(Page);
        var row = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/editor/posts/"),
            new PageWaitForURLOptions { Timeout = 15000 });
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
    }
}
