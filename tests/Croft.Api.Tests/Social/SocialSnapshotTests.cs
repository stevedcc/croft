using AwesomeAssertions;
using Croft.Api.Models;
using Croft.Api.Social;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 3: the snapshot hash is stable (replies sorted by external id) and covers every stored field.</summary>
public class SocialSnapshotTests
{
    private static SocialReply Row(string id) => new()
    {
        PostId = Guid.NewGuid(),
        Platform = SocialPlatform.Mastodon,
        ExternalId = id,
        ParentExternalId = null,
        AuthorHandle = "h",
        AuthorDisplayName = "Name",
        AuthorUrl = "https://social.example/@h",
        AuthorAccountId = "acct",
        AvatarHash = new string('a', 64),
        CreatedAt = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
        Permalink = "https://social.example/@h/1",
        ContentHtml = "<p>text</p>",
        ContentWarning = null,
        HasMedia = false,
        FetchedAt = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void TheHash_IsSixtyFourHexCharacters()
    {
        SocialSnapshot.Hash([Row("1")]).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void TheHash_DoesNotDependOnTheOrderOfTheReplies()
    {
        SocialSnapshot.Hash([Row("1"), Row("2"), Row("3")]).Should().Be(SocialSnapshot.Hash([Row("3"), Row("1"), Row("2")]));
    }

    [Fact]
    public void TheHash_IgnoresWhenItWasFetched_AndWhichPostItIs()
    {
        var a = Row("1");
        var b = Row("1");
        b.FetchedAt = a.FetchedAt.AddDays(3);
        b.PostId = Guid.NewGuid();

        SocialSnapshot.Hash([a]).Should().Be(SocialSnapshot.Hash([b]));
    }

    [Fact]
    public void TheHash_OfNoReplies_IsDefined_AndDiffersFromOne()
    {
        SocialSnapshot.Hash([]).Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(SocialSnapshot.Hash([Row("1")]));
    }

    public static TheoryData<string, Action<SocialReply>> Changes => new()
    {
        { nameof(SocialReply.ExternalId), r => r.ExternalId = "other" },
        { nameof(SocialReply.ParentExternalId), r => r.ParentExternalId = "parent" },
        { nameof(SocialReply.AuthorHandle), r => r.AuthorHandle = "other" },
        { nameof(SocialReply.AuthorDisplayName), r => r.AuthorDisplayName = "Other" },
        { nameof(SocialReply.AuthorUrl), r => r.AuthorUrl = "https://social.example/@other" },
        { nameof(SocialReply.AuthorAccountId), r => r.AuthorAccountId = "other" },
        { nameof(SocialReply.AvatarHash), r => r.AvatarHash = null },
        { nameof(SocialReply.CreatedAt), r => r.CreatedAt = r.CreatedAt.AddSeconds(1) },
        { nameof(SocialReply.Permalink), r => r.Permalink = "https://social.example/@h/2" },
        { nameof(SocialReply.ContentHtml), r => r.ContentHtml = "<p>edited</p>" },
        { nameof(SocialReply.ContentWarning), r => r.ContentWarning = "CW" },
        { nameof(SocialReply.HasMedia), r => r.HasMedia = true },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void TheHash_CoversEveryStoredField(string field, Action<SocialReply> change)
    {
        var changed = Row("1");
        change(changed);

        SocialSnapshot.Hash([changed]).Should().NotBe(SocialSnapshot.Hash([Row("1")]), $"a change in {field} is a changed snapshot");
    }

    [Fact]
    public void ReplyFields_CannotBleedIntoEachOther()
    {
        var a = Row("1");
        a.AuthorHandle = "ab";
        a.AuthorDisplayName = "c";
        var b = Row("1");
        b.AuthorHandle = "a";
        b.AuthorDisplayName = "bc";

        SocialSnapshot.Hash([a]).Should().NotBe(SocialSnapshot.Hash([b]));
    }
}
