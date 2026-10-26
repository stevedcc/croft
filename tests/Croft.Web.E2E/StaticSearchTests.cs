using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 09f item 3: the search is a plain GET form, so it works on the static page (no circuit).
/// Enter in the field and the button both submit it to /search?q=term by enhanced navigation (the
/// window marker survives: no full document load) and the results show; the pager's links keep
/// q and paging works the same way; the header says "1 result" for one and "2 results" for two.
/// Fixtures: a run-keyed term found by exactly one post, one found by exactly two, and one found
/// by 11 (a real second page; the page size is 10).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class StaticSearchTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static readonly string OneTerm = $"solo{RunId}";
    private static readonly string TwoTerm = $"pair{RunId}";
    private static readonly string ManyTerm = $"many{RunId}";
    private static readonly string SoloTitle = $"Search Solo {RunId}";

    private static bool _dataReady;

    [Test]
    public async Task Enter_SearchesByUrl_ShowsTheResults_WithoutAFullReload()
    {
        await EnsureFixturesAsync();
        var sockets = EventLog.WebSocketUrls(Page);
        await Page.GotoAsync($"{BaseUrl}/search");
        await WaitForInteractiveAsync(Page);
        await Page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        await Page.Locator("#search-field").FillAsync(OneTerm);
        await Page.Locator("#search-field").PressAsync("Enter");

        await Page.WaitForURLAsync(url => url.EndsWith($"/search?q={OneTerm}"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"1 result for '{OneTerm}'" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = SoloTitle })).ToBeVisibleAsync();
        Assert.That(await Page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "Enter must search by enhanced navigation: a full document load would drop the window marker.");
        await Expect(Page.Locator("#search-field")).ToHaveValueAsync(OneTerm);
        Assert.That(sockets.Snapshot(), Is.Empty, "searching must open no circuit.");
    }

    [Test]
    public async Task TheSearchButton_SubmitsTheSameSearch()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/search");
        await WaitForInteractiveAsync(Page);
        await Page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        await Page.Locator("#search-field").FillAsync(TwoTerm);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search" }).ClickAsync();

        await Page.WaitForURLAsync(url => url.EndsWith($"/search?q={TwoTerm}"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"2 results for '{TwoTerm}'" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(2);
        Assert.That(await Page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "the button must search by enhanced navigation too.");
    }

    [Test]
    public async Task ASearchTermWithSpacesAndAmpersands_ArrivesIntact()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/search");
        await WaitForInteractiveAsync(Page);

        await Page.Locator("#search-field").FillAsync($"nothing & zzz{RunId}");
        await Page.Locator("#search-field").PressAsync("Enter");

        await Page.WaitForURLAsync(url => url.Contains("/search?q="));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"0 results for 'nothing & zzz{RunId}'" })).ToBeVisibleAsync();
        await Expect(Page.Locator("#search-field")).ToHaveValueAsync($"nothing & zzz{RunId}");
    }

    [Test]
    public async Task PagingLinks_KeepTheQuery_AndPage2Opens()
    {
        await EnsureFixturesAsync();
        await Page.GotoAsync($"{BaseUrl}/search?q={ManyTerm}");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"11 results for '{ManyTerm}'" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(10);
        await Page.EvaluateAsync("() => { window.__docMarker = 'one-document'; }");

        var page2 = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "2", Exact = true });
        await Expect(page2).ToHaveAttributeAsync("href", new Regex(Regex.Escape($"q={ManyTerm}&page=2")));
        await page2.ClickAsync();

        await Page.WaitForURLAsync(url => url.Contains($"q={ManyTerm}&page=2"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"11 results for '{ManyTerm}'" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".post-card")).ToHaveCountAsync(1);
        Assert.That(await Page.EvaluateAsync<string?>("() => window.__docMarker"), Is.EqualTo("one-document"),
            "paging is an enhanced navigation too.");
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task InDarkMode_TheSearchButtonIsTransparent_NotTheBrowsersLightGreyFace(int width, int height)
    {
        await EnsureFixturesAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = width, Height = height },
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/search?q={OneTerm}");
        await WaitForInteractiveAsync(page);

        // The button's own 150 ms background transition has run when no animation is left on it
        // (at load it can still be fading from the browser's default face, as it always did).
        var button = page.Locator(".search-field__button");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.search-field__button').getAnimations().length === 0");

        var style = await button.EvaluateAsync<string>(
            "el => JSON.stringify({ bg: getComputedStyle(el).backgroundColor, color: getComputedStyle(el).color })");
        using var doc = System.Text.Json.JsonDocument.Parse(style);
        Assert.That(doc.RootElement.GetProperty("bg").GetString(), Is.EqualTo("rgba(0, 0, 0, 0)"),
            "in dark mode the search button must have no background of its own (it showed the browser's light-grey face).");
        Assert.That(doc.RootElement.GetProperty("color").GetString(), Is.EqualTo("rgb(173, 173, 177)"),
            "the button's icon colour is the field's dark icon colour (the same value it has always had).");
    }

    [Test]
    public async Task TheHeader_SaysOneResult_AndTwoResults()
    {
        await EnsureFixturesAsync();

        await Page.GotoAsync($"{BaseUrl}/search?q={OneTerm}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"1 result for '{OneTerm}'", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("1 results")).ToHaveCountAsync(0);

        await Page.GotoAsync($"{BaseUrl}/search?q={TwoTerm}");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"2 results for '{TwoTerm}'", Exact = true })).ToBeVisibleAsync();
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

            await PublishAsync(SoloTitle, $"Body about {OneTerm} only.");
            await PublishAsync($"Search Pair One {RunId}", $"Body about {TwoTerm}.");
            await PublishAsync($"Search Pair Two {RunId}", $"Another body about {TwoTerm}.");
            for (var n = 1; n <= 11; n++)
            {
                await PublishAsync($"Search Many {n} {RunId}", $"Body number {n} about {ManyTerm}.");
            }

            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
