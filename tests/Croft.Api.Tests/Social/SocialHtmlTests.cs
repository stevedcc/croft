using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Croft.Api.Social;
using static Croft.Api.Tests.Fixtures.Social.BlueskyThreadBuilder;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 2, sanitising: the HTML that is stored is safe to render as it is (only p, br and a), whatever the platform sent.
/// Exact expected output, so a change in what is allowed shows up here.
/// </summary>
public class SocialHtmlTests
{
    private const string Rel = "rel=\"nofollow ugc noopener\"";

    [Theory]
    [InlineData("<p>Plain text</p>", "<p>Plain text</p>")]
    [InlineData("<p>One<br>two<br />three</p>", "<p>One<br>two<br>three</p>")]
    [InlineData("<p>One</p><p>Two</p>", "<p>One</p><p>Two</p>")]
    [InlineData("", "")]
    public void Mastodon_KeepsParagraphsAndLineBreaks(string input, string expected) =>
        SocialHtml.FromMastodon(input).Should().Be(expected);

    [Fact]
    public void Mastodon_ALinkKeepsOnlyItsHttpsHref_AndGetsTheRel()
    {
        SocialHtml.FromMastodon("""<p><a href="https://example.com/a?x=1&y=2" class="u-url" target="_blank" onclick="evil()">go</a></p>""")
            .Should().Be($"<p><a href=\"https://example.com/a?x=1&amp;y=2\" {Rel}>go</a></p>");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("/relative/path")]
    [InlineData("//example.com/protocol-relative")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("")]
    public void Mastodon_ALinkWithAnyOtherHref_LosesTheHref_ButKeepsItsText(string href)
    {
        var html = SocialHtml.FromMastodon($"<p><a href=\"{href}\">text</a></p>");

        html.Should().Be($"<p><a {Rel}>text</a></p>");
    }

    [Fact]
    public void Mastodon_AnHttpLinkIsKept()
    {
        SocialHtml.FromMastodon("""<p><a href="http://example.com/">plain http</a></p>""")
            .Should().Be($"<p><a href=\"http://example.com/\" {Rel}>plain http</a></p>");
    }

    [Fact]
    public void Mastodon_AMentionAndAHashtag_BecomeOrdinaryLinks()
    {
        const string html = """<p><span class="h-card"><a href="https://social.example/@owner" class="u-url mention">@<span>owner</span></a></span> <a href="https://social.example/tags/blog" class="mention hashtag" rel="tag">#<span>blog</span></a></p>""";

        SocialHtml.FromMastodon(html).Should().Be(
            $"<p><a href=\"https://social.example/@owner\" {Rel}>@owner</a> <a href=\"https://social.example/tags/blog\" {Rel}>#blog</a></p>");
    }

    [Fact]
    public void Mastodon_AScriptElement_IsRemovedWithItsContent()
    {
        SocialHtml.FromMastodon("<p>Before<script>alert('x')</script>After</p>").Should().Be("<p>BeforeAfter</p>");
    }

    [Fact]
    public void Mastodon_AStyleElement_IsRemovedWithItsContent()
    {
        SocialHtml.FromMastodon("<p>Text</p><style>p { display:none }</style>").Should().Be("<p>Text</p>");
    }

    [Fact]
    public void Mastodon_EveryAttributeIsDropped()
    {
        SocialHtml.FromMastodon("""<p class="x" style="color:red" id="y" onclick="evil()" onmouseover="evil()" data-a="b">Text</p>""")
            .Should().Be("<p>Text</p>");
    }

    [Fact]
    public void Mastodon_NestedDivsAndSpans_AreUnwrappedToTheirText()
    {
        SocialHtml.FromMastodon("""<div class="a"><span style="x">Some <b>bold</b> and <i>italic</i> <div><span>nested</span></div> text</span></div>""")
            .Should().Be("Some bold and italic nested text");
    }

    [Fact]
    public void Mastodon_AnImage_IsRemoved()
    {
        SocialHtml.FromMastodon("""<p>Look <img src="https://example.com/x.png" onerror="evil()" alt="x"> here</p>""")
            .Should().Be("<p>Look  here</p>");
    }

    [Fact]
    public void Mastodon_OtherElementsAreUnwrapped_NotKept()
    {
        SocialHtml.FromMastodon("<p><iframe src=\"https://example.com\"></iframe><object data=\"x\">fallback</object><svg><text>vector</text></svg></p>")
            .Should().Be("<p>fallbackvector</p>");
    }

    [Fact]
    public void Mastodon_AFormAndItsInputs_AreUnwrappedToTheirText()
    {
        SocialHtml.FromMastodon("<form action=\"/x\"><input value=\"v\" name=\"n\">typed<button onclick=\"evil()\">press</button></form>")
            .Should().Be("typedpress");
    }

    [Fact]
    public void Mastodon_EscapedMarkupStaysText_AndIsEscapedAgain()
    {
        SocialHtml.FromMastodon("<p>Use &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;quotes&quot;</p>")
            .Should().Be("<p>Use &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;quotes&quot;</p>");
    }

    [Fact]
    public void Mastodon_CustomEmojiStayText()
    {
        SocialHtml.FromMastodon("<p>Nice :blobcat: post</p>").Should().Be("<p>Nice :blobcat: post</p>");
    }

    [Fact]
    public void Mastodon_BrokenMarkup_IsRepairedNotPassedThrough()
    {
        SocialHtml.FromMastodon("<p>Open <a href=\"https://example.com\">link <b>bold").Should()
            .Be($"<p>Open <a href=\"https://example.com/\" {Rel}>link bold</a></p>");
    }

    [Fact]
    public void Bluesky_PlainText_IsEscaped_InOneParagraph()
    {
        SocialHtml.FromBluesky("1 < 2 & <b>bold</b> \"q\"", default).Should()
            .Be("<p>1 &lt; 2 &amp; &lt;b&gt;bold&lt;/b&gt; &quot;q&quot;</p>");
    }

    [Theory]
    [InlineData("one\ntwo", "<p>one<br>two</p>")]
    [InlineData("one\r\ntwo", "<p>one<br>two</p>")]
    [InlineData("one\n\ntwo", "<p>one</p><p>two</p>")]
    [InlineData("one\n \n\ntwo\nthree", "<p>one</p><p>two<br>three</p>")]
    [InlineData("", "")]
    public void Bluesky_LineBreaksBecomeBr_AndBlankLinesBecomeParagraphs(string text, string expected) =>
        SocialHtml.FromBluesky(text, default).Should().Be(expected);

    [Fact]
    public void Bluesky_LinkMentionAndTagFacets_BecomeLinks()
    {
        const string text = "hi @owner.example read https://example.com/x #Blog";
        var facets = Facets(
            (text, "@owner.example", MentionFeature("did:plc:ownerowner00000000000000")),
            (text, "https://example.com/x", LinkFeature("https://example.com/x")),
            (text, "#Blog", TagFeature("Blog")));

        SocialHtml.FromBluesky(text, facets).Should().Be(
            $"<p>hi <a href=\"https://bsky.app/profile/did:plc:ownerowner00000000000000\" {Rel}>@owner.example</a> read "
            + $"<a href=\"https://example.com/x\" {Rel}>https://example.com/x</a> "
            + $"<a href=\"https://bsky.app/hashtag/Blog\" {Rel}>#Blog</a></p>");
    }

    [Fact]
    public void Bluesky_FacetOffsetsAreUtf8Bytes_NotUtf16Indices()
    {
        // Umlauts (2 UTF-8 bytes, 1 UTF-16 unit each) and an emoji (4 bytes, 2 units) in front of the link: the byte offset
        // and the UTF-16 index of the link's start differ, so reading the offsets as indices wraps the wrong text.
        const string text = "Grüße aus Köln 😀 https://example.com/ok";
        var facets = Facets((text, "https://example.com/ok", LinkFeature("https://example.com/ok")));

        SocialHtml.FromBluesky(text, facets).Should().Be(
            $"<p>Grüße aus Köln 😀 <a href=\"https://example.com/ok\" {Rel}>https://example.com/ok</a></p>");
    }

    [Fact]
    public void Bluesky_ATagWithNonAsciiCharacters_IsEscapedInItsUrl()
    {
        const string text = "#Café";
        var facets = Facets((text, "#Café", TagFeature("Café")));

        SocialHtml.FromBluesky(text, facets).Should().Be($"<p><a href=\"https://bsky.app/hashtag/Caf%C3%A9\" {Rel}>#Café</a></p>");
    }

    [Fact]
    public void Bluesky_ALinkFacetWithAnUnsafeUri_StaysPlainText()
    {
        const string text = "click here now";
        var facets = Facets((text, "here", LinkFeature("javascript:alert(1)")));

        SocialHtml.FromBluesky(text, facets).Should().Be("<p>click here now</p>");
    }

    [Fact]
    public void Bluesky_BrokenFacets_AreIgnored_NotCrashed()
    {
        const string text = "Grüße https://example.com/ok end";
        var ok = FacetNodes((text, "https://example.com/ok", LinkFeature("https://example.com/ok")))[0];
        var overlapping = JsonNode.Parse(ok.ToJsonString())!;
        overlapping["index"]!["byteStart"] = ok["index"]!["byteStart"]!.GetValue<int>() + 2;
        var beyondTheEnd = JsonNode.Parse(ok.ToJsonString())!;
        beyondTheEnd["index"]!["byteEnd"] = 9999;
        var backwards = JsonNode.Parse(ok.ToJsonString())!;
        backwards["index"]!["byteStart"] = 12;
        backwards["index"]!["byteEnd"] = 10;
        var insideAnUmlaut = JsonNode.Parse(ok.ToJsonString())!; // byte 3 is the middle of "ü"
        insideAnUmlaut["index"]!["byteStart"] = 3;
        insideAnUmlaut["index"]!["byteEnd"] = 5;
        var noFeatures = new JsonObject { ["index"] = new JsonObject { ["byteStart"] = 0, ["byteEnd"] = 2 }, ["features"] = new JsonArray() };
        var notAnObject = JsonValue.Create("x");

        var facets = new JsonArray(ok.DeepClone(), overlapping, beyondTheEnd, backwards, insideAnUmlaut, noFeatures, notAnObject);

        SocialHtml.FromBluesky(text, Element(facets.ToJsonString())).Should().Be(
            $"<p>Grüße <a href=\"https://example.com/ok\" {Rel}>https://example.com/ok</a> end</p>");
    }

    [Fact]
    public void Bluesky_FacetsComeInAnyOrder()
    {
        const string text = "a https://example.com/1 b https://example.com/2";
        var facets = Facets(
            (text, "https://example.com/2", LinkFeature("https://example.com/2")),
            (text, "https://example.com/1", LinkFeature("https://example.com/1")));

        SocialHtml.FromBluesky(text, facets).Should().Be(
            $"<p>a <a href=\"https://example.com/1\" {Rel}>https://example.com/1</a> b <a href=\"https://example.com/2\" {Rel}>https://example.com/2</a></p>");
    }

    [Fact]
    public void Bluesky_ALinkFacetCannotSpanALineBreak()
    {
        const string text = "one\n\ntwo";
        var facets = Facets((text, "one\n\ntwo", LinkFeature("https://example.com/")));

        SocialHtml.FromBluesky(text, facets).Should().Be("<p>one</p><p>two</p>");
    }

    private static JsonObject LinkFeature(string uri) => new() { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = uri };

    private static JsonObject MentionFeature(string did) => new() { ["$type"] = "app.bsky.richtext.facet#mention", ["did"] = did };

    private static JsonObject TagFeature(string tag) => new() { ["$type"] = "app.bsky.richtext.facet#tag", ["tag"] = tag };

    /// <summary>Facets for the <c>span</c> of <c>text</c> each entry names, with UTF-8 byte offsets as the platform sends them.</summary>
    private static System.Text.Json.JsonElement Facets(params (string Text, string Span, JsonObject Feature)[] spans) =>
        Element(new JsonArray([.. FacetNodes(spans)]).ToJsonString());

    private static JsonNode[] FacetNodes(params (string Text, string Span, JsonObject Feature)[] spans) =>
        [.. spans.Select(s =>
        {
            var start = Encoding.UTF8.GetByteCount(s.Text[..s.Text.IndexOf(s.Span, StringComparison.Ordinal)]);
            return (JsonNode)new JsonObject
            {
                ["index"] = new JsonObject { ["byteStart"] = start, ["byteEnd"] = start + Encoding.UTF8.GetByteCount(s.Span) },
                ["features"] = new JsonArray(s.Feature),
            };
        })];
}
