using System.Text;
using System.Text.Json;
using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10a item 5: the Markdown box and the Preview stay side by side while the owner scrolls a
/// long post. From 960px the editor fits the viewport instead of growing with the Preview: the
/// Preview column is sticky and scrolls on its own, the Markdown box fills the viewport height,
/// and the Preview follows the box's top visible source line. Under 960px nothing changes.
/// Every assertion is measured geometry or a scroll position after a real scroll (rule 7).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class EditorLayoutTests : WebTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>200 sections of four source lines each: "## Heading n", a blank line, one short
    /// paragraph (no wrapping in the Markdown box), a blank line. Heading n is on line 4n-3.</summary>
    private static string LongBody()
    {
        var body = new StringBuilder();
        for (var n = 1; n <= 200; n++)
        {
            body.Append($"## Heading {n}\n\nParagraph {n} of the long post.\n\n");
        }

        return body.ToString();
    }

    private async Task<Guid> CreateLongPostAsync()
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        using var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest($"Long post {RunId}", LongBody()), token);
        return Guid.Parse(post.RootElement.GetProperty("id").GetString()!);
    }

    private async Task OpenEditorAsync(Guid id, int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.Locator(".post-edit__side .markdown-body h2").First).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");
    }

    [Test]
    public async Task At1440x900_TheMarkdownBoxFillsTheViewport_AndThePreviewStaysStickyAndScrollsOnItsOwn()
    {
        var id = await CreateLongPostAsync();
        await OpenEditorAsync(id, 1440, 900);

        var boxHeight = await Page.Locator(".md-editor__textarea").EvaluateAsync<double>("el => el.getBoundingClientRect().height");
        Assert.That(boxHeight, Is.GreaterThanOrEqualTo(600), "the Markdown box grows to about the viewport's height");

        await Page.EvaluateAsync("() => window.scrollTo(0, 400)");
        await Page.WaitForFunctionAsync("() => Math.round(window.scrollY) === 400");

        var json = await Page.EvaluateAsync<string>(
            """
            () => { const side = document.querySelector('.post-edit__side');
                const top = parseFloat(getComputedStyle(side).top);
                return JSON.stringify({ top, rectTop: side.getBoundingClientRect().top,
                    height: side.getBoundingClientRect().height, scrollHeight: side.scrollHeight,
                    clientHeight: side.clientHeight, viewport: window.innerHeight,
                    overflowY: getComputedStyle(side).overflowY }); }
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("rectTop").GetDouble(), Is.EqualTo(r.GetProperty("top").GetDouble()).Within(2),
            "after the page scrolled 400px the Preview column is parked at its sticky top");
        Assert.That(r.GetProperty("scrollHeight").GetDouble(), Is.GreaterThan(r.GetProperty("clientHeight").GetDouble()),
            "the Preview column scrolls internally");
        Assert.That(r.GetProperty("height").GetDouble(), Is.LessThanOrEqualTo(r.GetProperty("viewport").GetDouble() - r.GetProperty("top").GetDouble() + 1),
            "the Preview column is no taller than the viewport below its top gap");
        Assert.That(r.GetProperty("overflowY").GetString(), Is.EqualTo("auto"));
    }

    [Test]
    public async Task ScrollingTheMarkdownBox_BringsThePreviewToTheBlockOfItsTopLine()
    {
        var id = await CreateLongPostAsync();
        await OpenEditorAsync(id, 1440, 900);
        const int headingLine = 4 * 120 - 3;

        // Set the box's scroll so that the heading's source line is the top line (the lines do
        // not wrap: each is short), then wait on the Preview's own geometry.
        await Page.Locator(".md-editor__textarea").EvaluateAsync(
            """
            (el, line) => { const lineHeight = parseFloat(getComputedStyle(el).lineHeight);
                el.scrollTop = (line - 1) * lineHeight; }
            """, headingLine);

        await Page.WaitForFunctionAsync(
            """
            () => { const side = document.querySelector('.post-edit__side');
                const h = [...side.querySelectorAll('h2')].find(e => e.textContent.trim() === 'Heading 120');
                if (!h) return false;
                const offset = h.getBoundingClientRect().top - side.getBoundingClientRect().top;
                return offset >= -2 && offset < side.clientHeight / 3; }
            """,
            null, new PageWaitForFunctionOptions { Timeout = 500, PollingInterval = 20 });

        // One-way: scrolling the Preview by hand leaves the Markdown box where it is.
        var before = await Page.Locator(".md-editor__textarea").EvaluateAsync<double>("el => el.scrollTop");
        await Page.Locator(".post-edit__side").EvaluateAsync("el => { el.scrollTop = 0; }");
        await Page.WaitForFunctionAsync("() => document.querySelector('.post-edit__side').scrollTop === 0");
        var after = await Page.Locator(".md-editor__textarea").EvaluateAsync<double>("el => el.scrollTop");
        Assert.That(after, Is.EqualTo(before), "the Preview never drives the Markdown box");
    }

    [Test]
    public async Task At390_TheColumnsStack_AndTheBoxKeepsItsMinimumHeight()
    {
        var id = await CreateLongPostAsync();
        await OpenEditorAsync(id, 390, 800);

        var json = await Page.EvaluateAsync<string>(
            """
            () => { const side = document.querySelector('.post-edit__side');
                const form = document.querySelector('.post-edit__form');
                const box = document.querySelector('.md-editor__textarea');
                return JSON.stringify({ sideTop: side.getBoundingClientRect().top + window.scrollY,
                    formBottom: form.getBoundingClientRect().bottom + window.scrollY,
                    position: getComputedStyle(side).position, overflowY: getComputedStyle(side).overflowY,
                    boxHeight: box.getBoundingClientRect().height,
                    minHeight: parseFloat(getComputedStyle(box).minHeight) }); }
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("sideTop").GetDouble(), Is.GreaterThanOrEqualTo(r.GetProperty("formBottom").GetDouble() - 1),
            "the Preview stacks below the form");
        Assert.That(r.GetProperty("position").GetString(), Is.EqualTo("static"));
        Assert.That(r.GetProperty("overflowY").GetString(), Is.EqualTo("visible"));
        Assert.That(r.GetProperty("minHeight").GetDouble(), Is.EqualTo(320));
        Assert.That(r.GetProperty("boxHeight").GetDouble(), Is.GreaterThanOrEqualTo(320));
    }
}
