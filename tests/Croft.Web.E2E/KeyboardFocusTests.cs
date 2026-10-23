using System.Text.Json;
using static Croft.Web.E2E.ColorContrast;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 09i item 5: every focusable control on the static reading pages and the shell shows a
/// visible focus indicator for keyboard focus (WCAG 2.4.7 / 1.4.11) and none for mouse focus.
/// The outcome asserted is what the browser computes while the control really holds keyboard
/// focus: outline style, width and the contrast of the outline colour with the background it sits
/// on, computed here from the computed colours. Backgrounds are alpha-aware (ColorContrast): each
/// semi-transparent layer is composited over the opaque backdrop beneath it, and the outline is
/// checked against both sides of it, the backdrop outside the control and the control's own fill.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class KeyboardFocusTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static string? _postSlug;

    /// <summary>Safety net, not a budget: the traversal ends when focus wraps around to an element
    /// it already reached (identity-tagged in the page). Only a document with more tabbable
    /// elements than this is truncated.</summary>
    private const int MaxTabSteps = 250;

    private sealed record Focused(string Key, string Style, double Width, double Offset, string Color, string OwnBackground, IReadOnlyList<string> AncestorBackgrounds, string Label);

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task Home_EveryKeyboardFocusedControl_ShowsAnOutline(int width, int height, string theme)
    {
        var seen = await TabThroughAsync("/", width, height, theme);
        AssertShellControlsSeen(seen, width);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task Post_EveryKeyboardFocusedControl_ShowsAnOutline(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        var seen = await TabThroughAsync($"/post/{_postSlug}", width, height, theme);
        AssertShellControlsSeen(seen, width);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task StaticNotFound_TheLinks_ShowAnOutline(int width, int height, string theme)
    {
        var seen = await TabThroughAsync($"/post/missing-{RunId}", width, height, theme);

        Assert.That(seen.Count(f => f.Key.Contains("not-found__link")), Is.EqualTo(2),
            "both not-found links take keyboard focus");
        AssertShellControlsSeen(seen, width);
    }

    [TestCase(1440, 900, "light", ".theme-switch")]
    [TestCase(1440, 900, "dark", ".collapse-toggle")]
    [TestCase(390, 844, "light", ".menu-button")]
    [TestCase(390, 844, "dark", ".menu-button")]
    public async Task MouseClick_OnAShellButton_LeavesNoOutline(int width, int height, string theme, string selector)
    {
        await using var context = await NewContextAsync(width, height, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.interactive === 'on'");

        var button = page.Locator(selector).First;
        await button.ClickAsync();

        // The menu button opens the drawer, which moves focus into it; the other buttons keep it. Either
        // way the outline is read off the element that HAS focus after the click: reading the button's
        // own outline once focus has moved away would pass for any unfocused element (it always has none).
        if (selector != ".menu-button")
        {
            Assert.That(await button.EvaluateAsync<bool>("el => el === document.activeElement"), Is.True,
                "the click focused the button (otherwise this test proves nothing)");
        }

        var focused = await page.EvaluateAsync<string>(
            "() => { const el = document.activeElement; return JSON.stringify({ isBody: el === document.body, describe: el.tagName.toLowerCase() + (el.className ? '.' + String(el.className).trim().replace(/\\s+/g, '.') : ''), outline: getComputedStyle(el).outlineStyle }); }");
        using var doc = System.Text.Json.JsonDocument.Parse(focused!);
        Assert.That(doc.RootElement.GetProperty("isBody").GetBoolean(), Is.False,
            "something holds focus after the click (otherwise there is no outline to read)");
        Assert.That(doc.RootElement.GetProperty("outline").GetString(), Is.EqualTo("none"),
            $"mouse focus draws no outline on the element that took it ({doc.RootElement.GetProperty("describe").GetString()}): the rule is :focus-visible only");
    }

    private static void AssertShellControlsSeen(List<Focused> seen, int width)
    {
        // Desktop: the sidebar's controls; phone: the top bar's menu button.
        string[] expected = width >= 960
            ? ["theme-switch", "collapse-toggle", "nav-item"]
            : ["menu-button"];
        foreach (var name in expected)
        {
            Assert.That(seen.Any(f => f.Key.Contains(name)), Is.True,
                $"Tab reached a '{name}' control (saw: {string.Join(", ", seen.Select(f => f.Key))})");
        }
    }

    private async Task<List<Focused>> TabThroughAsync(string path, int width, int height, string theme)
    {
        await using var context = await NewContextAsync(width, height, theme);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}{path}");
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.interactive === 'on'");

        var seen = new List<Focused>();
        for (var press = 0; press < MaxTabSteps; press++)
        {
            await page.Keyboard.PressAsync("Tab");
            var json = await page.EvaluateAsync<string>(
                @"() => {
                    const el = document.activeElement;
                    if (!el || el === document.body) return null;
                    // Wrap-around is detected by element IDENTITY, not by a descriptive key: every
                    // element this traversal reached carries a counter tag, so the first focus of
                    // an already-tagged element is the wrap. A key is not identity — two cards
                    // chipped to the same category share one, which ended the traversal early and
                    // skipped the shell controls (brief 11a's end-of-branch E2E runs).
                    if (el.hasAttribute('data-tabseen')) return JSON.stringify({ wrapped: true });
                    el.setAttribute('data-tabseen', String((window.__tabSteps = (window.__tabSteps || 0) + 1)));
                    const cs = getComputedStyle(el);
                    const ancestors = [];
                    for (let p = el.parentElement; p; p = p.parentElement) ancestors.push(getComputedStyle(p).backgroundColor);
                    const key = el.tagName.toLowerCase() + '.' + (el.getAttribute('class') || '').split(/\s+/).filter(Boolean).join('.') + '[' + (el.getAttribute('href') || '') + ']';
                    return JSON.stringify({ key, style: cs.outlineStyle, width: parseFloat(cs.outlineWidth), offset: parseFloat(cs.outlineOffset), color: cs.outlineColor, own: cs.backgroundColor, ancestors,
                        label: el.matches('.skip-link') ? 'skip' : '' });
                }");
            if (json is null)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("wrapped", out _))
            {
                break; // the focus ring wrapped around to an element already visited
            }

            var r = doc.RootElement;
            var focused = new Focused(r.GetProperty("key").GetString()!, r.GetProperty("style").GetString()!,
                r.GetProperty("width").GetDouble(), r.GetProperty("offset").GetDouble(), r.GetProperty("color").GetString()!,
                r.GetProperty("own").GetString()!, [.. r.GetProperty("ancestors").EnumerateArray().Select(e => e.GetString()!)],
                r.GetProperty("label").GetString()!);
            seen.Add(focused);
        }

        Assert.That(seen, Is.Not.Empty, "Tab focused something");
        foreach (var f in seen)
        {
            Assert.That(f.Style, Is.Not.EqualTo("none"), $"{f.Key} shows no outline while keyboard-focused ({theme}, {width}px)");
            Assert.That(f.Width, Is.GreaterThanOrEqualTo(2), $"{f.Key}: outline width");
            var canvas = theme == "dark" ? new Rgba(0, 0, 0, 1) : new Rgba(255, 255, 255, 1);
            var backdrop = BackdropOf(f.AncestorBackgrounds, canvas);
            var outline = Composite(Parse(f.Color), backdrop);
            var fill = Composite(Parse(f.OwnBackground), backdrop);
            var outside = Ratio(outline, backdrop);
            // The control's own fill is adjacent to the outline only when the outline overlays the
            // control (an offset of 0 or less); with a positive offset the backdrop shows in the gap
            // between them, so the skip link's outline, the same colour as its own fill, is fine.
            // At offset 0 the outline's inner edge abuts the fill, so that is compared too.
            var inside = f.Offset <= 0 ? Ratio(outline, fill) : double.PositiveInfinity;
            Assert.That(outside, Is.GreaterThanOrEqualTo(3),
                $"{f.Key}: outline {f.Color} on the backdrop {Describe(backdrop)} contrasts {outside:0.00}:1 ({theme}, {width}px), WCAG 1.4.11 needs 3:1");
            Assert.That(inside, Is.GreaterThanOrEqualTo(3),
                $"{f.Key}: outline {f.Color} on the control's own fill {Describe(fill)} contrasts {inside:0.00}:1 ({theme}, {width}px), WCAG 1.4.11 needs 3:1");
        }

        return seen;
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

    private static string Describe(Rgba c) => $"rgb({c.R:0}, {c.G:0}, {c.B:0})";

    private async Task EnsureFixturesAsync()
    {
        if (_postSlug is not null)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            _postSlug ??= (await PublishAsync($"Focus Post {RunId}", BodyWithSections(RunId, 3))).Slug;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
