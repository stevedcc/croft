using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14c items 2 and 4: the pages use the Latin subset fonts. Every font a stylesheet or a
/// preload link names is served, and no served stylesheet still names the fonts the subsets
/// replaced (Source Serif 4 and Source Sans 3 are not shipped under those names any more: a subset
/// is an OFL Modified Version and may not carry the Reserved Font Name "Source").
/// </summary>
public partial class FontFilesTests
{
    [GeneratedRegex("""url\(\s*["']?([^"')\s]+)["']?\s*\)""")]
    private static partial Regex CssUrl();

    [GeneratedRegex("""<link\b[^>]*\brel="preload"[^>]*>""")]
    private static partial Regex PreloadLink();

    [GeneratedRegex("""<link\b[^>]*\brel="stylesheet"[^>]*>""")]
    private static partial Regex StylesheetLink();

    [GeneratedRegex("\\bhref=\"([^\"]+)\"")]
    private static partial Regex Href();

    /// <summary>The pages' <c>&lt;base href="/"&gt;</c>: relative URLs in the markup resolve against it.</summary>
    private static readonly Uri BaseHref = new("http://localhost/");

    private static async Task<string> BodyAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The font URLs app.css declares, resolved against the stylesheet's own URL (the
    /// rules use relative URLs).</summary>
    private static async Task<List<string>> FontUrlsInAppCssAsync(HttpClient client) =>
        [.. CssUrl().Matches(await BodyAsync(client, "/app.css")).Select(m => m.Groups[1].Value)
            .Where(u => u.Contains("/fonts/", StringComparison.Ordinal) || u.StartsWith("fonts/", StringComparison.Ordinal))
            .Select(u => new Uri(BaseHref, u).AbsolutePath)];

    /// <summary>
    /// Brief 14c items 2 and 4, the guard: every font URL in app.css is a fingerprinted one that
    /// answers 200 and is cached for a year. app.css is not processed by the build, so the
    /// fingerprints in its @font-face rules are written by hand (the comment above the rules says
    /// where from). A font file that changes gets a new fingerprint and the old URL answers 404,
    /// which is how this test finds a rule that was not updated.
    /// The year-long header is read from the build's endpoint manifest
    /// (Croft.Web.staticwebassets.endpoints.json, the headers a published site sends for the route):
    /// a build-output host, this one and the E2E stack, answers every static asset with "no-cache"
    /// whatever the manifest says (the framework's development behaviour, also seen with the
    /// environment set to Production), so the live header cannot be read here.
    /// </summary>
    [Fact]
    public async Task EveryFontUrlInAppCss_Answers200_AndIsCachedForAYear()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Croft.Web.staticwebassets.endpoints.json")));
        var cacheControlOf = manifest.RootElement.GetProperty("Endpoints").EnumerateArray()
            .ToLookup(
                endpoint => "/" + endpoint.GetProperty("Route").GetString(),
                endpoint => endpoint.GetProperty("ResponseHeaders").EnumerateArray()
                    .Where(header => header.GetProperty("Name").GetString() == "Cache-Control")
                    .Select(header => header.GetProperty("Value").GetString()).SingleOrDefault(),
                StringComparer.Ordinal);

        var urls = await FontUrlsInAppCssAsync(client);

        urls.Should().HaveCount(7, "the seven faces: serif regular, italic, semibold, bold; sans regular, semibold; mono");
        foreach (var url in urls)
        {
            using var response = await client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"app.css names {url}: a font changed, so update the fingerprint in the @font-face rule (the comment above the rules says how)");
            response.Content.Headers.ContentLength.Should().BeGreaterThan(1000, $"{url} is a real font file");

            cacheControlOf[url].Should().NotBeEmpty($"{url} must be a static-asset route");
            cacheControlOf[url].Should().OnlyContain(cacheControl => cacheControl != null && cacheControl.Contains("immutable"),
                $"{url} must be the fingerprinted URL (fonts/<name>.<fingerprint>.woff2), which a published site serves immutable for a year");
        }
    }

    /// <summary>A preload is reused only for the identical URL: the preload and the @font-face rule
    /// of a face must name the same fingerprinted file, or the browser downloads it twice.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData($"/post/{CacheHost.PostSlug}")]
    public async Task EveryFontPreload_IsTheSameUrlAsTheFontFaceRuleOfItsFace(string path)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var cssUrls = await FontUrlsInAppCssAsync(client);
        var html = await BodyAsync(client, path);
        var preloaded = PreloadLink().Matches(html).Select(m => m.Value).Where(l => l.Contains("as=\"font\""))
            .Select(l => new Uri(BaseHref, Href().Match(l).Groups[1].Value).AbsolutePath)
            .ToList();

        preloaded.Should().NotBeEmpty();
        preloaded.Should().OnlyContain(url => cssUrls.Contains(url),
            "the preload href must be exactly the URL app.css names for that face (fingerprint included)");
        PreloadLink().Matches(html).Select(m => m.Value).Where(l => l.Contains("as=\"font\""))
            .Should().OnlyContain(l => l.Contains("crossorigin"), "a font preload is reused only with the same CORS mode");
    }

    [Theory]
    [InlineData("/")]
    [InlineData($"/post/{CacheHost.PostSlug}")]
    public async Task EveryFontPreloadInTheRenderedPage_Answers200(string path)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var html = await BodyAsync(client, path);
        var preloads = PreloadLink().Matches(html).Select(m => m.Value).Where(l => l.Contains("as=\"font\"")).ToList();

        preloads.Should().NotBeEmpty("the page preloads the faces every reading page uses");
        foreach (var link in preloads)
        {
            var href = Href().Match(link).Groups[1].Value;
            using var response = await client.GetAsync(new Uri(BaseHref, href).AbsolutePath);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} preloads {href}");
        }
    }

    /// <summary>
    /// Brief 14c item 3: the faces every reading page uses, measured in the browser (FontPreloadTests,
    /// E2E, on the home page, a post, a category and a tag at both viewports): the shell sets the site
    /// name in bold serif and its tagline in italic serif, the navigation uses the semibold sans, and
    /// the text is regular serif and regular sans. The semibold serif and the monospace face load only
    /// on pages that use them. A new preload is a download on every page: change this list on purpose,
    /// after measuring.
    /// </summary>
    private static readonly string[] PreloadedFaces =
    [
        "CroftSerif-Regular", "CroftSerif-Italic", "CroftSerif-Bold", "CroftSans-Regular", "CroftSans-SemiBold",
    ];

    [Theory]
    [InlineData("/")]
    [InlineData($"/post/{CacheHost.PostSlug}")]
    [InlineData($"/category/{CacheHost.CategorySlug}")]
    [InlineData($"/tag/{CacheHost.TagSlug}")]
    public async Task TheFontPreloadList_IsPinned(string path)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var html = await BodyAsync(client, path);
        var preloaded = PreloadLink().Matches(html).Select(m => m.Value).Where(l => l.Contains("as=\"font\""))
            .Select(l => Path.GetFileNameWithoutExtension(Href().Match(l).Groups[1].Value))
            .Select(name => Regex.Replace(name, @"\.[A-Za-z0-9_-]{10}$", ""))
            .ToList();

        preloaded.Should().BeEquivalentTo(PreloadedFaces, $"{path} preloads exactly the faces every reading page uses");
    }

    [Fact]
    public async Task NoServedStylesheet_NamesTheSourceFonts()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var html = await BodyAsync(client, $"/post/{CacheHost.PostSlug}");
        var sheets = StylesheetLink().Matches(html).Select(m => Href().Match(m.Value).Groups[1].Value)
            .Where(h => !h.StartsWith("http", StringComparison.Ordinal)).ToList();

        sheets.Should().Contain(h => h.Contains("app", StringComparison.Ordinal), "the page links app.css");
        sheets.Should().Contain(h => h.EndsWith("styles.css", StringComparison.Ordinal), "and the scoped bundle");
        foreach (var sheet in sheets)
        {
            var css = await BodyAsync(client, sheet);
            css.Should().NotContain("Source Serif", $"{sheet} names the fonts by their Croft names");
            css.Should().NotContain("Source Sans", $"{sheet} names the fonts by their Croft names");
        }
    }
}
