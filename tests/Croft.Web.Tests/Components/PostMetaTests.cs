using Croft.Shared.Enums;
using Croft.Web.Components;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: the meta line — "2 October 2026 · 7 min read" (plan key rules: en-GB date), with an
/// optional status badge for admin/draft contexts (spec §7.3).
/// </summary>
public class PostMetaTests : BlogBunitContext
{
    [Fact]
    public void PostMeta_FormatsEnGbDateAndReadingTime()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc))
            .Add(x => x.ReadingMinutes, 7));

        cut.Find(".post-meta__when").TextContent.Should().Be("2 October 2026 · 7 min read");
    }

    [Fact]
    public void PostMeta_NullDate_RendersReadingTimeOnly()
    {
        // Review Focus 1: drafts have no publish date; no "null", no dangling separator.
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, null)
            .Add(x => x.ReadingMinutes, 3));

        var text = cut.Find(".post-meta__when").TextContent;
        text.Should().Be("3 min read");
        text.Should().NotContain("null").And.NotContain("·");
    }

    [Theory]
    [InlineData(PostStatus.Published, "Published")]
    [InlineData(PostStatus.Scheduled, "Scheduled")]
    [InlineData(PostStatus.Draft, "Draft")]
    public void PostMeta_WithStatus_RendersStatusBadge(PostStatus status, string text)
    {
        // PostMeta delegates to StatusBadge (spec §7.4), so the badge comes from that component —
        // its per-status class expectations live in StatusBadgeTests, where it is under test.
        // Here the behaviour is unchanged: shown for a status, with the status text.
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, null)
            .Add(x => x.ReadingMinutes, 1)
            .Add(x => x.Status, status));

        var badge = cut.Find(".status-badge");
        badge.TextContent.Should().Be(text);
    }

    [Fact]
    public void PostMeta_NoStatus_RendersNoBadge()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc))
            .Add(x => x.ReadingMinutes, 7));

        cut.FindAll(".status-badge").Should().BeEmpty();
    }

    private static readonly DateTime Published = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PostMeta_LongForm_NotEditedOnALaterDay_SaysPublishedOn()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.LongForm, true)
            .Add(x => x.PublishedAt, Published)
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc)));

        cut.Find(".post-meta__when").TextContent.Should().Be("Published on 18 October 2026 · 7 min read");
    }

    [Fact]
    public void PostMeta_LongForm_EditedOnALaterDay_SaysLastUpdatedOn()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.LongForm, true)
            .Add(x => x.PublishedAt, Published)
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 23, 0, 5, 0, DateTimeKind.Utc)));

        cut.Find(".post-meta__when").TextContent.Should()
            .Be("Published on 18 October 2026, last updated on 23 October 2026 · 7 min read");
    }

    [Fact]
    public void PostMeta_LongForm_WithoutReadingMinutes_DropsThatPart()
    {
        var edited = Render<PostMeta>(p => p
            .Add(x => x.LongForm, true)
            .Add(x => x.PublishedAt, Published)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 23, 0, 5, 0, DateTimeKind.Utc)));
        var plain = Render<PostMeta>(p => p
            .Add(x => x.LongForm, true)
            .Add(x => x.PublishedAt, Published));

        edited.Find(".post-meta__when").TextContent.Should().Be("Published on 18 October 2026, last updated on 23 October 2026");
        plain.Find(".post-meta__when").TextContent.Should().Be("Published on 18 October 2026");
    }

    [Fact]
    public void PostMeta_LongForm_WithoutAPublishDate_IsAsForADraft()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.LongForm, true)
            .Add(x => x.ReadingMinutes, 3));

        cut.Find(".post-meta__when").TextContent.Should().Be("3 min read");
    }

    [Fact]
    public void PostMeta_CardForm_NeverShowsUpdated_EvenWhenEditedOnALaterDay()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, Published)
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 23, 0, 5, 0, DateTimeKind.Utc)));

        cut.Find(".post-meta__when").TextContent.Should().Be("18 October 2026 · 7 min read");
    }

    [Fact]
    public void PostMeta_LongForm_TheDayBoundary_IsTheUtcCalendarDay()
    {
        var cut = Render<PostMeta>(p => p
            .Add(x => x.LongForm, true)
            .Add(x => x.PublishedAt, new DateTime(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc))
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 3, 0, 30, 0, DateTimeKind.Utc)));

        cut.Find(".post-meta__when").TextContent.Should()
            .Be("Published on 2 October 2026, last updated on 3 October 2026 · 7 min read");
    }
}
