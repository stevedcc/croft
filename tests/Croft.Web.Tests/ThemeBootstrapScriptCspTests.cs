using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// App.razor's inline theme bootstrap script is allowed by its hash: script-src carries
/// <c>'sha256-…'</c> of EXACTLY the script element's text as the browser receives it. The
/// expectation is computed here from the rendered markup, never from the constant the app
/// hashes, so a rendered script that drifts from the hashed text fails here just as it
/// fails in the browser.
/// </summary>
public class ThemeBootstrapScriptCspTests
{
    [Fact]
    public async Task ScriptSrc_AllowsTheThemeBootstrapScript_ByTheHashOfItsRenderedText()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        // The theme bootstrap is the document's one inline script element: every other script
        // carries a src or a data block type (the JSON-LD scripts in SeoHead).
        var scripts = Regex.Matches(html, "<script>(?<text>.*?)</script>", RegexOptions.Singleline)
            .Select(match => match.Groups["text"].Value)
            .ToList();
        scripts.Should().ContainSingle(
            "App.razor's theme bootstrap is the only inline script in the rendered markup");
        var scriptText = scripts[0];

        // What the browser hashes for an inline script: the element's text, byte for byte.
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(scriptText)));

        var policy = response.Headers.GetValues("Content-Security-Policy-Report-Only").Should()
            .ContainSingle("one middleware writes one header").Which;
        var scriptSrc = policy.Split("; ").Should()
            .ContainSingle(directive => directive.StartsWith("script-src ", StringComparison.Ordinal),
                "the policy names script-src").Which;
        scriptSrc.Should().Contain($"'sha256-{hash}'",
            "the inline theme bootstrap script runs before first paint and must be allowed by the hash of its exact rendered text");
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
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
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler()));
        });
}
