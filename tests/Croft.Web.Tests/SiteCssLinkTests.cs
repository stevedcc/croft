using System.Net;
using System.Security.Cryptography;
using AngleSharp.Html.Parser;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 18d item 2: the site's optional site.css is linked last in the head of every document
/// (a static reading page and an interactive staff page alike), so its rules win the cascade; a
/// site without the file gets no link; and the Content-Security-Policy does not change (the
/// stylesheet is same-origin, which style-src and style-src-elem already allow).
/// </summary>
public class SiteCssLinkTests : IDisposable
{
    private const string StaffPage = "/admin/pages";

    private const string SiteCss = "/* the site's rules */\n:root { --primary: #123456; }\n";

    private readonly List<string> _folders = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        foreach (var folder in _folders)
        {
            Directory.Delete(folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private HttpClient CreateClient(bool withSiteCss, bool signedIn = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"blog-site-css-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        File.WriteAllText(Path.Combine(folder, "branding.json"), """{ "name": "Field Notes" }""");
        if (withSiteCss)
        {
            File.WriteAllText(Path.Combine(folder, "site.css"), SiteCss);
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Blog:BrandingPath", folder);
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
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private static async Task<IReadOnlyList<string>> HeadStylesheetHrefsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        return [.. document.Head!.QuerySelectorAll("link[rel=stylesheet]").Select(link => link.GetAttribute("href")!)];
    }

    private static string SiteCssUrl() =>
        $"/branding/site.css?v={Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SiteCss)))[..16].ToLowerInvariant()}";

    [Theory]
    [InlineData("/", false)]
    [InlineData(StaffPage, true)]
    public async Task WithSiteCss_TheHeadLinksItOnceAndLast(string path, bool signedIn)
    {
        using var client = CreateClient(withSiteCss: true, signedIn);

        var hrefs = await HeadStylesheetHrefsAsync(client, path);

        hrefs.Should().ContainSingle(href => href.Contains("site.css", StringComparison.Ordinal),
            "the file is linked exactly once")
            .Which.Should().Be(SiteCssUrl(), "the link carries the hash of the file's bytes");
        hrefs[^1].Should().Be(SiteCssUrl(),
            $"site.css is the last stylesheet, after the framework's, so its rules win; the head has {string.Join(", ", hrefs)}");
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData(StaffPage, true)]
    public async Task WithoutSiteCss_NoPageLinksIt(string path, bool signedIn)
    {
        using var client = CreateClient(withSiteCss: false, signedIn);

        var hrefs = await HeadStylesheetHrefsAsync(client, path);

        hrefs.Should().NotBeEmpty("the framework stylesheets are there");
        hrefs.Should().NotContain(href => href.Contains("site.css", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheContentSecurityPolicy_IsIdenticalWithAndWithoutTheFile()
    {
        using var with = CreateClient(withSiteCss: true);
        using var without = CreateClient(withSiteCss: false);

        using var withResponse = await with.GetAsync("/");
        using var withoutResponse = await without.GetAsync("/");

        string[] Policy(HttpResponseMessage response) =>
            [.. response.Headers.Where(header => header.Key.StartsWith("Content-Security-Policy", StringComparison.Ordinal))
                .OrderBy(header => header.Key, StringComparer.Ordinal)
                .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}")];

        Policy(withoutResponse).Should().NotBeEmpty("the page carries a policy");
        Policy(withResponse).Should().Equal(Policy(withoutResponse),
            "the stylesheet is same-origin and linked, which 'self' already allows: the policy does not move");
    }
}
