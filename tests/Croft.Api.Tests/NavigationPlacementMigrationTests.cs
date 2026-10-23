using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Croft.Api.Tests;

/// <summary>Brief 10f: the NavigationPlacement migration turns the old ShowInNavigation flag into
/// the placement column (true → Sidebar, false → None), keeps the order, and reverses cleanly.</summary>
public class NavigationPlacementMigrationTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    private const string PreviousMigration = "20261018091156_RecomputePostSummaries";
    private const string ThisMigration = "20261018114827_NavigationPlacement";

    [Fact]
    public async Task Up_MapsTrueToSidebar_AndFalseToNone_KeepingTheOrder()
    {
        var options = new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var db = new BlogDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);
        var shown = Guid.CreateVersion7();
        var hidden = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        await InsertAsync(db, shown, "shown", true, 4, now);
        await InsertAsync(db, hidden, "hidden", false, 9, now);

        await migrator.MigrateAsync(ThisMigration);

        var rows = await db.Database
            .SqlQuery<PlacementRow>($"""SELECT "Id", "NavigationPlacement" AS "Placement", "NavigationOrder" AS "Order" FROM "Posts" """)
            .ToListAsync();
        rows.Single(r => r.Id == shown).Placement.Should().Be("Sidebar");
        rows.Single(r => r.Id == shown).Order.Should().Be(4);
        rows.Single(r => r.Id == hidden).Placement.Should().Be("None");
        rows.Single(r => r.Id == hidden).Order.Should().Be(9);
    }

    [Fact]
    public async Task Up_KeepsTheOldColumn_SoPodsOfThePreviousReleaseKeepWorking()
    {
        // Expand-only: the rolling deployment runs old API pods beside new ones, and an old pod's
        // queries and inserts name ShowInNavigation.
        var options = new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var db = new BlogDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var shown = Guid.CreateVersion7();
        await InsertAsync(db, shown, "keep-shown", true, 1, DateTime.UtcNow);

        await migrator.MigrateAsync(ThisMigration);

        var flags = await db.Database
            .SqlQuery<FlagRow>($"""SELECT "Id", "ShowInNavigation" AS "Shown" FROM "Posts" WHERE "Id" = {shown}""")
            .ToListAsync();
        flags.Should().ContainSingle().Which.Shown.Should().BeTrue("the old column survives the migration, value intact");

        var oldPodInsert = Guid.CreateVersion7();
        await InsertAsync(db, oldPodInsert, "old-pod-insert", true, 2, DateTime.UtcNow);
        var placement = await db.Database
            .SqlQuery<PlacementRow>($"""SELECT "Id", "NavigationPlacement" AS "Placement", "NavigationOrder" AS "Order" FROM "Posts" WHERE "Id" = {oldPodInsert}""")
            .ToListAsync();
        placement.Single().Placement.Should().Be("None",
            "an old pod's insert (which does not know the new column) takes the column default");
    }

    [Fact]
    public async Task Down_MapsSidebarBackToTrue_AndEverythingElseToFalse()
    {
        var options = new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var db = new BlogDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(ThisMigration);
        var sidebar = Guid.CreateVersion7();
        var footer = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount", "NavigationPlacement")
            VALUES ({sidebar}, 'dn-sidebar', 'S', 'x', 0, {now}, {now}, 'a', 'A', 0, 'Sidebar'),
                   ({footer}, 'dn-footer', 'F', 'x', 0, {now}, {now}, 'a', 'A', 0, 'Footer')
            """);

        await migrator.MigrateAsync(PreviousMigration);

        var rows = await db.Database
            .SqlQuery<FlagRow>($"""SELECT "Id", "ShowInNavigation" AS "Shown" FROM "Posts" """)
            .ToListAsync();
        rows.Single(r => r.Id == sidebar).Shown.Should().BeTrue();
        rows.Single(r => r.Id == footer).Shown.Should().BeFalse();
        await migrator.MigrateAsync(ThisMigration);
    }

    private static Task<int> InsertAsync(BlogDbContext db, Guid id, string slug, bool show, int order, DateTime now) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount", "ShowInNavigation", "NavigationOrder")
            VALUES ({id}, {slug}, {slug}, 'x', 0, {now}, {now}, 'a', 'A', 0, {show}, {order})
            """);

    private sealed record PlacementRow(Guid Id, string Placement, int Order);

    private sealed record FlagRow(Guid Id, bool Shown);
}
