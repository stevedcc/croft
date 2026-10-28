using Croft.Api.Data;
using Croft.Api.Models;
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
/// Croft's own counters on the Croft.Api meter: what the owner's dashboards read. A page view is
/// counted by its deduplication outcome (a stored view is `recorded`, a repeat is `duplicate` and
/// must not inflate `recorded`); a post going live is counted by who published it (`editor` or
/// `scheduled`). Attribute values stay in those fixed sets so the series count stays small.
/// </summary>
[Collection(nameof(CroftMetricsGroup))]
public class ApiMetricsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string TestIngestKey = "test-ingest-key";

    [Fact]
    public async Task PageView_Stored_CountsOnceWithOutcomeRecorded()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Recorded Metric Test");
        var anonymous = factory.CreateClient();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.90", TestIngestKey);

        metrics.Measurements.Should().ContainSingle(
                m => m.Instrument == "croft.page_views.recorded" && m.Value == 1 && m.HasTag("outcome", "recorded"),
            "one stored page view is one recorded event")
            .Which.Tags.Should().ContainSingle(t => t.Key == "outcome",
                "outcome is the only attribute: fixed sets keep the series count small");
    }

    [Fact]
    public async Task PageView_Duplicate_CountsDuplicate_NotRecorded()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Duplicate Metric Test");
        var anonymous = factory.CreateClient();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.91", TestIngestKey);
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.91", TestIngestKey);

        metrics.Measurements.Where(m => m.Instrument == "croft.page_views.recorded" && m.HasTag("outcome", "recorded"))
            .Should().HaveCount(1, "the duplicate view must not inflate the recorded count");
        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.page_views.recorded" && m.Value == 1 && m.HasTag("outcome", "duplicate"),
            "the repeat view counts as duplicate");
    }

    [Fact]
    public async Task PageView_AcrossMidnight_CountsDuplicate_NotRecorded()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 23, 59, 50, TimeSpan.Zero));
        await using var factory = new BlogApiFactory(
            fixture.ConnectionString,
            storagePath: Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}"),
            configuration: new Dictionary<string, string?> { ["Analytics:IngestKey"] = TestIngestKey },
            configureServices: s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Midnight Metric Test", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = (await postResponse.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(new DateTime(2026, 10, 1)))).EnsureSuccessStatusCode();
        var anonymous = factory.CreateClient();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.92", TestIngestKey);
        clock.Advance(TimeSpan.FromSeconds(20)); // 2026-10-06 00:00:10
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.92", TestIngestKey);

        metrics.Measurements.Where(m => m.Instrument == "croft.page_views.recorded" && m.HasTag("outcome", "recorded"))
            .Should().HaveCount(1, "the view after midnight is not a new view");
        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.page_views.recorded" && m.Value == 1 && m.HasTag("outcome", "duplicate"),
            "the reload across midnight counts as duplicate, like a same-day repeat");
    }

    [Fact]
    public async Task ScheduledPublisher_CountsEachPublishedPostWithSourceScheduled()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            db.Posts.Add(new Post
            {
                Id = Guid.NewGuid(),
                Slug = "metric-due-post",
                Title = "Metric Due",
                MarkdownContent = "x",
                Status = PostStatus.Scheduled,
                PublishedAt = DateTime.UtcNow.AddMinutes(-5),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AuthorId = "a",
                AuthorName = "A"
            });
            await db.SaveChangesAsync();
        }

        var publisher = new ScheduledPublisherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScheduledPublisherHostedService>.Instance,
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new ScheduledPublishWakeup(), TestCache.NewNotifier());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            await publisher.PublishDuePostsAsync(db);
        }

        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.posts.published" && m.Value == 1 && m.HasTag("source", "scheduled"),
            "the background publisher publishes the posts it makes live");
    }

    [Fact]
    public async Task EditorPublish_CountsOnceWithSourceEditor()
    {
        using var metrics = new MetricCapture("Croft.Api").Start();
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");

        await CreatePublishedPostAsync(admin, "Editor Metric Test");

        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.posts.published" && m.Value == 1 && m.HasTag("source", "editor"),
            "an editor hitting publish is the other way posts go live");
    }

    private BlogApiFactory CreateFactory() => new(
        fixture.ConnectionString,
        storagePath: Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}"),
        configuration: new Dictionary<string, string?> { ["Analytics:IngestKey"] = TestIngestKey });

    private static async Task<PostDto> CreatePublishedPostAsync(HttpClient admin, string title)
    {
        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        var publishResponse = await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest(null));
        publishResponse.EnsureSuccessStatusCode();
        return post;
    }

    private static async Task PostPageViewAsync(HttpClient client, Guid postId, string visitorIp, string ingestKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
        {
            Content = JsonContent.Create(new PageViewRequest(postId))
        };
        request.Headers.Add("X-Visitor-IP", visitorIp);
        request.Headers.Add("X-Analytics-Key", ingestKey);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// The Croft.Api counters are process-wide statics: this collection opts out of parallel
/// execution so exact-count assertions cannot be corrupted by another class's events.
/// </summary>
[CollectionDefinition(nameof(CroftMetricsGroup), DisableParallelization = true)]
public sealed class CroftMetricsGroup;
