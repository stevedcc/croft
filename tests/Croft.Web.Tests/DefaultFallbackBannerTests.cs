using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 10b item 4: the framework's default fallback banner is the Government / 42nd / Black
/// Watch tartan (Scottish Register of Tartans, reference 277), no longer the slate stripes. A site
/// with no branding folder serves it with the SVG content type and a content hash that differs
/// from the old file's, so every cached copy updates (the hash is the ?v= of the banner URL in
/// theme.css). The rendered colours are asserted by the E2E pixel test; here the file itself:
/// small, self-contained, credited, built from the three colours.
/// </summary>
public class DefaultFallbackBannerTests
{
    /// <summary>The content-hash prefix of the retired slate-stripes banner (git: e2bb4df).</summary>
    private const string RetiredSlateStripesHash = "226ea8ea24d78cd8";

    private static string DefaultsFolder => Path.Combine(RepoRoot(), "src", "Croft.Web", "wwwroot", "branding-default");

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

    [Fact]
    public async Task ASiteWithNoBrandingFolder_ServesTheBlackWatchBannerAsSvg()
    {
        // The factory is held and disposed with the client: a dropped factory's host lives to process exit.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Blog:BrandingPath", "");
            builder.ConfigureServices(services => services.AddHttpClient("BlogApi")
                .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler()));
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/branding/fallback-banner.svg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Black Watch", "the default banner is the Black Watch tartan, credited in the file");
        body.Should().NotContain("#334155", "the slate stripes are gone");
    }

    [Fact]
    public void TheDefaultBannersContentHash_DiffersFromTheRetiredFiles_SoCachedCopiesUpdate()
    {
        var service = BrandingService.Load(null, DefaultsFolder);

        var hash = service.Current.FileHashes["fallback-banner.svg"];
        hash.Should().NotBe(RetiredSlateStripesHash);
        service.ThemeCss.Should().Contain($"/branding/fallback-banner.svg?v={hash}",
            "theme.css carries the new hash, so browsers fetch the new banner");
    }

    [Fact]
    public void TheFile_IsSmallSelfContainedAndCreditedAndBuiltFromTheThreeColours()
    {
        var path = Path.Combine(DefaultsFolder, "fallback-banner.svg");
        var svg = File.ReadAllText(path);

        new FileInfo(path).Length.Should().BeLessThanOrEqualTo(8 * 1024);
        svg.Should().MatchRegex("(?i)Black Watch").And.Contain("277", "the credit names the sett and its Register reference");
        svg.Should().ContainEquivalentOf("1739").And.ContainEquivalentOf("tartanflag.com",
            "the credit names where the thread count comes from");
        svg.Should().ContainEquivalentOf("DB/44 K4 DB4 K4 DB4 K32 G32 K4 G32 K32 DB32 K4 DB/4");
        foreach (var colour in new[] { "#1C2841", "#0E0E10", "#1F4730" })
        {
            svg.Should().ContainEquivalentOf(colour);
        }

        svg.Should().NotContainEquivalentOf("<script").And.NotContainEquivalentOf("<style")
            .And.NotContainEquivalentOf("href=").And.NotContainEquivalentOf("url(http")
            .And.NotContainEquivalentOf("<image").And.NotContainEquivalentOf("@import");
        svg.Should().Contain("<mask", "the weft runs under a twill mask, as a Black Watch tartan is built");
    }
}
