using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts.Social;
using Croft.Web.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 12b item 1: the discussion section under a post. Static markup, rendered from the API's
/// <see cref="PostDiscussionDto"/>; the API sanitised the content, so it is rendered as it is.
/// Names and texts are invented.
/// </summary>
public class DiscussionSectionTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private const string Rel = "nofollow ugc noopener";

    private readonly FakeTimeProvider _clock = new(Now);

    public DiscussionSectionTests() => Services.AddSingleton<TimeProvider>(_clock);

    private static DiscussionReplyDto Reply(
        string id, SocialPlatform platform = SocialPlatform.Mastodon, DateTimeOffset? at = null,
        string content = "<p>Hello there</p>", string? warning = null, bool media = false,
        string? avatar = null, string name = "Nora Example", string handle = "nora@social.example",
        params DiscussionReplyDto[] answers) =>
        new(platform, id, handle, name, $"https://social.example/@{id}", $"acct-{id}", avatar,
            at ?? Now.AddHours(-1), $"https://social.example/@nora/{id}", content, warning, media, answers);

    private static PostDiscussionDto Discussion(
        int count, string? mastodon = "https://social.example/@owner/1", string? bluesky = "https://bsky.example/profile/owner/post/3k",
        params DiscussionReplyDto[] replies) => new(mastodon, bluesky, count, replies);

    private IRenderedComponent<DiscussionSection> RenderSection(PostDiscussionDto? discussion) =>
        Render<DiscussionSection>(p => p.Add(x => x.Discussion, discussion).Add(x => x.Slug, "a-post"));

    [Fact]
    public void NoDiscussion_OrNoLinks_RendersNothing()
    {
        RenderSection(null).Markup.Should().BeEmpty("an unknown or unpublished post has no section");
        RenderSection(Discussion(0, mastodon: null, bluesky: null)).Markup.Should().BeEmpty(
            "no linked announcement (or Social:Enabled off) means no section at all");
    }

    [Fact]
    public void LinkedPostWithoutReplies_ShowsTheSection_AndNoRepliesYet()
    {
        var cut = RenderSection(Discussion(0));

        cut.Find("section#discussion h2#discussion-heading").TextContent.Should().Be("Discussion");
        cut.Markup.Should().Contain("No replies yet");
        cut.FindAll("ol").Should().BeEmpty();
        cut.FindAll(".discussion__count").Should().BeEmpty("zero replies show the 'No replies yet' text, not a count line");
    }

    [Fact]
    public void TheSection_HasItsAccessibilityAttributes()
    {
        var cut = RenderSection(Discussion(1, replies: Reply("a")));

        var section = cut.Find("section");
        section.Id.Should().Be("discussion");
        section.HasAttribute("data-nosnippet").Should().BeTrue("the replies stay out of search snippets");
        var labelledBy = section.GetAttribute("aria-labelledby");
        labelledBy.Should().Be("discussion-heading");
        cut.Find($"#{labelledBy}").TagName.Should().Be("H2");
    }

    [Theory]
    [InlineData(1, "1 reply")]
    [InlineData(7, "7 replies")]
    public void TheCount_IsSingularOrPlural(int count, string expected)
    {
        var cut = RenderSection(Discussion(count, replies: Reply("a")));

        cut.Find(".discussion__count").TextContent.Trim().Should().Be(expected);
    }

    [Fact]
    public void TwoTopLevelReplies_RenderInTheGivenOrder_WithPlatformHandlePermalinkAndRel()
    {
        var first = Reply("m1", SocialPlatform.Mastodon, name: "Nora Example", handle: "nora@social.example");
        var second = Reply("b1", SocialPlatform.Bluesky, name: "Bo Sample", handle: "bo.sample.example");

        var cut = RenderSection(Discussion(2, replies: [first, second]));

        var items = cut.FindAll("ol.discussion__list > li > article");
        items.Should().HaveCount(2);
        items[0].TextContent.Should().Contain("Nora Example").And.Contain("@nora@social.example").And.Contain("Mastodon");
        items[1].TextContent.Should().Contain("Bo Sample").And.Contain("@bo.sample.example").And.Contain("Bluesky");

        var view0 = items[0].QuerySelector("a.discussion-reply__view")!;
        view0.TextContent.Should().Be("View on Mastodon");
        view0.GetAttribute("href").Should().Be(first.Permalink);
        view0.GetAttribute("rel").Should().Be(Rel);
        var view1 = items[1].QuerySelector("a.discussion-reply__view")!;
        view1.TextContent.Should().Be("View on Bluesky");
        view1.GetAttribute("href").Should().Be(second.Permalink);
        view1.GetAttribute("rel").Should().Be(Rel);

        var author = items[0].QuerySelector("a.discussion-reply__name")!;
        author.GetAttribute("href").Should().Be(first.AuthorUrl);
        author.GetAttribute("rel").Should().Be(Rel);
    }

    [Fact]
    public void AnswersNestAsOrderedLists_ThreeLevelsDeep()
    {
        var grandchild = Reply("c", name: "Level Three");
        var child1 = Reply("b1", name: "Level Two A", answers: grandchild);
        var child2 = Reply("b2", name: "Level Two B");
        var top = Reply("a", name: "Level One", answers: [child1, child2]);

        var cut = RenderSection(Discussion(4, replies: top));

        cut.FindAll("ol").Should().HaveCount(3, "the top-level list, the answers of the top reply and the answer of its first answer");
        cut.FindAll("ol.discussion__list > li > ol > li > ol > li > article").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Level Three");
        var second = cut.FindAll("ol.discussion__list > li > ol > li > article");
        second.Should().HaveCount(2);
        second[0].TextContent.Should().Contain("Level Two A");
        second[1].TextContent.Should().Contain("Level Two B");
    }

    [Fact]
    public void ContentIsRenderedAsItIs_NotEncodedAgain()
    {
        var cut = RenderSection(Discussion(1, replies: Reply("a", content: "<p>One <a href=\"https://x.example/\" rel=\"nofollow ugc noopener\">link</a><br>two</p>")));

        var content = cut.Find(".discussion-reply__content");
        content.QuerySelector("p a")!.GetAttribute("href").Should().Be("https://x.example/");
        content.QuerySelector("br").Should().NotBeNull();
    }

    [Fact]
    public void AContentWarning_FoldsTheContentInADetailsElement()
    {
        var cut = RenderSection(Discussion(1, replies: Reply("a", content: "<p>The hidden text</p>", warning: "Spoilers")));

        var details = cut.Find("details");
        details.QuerySelector("summary")!.TextContent.Should().Be("CW: Spoilers");
        details.HasAttribute("open").Should().BeFalse();
        details.QuerySelector(".discussion-reply__content")!.TextContent.Should().Contain("The hidden text");
    }

    [Fact]
    public void WithoutAContentWarning_ThereIsNoDetails()
    {
        RenderSection(Discussion(1, replies: Reply("a"))).FindAll("details").Should().BeEmpty();
    }

    [Theory]
    [InlineData(SocialPlatform.Mastodon, "Mastodon")]
    [InlineData(SocialPlatform.Bluesky, "Bluesky")]
    public void AReplyWithMedia_SaysSoAndLinksTheOriginal(SocialPlatform platform, string name)
    {
        var reply = Reply("a", platform, media: true);

        var cut = RenderSection(Discussion(1, replies: reply));

        var note = cut.Find(".discussion-reply__media");
        note.TextContent.Should().Contain("Has media: view on " + name);
        var link = note.QuerySelector("a")!;
        link.GetAttribute("href").Should().Be(reply.Permalink);
        link.GetAttribute("rel").Should().Be(Rel);
    }

    [Fact]
    public void AReplyWithoutMedia_HasNoMediaNote() =>
        RenderSection(Discussion(1, replies: Reply("a"))).FindAll(".discussion-reply__media").Should().BeEmpty();

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(119, "just now")]
    [InlineData(5 * 60, "5 minutes ago")]
    [InlineData(3 * 3600, "3 hours ago")]
    [InlineData(2 * 86400, "2 days ago")]
    [InlineData(40 * 86400, "12 Sep 2026")]
    public void TheTime_IsRelative_WithTheAbsoluteTimeInTitleAndDatetime(int secondsAgo, string expected)
    {
        var at = Now.AddSeconds(-secondsAgo);

        var cut = RenderSection(Discussion(1, replies: Reply("a", at: at)));

        var time = cut.Find("time");
        time.TextContent.Should().Be(expected);
        time.GetAttribute("datetime").Should().Be(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        time.GetAttribute("title").Should().Be(at.UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC");
    }

    [Theory]
    [InlineData(2 * 60, "2 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(86400, "1 day ago")]
    public void TheTime_UsesTheSingularForOne(int secondsAgo, string expected) =>
        RenderSection(Discussion(1, replies: Reply("a", at: Now.AddSeconds(-secondsAgo))))
            .Find("time").TextContent.Should().Be(expected);

    // Item 2: avatars. The image is ours (/social/avatar/<hash>.webp), decorative, with a fixed size.
    [Fact]
    public void AnAvatarHash_RendersTheSameOriginImage()
    {
        var hash = new string('b', 64);

        var cut = RenderSection(Discussion(1, replies: Reply("a", avatar: hash)));

        var img = cut.Find("article img");
        img.GetAttribute("src").Should().Be($"/social/avatar/{hash}.webp");
        img.GetAttribute("width").Should().Be("48");
        img.GetAttribute("height").Should().Be("48");
        img.GetAttribute("alt").Should().Be(string.Empty, "decorative: the name is next to it");
        img.GetAttribute("loading").Should().Be("lazy");
        img.GetAttribute("decoding").Should().Be("async");
        cut.FindAll(".discussion-reply__initial").Should().BeEmpty();
    }

    [Theory]
    [InlineData("nora example", "nora@social.example", "N")]
    [InlineData("", "nora@social.example", "N")]
    [InlineData("  ", "bo.sample.example", "B")]
    [InlineData("\u00e9lan", "x@y.example", "\u00c9")]
    public void WithoutAnAvatar_TheInitialOfTheNameOrTheHandleIsShown(string name, string handle, string expected)
    {
        var cut = RenderSection(Discussion(1, replies: Reply("a", avatar: null, name: name, handle: handle)));

        var initial = cut.Find("article .discussion-reply__initial");
        initial.TextContent.Should().Be(expected);
        initial.GetAttribute("aria-hidden").Should().Be("true");
        cut.FindAll("article img").Should().BeEmpty();
    }

    // Item 3: the reply actions.
    [Fact]
    public void WithBothLinks_BothActionsShow()
    {
        var cut = RenderSection(Discussion(0));

        var bluesky = cut.Find("a.discussion__reply-bluesky");
        bluesky.TextContent.Should().Be("Reply on Bluesky");
        bluesky.GetAttribute("href").Should().Be("https://bsky.example/profile/owner/post/3k");
        bluesky.GetAttribute("rel").Should().Be("noopener");

        var form = cut.Find("form.mastodon-reply");
        form.GetAttribute("method").Should().Be("get");
        form.GetAttribute("action").Should().Be("/discussion/mastodon/reply");
        form.QuerySelector("input[type=hidden][name=post]")!.GetAttribute("value").Should().Be("a-post", "the slug, not the id");
        var input = form.QuerySelector("input[name=server]")!;
        input.GetAttribute("autocomplete").Should().Be("on");
        input.GetAttribute("inputmode").Should().Be("url");
        input.GetAttribute("spellcheck").Should().Be("false");
        input.GetAttribute("autocapitalize").Should().Be("none");
        cut.Find($"label[for={input.Id}]").TextContent.Should().Be("Your Mastodon server");
        form.QuerySelector("button[type=submit]")!.TextContent.Trim().Should().Be("Reply");
        cut.Markup.Should().Contain("Reply on Mastodon");
        cut.FindAll("script").Should().BeEmpty();
    }

    [Fact]
    public void WithoutABlueskyLink_NoBlueskyAction_ButTheMastodonFormStays()
    {
        var cut = RenderSection(Discussion(0, bluesky: null));

        cut.FindAll("a.discussion__reply-bluesky").Should().BeEmpty();
        cut.Find("form.mastodon-reply");
    }

    [Fact]
    public void WithoutAMastodonLink_NoMastodonForm_ButTheBlueskyActionStays()
    {
        var cut = RenderSection(Discussion(0, mastodon: null));

        cut.FindAll("form").Should().BeEmpty();
        cut.Find("a.discussion__reply-bluesky");
    }
}
