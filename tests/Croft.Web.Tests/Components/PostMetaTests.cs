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

    [Fact]
    public void PostMeta_EditedOnALaterUtcDay_ShowsTheUpdatedDate()
    {
        // Brief 07g item 3's date rule: "· Updated <date>" when the content edit falls on a later
        // calendar day (UTC) than the publication.
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc))
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 5, 23, 59, 0, DateTimeKind.Utc)));

        cut.Find(".post-meta__when").TextContent.Should().Be("2 October 2026 · 7 min read · Updated 5 October 2026");
    }

    [Fact]
    public void PostMeta_EditedTheSameUtcDay_ShowsNoUpdatedDate()
    {
        // A later moment on the same UTC calendar day is not an "Updated" the reader needs.
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc))
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc)));

        var text = cut.Find(".post-meta__when").TextContent;
        text.Should().Be("2 October 2026 · 7 min read");
        text.Should().NotContain("Updated", "same-day edits are not advertised as updates");
    }

    [Fact]
    public void PostMeta_TheDayBoundary_IsTheUtcCalendarDay()
    {
        // 23:30 UTC on the 2nd → 00:30 UTC on the 3rd: a different UTC day, so "Updated" shows,
        // dated in UTC.
        var cut = Render<PostMeta>(p => p
            .Add(x => x.PublishedAt, new DateTime(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc))
            .Add(x => x.ReadingMinutes, 7)
            .Add(x => x.ContentEditedAt, new DateTime(2026, 10, 3, 0, 30, 0, DateTimeKind.Utc)));

        cut.Find(".post-meta__when").TextContent.Should().Be("2 October 2026 · 7 min read · Updated 3 October 2026");
    }
}
