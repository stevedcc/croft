using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Croft.Api.Tests;

/// <summary>
/// The Posts.ViewCount / Posts.ShowInNavigation contract. Step one (brief 17c): the model no longer maps
/// either column and ViewCount got a default so the model's INSERTs succeed. Step two (brief 18c, issue 15):
/// the DropRetiredPostColumns migration drops both once no pod older than 0.7.4 runs.
/// </summary>
public class RetiredPostColumnsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed record ColumnInfo(string Name, string? Default);

    private BlogDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    [Fact]
    public async Task AnInsertThroughTheModel_SucceedsWithoutNamingTheRetiredColumns()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync();
        var id = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        db.Posts.Add(new Post
        {
            Id = id, Slug = "retired-columns", Title = "Retired columns", MarkdownContent = "body",
            Status = PostStatus.Draft, CreatedAt = now, UpdatedAt = now, ContentEditedAt = now,
            AuthorId = "a", AuthorName = "A",
        });

        await db.SaveChangesAsync();

        var row = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id);
        row.Slug.Should().Be("retired-columns");
        row.Status.Should().Be(PostStatus.Draft);
    }

    [Fact]
    public async Task TheModel_DoesNotSelectTheRetiredColumns()
    {
        await using var db = CreateContext();

        var sql = db.Posts.ToQueryString();

        sql.Should().NotContain("\"ViewCount\"").And.NotContain("\"ShowInNavigation\"");
    }

    [Fact]
    public async Task AfterMigrating_TheRetiredColumnsAreGone()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync();

        var columns = await RetiredColumnsOfPosts(db);

        columns.Should().BeEmpty();
    }

    [Fact]
    public async Task RollingBackTheDrop_PutsTheColumnsBackWithTheirDefaults()
    {
        await using var db = CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();

        try
        {
            await migrator.MigrateAsync("20261026174607_SocialThreadCounts");

            var columns = await RetiredColumnsOfPosts(db);

            columns.Select(c => c.Name).Should().BeEquivalentTo("ViewCount", "ShowInNavigation");
            columns.Single(c => c.Name == "ViewCount").Default.Should().Be("0");
            columns.Single(c => c.Name == "ShowInNavigation").Default.Should().Be("false");
        }
        finally
        {
            await migrator.MigrateAsync();
        }
    }

    private static Task<List<ColumnInfo>> RetiredColumnsOfPosts(BlogDbContext db) =>
        db.Database
            .SqlQuery<ColumnInfo>($"""
                SELECT column_name AS "Name", column_default AS "Default" FROM information_schema.columns
                WHERE table_name = 'Posts' AND column_name IN ('ViewCount', 'ShowInNavigation')
                """)
            .ToListAsync();
}
