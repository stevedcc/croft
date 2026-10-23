using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 3 (review I-3, M-15): the search field is a search field. Its role is the native
/// searchbox, the submit button is a tab stop, the hairline that is the field's only visible
/// boundary reaches 3:1 against the page in every theme, and the native controls follow the theme.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class SearchFieldTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task TheField_IsASearchbox_AndTheButtonIsTheNextTabStop_ThatSubmitsTheSearch()
    {
        var title = $"Search Field {RunId}";
        await PublishAsync(title, $"Body of {RunId}.");
        await Page.GotoAsync($"{BaseUrl}/search");
        await WaitForInteractiveAsync(Page);

        var field = Page.GetByRole(AriaRole.Searchbox, new PageGetByRoleOptions { Name = "Search" });
        await Expect(field).ToHaveCountAsync(1);
        await Expect(Page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search" })).ToHaveCountAsync(0);

        await field.FillAsync(RunId);
        await Page.Keyboard.PressAsync("Tab");
        var button = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search" });
        await Expect(button).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");

        await Page.WaitForURLAsync(url => url.EndsWith($"/search?q={RunId}"));
        await Expect(Page.Locator(".post-card", new PageLocatorOptions { HasText = title })).ToBeVisibleAsync();
    }

    [TestCase("light")]
    [TestCase("dark")]
    [TestCase("system-dark")]
    public async Task TheFieldsHairline_ContrastsAtLeast3To1_WithThePage(string theme)
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            // system-dark: no stored preference, the operating system asks for dark.
            ColorScheme = theme == "light" ? ColorScheme.Light : ColorScheme.Dark,
        });
        if (theme != "system-dark")
        {
            await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        }

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/search");
        await WaitForInteractiveAsync(page);
        if (theme != "light")
        {
            await WaitForDarkPaletteAsync(page);
        }

        // The hairline transitions its colour (200 ms) when the theme settles: read it at rest.
        await page.WaitForFunctionAsync("() => document.getAnimations().every(a => a.playState !== 'running')");
        var json = await page.EvaluateAsync<string>(
            @"() => {
                const line = document.querySelector('.search-field__border');
                const box = document.querySelector('.search-field__box');
                const ancestors = [];
                for (let p = line.parentElement; p; p = p.parentElement) ancestors.push(getComputedStyle(p).backgroundColor);
                return JSON.stringify({ line: getComputedStyle(line).borderTopColor, ancestors, scheme: getComputedStyle(box).colorScheme });
            }");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var r = doc.RootElement;
        var canvas = theme == "light" ? new Rgba(255, 255, 255, 1) : new Rgba(0, 0, 0, 1);
        var backdrop = BackdropOf([.. r.GetProperty("ancestors").EnumerateArray().Select(e => e.GetString()!)], canvas);
        var line = Composite(Parse(r.GetProperty("line").GetString()!), backdrop);
        Assert.That(Ratio(line, backdrop), Is.GreaterThanOrEqualTo(3),
            $"the field's hairline {r.GetProperty("line").GetString()} over the page backdrop contrasts at least 3:1 ({theme}), WCAG 1.4.11");
        Assert.That(r.GetProperty("scheme").GetString(), Is.EqualTo("light dark"),
            "the box declares both schemes, so the native clear button and caret follow the theme (review M-15)");
    }
}
