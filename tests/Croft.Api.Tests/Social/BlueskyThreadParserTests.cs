using System.Text.Json.Nodes;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;
using static Croft.Api.Tests.Fixtures.Social.BlueskyThreadBuilder;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 2, Bluesky: what is kept from a post thread. One test per filter case of the spec.</summary>
public class BlueskyThreadParserTests
{
    private static IReadOnlyList<IngestedReply> Parse(string json, SocialBlockSet? blocks = null) =>
        BlueskyThreadParser.Parse(json, blocks ?? SocialBlockSet.Empty);

    private static string[] Keys(IEnumerable<IngestedReply> replies) =>
        [.. replies.Select(r => r.ExternalId[(r.ExternalId.LastIndexOf('/') + 1)..])];

    [Fact]
    public void TheRecordedThread_KeepsWhatThePlatformsOptOutsAllow_AndNoSubtreeOfADroppedReply()
    {
        var replies = Parse(SocialFixtures.Read("bluesky-thread.json"));

        // The recording has 100 replies in 6 top-level trees: 22 by authors labelled !no-unauthenticated and 1 labelled porn
        // are dropped, and one of those has an answer that goes with it.
        replies.Should().HaveCount(76);
        replies.Select(r => r.ExternalId).Should().OnlyHaveUniqueItems();
        var ids = replies.Select(r => r.ExternalId).ToHashSet();
        replies.Where(r => r.ParentExternalId is not null).Should().OnlyContain(r => ids.Contains(r.ParentExternalId!),
            "every kept answer has its parent kept");
        replies.Count(r => r.ParentExternalId is null).Should().Be(4, "two of the six top-level trees are by !no-unauthenticated authors");
    }

    [Fact]
    public void TheRecordedThread_RootCarriesThePlatformsOwnCounts()
    {
        BlueskyThreadParser.ParseCounts(SocialFixtures.Read("bluesky-thread.json")).Should().Be(new SocialCounts(Likes: 63724, Reposts: 9545, Replies: 8675));
    }

    [Fact]
    public void ARootWithoutTheCountFields_YieldsNullCounts_NotZero()
    {
        var thread = Thread([Node("only")]);
        var root = System.Text.Json.Nodes.JsonNode.Parse(thread)!;
        var post = (JsonObject)root["thread"]!["post"]!;
        post.Remove("likeCount");
        post.Remove("repostCount");
        post.Remove("replyCount");

        BlueskyThreadParser.ParseCounts(root.ToJsonString()).Should().BeNull();
    }

    [Fact]
    public void AReplyIsMappedToItsStoredFields_AndTopLevelHasNoParent()
    {
        var child = Node("child");
        var top = Node("top", p =>
        {
            p["record"]!["text"] = "Hello there";
            p["record"]!["createdAt"] = "2026-10-20T10:00:00.000Z";
            p["indexedAt"] = "2026-10-20T10:01:00.000Z";
            ((JsonObject)p["author"]!)["handle"] = "reader.example";
            ((JsonObject)p["author"]!)["displayName"] = "Reader One";
        }, child);

        var replies = Parse(Thread([top]));

        replies.Select(r => r.ExternalId).Should().Equal("at://did:plc:top/app.bsky.feed.post/top", "at://did:plc:child/app.bsky.feed.post/child");
        var reply = replies[0];
        reply.ParentExternalId.Should().BeNull();
        reply.AuthorHandle.Should().Be("reader.example");
        reply.AuthorDisplayName.Should().Be("Reader One");
        reply.AuthorAccountId.Should().Be("did:plc:top");
        reply.AuthorUrl.Should().Be("https://bsky.app/profile/reader.example");
        reply.AvatarSourceUrl.Should().Be("https://social.example/avatar/top");
        reply.Permalink.Should().Be("https://bsky.app/profile/reader.example/post/top");
        reply.CreatedAt.Should().Be(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        reply.ContentHtml.Should().Be("<p>Hello there</p>");
        reply.ContentWarning.Should().BeNull();
        replies[1].ParentExternalId.Should().Be("at://did:plc:top/app.bsky.feed.post/top");
    }

    [Fact]
    public void ACreatedAtThePostClaimsInTheFuture_IsClampedToWhenTheServerSawIt()
    {
        var top = Node("top", p =>
        {
            p["record"]!["createdAt"] = "2099-01-01T00:00:00.000Z";
            p["indexedAt"] = "2026-10-20T10:01:00.000Z";
        });

        Parse(Thread([top])).Single().CreatedAt.Should().Be(new DateTimeOffset(2026, 10, 20, 10, 1, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AnAuthorWithNoDisplayName_ShowsTheirHandle_AndAnInvalidHandleShowsTheDid()
    {
        var plain = Node("plain", p => ((JsonObject)p["author"]!)["displayName"] = "");
        var invalid = Node("invalid", p => ((JsonObject)p["author"]!)["handle"] = "handle.invalid");

        var replies = Parse(Thread([plain, invalid]));

        replies[0].AuthorDisplayName.Should().Be("plain.example");
        replies[1].AuthorUrl.Should().Be("https://bsky.app/profile/did:plc:invalid");
        replies[1].Permalink.Should().Be("https://bsky.app/profile/did:plc:invalid/post/invalid");
    }

    [Fact]
    public void ADeletedReply_IsDropped_WithItsSubtree()
    {
        var gone = NotFound("at://did:plc:gone/app.bsky.feed.post/gone");
        var kept = Node("kept");

        Keys(Parse(Thread([gone, kept]))).Should().Equal("kept");
    }

    [Fact]
    public void ABlockedPost_IsDropped_WithItsSubtree()
    {
        var blocked = Blocked("at://did:plc:blocked/app.bsky.feed.post/blocked");
        var kept = Node("kept");

        Keys(Parse(Thread([blocked, kept]))).Should().Equal("kept");
    }

    [Fact]
    public void AnAuthorWhoAsksNotToBeShownToLoggedOutUsers_IsDropped_WithTheSubtree()
    {
        var hidden = Node("optout", p => ((JsonObject)p["author"]!)["labels"] = Labels("!no-unauthenticated"), Node("below"));
        var kept = Node("kept");

        Keys(Parse(Thread([hidden, kept]))).Should().Equal("kept");
    }

    [Fact]
    public void AReplyTheOwnerHidInBluesky_IsDropped_WithItsSubtree()
    {
        var hidden = Node("hidden", null, Node("below"));
        var kept = Node("kept");

        Keys(Parse(Thread([hidden, kept], UriOf(hidden)))).Should().Equal("kept");
    }

    [Theory]
    [InlineData("porn")]
    [InlineData("sexual")]
    [InlineData("nudity")]
    [InlineData("graphic-media")]
    public void APostLabelledAdult_IsDropped_WithItsSubtree_OnThePostTheAuthorOrASelfLabel(string label)
    {
        var onPost = Node("onpost", p => p["labels"] = Labels(label), Node("below1"));
        var onAuthor = Node("onauthor", p => ((JsonObject)p["author"]!)["labels"] = Labels(label), Node("below2"));
        var selfLabel = Node("selflabel", p => p["record"]!["labels"] = new JsonObject
        {
            ["$type"] = "com.atproto.label.defs#selfLabels",
            ["values"] = new JsonArray(new JsonObject { ["val"] = label }),
        }, Node("below3"));
        var kept = Node("kept");

        Keys(Parse(Thread([onPost, onAuthor, selfLabel, kept]))).Should().Equal("kept");
    }

    [Fact]
    public void OtherLabels_AndANegatedLabel_DoNotHideAReply()
    {
        var spam = Node("spam", p => p["labels"] = Labels("spam"));
        var negated = Node("negated", p => p["labels"] = new JsonArray(new JsonObject { ["val"] = "porn", ["neg"] = true }));

        Keys(Parse(Thread([spam, negated]))).Should().Equal("spam", "negated");
    }

    [Fact]
    public void ABlockedReplyAndABlockedAccount_AreDropped_WithTheirSubtrees()
    {
        var byReply = Node("byreply", null, Node("below1"));
        var byAccount = Node("byaccount", null, Node("below2"));
        var kept = Node("kept");

        var replies = Parse(Thread([byReply, byAccount, kept]),
            new SocialBlockSet([UriOf(byReply)], ["did:plc:byaccount"]));

        Keys(replies).Should().Equal("kept");
    }

    [Fact]
    public void TheOwnersOwnReplies_AreKept()
    {
        var own = Node("own", p => ((JsonObject)p["author"]!)["did"] = OwnerDid);

        Parse(Thread([own])).Should().ContainSingle().Which.AuthorAccountId.Should().Be(OwnerDid);
    }

    [Theory]
    [InlineData("app.bsky.embed.images#view", true)]
    [InlineData("app.bsky.embed.video#view", true)]
    [InlineData("app.bsky.embed.recordWithMedia#view", true)]
    [InlineData("app.bsky.embed.external#view", false)]
    [InlineData("app.bsky.embed.record#view", false)]
    public void HasMedia_IsSetForImagesVideoAndRecordWithMedia(string embedType, bool expected)
    {
        var node = Node("media", p => p["embed"] = new JsonObject { ["$type"] = embedType });

        Parse(Thread([node])).Single().HasMedia.Should().Be(expected);
    }

    [Fact]
    public void APostWithoutAnEmbed_HasNoMedia()
    {
        Parse(Thread([Node("plain")])).Single().HasMedia.Should().BeFalse();
    }

    [Fact]
    public void FacetsTurnIntoLinks_InTheContent()
    {
        var node = Node("faceted", p =>
        {
            p["record"]!["text"] = "see example";
            p["record"]!["facets"] = new JsonArray(new JsonObject
            {
                ["index"] = new JsonObject { ["byteStart"] = 4, ["byteEnd"] = 11 },
                ["features"] = new JsonArray(new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = "https://example.com/a" }),
            });
        });

        Parse(Thread([node])).Single().ContentHtml.Should().Be("<p>see <a href=\"https://example.com/a\" rel=\"nofollow ugc noopener\">example</a></p>");
    }

    [Fact]
    public void ARootThatIsGoneOrBlocked_YieldsNoReplies()
    {
        var json = new JsonObject { ["thread"] = NotFound("at://did:plc:x/app.bsky.feed.post/y") }.ToJsonString();

        Parse(json).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"thread":{"$type":"app.bsky.feed.defs#threadViewPost"}}""")]
    public void AnAnswerThatIsNotAThread_Fails(string json)
    {
        var act = () => Parse(json);

        act.Should().Throw<SocialParseException>();
    }
}
