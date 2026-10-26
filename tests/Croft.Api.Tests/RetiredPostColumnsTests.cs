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
/// Brief 17c, step one of the Posts.ViewCount / Posts.ShowInNavigation contract: the model no longer
/// maps either column, the database gives ViewCount a default so the model's INSERTs succeed, and the
/// columns still exist for the pods of the previous release that read and write them during a rolling deploy.
/// </summary>
public class RetiredPostColumnsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed record RetiredColumns(long ViewCount, bool ShowInNavigation);

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

        var row = await db.Database
            .SqlQuery<RetiredColumns>($"""SELECT "ViewCount", "ShowInNavigation" FROM "Posts" WHERE "Id" = {id}""")
            .SingleAsync();
        row.Should().Be(new RetiredColumns(0, false));
    }

    [Fact]
    public async Task TheModel_DoesNotSelectTheRetiredColumns()
    {
        await using var db = CreateContext();

        var sql = db.Posts.ToQueryString();

        sql.Should().NotContain("\"ViewCount\"").And.NotContain("\"ShowInNavigation\"");
    }

    // Step two (the DropColumn migration) deletes this test.
    [Fact]
    public async Task TheRetiredColumns_StillExist_ForPodsOfThePreviousRelease()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync();

        var columns = await db.Database
            .SqlQuery<ColumnInfo>($"""
                SELECT column_name AS "Name", column_default AS "Default" FROM information_schema.columns
                WHERE table_name = 'Posts' AND column_name IN ('ViewCount', 'ShowInNavigation')
                """)
            .ToListAsync();

        columns.Select(c => c.Name).Should().BeEquivalentTo("ViewCount", "ShowInNavigation");
        columns.Single(c => c.Name == "ViewCount").Default.Should().Be("0");
    }
}
