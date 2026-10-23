using AwesomeAssertions;
using Croft.Api.Models;
using Croft.Api.Social;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 2: stored rows become the nested replies a reader sees: oldest first, nested to depth 3, deeper ones flattened into level 3.</summary>
public class ReplyTreeTests
{
    private static readonly DateTime Start = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static SocialReply Row(string id, string? parent, int minutes, SocialPlatform platform = SocialPlatform.Mastodon) => new()
    {
        PostId = Guid.Empty,
        Platform = platform,
        ExternalId = id,
        ParentExternalId = parent,
        AuthorHandle = $"h-{id}",
        AuthorDisplayName = $"Name {id}",
        AuthorUrl = $"https://social.example/@{id}",
        AuthorAccountId = $"acct-{id}",
        AvatarHash = null,
        CreatedAt = Start.AddMinutes(minutes),
        Permalink = $"https://social.example/@{id}/1",
        ContentHtml = $"<p>Example reply text {id}.</p>",
        ContentWarning = null,
        HasMedia = false,
        FetchedAt = Start,
    };

    private static string Shape(IEnumerable<DiscussionReplyDto> replies) =>
        string.Join(",", replies.Select(r => r.Replies.Count == 0 ? r.ExternalId : $"{r.ExternalId}[{Shape(r.Replies)}]"));

    [Fact]
    public void NoRows_NoReplies()
    {
        var tree = ReplyTree.Build([]);

        tree.Replies.Should().BeEmpty();
        tree.Count.Should().Be(0);
    }

    [Fact]
    public void RepliesNestUnderTheirParents_OldestFirstAtEveryLevel()
    {
        var tree = ReplyTree.Build([
            Row("b", null, 20), Row("a", null, 10), Row("a2", "a", 40), Row("a1", "a", 30), Row("b1", "b", 25)]);

        Shape(tree.Replies).Should().Be("a[a1,a2],b[b1]");
        tree.Count.Should().Be(5, "the count is every reply in the tree below, nested ones included");
    }

    [Fact]
    public void ADepth5Chain_RendersAsThreeLevels_WithTheRestFlattenedInOrder()
    {
        var tree = ReplyTree.Build([
            Row("l1", null, 1), Row("l2", "l1", 2), Row("l3", "l2", 3), Row("l4", "l3", 4), Row("l5", "l4", 5)]);

        Shape(tree.Replies).Should().Be("l1[l2[l3,l4,l5]]", "levels 3, 4 and 5 sit side by side in level 3, oldest first");
        tree.Count.Should().Be(5);
        tree.Replies[0].Replies[0].Replies.Should().OnlyContain(r => r.Replies.Count == 0, "level 3 is the last level");
    }

    [Fact]
    public void DeepDescendantsOfOneLevel2Reply_AreFlattenedByTime_AcrossBranches()
    {
        var tree = ReplyTree.Build([
            Row("top", null, 1), Row("mid", "top", 2),
            Row("x", "mid", 10), Row("y", "mid", 30), Row("x1", "x", 20), Row("x2", "x1", 40), Row("y1", "y", 35)]);

        Shape(tree.Replies).Should().Be("top[mid[x,x1,y,y1,x2]]");
    }

    [Fact]
    public void ARowWhoseParentIsMissing_IsShownAtTheTopLevel_NeverLost()
    {
        var tree = ReplyTree.Build([Row("orphan", "gone", 5), Row("a", null, 1)]);

        Shape(tree.Replies).Should().Be("a,orphan");
        tree.Count.Should().Be(2);
    }

    [Fact]
    public void ARowsFieldsReachTheDto()
    {
        var row = Row("a", null, 5);
        row.AvatarHash = new string('a', 64);
        row.ContentWarning = "CW: example";
        row.HasMedia = true;

        var dto = ReplyTree.Build([row]).Replies.Single();

        dto.Should().BeEquivalentTo(new DiscussionReplyDto(
            SocialPlatform.Mastodon, "a", "h-a", "Name a", "https://social.example/@a", "acct-a", new string('a', 64),
            new DateTimeOffset(Start.AddMinutes(5)), "https://social.example/@a/1", "<p>Example reply text a.</p>", "CW: example", true, []));
    }
}
