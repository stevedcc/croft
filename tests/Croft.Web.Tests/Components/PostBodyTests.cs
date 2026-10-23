using Croft.Web.Components;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.4: the post body (spec §7.3) — renders the markdown produced by the Task R3.1
/// IMarkdownRenderer (ruling R3-4-b: the composing page parses once and shares the
/// RenderedMarkdown with the TOC) and triggers highlight.js after each render (spec §5.4:
/// "re-run after each interactive render").
/// </summary>
public class PostBodyTests : BlogBunitContext
{
    [Fact]
    public void PostBody_RendersRenderedHtml()
    {
        var cut = Render<PostBody>(p => p
            .Add(x => x.Markdown, new RenderedMarkdown("<p>Rendered <em>once</em>.</p>", [])));

        var body = cut.Find(".markdown-body");
        body.InnerHtml.Should().Contain("<em>once</em>", "the renderer's HTML is the body's content");
    }

    [Fact]
    public void PostBody_TriggersHighlightAfterRender()
    {
        JSInterop.SetupVoid("blogPost.highlightAll");

        Render<PostBody>(p => p
            .Add(x => x.Markdown, new RenderedMarkdown("<pre><code>var x = 1;</code></pre>", [])));

        JSInterop.VerifyInvoke("blogPost.highlightAll", calledTimes: 1);
    }

    [Fact]
    public void PostBody_TriggersImageCaptions_AfterRender()
    {
        // Spec §5.4 content styling: images get their caption from the Markdown title — the
        // wrapping happens in post.js (ruling R3-4-d), so the body must trigger it after render.
        JSInterop.SetupVoid("blogPost.decorateImages");

        Render<PostBody>(p => p
            .Add(x => x.Markdown, new RenderedMarkdown(
                "<p><img src=\"/branding/fallback-banner.svg\" alt=\"A pattern\" title=\"Fallback banner\"></p>",
                [])));

        JSInterop.VerifyInvoke("blogPost.decorateImages", calledTimes: 1);
    }
}
