namespace Croft.Web.E2E;

/// <summary>
/// The three sidebar footer items (Theme, Log in, Collapse) start at the same left edge, icon and
/// label, at 1440 px. app.css gives every &lt;button&gt; justify-content: center; Firefox applied it
/// to the two button items (the longer label started further left) while Chromium looked left
/// aligned, so the test runs in both engines. The engine is chosen in the test, not by the suite.
/// Break-it-once (recorded in the commit): the explicit flex-start removed; the Firefox case fails.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class SidebarFooterAlignmentTests : WebTest
{
    [TestCase("chromium")]
    [TestCase("firefox")]
    public async Task FooterItems_IconsAndLabels_ShareTheirLeftEdges(string engine)
    {
        var browserType = engine == "firefox" ? Playwright.Firefox : Playwright.Chromium;
        await using var browser = await browserType.LaunchAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var edges = await page.EvaluateAsync<double[][]>(
            """
            () => ['.theme-switch', '.sidebar-footer a.footer-item', '.collapse-toggle'].map(selector => {
                const item = document.querySelector(selector);
                const icon = Array.from(item.querySelectorAll('svg')).find(s => s.getBoundingClientRect().width > 0);
                return [icon.getBoundingClientRect().left, item.querySelector('.label').getBoundingClientRect().left];
            })
            """);

        Assert.That(edges, Has.Length.EqualTo(3));
        for (var column = 0; column < 2; column++)
        {
            var lefts = edges.Select(e => e[column]).ToArray();
            Assert.That(lefts.Max() - lefts.Min(), Is.LessThanOrEqualTo(1),
                $"{engine}: the footer items' {(column == 0 ? "icon" : "label")} left edges are {string.Join(", ", lefts)}");
        }
    }
}
