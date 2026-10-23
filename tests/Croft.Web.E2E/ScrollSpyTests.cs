using System.Text;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Clicking an "On this page" entry must take the reader to that section and leave that entry as
/// the TOC's active one: the clicked link becomes <c>aria-current="true"</c> and stays the only
/// active entry after the jump. A TOC click is a jump scroll, not a scroll — it can land with no
/// heading inside the scroll-spy's observed band, so the marking must also be recomputed on
/// scroll itself (measured: with ~150-word sections a TOC click left the clicked entry unmarked,
/// the previous entry staying active).
///
/// Outcome assertions (rule 7): the clicked entry's aria-current state, the page actually jumping
/// to the section, and the entry still being the active one afterwards — all awaited on concrete
/// conditions (the attribute change), never on sleeps.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ScrollSpyTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task PostPage_ClickingATocEntryFarDownTheList_MarksItActive_AndItStaysAfterTheJump()
    {
        // Long-post shape per brief 07d's fixture: 12 h2 sections of ~150 words each, published
        // through the admin editor (the suite's fixture rule), run-keyed like every other test.
        var slug = await PublishPostAsync($"R7E2 Spy {RunId}", BodyWithSections(12));

        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".post-rail .toc")).ToBeVisibleAsync();

        // Section 10 of 12: far enough down the list that the jump lands deep in the post.
        var link = Page.Locator(".post-rail .toc__link", new PageLocatorOptions { HasText = "Section 10" });
        var href = await link.GetAttributeAsync("href") ?? string.Empty;
        var fragment = href[(href.IndexOf('#') + 1)..];
        await link.ClickAsync();

        // The jump itself must have happened: the URL carries the entry's fragment and the page
        // moved down the post.
        await Page.WaitForURLAsync(url => url.EndsWith($"#{fragment}"), new PageWaitForURLOptions { Timeout = 15000 });
        var scrollY = await Page.EvaluateAsync<double>("() => window.scrollY");
        Assert.That(scrollY, Is.GreaterThan(0), $"clicking the TOC entry must scroll the page down the post, scrollY was {scrollY:0.#}px.");

        // The outcome a user cares about: the clicked entry becomes the active one (awaited on the
        // concrete attribute change) …
        await Expect(link).ToHaveAttributeAsync("aria-current", "true");

        // The click sets aria-current at once; the regression this test is for (the previous entry
        // staying active) shows in the spy's own handler, which runs on the animation frame after
        // the jump's scroll event. Let that scroll event and the frames its handlers queue land
        // before the one-shot read below, or the read can see the state from before the spy ran.
        await Page.EvaluateAsync(
            """
            async () => {
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            }
            """);

        // … and stays it after the jump: still the clicked entry, the only active one, with the
        // reader in the section it names (its heading at or above the top third of the viewport).
        var state = await Page.EvaluateAsync<string>(
            @"() => {
                const links = [...document.querySelectorAll('.post-rail .toc__link')];
                return JSON.stringify({
                    currentIdx: links.findIndex(l => l.getAttribute('aria-current') === 'true'),
                    currentCount: links.filter(l => l.getAttribute('aria-current') === 'true').length,
                    clickedTop: document.querySelectorAll('.markdown-body h2')[9].getBoundingClientRect().top,
                    viewportHeight: window.innerHeight
                });
            }");
        using var doc = JsonDocument.Parse(state!);
        var r = doc.RootElement;
        Assert.That(r.GetProperty("currentCount").GetInt32(), Is.EqualTo(1),
            $"exactly one rail TOC entry must be active after the jump, was {r.GetProperty("currentCount").GetInt32()}.");
        Assert.That(r.GetProperty("currentIdx").GetInt32(), Is.EqualTo(9),
            "the clicked entry (Section 10, index 9) must stay the active one after the jump.");
        Assert.That(r.GetProperty("clickedTop").GetDouble(), Is.InRange(-1, r.GetProperty("viewportHeight").GetDouble() * 0.3 + 1),
            $"the page must have jumped to the clicked section: heading top {r.GetProperty("clickedTop").GetDouble():0.#}px.");
    }

    /// <summary>An intro plus N h2 sections of the brief's ~150 words each (6 × 26 words) — a body
    /// tall enough that a TOC jump lands deep in the post.</summary>
    private static string BodyWithSections(int sections)
    {
        var body = new StringBuilder();
        body.Append($"Fixture intro {RunId} with enough words to start the page comfortably.\n\n");
        for (var n = 1; n <= sections; n++)
        {
            body.Append($"## Section {n}\n\n");
            for (var repeat = 0; repeat < 6; repeat++)
            {
                body.Append(Sentence).Append(". ");
            }

            body.Append("\n\n");
        }

        return body.ToString();
    }

    /// <summary>
    /// Publishes a post through the API as the mock admin (one request; the shape of the post is
    /// what these tests measure, so the editor and its IdP login add nothing but time).
    /// </summary>
    private async Task<string> PublishPostAsync(string title, string body)
    {
        var published = await PublishAsync(title, body);
        return published.Slug;
    }
}
