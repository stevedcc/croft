namespace Croft.Web.E2E;

/// <summary>
/// Brief 14c item 2: the text on a post is set in the faces the stylesheet names, and each one is a
/// font file the browser really loaded (a family name nothing declares falls back silently to the
/// next family in the stack, so the computed value alone proves nothing).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FontFacesTests : FirstVisitTest
{
    [TestCase(".markdown-body > p:not(:has(img))", "Croft Serif", "400", "normal", "the body text")]
    [TestCase(".markdown-body h2", "Croft Serif", "700", "normal", "a heading")]
    [TestCase(".markdown-body p code", "JetBrains Mono", "400", "normal", "an inline code span")]
    [TestCase(".markdown-body em", "Croft Serif", "400", "italic", "italic text")]
    [TestCase(".markdown-body strong", "Croft Serif", "700", "normal", "bold text")]
    public async Task APostsText_IsSetInAFaceTheBrowserLoaded(string selector, string family, string weight, string style, string what)
    {
        var fixtures = await EnsureFixturesAsync();
        await using var context = await NewDesktopContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}{fixtures.PostPath}");
        var element = page.Locator(selector).First;
        await element.WaitForAsync();
        await page.EvaluateAsync("() => document.fonts.ready");

        var computed = await element.EvaluateAsync<string[]>(
            "el => { const cs = getComputedStyle(el); return [cs.fontFamily, cs.fontWeight, cs.fontStyle]; }");
        var first = computed[0].Split(',')[0].Trim().Trim('"', '\'');
        Assert.That(first, Is.EqualTo(family), $"{what} is set in {family}; the stack is {computed[0]}.");
        Assert.That((computed[1], computed[2]), Is.EqualTo((weight, style)), $"{what}: weight and style.");

        var loaded = await page.EvaluateAsync<string[][]>(
            @"() => [...document.fonts].filter(f => f.status === 'loaded')
                .map(f => [f.family.replace(/^[""']|[""']$/g, ''), String(f.weight), f.style])");
        Assert.That(loaded.Select(f => (f[0], f[1], f[2])), Does.Contain((family, weight, style)),
            $"{what} resolves to {family} {weight} {style}, which the browser must have loaded; loaded: " +
            string.Join("; ", loaded.Select(f => string.Join(' ', f))));
    }

    /// <summary>
    /// A cold load of a post asks for each font once (a preload and its @font-face rule name the same
    /// URL) and only for fingerprinted URLs. Whether the response says "immutable" cannot be read
    /// here: the E2E stack runs from the build output, where the framework answers every static asset
    /// with "no-cache"; FontFilesTests reads the year-long header from the build's endpoint manifest
    /// and the commit message of brief 14c item 4 records it from a published build.
    /// </summary>
    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task OnAColdLoadOfAPost_EachFontIsRequestedOnce_AtAFingerprintedUrl(int width, int height)
    {
        var fixtures = await EnsureFixturesAsync();

        var visit = await VisitAsync(fixtures.PostPath, width, height);

        Assert.That(visit.FontUrls, Is.Not.Empty, "the post loaded fonts");
        Assert.That(visit.FontUrls.Select(FaceFile), Is.Unique,
            $"a preload and its @font-face rule must name the same URL, or the face downloads twice: {string.Join(", ", visit.FontUrls)}");
        foreach (var (url, status, _) in visit.FontResponses)
        {
            Assert.That(status, Is.EqualTo(200), url);
            Assert.That(PathOf(url), Does.Match(@"^/fonts/[A-Za-z-]+\.[a-z0-9]{10}\.woff2$"),
                $"{url} must be the fingerprinted URL (fonts/<name>.<fingerprint>.woff2).");
        }
    }
}
