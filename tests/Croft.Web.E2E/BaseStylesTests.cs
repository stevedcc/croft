namespace Croft.Web.E2E;

/// <summary>
/// Brief 09i: the reading pages load no MudBlazor stylesheet, so the base rules app.css carries
/// (what MudBlazor's reset used to give every page) are what keep the shell's geometry and type.
/// The computed values that matter are asserted on a static page, so a regression fails CI and
/// not only the manual screenshot gate.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class BaseStylesTests : WebTest
{
    [Test]
    public async Task StaticPage_KeepsTheBaseRulesAppCssCarries()
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const body = getComputedStyle(document.body);
                const shell = getComputedStyle(document.querySelector('.shell'));
                const button = getComputedStyle(document.querySelector('button.theme-switch, button.menu-button'));
                return JSON.stringify({
                    margin: body.margin, fontSize: body.fontSize, lineHeight: body.lineHeight,
                    letterSpacing: body.letterSpacing, boxSizing: shell.boxSizing,
                    buttonFont: button.fontFamily, bodyFont: body.fontFamily, buttonPosition: button.position });
            }");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var v = doc.RootElement;

        Assert.That(v.GetProperty("margin").GetString(), Is.EqualTo("0px"), "body margin (the UA's 8px would shift the whole shell)");
        Assert.That(v.GetProperty("boxSizing").GetString(), Is.EqualTo("border-box"), "shell elements size with border-box");
        Assert.That(v.GetProperty("fontSize").GetString(), Is.EqualTo("14px"), "body font size");
        Assert.That(v.GetProperty("lineHeight").GetString(), Is.EqualTo("20.02px"), "body line height 1.43");
        Assert.That(v.GetProperty("buttonFont").GetString(), Is.EqualTo(v.GetProperty("bodyFont").GetString()),
            "shell buttons inherit the site's UI font, not the browser's Arial");
        Assert.That(v.GetProperty("buttonPosition").GetString(), Is.EqualTo("relative"), "shell buttons are positioned");
    }
}
