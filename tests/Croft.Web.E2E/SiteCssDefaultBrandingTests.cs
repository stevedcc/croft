namespace Croft.Web.E2E;

/// <summary>
/// Brief 18d item 3, on a site with NO branding folder (dev/run-e2e.sh --default-branding, which
/// the plain dev/run-e2e.sh also does after the main suite): there is no site.css, so no page
/// links one, and /branding/site.css is 404 (there is no framework default stylesheet).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("DefaultBranding")]
public class SiteCssDefaultBrandingTests : WebTest
{
    [Test]
    public async Task WithoutABrandingFolder_NoPageLinksSiteCss_AndTheFileIs404()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.That(await Page.Locator("link[rel=stylesheet]").CountAsync(), Is.GreaterThan(0),
            "the framework stylesheets are linked, so the page did load");
        Assert.That(await Page.Locator("link[href*='site.css']").CountAsync(), Is.EqualTo(0),
            "a site without site.css links none");

        var response = await Page.APIRequest.GetAsync($"{BaseUrl}/branding/site.css");
        Assert.That(response.Status, Is.EqualTo(404), "there is no framework default stylesheet");
    }
}
