using System.Text.Json.Nodes;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 2, Mastodon: what is kept from a status context before anything is stored. Pure functions over the
/// recorded answer's shape; one test per filter case of the spec.
/// </summary>
public class MastodonThreadParserTests
{
    private const string Root = MastodonThreadBuilder.RootId;
    private const string Server = "mastodon.example";

    private static IReadOnlyList<IngestedReply> Parse(MastodonThreadBuilder thread, SocialBlockSet? blocks = null) =>
        MastodonThreadParser.Parse(thread.ToJson(), Root, Server, blocks ?? SocialBlockSet.Empty);

    private static string[] Ids(IEnumerable<IngestedReply> replies) => [.. replies.Select(r => r.ExternalId)];

    [Fact]
    public void TheRecordedContext_KeepsItsFourReplies_WithTheirParents()
    {
        var replies = MastodonThreadParser.Parse(SocialFixtures.Read("mastodon-context.json"), Root, Server, SocialBlockSet.Empty);

        replies.Select(r => (r.ExternalId, r.ParentExternalId)).Should().Equal(
            ("110054913016854354", null),
            ("110054956705310518", null),
            ("110054985368879806", "110054956705310518"),
            ("110055006448893422", "110054985368879806"));
    }

    [Fact]
    public void AReplyIsMappedToItsStoredFields()
    {
        var thread = new MastodonThreadBuilder().Reply("100", Root, s =>
        {
            s["created_at"] = "2026-10-20T09:30:00.000Z";
            s["content"] = "<p>Hello <a href=\"https://example.com/x\">link</a></p>";
            ((JsonObject)s["account"]!)["display_name"] = "Reader One";
            ((JsonObject)s["account"]!)["acct"] = "reader@other.example";
            ((JsonObject)s["account"]!)["id"] = "account-1";
        });

        var reply = Parse(thread).Should().ContainSingle().Subject;

        reply.ExternalId.Should().Be("100");
        reply.ParentExternalId.Should().BeNull("an answer to the post itself has no parent reply");
        reply.AuthorHandle.Should().Be("reader@other.example");
        reply.AuthorDisplayName.Should().Be("Reader One");
        reply.AuthorAccountId.Should().Be("account-1");
        reply.AuthorUrl.Should().Be("https://social.example/@reader100");
        reply.AvatarSourceUrl.Should().Be("https://social.example/avatar/100");
        reply.CreatedAt.Should().Be(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero));
        reply.Permalink.Should().Be("https://social.example/url/100");
        reply.ContentHtml.Should().Be("<p>Hello <a href=\"https://example.com/x\" rel=\"nofollow ugc noopener\">link</a></p>");
        reply.ContentWarning.Should().BeNull();
        reply.HasMedia.Should().BeFalse();
    }

    [Fact]
    public void ALocalAccount_GetsTheServerAddedToItsHandle_AndANamelessOneItsUsername()
    {
        var thread = new MastodonThreadBuilder().Reply("100", Root, s =>
        {
            ((JsonObject)s["account"]!)["acct"] = "local";
            ((JsonObject)s["account"]!)["username"] = "local";
            ((JsonObject)s["account"]!)["display_name"] = "";
        });

        var reply = Parse(thread).Single();

        reply.AuthorHandle.Should().Be("local@mastodon.example", "a local account's acct has no domain");
        reply.AuthorDisplayName.Should().Be("local");
    }

    [Theory]
    [InlineData("public", true)]
    [InlineData("unlisted", true)]
    [InlineData("private", false)]
    [InlineData("direct", false)]
    public void OnlyPublicAndUnlistedRepliesAreKept(string visibility, bool kept)
    {
        var thread = new MastodonThreadBuilder().Reply("100", Root, s => s["visibility"] = visibility);

        Ids(Parse(thread)).Should().Equal(kept ? ["100"] : []);
    }

    [Fact]
    public void ARepliesWholeSubtree_GoesWithIt()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", Root, s => s["visibility"] = "private")
            .Reply("101", "100")
            .Reply("102", "101")
            .Reply("200", Root)
            .Reply("201", "200");

        Ids(Parse(thread)).Should().Equal("200", "201");
    }

    [Fact]
    public void AnAccountWithNoindex_IsDropped_AndOneWithIndexableFalseIsKept()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", Root, s => ((JsonObject)s["account"]!)["noindex"] = true)
            .Reply("101", "100")
            .Reply("200", Root, s => ((JsonObject)s["account"]!)["indexable"] = false)
            .Reply("300", Root, s => ((JsonObject)s["account"]!)["noindex"] = false);

        Ids(Parse(thread)).Should().Equal("200", "300");
    }

    [Theory]
    [InlineData("Spoilers ahead", false, "Spoilers ahead")]
    [InlineData("", true, "Sensitive content")]
    [InlineData("Spoilers ahead", true, "Spoilers ahead")]
    [InlineData("", false, null)]
    public void SensitiveContentIsMarked_WithItsSpoilerTextOrADefault(string spoiler, bool sensitive, string? expected)
    {
        var thread = new MastodonThreadBuilder().Reply("100", Root, s =>
        {
            s["spoiler_text"] = spoiler;
            s["sensitive"] = sensitive;
        });

        Parse(thread).Single().ContentWarning.Should().Be(expected);
    }

    [Fact]
    public void MediaAttachments_SetHasMedia_ButAreNeverKept()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", Root, s => s["media_attachments"] = new JsonArray(new JsonObject { ["id"] = "m1", ["type"] = "image", ["url"] = "https://social.example/media/1" }))
            .Reply("200", Root);

        var replies = Parse(thread);

        replies.Single(r => r.ExternalId == "100").HasMedia.Should().BeTrue();
        replies.Single(r => r.ExternalId == "200").HasMedia.Should().BeFalse();
        replies.Single(r => r.ExternalId == "100").ContentHtml.Should().NotContain("media");
    }

    [Fact]
    public void ABlockedReply_IsDropped_WithItsSubtree()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", Root)
            .Reply("101", "100")
            .Reply("200", Root);

        Ids(Parse(thread, new SocialBlockSet(["100"], []))).Should().Equal("200");
    }

    [Fact]
    public void ABlockedAccount_IsDropped_WithItsSubtree_ButOtherRepliesInTheThreadStay()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", Root, s => ((JsonObject)s["account"]!)["id"] = "troll")
            .Reply("101", "100")
            .Reply("102", Root, s => ((JsonObject)s["account"]!)["id"] = "troll")
            .Reply("200", Root);

        Ids(Parse(thread, new SocialBlockSet([], ["troll"]))).Should().Equal("200");
    }

    [Fact]
    public void TheOwnersOwnReplies_AreKept()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", Root)
            .Reply("101", "100", s => ((JsonObject)s["account"]!)["acct"] = "owner");

        Ids(Parse(thread)).Should().Equal("100", "101");
    }

    [Fact]
    public void AReplyWhoseParentIsNotInTheThread_IsDropped_ItCannotBeReachedFromThePost()
    {
        var thread = new MastodonThreadBuilder()
            .Reply("100", "999-missing")
            .Reply("200", Root);

        Ids(Parse(thread)).Should().Equal("200");
    }

    [Fact]
    public void ACycleThatNeverReachesThePost_IsDropped()
    {
        var thread = new MastodonThreadBuilder().Reply("100", "101").Reply("101", "100").Reply("200", Root);

        Ids(Parse(thread)).Should().Equal("200");
    }

    [Fact]
    public void AnIdTooLongToStore_IsDropped()
    {
        var thread = new MastodonThreadBuilder().Reply(new string('9', 513), Root).Reply("200", Root);

        Ids(Parse(thread)).Should().Equal("200");
    }

    [Fact]
    public void UnsafeUrls_AreReplacedByTheServersOwnAddresses()
    {
        var thread = new MastodonThreadBuilder().Reply("100", Root, s =>
        {
            s["url"] = "javascript:alert(1)";
            ((JsonObject)s["account"]!)["url"] = "data:text/html,x";
            ((JsonObject)s["account"]!)["avatar_static"] = "javascript:alert(2)";
        });

        var reply = Parse(thread).Single();

        reply.Permalink.Should().Be("https://mastodon.example/@reader100/100");
        reply.AuthorUrl.Should().Be("https://mastodon.example/@reader100");
        reply.AvatarSourceUrl.Should().BeNull("only an http(s) URL is ever downloaded");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"descendants":{}}""")]
    public void AnAnswerThatIsNotAContext_Fails(string json)
    {
        var act = () => MastodonThreadParser.Parse(json, Root, Server, SocialBlockSet.Empty);

        act.Should().Throw<SocialParseException>();
    }
}
