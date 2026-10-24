using System.Net;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests;

/// <summary>
/// Publishing a published post keeps its date, and an explicit past date sets it back (issue #1).
/// </summary>
public class PublishedDateTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Original = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PublishWithNoDate_OnAPublishedPost_KeepsItsDateAndQueuesNothing()
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactory(clock);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin);
        await PublishAsync(admin, post.Id, new PublishRequest(Original));
        var rowsBefore = await RowCountAsync(factory, post.Id);

        clock.Advance(TimeSpan.FromDays(3));
        await PublishAsync(admin, post.Id, new PublishRequest());

        var read = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{post.Id}"))!;
        read.PublishedAt.Should().Be(Original, "publishing an already published post is not a new publication");
        read.Status.Should().Be(PostStatus.Published);
        (await RowCountAsync(factory, post.Id)).Should().Be(rowsBefore);
    }

    [Fact]
    public async Task PublishWithAPastDate_OnAPublishedPost_SetsIt()
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactory(clock);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin);
        await PublishAsync(admin, post.Id, new PublishRequest(Original));
        var backDated = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);

        await PublishAsync(admin, post.Id, new PublishRequest(backDated));

        var read = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{post.Id}"))!;
        read.PublishedAt.Should().Be(backDated);
        read.Status.Should().Be(PostStatus.Published);
    }

    [Fact]
    public async Task PublishWithNoDate_OnADraft_PublishesAtTheClocksNow()
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactory(clock);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin);

        await PublishAsync(admin, post.Id, new PublishRequest());

        var read = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{post.Id}"))!;
        read.PublishedAt.Should().Be(Start.UtcDateTime);
        read.Status.Should().Be(PostStatus.Published);
    }

    [Fact]
    public async Task PublishWithAFutureDate_OnAPublishedPost_Schedules()
    {
        var clock = new FakeTimeProvider(Start);
        await using var factory = CreateFactory(clock);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin);
        await PublishAsync(admin, post.Id, new PublishRequest(Original));
        var future = Start.UtcDateTime.AddDays(2);

        await PublishAsync(admin, post.Id, new PublishRequest(future));

        var read = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{post.Id}"))!;
        read.Status.Should().Be(PostStatus.Scheduled);
        read.PublishedAt.Should().Be(future);
    }

    private static async Task<int> RowCountAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlogDbContext>().SocialAnnouncements.CountAsync(a => a.PostId == postId);
    }

    private static async Task<PostDto> CreateAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Published date {Guid.NewGuid():N}", "body"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task PublishAsync(HttpClient admin, Guid id, PublishRequest request)
    {
        var response = await admin.PostAsJsonAsync($"/api/posts/{id}/publish", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private BlogApiFactory CreateFactory(TimeProvider clock)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath,
            configureServices: s => s.AddSingleton(clock));
    }
}
