using Microsoft.Extensions.DependencyInjection;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Croft.Web.Tests;

/// <summary>
/// The generated endpoints over HTTP (brief 07a): GET /branding/theme.css serves the palette
/// CSS, GET /manifest.webmanifest the manifest, and GET /branding/{file} the five image files —
/// the site's file if present, else the framework default — with their content types and
/// immutable cache headers. Unknown names (including encoded traversal) are 404.
/// </summary>
public class BrandingEndpointTests : IDisposable
{
    private readonly string _brandingFolder = CreateBrandingFolder();

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        Directory.Delete(_brandingFolder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CreateBrandingFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-branding-endpoint-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "branding.json"),
            """{ "name": "Field Notes", "palette": { "light": { "primary": "#9A3412" } } }""");
        return path;
    }

    private void WriteSiteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_brandingFolder, name), content);

    private static byte[] DefaultFileBytes(string name) =>
        File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "Croft.Web", "wwwroot", "branding-default", name));

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

    // The factories the client calls are kept and disposed with the class: a dropped factory's host lives to process exit.
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private HttpClient CreateClient(string? brandingPath = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Blog:BrandingPath", brandingPath ?? _brandingFolder);
            // Pages render against a stub API, so "/" answers (the pages' own data is not under test).
            builder.ConfigureServices(services => services.AddHttpClient("BlogApi")
                .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler()));
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    [Fact]
    public async Task ThemeCss_IsServedAsCssWithTheSitePalette()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/theme.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/css; charset=utf-8");
        response.Headers.CacheControl!.ToString().Should().Contain("max-age=31536000")
            .And.Contain("immutable", "the palette hash on the link makes the URL unique, so the CSS can be cached forever");
        var css = await response.Content.ReadAsStringAsync();
        css.Should().Contain("--primary: #9A3412;", "the site's palette is what gets generated");
        css.Should().Contain(":root[data-theme=dark]");
    }

    [Fact]
    public async Task ThemeCss_CarriesNoMudBlazorPaletteVariable()
    {
        // Brief 09i: the reading pages are static SSR without any MudBlazor component, and the
        // interactive pages have MudThemeProvider, which handles dark mode itself. Nothing reads
        // --mud-palette-* from theme.css any more, so it carries no such declaration.
        using var client = CreateClient();

        var css = await (await client.GetAsync("/branding/theme.css")).Content.ReadAsStringAsync();

        css.Should().NotContain("--mud-palette-", "theme.css holds the site's own tokens only");
        css.Should().NotContain("--mud-", "no MudBlazor variable of any kind rides in theme.css");
        css.Should().Contain(":root[data-theme=dark]", "the site's own dark tokens are still there");
        css.Should().Contain("@media (prefers-color-scheme: dark)");
    }

    [Fact]
    public async Task ThemeCssLink_CacheBuster_IsTheHashOfTheExactServedBytes()
    {
        // theme.css is served immutable for a year under ?v=…: the buster must move whenever any
        // byte of the served text moves.
        using var client = CreateClient();

        var page = await client.GetStringAsync("/");
        var served = await client.GetByteArrayAsync("/branding/theme.css");

        var match = System.Text.RegularExpressions.Regex.Match(page, @"/branding/theme\.css\?v=([0-9a-f]+)");
        match.Success.Should().BeTrue("the page links theme.css with a ?v= cache-buster");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(served))[..16].ToLowerInvariant();
        match.Groups[1].Value.Should().Be(expected,
            "?v= is the hash of exactly the bytes theme.css serves, so any change to them changes the URL");
    }

    [Fact]
    public void ThemeStylesheet_Version_ChangesWithTheServedText()
    {
        var first = new Croft.Web.Theme.ThemeStylesheet();
        var second = new Croft.Web.Theme.ThemeStylesheet();
        first.Initialize(":root { --a: 1; }");
        second.Initialize(":root { --a: 2; }");

        second.Version.Should().NotBe(first.Version, "a changed served text changes the version");
        first.Version.Should().HaveLength(16);
    }

    [Fact]
    public async Task Manifest_IsServedAsManifestJsonForTheSite()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/manifest.webmanifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/manifest+json; charset=utf-8");
        response.Headers.CacheControl!.ToString().Should().Contain("no-store",
            "the manifest changes with the site's name and colours and is never cached hard");
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"name\": \"Field Notes\"");
    }

    [Theory]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("apple-touch-icon.png", "image/png")]
    public async Task BrandingFile_TheSiteSupplied_IsServedWithItsContent(string file, string contentType)
    {
        WriteSiteFile(file, "the site's own file");
        using var client = CreateClient();

        using var response = await client.GetAsync($"/branding/{file}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        (await response.Content.ReadAsStringAsync()).Should().Be("the site's own file",
            "the site's file wins over the framework default");
        AssertImmutableCacheHeaders(response);
    }

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("favicon.ico")]
    [InlineData("apple-touch-icon.png")]
    [InlineData("fallback-banner.svg")]
    public async Task BrandingFile_TheSiteDidNotSupply_FallsBackToTheFrameworkDefault(string file)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"/branding/{file}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(DefaultFileBytes(file),
            "a site that supplies no images still gets the finished defaults");
        AssertImmutableCacheHeaders(response);
    }

    [Fact]
    public async Task FallbackBanner_IsTheFirstOfSvgPngJpgTheSiteSupplies()
    {
        WriteSiteFile("fallback-banner.jpg", "the jpg banner");
        WriteSiteFile("fallback-banner.png", "the png banner");
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/fallback-banner.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("image/png");
        (await response.Content.ReadAsStringAsync()).Should().Be("the png banner",
            "png beats jpg and the site's svg is absent");
    }

    [Fact]
    public async Task MastheadBanner_TheSiteSupplied_IsServedLikeTheFallbackBanner()
    {
        WriteSiteFile("masthead-banner.jpg", "the jpg masthead");
        WriteSiteFile("masthead-banner.png", "the png masthead");
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/masthead-banner.jpg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("image/png",
            "one logical file: the resolved png goes out with its own content type");
        (await response.Content.ReadAsStringAsync()).Should().Be("the png masthead");
        AssertImmutableCacheHeaders(response);
    }

    [Fact]
    public async Task SiteCss_TheSiteSupplied_IsServedAsCssUnchangedAndImmutable()
    {
        byte[] bytes = [.. "/* site */\r\n:root { --primary: #123456; }\n"u8, 0xC3, 0xA9];
        File.WriteAllBytes(Path.Combine(_brandingFolder, "site.css"), bytes);
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/site.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/css; charset=utf-8");
        AssertImmutableCacheHeaders(response);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes, "the file is served as it is, byte for byte");
    }

    [Fact]
    public async Task SiteCss_TheSiteDidNotSupply_Is404()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/site.css");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "there is no framework default stylesheet");
    }

    [Fact]
    public async Task SiteCss_WithoutABrandingFolder_Is404()
    {
        using var client = CreateClient(Path.Combine(Path.GetTempPath(), $"blog-branding-missing-{Guid.NewGuid():N}"));

        using var response = await client.GetAsync("/branding/site.css");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("masthead-banner.svg")]
    [InlineData("masthead-banner.png")]
    [InlineData("masthead-banner.jpg")]
    public async Task MastheadBanner_TheSiteDidNotSupply_Is404(string file)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"/branding/{file}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "there is no framework default masthead file: the masthead uses the fallback banner");
    }

    [Fact]
    public async Task FallbackBanner_WithAllThree_SvgWins()
    {
        WriteSiteFile("fallback-banner.svg", "the svg banner");
        WriteSiteFile("fallback-banner.png", "the png banner");
        WriteSiteFile("fallback-banner.jpg", "the jpg banner");
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/fallback-banner.jpg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("the svg banner",
            "the first of svg, png, jpg the site supplies wins");
    }

    [Fact]
    public async Task FallbackBanner_AnyRequestName_ServesTheResolvedFileWithItsOwnContentType()
    {
        // The fallback banner is one logical file: whatever extension the request uses, the
        // resolved file goes out with that file's content type, not the requested name's.
        WriteSiteFile("fallback-banner.svg", "the svg banner");
        using var client = CreateClient();

        using var response = await client.GetAsync("/branding/fallback-banner.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("image/svg+xml",
            "the served file is the resolved svg, so it carries the svg content type");
        (await response.Content.ReadAsStringAsync()).Should().Be("the svg banner");
    }

    [Theory]
    [InlineData("unknown.svg")]
    [InlineData("appsettings.json")]
    [InlineData("logo.png")]
    public async Task BrandingFile_UnknownName_IsNotFound(string file)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"/branding/{file}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("..%2Fappsettings.json")]
    [InlineData("..%2F..%2Fappsettings.json")]
    public async Task BrandingFile_EncodedTraversal_IsNotFound(string file)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"/branding/{file}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the endpoint serves its five known names and nothing else, so no path can escape");
    }

    private static void AssertImmutableCacheHeaders(HttpResponseMessage response) =>
        response.Headers.CacheControl!.ToString().Should().Contain("max-age=31536000")
            .And.Contain("immutable", "every /branding file link carries its content hash, so the URL is unique");
}
