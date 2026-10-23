namespace Croft.Web.E2E;

/// <summary>
/// Brief 10c item 4: Trusted Types on the reading pages, as far as the spike allows. The pages
/// ship <c>require-trusted-types-for 'script'; trusted-types 'none'</c> in
/// Content-Security-Policy-Report-Only, and the measurement here is what the browsers report:
/// every violation comes from <c>blazor.web.js</c> (its enhanced navigation parses the fetched
/// document with DOMParser, and Firefox also re-creates the JSON-LD script element), none from
/// the site's own scripts. A post with a code block loads with ZERO Trusted Types violations
/// (highlight.js's innerHTML sink is gone from post.js), and the theme switch, the contents panel
/// and the search add none either. Any engine (<c>BROWSER=…</c>); Category=Csp.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Csp")]
public class TrustedTypesTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;

    [Test]
    public async Task ReportOnlyHeader_IsOnTheReadingPage_AndNotOnTheEnforcingPolicy()
    {
        await using var response = await Page.APIRequest.GetAsync(BaseUrl);
        var headers = response.Headers;
        Assert.That(headers["content-security-policy-report-only"], Does.Contain("require-trusted-types-for 'script'"));
        Assert.That(headers["content-security-policy-report-only"], Does.Contain("trusted-types 'none'"));
        Assert.That(headers["content-security-policy"], Does.Not.Contain("trusted-types"),
            "blazor.web.js violates Trusted Types, so they are reported, never enforced");
    }

    [Test]
    public async Task APostWithCode_LoadsWithZeroTrustedTypesViolations_AndTheCodeIsStillHighlighted()
    {
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);

        await probe.StepAsync("post with a code block");
        await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await Expect(Page.Locator("pre code.hljs .hljs-keyword").First).ToBeAttachedAsync();
        Assert.That(await Page.Locator("pre code.hljs").First.TextContentAsync(), Does.Contain("var answer = 42;"),
            "the highlighted block still holds the code as text");

        await probe.DrainAsync();
        var trustedTypes = probe.TrustedTypesReports.ToList();
        Assert.That(trustedTypes, Is.Empty,
            $"loading a post must not touch a Trusted Types sink; got: {string.Join("; ", trustedTypes)}");
    }

    [Test]
    public async Task EveryTrustedTypesViolationOnTheReadingPages_ComesFromBlazorWebJs()
    {
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);

        await probe.StepAsync("post");
        await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);

        await probe.StepAsync("theme switch");
        var before = (await Page.Locator(".theme-switch").TextContentAsync())?.Trim();
        await Page.Locator(".theme-switch").ClickAsync();
        await Page.WaitForFunctionAsync(
            "prev => document.querySelector('.theme-switch').textContent.trim() !== prev", before);

        await probe.StepAsync("contents rail");
        await Page.Locator(".toc__link").First.ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains('#'));

        await probe.StepAsync("enhanced navigation to a category");
        await Page.EvaluateAsync("() => { window.__ttMarker = 'same document'; }");
        await Page.Locator("#sidebar-nav a[href^='/category/']").First.ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("/category/"));
        await WaitForInteractiveAsync(Page);
        Assert.That(await Page.EvaluateAsync<string?>("() => window.__ttMarker ?? null"), Is.EqualTo("same document"),
            "the category link was followed by an enhanced navigation (no new document)");
        await Page.WaitForFunctionAsync("() => (window.__cspProbeEvents ?? []).length > 0",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });

        await probe.StepAsync("search");
        await Page.GotoAsync($"{BaseUrl}/search");
        await WaitForInteractiveAsync(Page);
        await Page.Locator("#search-field").FillAsync(RunId);
        await Page.Locator("#search-field").PressAsync("Enter");
        await Page.WaitForURLAsync(url => url.Contains("q="));
        await WaitForInteractiveAsync(Page);

        await probe.DrainAsync();
        var trustedTypes = probe.TrustedTypesReports.ToList();
        Assert.That(trustedTypes, Is.Not.Empty,
            "enhanced navigation reaches a Trusted Types sink in blazor.web.js: the report-only policy is live; "
            + CspViolationProbe.FormatFindings(probe.Violations, probe.ConsoleErrors));
        Assert.That(trustedTypes.Where(v => !v.SourceFile.Contains("blazor.web", StringComparison.Ordinal)), Is.Empty,
            $"only blazor.web.js may violate Trusted Types; the others: {string.Join("; ", trustedTypes.Where(v => !v.SourceFile.Contains("blazor.web", StringComparison.Ordinal)))}");
        Assert.That(trustedTypes.All(v => v.Disposition == "report"), Is.True, "they are reported, not enforced");
    }

    private async Task EnsureFixturesAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            var category = await CreateCategoryAsync($"Tt Cat {RunId}");
            _categorySlug = category.Slug;
            var post = await PublishAsync($"Tt Post {RunId}", BodyWithSections(RunId, 4), null, category.Id);
            _postSlug = post.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
