using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Croft.Api.Tests;

/// <summary>
/// ContentEditedAt, the reader-facing "content last edited" date (brief 07g item 3): it moves on
/// content edits only — create and update — and never on publish, unpublish or the scheduled
/// publisher's flip. UpdatedAt moves on all of those (the reason this field exists), so the tests
/// assert both halves: ContentEditedAt frozen, UpdatedAt moving.
/// </summary>
public class ContentEditedAtTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PostKind.Post)]
    [InlineData(PostKind.Page)]
    public async Task Create_SetsContentEditedAtToTheCreationTime(PostKind kind)
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, kind);

        created.ContentEditedAt.Should().Be(Start.UtcDateTime,
            "a new post's content was just written, at the create time");
    }

    [Theory]
    [InlineData(PostKind.Post)]
    [InlineData(PostKind.Page)]
    public async Task Update_MovesContentEditedAt(PostKind kind)
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, kind);

        clock.Advance(TimeSpan.FromDays(1));
        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}",
            new UpdatePostRequest(created.Title, "edited content", Kind: kind));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PostDto>();

        updated!.ContentEditedAt.Should().Be(Start.UtcDateTime.AddDays(1),
            "an update is a content edit and must move ContentEditedAt");
    }

    [Fact]
    public async Task Update_ChangingOnlyAnAnnouncementFlag_DoesNotMoveContentEditedAt_ButUpdatedAtMoves()
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, PostKind.Post);

        clock.Advance(TimeSpan.FromDays(1));
        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}",
            new UpdatePostRequest(created.Title, created.MarkdownContent, AnnounceMastodon: !created.AnnounceMastodon));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PostDto>();

        updated!.ContentEditedAt.Should().Be(Start.UtcDateTime, "a flag is not content the reader sees");
        updated.UpdatedAt.Should().Be(Start.UtcDateTime.AddDays(1));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("subtitle")]
    [InlineData("content")]
    public async Task Update_ChangingTitleSubtitleOrContent_MovesContentEditedAt(string field)
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, PostKind.Post);

        clock.Advance(TimeSpan.FromDays(1));
        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest(
            field == "title" ? created.Title + " v2" : created.Title,
            field == "content" ? created.MarkdownContent + " more" : created.MarkdownContent,
            Subtitle: field == "subtitle" ? "A subtitle" : created.Subtitle));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PostDto>();

        updated!.ContentEditedAt.Should().Be(Start.UtcDateTime.AddDays(1));
    }

    [Theory]
    [InlineData(PostKind.Post)]
    [InlineData(PostKind.Page)]
    public async Task PublishAndUnpublish_NeverMoveContentEditedAt(PostKind kind)
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, kind);

        clock.Advance(TimeSpan.FromDays(1));
        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}",
            new UpdatePostRequest(created.Title, "edited content", Kind: kind));
        update.EnsureSuccessStatusCode();
        var editedAt = (await update.Content.ReadFromJsonAsync<PostDto>())!.ContentEditedAt;

        clock.Advance(TimeSpan.FromDays(1));
        var publish = await admin.PostAsJsonAsync($"/api/posts/{created.Id}/publish", new PublishRequest());
        publish.EnsureSuccessStatusCode();
        var published = await publish.Content.ReadFromJsonAsync<PostDto>();

        published!.ContentEditedAt.Should().Be(editedAt,
            "publishing is not a content edit (UpdatedAt moves; the reader-facing date must not)");
        published.UpdatedAt.Should().Be(Start.UtcDateTime.AddDays(2),
            "UpdatedAt is the row's last-change time and does move on publish");

        clock.Advance(TimeSpan.FromDays(1));
        var unpublish = await admin.PostAsync($"/api/posts/{created.Id}/unpublish", null);
        unpublish.EnsureSuccessStatusCode();
        var unpublished = await unpublish.Content.ReadFromJsonAsync<PostDto>();

        unpublished!.ContentEditedAt.Should().Be(editedAt, "unpublishing is not a content edit either");
    }

    [Fact]
    public async Task Scheduling_NeverMovesContentEditedAt()
    {
        var connectionString = fixture.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();

        var editedAt = Start.UtcDateTime;
        var scheduledFor = Start.UtcDateTime.AddHours(-5);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            db.Posts.Add(new Croft.Api.Models.Post
            {
                Slug = "content-edited-due-post",
                Title = "Due",
                MarkdownContent = "x",
                Status = PostStatus.Scheduled,
                PublishedAt = scheduledFor,
                CreatedAt = editedAt.AddDays(-1),
                UpdatedAt = editedAt.AddDays(-1),
                ContentEditedAt = editedAt,
                AuthorId = "a",
                AuthorName = "A"
            });
            await db.SaveChangesAsync();
        }

        var publisher = new ScheduledPublisherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScheduledPublisherHostedService>.Instance,
            // The publisher's clock is strictly later than the seeded ContentEditedAt: a
            // regression stamping ContentEditedAt = now beside UpdatedAt would then write a
            // different value and fail the assertion below (with the same clock it would write
            // exactly editedAt and pass vacuously).
            new FakeTimeProvider(Start.AddDays(1)),
            new ScheduledPublishWakeup(), TestCache.NewNotifier());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await publisher.PublishDuePostsAsync(db);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var due = await db.Posts.AsNoTracking()
                .FirstAsync(p => p.Slug == "content-edited-due-post");

            due.Status.Should().Be(PostStatus.Published, "the scheduled post was due");
            due.ContentEditedAt.Should().Be(editedAt,
                "the scheduler's status flip is not a content edit");
        }
    }

    private static async Task<PostDto> CreateAsync(HttpClient client, PostKind kind)
    {
        var create = await client.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Content Edited Fixture", "content", Kind: kind));
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private BlogApiFactory CreateFactoryAsync(Action<IServiceCollection> configureServices)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath,
            configureServices: configureServices);
    }
}
