using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 10c item 3: every <c>&lt;script src&gt;</c> the app renders carries an <c>integrity</c>
/// attribute, and the digest is the one of the bytes the server really serves at that URL. The
/// expectation is computed here from the served file, so a stale or invented digest fails the way
/// it fails in the browser (the script is refused). A static reading page and an interactive
/// document (MudBlazor, the reconnect modal's module script, the editor's scripts) are both
/// checked.
/// </summary>
public class ScriptIntegrityTests
{
    private static readonly Regex ScriptTag = new("<script\\b[^>]*\\bsrc=\"(?<src>[^\"]+)\"[^>]*>", RegexOptions.Compiled);
    private static readonly Regex IntegrityAttribute = new("\\bintegrity=\"(?<value>[^\"]+)\"", RegexOptions.Compiled);

    [Theory]
    [InlineData("/", "a static reading page")]
    [InlineData("/not-found", "an interactive document")]
    public async Task EveryScriptWithASrc_CarriesTheDigestOfTheFileItIsServedAs(string path, string kind)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var page = await client.GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.OK, kind);
        var html = await page.Content.ReadAsStringAsync();

        var compared = 0;
        var scripts = ScriptTag.Matches(html).ToList();
        scripts.Should().NotBeEmpty($"{kind} loads scripts");
        foreach (var script in scripts)
        {
            var src = script.Groups["src"].Value;
            var integrity = IntegrityAttribute.Match(script.Value);
            integrity.Success.Should().BeTrue($"{src} on {kind} must carry an integrity attribute");

            var declared = WebUtility.HtmlDecode(integrity.Groups["value"].Value);
            declared.Should().MatchRegex("^sha256-[A-Za-z0-9+/]{43}=$", $"the integrity of {src} is a SHA-256 digest");

            // Test limit, not a product workaround: the test host runs the static assets in
            // development mode, where files that only exist inside a package or the shared
            // framework (_framework/, scoped component scripts, _content/) have no file under
            // wwwroot and answer 500. The E2E suite fetches them for real in three browsers, and a
            // wrong digest there makes the browser refuse the script.
            using var file = await client.GetAsync(new Uri(new Uri("http://localhost/"), src));
            if (file.StatusCode != HttpStatusCode.OK)
            {
                src.Should().MatchRegex("^(_framework/|Components/|_content/)", $"{src} is the app's own file and must be served");
                continue;
            }

            var digest = Convert.ToBase64String(SHA256.HashData(await file.Content.ReadAsByteArrayAsync()));
            declared.Should().Be($"sha256-{digest}",
                $"the integrity of {src} must be the digest of the bytes served at that URL");
            compared++;
        }

        compared.Should().BeGreaterThan(0, "at least the app's own js/ files are compared with their served bytes");
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler()));
        });
}
