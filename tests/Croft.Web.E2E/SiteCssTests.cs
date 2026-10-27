namespace Croft.Web.E2E;

/// <summary>
/// Brief 18d item 3, on the example site (start-local.sh points Blog__BrandingPath at
/// branding.example, which carries a site.css with one marker rule, <c>:root { --site-css:
/// "branding.example" }</c>). The stylesheet is requested and answered as CSS, the browser
/// APPLIED it (the marker custom property is computed on the root element: a link alone would
/// not prove that), and loading it raised no CSP violation. Measured on the home page and on a
/// post page the test publishes itself.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class SiteCssTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task HomePage_LoadsTheSitesStylesheetAndTheBrowserAppliesIt()
    {
        var probe = await CspViolationProbe.AttachAsync(Page);

        await AssertSiteCssAppliedAsync(probe, "home", BaseUrl);
    }

    [Test]
    public async Task PostPage_LoadsTheSitesStylesheetAndTheBrowserAppliesIt()
    {
        var post = await PublishAsync($"Site css post {RunId}", BodyWithSections(RunId, 1));
        var probe = await CspViolationProbe.AttachAsync(Page);

        await AssertSiteCssAppliedAsync(probe, "post", $"{BaseUrl}/post/{post.Slug}");
    }

    private async Task AssertSiteCssAppliedAsync(CspViolationProbe probe, string step, string url)
    {
        await probe.StepAsync(step);

        var response = await Page.RunAndWaitForResponseAsync(
            () => Page.GotoAsync(url),
            candidate => candidate.Url.Contains("/branding/site.css?v=", StringComparison.Ordinal));
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.That(response.Status, Is.EqualTo(200), "the stylesheet the page links is served");
        Assert.That(response.Headers["content-type"], Does.StartWith("text/css"),
            "it is served as CSS, or the browser would not apply it");
        var marker = await Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--site-css').trim()");
        Assert.That(marker, Is.EqualTo("\"branding.example\""),
            "the browser applied the example site's site.css: its marker rule is computed on the root");

        await probe.DrainAsync();
        Assert.That(probe.Violations, Is.Empty,
            CspViolationProbe.FormatFindings(probe.Violations, probe.ConsoleErrors));
    }
}
