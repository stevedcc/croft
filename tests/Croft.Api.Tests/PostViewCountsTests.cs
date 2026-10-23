using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13c item 4 (architecture finding 4, owner decision "a"): counting a page view upserts a row in
/// the narrow PostViewCounts table and never rewrites the Posts row; the migration carries the existing
/// totals over and keeps the old column for pods of the previous release.
/// </summary>
public class PostViewCountsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string IngestKey = "view-counts-ingest-key";
    private const string PreviousMigration = "20261018114827_NavigationPlacement";
    private const string ThisMigration = "20261019175325_PostViewCounts";

    private sealed record PostRowStamp(string Xmin, DateTime UpdatedAt, long LegacyViewCount);

    private sealed record CountRow(Guid PostId, long ViewCount);

    [Fact]
    public async Task CountingAView_IncrementsPostViewCounts_AndLeavesThePostsRowUntouched()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "View Counts Untouched");
        var before = await StampAsync(factory, post.Id);

        (await PostViewAsync(factory.CreateClient(), post.Id, "203.0.113.10")).EnsureSuccessStatusCode();
        (await PostViewAsync(factory.CreateClient(), post.Id, "203.0.113.11")).EnsureSuccessStatusCode();

        (await CountAsync(factory, post.Id)).Should().Be(2, "each distinct visitor adds one view to the narrow table");
        var after = await StampAsync(factory, post.Id);
        after.Xmin.Should().Be(before.Xmin, "no new row version of the Posts row: a view never rewrites the wide table");
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        after.LegacyViewCount.Should().Be(before.LegacyViewCount, "the old column is only for pods of the previous release");
    }

    [Fact]
    public async Task ConcurrentViewsFromDifferentAddresses_AreCountedExactly()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "View Counts Concurrent");
        const int Views = 30;

        var responses = await Task.WhenAll(Enumerable.Range(1, Views)
            .Select(i => PostViewAsync(factory.CreateClient(), post.Id, $"198.51.100.{i}")));

        responses.Should().OnlyContain(response => response.IsSuccessStatusCode);
        (await CountAsync(factory, post.Id)).Should().Be(Views, "the upsert increments atomically: no lost update, no duplicate key");
    }

    [Fact]
    public async Task TheSummaryAndTheAdminList_ReadTheNewTable()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "View Counts Reads");
        (await PostViewAsync(factory.CreateClient(), post.Id, "203.0.113.20")).EnsureSuccessStatusCode();
        (await PostViewAsync(factory.CreateClient(), post.Id, "203.0.113.21")).EnsureSuccessStatusCode();
        (await PostViewAsync(factory.CreateClient(), post.Id, "203.0.113.22")).EnsureSuccessStatusCode();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // A value in the old column that the reads must ignore.
            await scope.ServiceProvider.GetRequiredService<BlogDbContext>().Database
                .ExecuteSqlAsync($"""UPDATE "Posts" SET "ViewCount" = 5000 WHERE "Id" = {post.Id}""");
        }

        var summary = await admin.GetFromJsonAsync<AnalyticsSummaryDto>($"/api/analytics?postId={post.Id}");
        var list = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?searchTerm=View+Counts+Reads&pageSize=50");

        summary!.TotalViews.Should().Be(3);
        list!.Items.Single(item => item.Id == post.Id).ViewCount.Should().Be(3, "the admin list shows the new table's total");
    }

    [Fact]
    public async Task DeletingAPost_RemovesItsCountRow()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "View Counts Cascade");
        (await PostViewAsync(factory.CreateClient(), post.Id, "203.0.113.30")).EnsureSuccessStatusCode();
        (await CountAsync(factory, post.Id)).Should().Be(1);

        (await admin.DeleteAsync($"/api/posts/{post.Id}")).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.PostViewCounts.AnyAsync(v => v.PostId == post.Id)).Should().BeFalse("the foreign key cascades");
    }

    [Fact]
    public async Task TheMigration_CopiesTheExistingTotals_AndKeepsTheOldColumnForPodsOfTheLastRelease()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var db = scratch.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var viewed = Guid.CreateVersion7();
        var unviewed = Guid.CreateVersion7();
        await InsertPostAsync(db, viewed, "migration-viewed", 1234);
        await InsertPostAsync(db, unviewed, "migration-unviewed", 0);

        await migrator.MigrateAsync(ThisMigration);

        var rows = await db.Database.SqlQuery<CountRow>($"""SELECT "PostId", "ViewCount" FROM "PostViewCounts" """).ToListAsync();
        rows.Should().ContainSingle().Which.Should().Be(new CountRow(viewed, 1234), "the migration carries the totals over");
        var legacy = await db.Database.SqlQuery<long>($"""SELECT "ViewCount" AS "Value" FROM "Posts" WHERE "Id" = {viewed}""").ToListAsync();
        legacy.Should().Equal(1234L).And.HaveCount(1, "Posts.ViewCount stays, value intact: a pod of the previous release still reads and updates it");
    }

    private static async Task<PostDto> CreatePublishedPostAsync(HttpClient admin, string title)
    {
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "## Body"));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1)))).EnsureSuccessStatusCode();
        return post;
    }

    private static Task<HttpResponseMessage> PostViewAsync(HttpClient client, Guid postId, string visitorIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
        {
            Content = JsonContent.Create(new PageViewRequest(postId)),
        };
        request.Headers.Add("X-Visitor-IP", visitorIp);
        request.Headers.Add("X-Analytics-Key", IngestKey);
        return client.SendAsync(request);
    }

    private static async Task<long> CountAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return await db.PostViewCounts.AsNoTracking().Where(v => v.PostId == postId).Select(v => v.ViewCount).SingleOrDefaultAsync();
    }

    private static async Task<PostRowStamp> StampAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return (await db.Database.SqlQuery<PostRowStamp>(
            $"""SELECT xmin::text AS "Xmin", "UpdatedAt", "ViewCount" AS "LegacyViewCount" FROM "Posts" WHERE "Id" = {postId}""").ToListAsync()).Single();
    }

    private static async Task InsertPostAsync(BlogDbContext db, Guid id, string slug, long viewCount)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Posts" ("Id", "Slug", "Title", "MarkdownContent", "Status", "CreatedAt", "UpdatedAt", "AuthorId", "AuthorName", "ViewCount", "Kind", "ReadingMinutes", "ShowInNavigation", "NavigationPlacement", "NavigationOrder")
            VALUES ({id}, {slug}, {slug}, 'body', 1, {now}, {now}, 'a', 'A', {viewCount}, 0, 1, false, 'None', 0)
            """);
    }

    private BlogApiFactory CreateFactory()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configuration: new Dictionary<string, string?>
            {
                ["Analytics:IngestKey"] = IngestKey,
                ["Croft:Api:MaxConcurrentRequestsPerClient"] = "64",
            },
            configureServices: services => services.RemoveAll<IHostedService>());
    }
}
