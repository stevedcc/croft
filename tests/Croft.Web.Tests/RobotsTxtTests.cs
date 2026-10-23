using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Croft.Web.Tests;

public class RobotsTxtTests
{
    // With a trailing slash exactly like start-local.sh configures Blog:BaseUrl (k8s has none):
    // whatever the configured shape, the sitemap URL must come out with exactly one slash.
    private const string ConfiguredBaseUrl = "https://example.com/";

    [Fact]
    public async Task RobotsTxt_ReturnsExactBodyAsPlainText()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", ConfiguredBaseUrl);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/robots.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8");
        (await response.Content.ReadAsStringAsync()).Should().Be("""
            User-agent: *
            Allow: /
            Disallow: /admin
            Disallow: /editor
            Disallow: /login
            Disallow: /logout
            Disallow: /signin-oidc
            Disallow: /signout-callback-oidc
            Allow: /api/media/
            Disallow: /api/
            Sitemap: https://example.com/sitemap.xml

            """);
    }
}
