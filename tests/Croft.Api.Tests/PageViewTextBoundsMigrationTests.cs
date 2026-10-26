using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 17c: PageViews.Referrer and UserAgent become varchar(2048) / varchar(512). The migration
/// first trims rows an older pod may have written longer, then bounds the columns.
/// </summary>
public class PageViewTextBoundsMigrationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string PreviousMigration = "20261026133825_RetiredPostColumnsDefault";
    private const string ThisMigration = "20261026134409_PageViewTextBounds";

    private sealed record ViewRow(string IpHash, string? Referrer, string? UserAgent);

    private sealed record ColumnLength(string Name, int? Length);

    [Fact]
    public async Task Up_TrimsLongValuesToTheirBounds_LeavesExactOnesAlone_AndBoundsTheColumns()
    {
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var postId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount", "Kind", "ReadingMinutes", "NavigationPlacement", "NavigationOrder")
            VALUES ({postId}, 'bounds', 'bounds', 'body', 1, {now}, {now}, 'a', 'A', 0, 0, 1, 'None', 0)
            """);
        await InsertViewAsync(db, postId, "long", new string('r', 3000), new string('u', 600), now);
        await InsertViewAsync(db, postId, "exact", new string('e', 2048), new string('x', 512), now);
        await InsertViewAsync(db, postId, "none", null, null, now);

        await migrator.MigrateAsync(ThisMigration);

        var rows = await db.Database
            .SqlQuery<ViewRow>($"""SELECT "IpHash", "Referrer", "UserAgent" FROM "PageViews" """)
            .ToListAsync();
        rows.Single(r => r.IpHash == "long").Should().Be(new ViewRow("long", new string('r', 2048), new string('u', 512)));
        rows.Single(r => r.IpHash == "exact").Should().Be(new ViewRow("exact", new string('e', 2048), new string('x', 512)));
        rows.Single(r => r.IpHash == "none").Should().Be(new ViewRow("none", null, null));

        var lengths = await db.Database
            .SqlQuery<ColumnLength>($"""
                SELECT column_name AS "Name", character_maximum_length AS "Length" FROM information_schema.columns
                WHERE table_name = 'PageViews' AND column_name IN ('Referrer', 'UserAgent')
                """)
            .ToListAsync();
        lengths.Single(c => c.Name == "Referrer").Length.Should().Be(2048);
        lengths.Single(c => c.Name == "UserAgent").Length.Should().Be(512);
    }

    private static Task<int> InsertViewAsync(BlogDbContext db, Guid postId, string ipHash, string? referrer, string? userAgent, DateTime now) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PageViews" ("Id", "PostId", "Timestamp", "Day", "IpHash", "Referrer", "UserAgent")
            VALUES ({Guid.CreateVersion7()}, {postId}, {now}, {DateOnly.FromDateTime(now)}, {ipHash}, {referrer}, {userAgent})
            """);
}
