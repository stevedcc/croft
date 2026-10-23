using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 12d item 1: the SocialAnnouncements migration turns announcing on for the posts that are still to be published (Draft, Scheduled) and
/// leaves it off for published posts and for every page, so republishing an old post does not announce it by surprise.
/// </summary>
public class SocialAnnouncementsMigrationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string PreviousMigration = "20261021154522_SocialReplies";
    private const string ThisMigration = "20261023064746_SocialAnnouncements";

    [Fact]
    public async Task Up_SwitchesAnnouncingOn_ForDraftAndScheduledPosts_AndOffForPublishedPostsAndPages()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var db = scratch.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var draft = Guid.CreateVersion7();
        var scheduled = Guid.CreateVersion7();
        var published = Guid.CreateVersion7();
        var draftPage = Guid.CreateVersion7();
        var publishedPage = Guid.CreateVersion7();
        await InsertAsync(db, draft, "draft", kind: 0, status: 0);
        await InsertAsync(db, scheduled, "scheduled", kind: 0, status: 1);
        await InsertAsync(db, published, "published", kind: 0, status: 2);
        await InsertAsync(db, draftPage, "draft-page", kind: 1, status: 0);
        await InsertAsync(db, publishedPage, "published-page", kind: 1, status: 2);

        await migrator.MigrateAsync(ThisMigration);

        var rows = await db.Database
            .SqlQuery<FlagRow>($"""SELECT "Id", "AnnounceMastodon" AS "Mastodon", "AnnounceBluesky" AS "Bluesky", "AnnouncementText" AS "Text" FROM "Posts" """)
            .ToListAsync();
        rows.Single(r => r.Id == draft).Should().Be(new FlagRow(draft, true, true, null), "a draft will be announced when it is published");
        rows.Single(r => r.Id == scheduled).Should().Be(new FlagRow(scheduled, true, true, null), "a scheduled post will be announced when it is published");
        rows.Single(r => r.Id == published).Should().Be(new FlagRow(published, false, false, null), "a published post must not announce itself when it is republished");
        rows.Single(r => r.Id == draftPage).Should().Be(new FlagRow(draftPage, false, false, null), "pages are never announced");
        rows.Single(r => r.Id == publishedPage).Should().Be(new FlagRow(publishedPage, false, false, null));
    }

    [Fact]
    public async Task Up_LeavesThePreviousReleasesInsertsOff()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var db = scratch.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(ThisMigration);

        var oldPodInsert = Guid.CreateVersion7();
        await InsertAsync(db, oldPodInsert, "old-pod", kind: 0, status: 0);

        var row = await db.Database
            .SqlQuery<FlagRow>($"""SELECT "Id", "AnnounceMastodon" AS "Mastodon", "AnnounceBluesky" AS "Bluesky", "AnnouncementText" AS "Text" FROM "Posts" WHERE "Id" = {oldPodInsert}""")
            .SingleAsync();
        row.Mastodon.Should().BeFalse("a pod of the previous release does not know the columns: its insert takes the default, which announces nothing");
        row.Bluesky.Should().BeFalse();
    }

    private static Task<int> InsertAsync(BlogDbContext db, Guid id, string slug, int kind, int status)
    {
        var now = DateTime.UtcNow;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Kind", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount", "ContentEditedAt", "ReadingMinutes", "NavigationPlacement", "NavigationOrder", "ShowInNavigation")
            VALUES ({id}, {slug}, {slug}, 'x', {kind}, {status}, {now}, {now}, 'a', 'A', 0, {now}, 1, 'None', 0, false)
            """);
    }

    private sealed record FlagRow(Guid Id, bool Mastodon, bool Bluesky, string? Text);
}
