using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests;

/// <summary>
/// MudThemeProvider's inline style blocks — the theme variables for each mode and the chart
/// block the provider always renders beside them — are allowed by their hashes: every
/// response's style-src-elem carries 'sha256-…' of each block's text, taken exactly as the
/// browser's DOM holds it. Both modes go on every response because an interactive theme
/// switch replaces the block's text in the DOM without any new response. The scrollbar block
/// is not hashed: MainLayout turns it off (DefaultScrollbar "true") and app.css carries the
/// same rules verbatim. Expectations are derived from real renders at test time — no digest
/// is hard-coded — and the served page is hashed too, so the app's startup render and what
/// the browser actually receives cannot drift apart unnoticed.
/// </summary>
public class MudThemeStyleCspTests
{
    /// <summary>An interactive page: only the interactive documents (admin, the editor, login and
    /// the error and not-found pages) render MudThemeProvider; the reading pages are static SSR
    /// without MudBlazor (brief 09i), and since brief 13a so are the error and not-found pages:
    /// an interactive page needs a signed-in staff request (an admin page).</summary>
    private const string InteractivePage = "/admin/posts";

    [Fact]
    public async Task StyleSrcElem_AllowsEveryServedInlineStyle_ByTheHashOfItsRenderedText()
    {
        using var factory = CreateFactory(signedIn: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(InteractivePage);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var styles = MudThemeStyleRender.StyleTexts(await response.Content.ReadAsStringAsync());
        styles.Should().NotBeEmpty("MudThemeProvider renders its style blocks into every interactive page");
        var sources = StyleSrcElemSources(response);
        foreach (var text in styles)
        {
            sources.Should().Contain(MudThemeStyleRender.HashSource(text),
                $"the browser hashes the style element's exact text, and this page renders '{text[..Math.Min(48, text.Length)].ReplaceLineEndings(" ")}…'");
        }
    }

    [Fact]
    public async Task StaticReadingPage_ServesNoInlineStyleBlock_AtAll()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MudThemeStyleRender.StyleTexts(await response.Content.ReadAsStringAsync())
            .Should().BeEmpty("a reading page renders no MudThemeProvider, so no <style> block reaches it");
    }

    [Fact]
    public async Task StaticReadingPage_AllowsNoStyleHash_BecauseItRendersNoInlineStyle()
    {
        // Brief 10c item 1: a hash allows a style block that is on the page. The reading pages
        // carry no MudThemeProvider (09i), so MudBlazor's hashes would allow styles that are not
        // there; the page's inline styles are none, so its style-src-elem keeps no hash at all.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        StyleSrcElemSources(response).Should().Equal(["'self'"],
            "a static reading page has no inline <style>, so nothing but 'self' may load as a style element");
    }

    [Fact]
    public async Task StyleSrcElem_AllowsTheThemeVariableBlocks_OfAFreshRender_ForBothModes()
    {
        using var factory = CreateFactory(signedIn: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(InteractivePage);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sources = StyleSrcElemSources(response);

        // Croft's MudTheme — the one the app renders and hashes at startup — rendered again
        // right here, both modes, exactly as MainLayout renders the provider.
        var theme = factory.Services.GetRequiredService<MudTheme>();
        foreach (var isDarkMode in new[] { false, true })
        {
            var texts = await MudThemeStyleRender.StyleTextsAsync(theme, isDarkMode, defaultScrollbar: true);
            texts.Should().ContainSingle(text => text.Contains("--mud-palette-black:"),
                $"the provider renders one theme-variable block per mode (isDarkMode: {isDarkMode})");
            foreach (var text in texts)
            {
                sources.Should().Contain(MudThemeStyleRender.HashSource(text),
                    $"the {ModeName(isDarkMode)} mode's blocks must run under the served policy");
            }
        }
    }

    [Fact]
    public async Task TheChartStyleBlock_RendersWithoutAnyChart_AndIsAllowedByHash()
    {
        // MudThemeProvider emits this block unconditionally — it renders on every page of the
        // site even though no page here renders a chart, and the provider has no parameter to
        // switch it off — so it is hashed like the theme variables.
        using var factory = CreateFactory(signedIn: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(InteractivePage);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sources = StyleSrcElemSources(response);

        var theme = factory.Services.GetRequiredService<MudTheme>();
        var texts = await MudThemeStyleRender.StyleTextsAsync(theme, isDarkMode: false, defaultScrollbar: true);
        var chartBlock = texts.Should().ContainSingle(text => text.Contains(".mud-chart-serie:hover"),
            "the provider renders the chart block even on a page with no chart").Which;
        sources.Should().Contain(MudThemeStyleRender.HashSource(chartBlock),
            "the chart block cannot be turned off, so its hash allows it");
    }

    [Fact]
    public async Task ServedPage_CarriesNoInlineScrollbarRules_TheProvidersBlockIsOff()
    {
        using var factory = CreateFactory(signedIn: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(InteractivePage);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MudThemeStyleRender.StyleTexts(await response.Content.ReadAsStringAsync())
            .Should().NotContain(text => text.Contains("::-webkit-scrollbar"),
                "DefaultScrollbar \"true\" turns MudBlazor's inline scrollbar block off; app.css carries its rules instead");
    }

    [Fact]
    public async Task AppCss_CarriesTheProvidersScrollbarRules_VerbatimFromAFreshRender()
    {
        using var factory = CreateFactory();
        var theme = factory.Services.GetRequiredService<MudTheme>();
        var scrollbarBlock = await MudThemeStyleRender.ScrollbarStyleTextAsync(theme);
        scrollbarBlock.Should().Contain("::-webkit-scrollbar", "the derivation found the provider's scrollbar block");

        var appCss = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Croft.Web", "wwwroot", "app.css"));
        var (region, _) = MudBlazorScrollbarCopy.Split(appCss);

        // app.css is CRLF like the rest of the file; the rules themselves must be verbatim, so
        // the comparison is byte-exact after line-ending normalisation.
        region.ReplaceLineEndings("\n").Should().Be(scrollbarBlock.ReplaceLineEndings("\n"),
            "a copy that drifts from MudBlazor's rules changes the look the screenshot gate pins");
    }

    private static string ModeName(bool isDarkMode) => isDarkMode ? "dark" : "light";

    private static IReadOnlyList<string> StyleSrcElemSources(HttpResponseMessage response)
    {
        var policy = response.Headers.GetValues("Content-Security-Policy-Report-Only").Should()
            .ContainSingle("one middleware writes one header").Which;
        return policy.Split("; ").Should()
            .ContainSingle(directive => directive.StartsWith("style-src-elem ", StringComparison.Ordinal),
                "the policy names style-src-elem").Which
            ["style-src-elem ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool signedIn = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            // These tests read the policy through the report-only header name; the mode's
            // default is pinned by CspHeaderTests, so the input pins the mode here.
            builder.UseSetting("Csp:ReportOnly", "true");
            // Outside Development the app refuses to start without the analytics ingest key.
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler());
                if (signedIn)
                {
                    Fixtures.SignedInAdmin.AddTo(services);
                }
            });
        });

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent!;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }
}
