namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 8 (review M-2): with prefers-reduced-motion: reduce the phone drawer, the search
/// field and every other transition run for next to no time and animations do not repeat. The
/// measurement is the computed style the browser reports, and the same page without the preference
/// proves the test can tell the two apart.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReducedMotionTests : WebTest
{
    private static async Task<(double Drawer, double SearchLabel)> DurationsAsync(IBrowserContext context, string baseUrl)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/search");
        await WaitForInteractiveAsync(page);
        var json = await page.EvaluateAsync<string>(
            @"() => {
                const seconds = el => Math.max(...getComputedStyle(el).transitionDuration.split(',').map(parseFloat));
                return JSON.stringify({ drawer: seconds(document.querySelector('#sidebar-nav')), label: seconds(document.querySelector('.search-field__label')) });
            }");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        return (doc.RootElement.GetProperty("drawer").GetDouble(), doc.RootElement.GetProperty("label").GetDouble());
    }

    [Test]
    public async Task ReducedMotion_RemovesTheDrawerAndSearchTransitions_WithoutItTheyRun()
    {
        await using var reduced = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 },
            ReducedMotion = ReducedMotion.Reduce,
        });
        await using var normal = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 },
            ReducedMotion = ReducedMotion.NoPreference,
        });

        var without = await DurationsAsync(normal, BaseUrl);
        Assert.That(without.Drawer, Is.GreaterThan(0.1), "without the preference the drawer slides (0.2 s)");
        Assert.That(without.SearchLabel, Is.GreaterThan(0.1), "without the preference the search label animates");

        var with = await DurationsAsync(reduced, BaseUrl);
        Assert.That(with.Drawer, Is.LessThan(0.001), $"the drawer's transition lasts {with.Drawer} s with reduced motion");
        Assert.That(with.SearchLabel, Is.LessThan(0.001), $"the search label's transition lasts {with.SearchLabel} s with reduced motion");
    }
}
