using Microsoft.AspNetCore.Http;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Api.Services;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using System.Data.Common;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09b item 3: the two partial indexes land with the enum's stored values in their WHERE
/// clauses, the public list's SQL can be served by the published index (PublishedAt DESC, no
/// COALESCE), and the planner really picks each index for the query it exists for. The plans are
/// read with enable_seqscan off, so a small table cannot make the planner ignore an index.
/// </summary>
public class PostPartialIndexTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Migrating_CreatesTheTwoPartialIndexes_WithTheEnumStoredValuesInTheirFilters()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'Posts'
            """;

        var definitions = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                definitions[reader.GetString(0)] = reader.GetString(1);
        }

        definitions.Should().ContainKeys("IX_Posts_Scheduled_PublishedAt", "IX_Posts_Published_PublishedAt");

        var scheduled = Normalize(definitions["IX_Posts_Scheduled_PublishedAt"]);
        scheduled.Should().Contain("\"PublishedAt\"")
            .And.NotContain("\"PublishedAt\" DESC")
            .And.Contain($"\"Status\" = {(int)PostStatus.Scheduled}");

        var published = Normalize(definitions["IX_Posts_Published_PublishedAt"]);
        published.Should().Contain("\"PublishedAt\" DESC")
            .And.Contain("AND")
            .And.Contain($"\"Status\" = {(int)PostStatus.Published}")
            .And.Contain($"\"Kind\" = {(int)PostKind.Post}");
    }

    [Fact]
    public async Task PublicListSql_OrdersByPublishedAtDescending_WithNoCoalesce()
    {
        var capture = new SqlCommandCapture();
        await using var db = CreateDb(capture);

        var posts = CreatePostService(db);
        await posts.ListAsync(new PostListRequest(), publicOnly: true, includeAdminCounts: false);

        var clause = OrderByClause(capture.Commands);
        clause.Should().Contain("\"PublishedAt\" DESC");
        clause.Should().NotContain("COALESCE");
    }

    [Fact]
    public async Task AdminList_StillOrdersDraftsByCreatedAt()
    {
        var firstOfMonth = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = CreateDb())
        {
            db.Posts.AddRange(
                MakePost("idx3-old-draft", PostStatus.Draft, publishedAt: null, createdAt: firstOfMonth),
                MakePost("idx3-new-draft", PostStatus.Draft, publishedAt: null, createdAt: firstOfMonth.AddMonths(2)),
                // PublishedAt (April) outranks both drafts' CreatedAt even though this row is
                // the oldest by CreatedAt: pure CreatedAt-desc would put it last.
                MakePost("idx3-published", PostStatus.Published, publishedAt: firstOfMonth.AddMonths(3), createdAt: firstOfMonth.AddMonths(-1)));
            await db.SaveChangesAsync();
        }

        await using var readDb = CreateDb();
        var posts = CreatePostService(readDb);
        var result = await posts.ListAsync(new PostListRequest(PageSize: 50), publicOnly: false, includeAdminCounts: true);

        // "PublishedAt ?? CreatedAt" descending: the draft with the later CreatedAt ranks above
        // the older draft, and the published post's PublishedAt puts it above both.
        result.Items
            .Where(item => item.Slug.StartsWith("idx3-", StringComparison.Ordinal))
            .Select(item => item.Slug)
            .Should().Equal("idx3-published", "idx3-new-draft", "idx3-old-draft");
    }

    [Fact]
    public async Task Explain_SchedulersDuePostQuery_UsesTheScheduledPublishedAtIndex()
    {
        var capture = new SqlCommandCapture();
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options =>
        {
            options.UseNpgsql(fixture.ConnectionString);
            options.AddInterceptors(capture);
        });
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            db.Posts.Add(MakePost("sched-due-scheduled", PostStatus.Scheduled,
                publishedAt: DateTime.UtcNow.AddDays(1), createdAt: DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var scheduler = new ScheduledPublisherHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ScheduledPublisherHostedService>.Instance,
                TimeProvider.System,
                new ScheduledPublishWakeup(), TestCache.NewNotifier());
            await scheduler.PublishDuePostsAsync(db);
        }

        var dueQuery = capture.Commands.Single(command => command.Text.Contains("\"PublishedAt\" <="));
        var plan = await ExplainAsync(fixture.ConnectionString, dueQuery);
        plan.Should().Contain("IX_Posts_Scheduled_PublishedAt");
    }

    [Fact]
    public async Task Explain_PublicFirstPage_UsesThePublishedPublishedAtIndex()
    {
        await using (var db = CreateDb())
        {
            db.Posts.Add(MakePost("explain-one-published", PostStatus.Published,
                publishedAt: DateTime.UtcNow.AddDays(-2), createdAt: DateTime.UtcNow.AddDays(-3)));
            db.Posts.Add(MakePost("explain-two-published", PostStatus.Published,
                publishedAt: DateTime.UtcNow.AddDays(-1), createdAt: DateTime.UtcNow.AddDays(-2)));
            await db.SaveChangesAsync();
        }

        var capture = new SqlCommandCapture();
        await using var readDb = CreateDb(capture);
        var posts = CreatePostService(readDb);
        await posts.ListAsync(new PostListRequest(), publicOnly: true, includeAdminCounts: false);

        var itemsQuery = capture.Commands.Single(command => command.Text.Contains("ORDER BY"));
        var plan = await ExplainAsync(fixture.ConnectionString, itemsQuery);
        plan.Should().Contain("IX_Posts_Published_PublishedAt");
    }

    private static Post MakePost(string slug, PostStatus status, DateTime? publishedAt, DateTime createdAt) => new()
    {
        Slug = slug,
        Title = slug,
        MarkdownContent = "x",
        Status = status,
        Kind = PostKind.Post,
        PublishedAt = publishedAt,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        ContentEditedAt = createdAt,
        AuthorId = "idx3-author",
        AuthorName = "Index Test"
    };

    private BlogDbContext CreateDb(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<BlogDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    private static PostService CreatePostService(BlogDbContext db) => new(
        db,
        new SlugService(),
        new LocalFileStorage(Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}")),
        NullLogger<PostService>.Instance,
        TimeProvider.System,
        new ScheduledPublishWakeup(), TestCache.NewNotifier(), TestCache.NewCache(), new HttpContextAccessor(), new DateTimeNormalizer(TimeProvider.System), new NoSocialLinks(), NoSocialLinks.Options, NoSocialLinks.Outbox(db), NoSocialLinks.Counts(db));

    private static string OrderByClause(IEnumerable<SqlCommandCapture.CapturedCommand> commands)
    {
        var text = commands.Single(command => command.Text.Contains("ORDER BY")).Text;
        return text[text.IndexOf("ORDER BY", StringComparison.Ordinal)..];
    }

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static async Task<string> ExplainAsync(string connectionString, SqlCommandCapture.CapturedCommand command)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var set = connection.CreateCommand())
        {
            set.CommandText = "SET enable_seqscan = off";
            await set.ExecuteNonQueryAsync();
        }

        await using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN " + command.Text;
        foreach (var parameter in command.Parameters)
        {
            explain.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Type)
            {
                Value = parameter.Value ?? DBNull.Value
            });
        }

        var plan = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(0));

        return string.Join('\n', plan);
    }

    /// <summary>
    /// Keeps the text and typed parameters of every SQL reader command, so a captured query can
    /// be explained verbatim on a plain connection.
    /// </summary>
    private sealed class SqlCommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command) => Commands.Add(new CapturedCommand(
            command.CommandText,
            [.. command.Parameters.Cast<NpgsqlParameter>().Select(p => new CapturedParameter(p.ParameterName, p.NpgsqlDbType, p.Value))]));

        public sealed record CapturedCommand(string Text, IReadOnlyList<CapturedParameter> Parameters);

        public sealed record CapturedParameter(string Name, NpgsqlDbType Type, object? Value);
    }
}
