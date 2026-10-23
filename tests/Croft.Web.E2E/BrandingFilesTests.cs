using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 07a item 3, on the example site (start-local.sh points Blog__BrandingPath at
/// branding.example): /branding/logo.svg serves the example's own bytes (not the framework
/// default), and a post without a banner shows the example's fallback banner — asserted as the
/// rendered background URL and an actual image load (a broken image must fail the test).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class BrandingFilesTests : WebTest
{
    [Test]
    public async Task LogoRequest_ReturnsTheExampleSitesBytes()
    {
        var response = await Page.APIRequest.GetAsync($"{BaseUrl}/branding/logo.svg");

        Assert.That(response.Status, Is.EqualTo(200), "the logo must be served from /branding/logo.svg");
        var served = await response.BodyAsync();
        var expected = await File.ReadAllBytesAsync(
            Path.Combine(RepoRoot(), "branding.example", "logo.svg"));
        Assert.That(served, Is.EqualTo(expected),
            "the example site supplies its own logo.svg, and that is what must be served (not the default).");
    }

    [Test]
    public async Task PostWithoutBanner_ShowsTheExampleFallbackBanner()
    {
        var slug = await CreatePublishedPostAsync();

        await Page.GotoAsync($"{BaseUrl}/post/{slug}");

        var fallback = Page.Locator(".banner-or-fallback__fallback").First;
        await fallback.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var background = await fallback.EvaluateAsync<string>("el => getComputedStyle(el).backgroundImage");
        var match = Regex.Match(background, "url\\(\"(?<url>[^\"]+)\"\\)");
        Assert.That(match.Success, Is.True, $"The fallback has no background image URL (got: {background}).");
        var url = match.Groups["url"].Value;
        Assert.That(new Uri(url).AbsolutePath, Is.EqualTo("/branding/fallback-banner.svg"),
            "a post without a banner shows the site's fallback banner");

        // The outcome the visitor gets: the banner image really loads (a broken background is
        // still "visible", so assert the decoded image, not the element).
        var naturalWidth = await Page.EvaluateAsync<int>(
            @"async (src) => {
                const img = new Image();
                img.src = src;
                await img.decode();
                return img.naturalWidth;
            }", url);
        Assert.That(naturalWidth, Is.GreaterThan(0), $"The fallback banner at {url} must decode to a real image.");

        var response = await Page.APIRequest.GetAsync(url);
        var served = await response.BodyAsync();
        var expected = await File.ReadAllBytesAsync(
            Path.Combine(RepoRoot(), "branding.example", "fallback-banner.svg"));
        Assert.That(served, Is.EqualTo(expected),
            "the example site supplies its own fallback-banner.svg, and that is what the post shows.");
    }

    /// <summary>A published post of this test's own with no banner, created through the admin
    /// editor (the suite's fixture rule); its slug comes from the public API.</summary>
    private async Task<string> CreatePublishedPostAsync()
    {
        await LoginAsync("admin@example.com", "password");

        await OpenNewPostEditorAsync();

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"Branding Fixture Post {Guid.NewGuid():N}");
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("A post without a banner shows the site's fallback banner.");
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        return await SlugForPostAsync(PostIdFromEditorUrl());
    }

    /// <summary>The repository root (where branding.example lives).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.That(dir, Is.Not.Null, "the tests run from inside the repository");
        return dir!.FullName;
    }
}
