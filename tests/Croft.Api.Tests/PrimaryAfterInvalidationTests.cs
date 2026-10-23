using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13c item 2 (architecture finding 2, owner decision "a"): after a cache group is
/// invalidated, local or through NOTIFY, the next refill of that group reads from the primary,
/// not a replica that may not have replayed the write yet, for Croft:Api:Cache:PrimaryAfterInvalidation
/// (default 60 s). Outside the window refills use the replica as before. The tests see the
/// connection through its Application Name ("croft-api-read" is the replica).
/// </summary>
public class PrimaryAfterInvalidationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string ReplicaName = "croft-api-read";
    private const string PrimaryName = "after-invalidation-primary";

    [Fact]
    public async Task AfterALocalInvalidation_TheNextRefillReadsThePrimary_AndAfterTheWindowTheReplicaAgain()
    {
        var capture = new ConnectionApplicationNameCapture();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(capture, time, listener: false);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(admin, "After Invalidation Post");

        // The seeding writes invalidated the cache: wait the window out so the baseline is the replica.
        time.Advance(TimeSpan.FromSeconds(61));
        capture.Clear();
        (await anonymous.GetAsync($"/api/posts/{post.Slug}")).EnsureSuccessStatusCode();
        capture.Names.Should().NotBeEmpty().And.OnlyContain(name => name == ReplicaName,
            "outside the window a refill reads the replica");

        await UpdatePostAsync(admin, post, "After Invalidation Post, edited");
        capture.Clear();
        var refilled = await anonymous.GetStringAsync($"/api/posts/{post.Slug}");
        refilled.Should().Contain("edited");
        capture.Names.Should().NotBeEmpty().And.OnlyContain(name => name == PrimaryName,
            "the refill right after the edit reads the primary, which has the write");

        time.Advance(TimeSpan.FromSeconds(61));
        capture.Clear();
        (await anonymous.GetAsync("/api/posts?pageSize=7")).EnsureSuccessStatusCode();
        capture.Names.Should().NotBeEmpty().And.OnlyContain(name => name == ReplicaName,
            "after the window (60 s) the refill uses the replica again");
    }

    /// <summary>
    /// Brief 13h item 5. Brief 13f saw an anonymous <c>/api/posts?q=...&amp;page=4</c> not reach the read
    /// host while <c>/api/search</c> did. The list is not bypassing the replica: <c>q</c> is no parameter of
    /// the list (it binds <c>searchTerm</c>), so that URL was the plain page-4 list, which is a cached
    /// read, and a refill inside the PrimaryAfterInvalidation window of a write goes to the primary on
    /// purpose. Pinned here: outside the window a list refill reads the replica, inside it the primary;
    /// a list read that is never cached (a search term past the last cached page) always reads the replica.
    /// </summary>
    [Fact]
    public async Task ThePostsList_ReadsTheReplicaOutsideTheWindow_ThePrimaryInsideIt_AndAnUncachedSearchPageTheReplica()
    {
        var capture = new ConnectionApplicationNameCapture();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(capture, time, listener: false);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(admin, "Posts List Window Post");

        time.Advance(TimeSpan.FromSeconds(61));
        capture.Clear();
        (await anonymous.GetAsync("/api/posts?page=4")).EnsureSuccessStatusCode();
        capture.Names.Should().NotBeEmpty().And.OnlyContain(name => name == ReplicaName,
            "outside the window a list refill reads the replica");

        await UpdatePostAsync(admin, post, "Posts List Window Post, edited");
        capture.Clear();
        (await anonymous.GetAsync("/api/posts?q=anything&page=4")).EnsureSuccessStatusCode();
        capture.Names.Should().NotBeEmpty().And.OnlyContain(name => name == PrimaryName,
            "q is no list parameter: this is the cached page-4 list, refilled inside the window, so it reads the primary");

        capture.Clear();
        (await anonymous.GetAsync("/api/posts?searchTerm=window&page=4")).EnsureSuccessStatusCode();
        capture.Names.Should().NotBeEmpty().And.OnlyContain(name => name == ReplicaName,
            "a search page past the last cached one is never cached, so there is no refill to force to the primary");
    }

    [Fact]
    public async Task AfterAnInvalidationFromAnotherInstance_TheNextRefillReadsThePrimary()
    {
        var capture = new ConnectionApplicationNameCapture();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var a = CreateFactory(new ConnectionApplicationNameCapture(), new FakeTimeProvider(DateTimeOffset.UtcNow), listener: false);
        await using var b = CreateFactory(capture, time, listener: true);
        await WaitForListenerAsync(b);
        var adminA = a.CreateClient().WithRole("Admin");
        var anonymousB = b.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(adminA, "After Notify Post");

        // B's own startup cleared its cache: let that window pass, then fill B's entry from the replica.
        time.Advance(TimeSpan.FromSeconds(61));
        (await anonymousB.GetAsync($"/api/posts/{post.Slug}")).EnsureSuccessStatusCode();

        await UpdatePostAsync(adminA, post, "After Notify Post, edited");

        // B hears the NOTIFY and drops the entry; poll until a read of B reaches the database.
        var names = await WaitForRefillAsync(anonymousB, capture, $"/api/posts/{post.Slug}");
        names.Should().OnlyContain(name => name == PrimaryName,
            "the refill after a NOTIFY-driven invalidation reads the primary too");
    }

    private static async Task<IReadOnlyList<string>> WaitForRefillAsync(HttpClient anonymous, ConnectionApplicationNameCapture capture, string url)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            capture.Clear();
            (await anonymous.GetAsync(url)).EnsureSuccessStatusCode();
            if (capture.Names.Count > 0)
                return capture.Names;
            await Task.Yield();
        }

        throw new Xunit.Sdk.XunitException("B never refilled the entry after the invalidation");
    }

    private static async Task WaitForListenerAsync(BlogApiFactory factory)
    {
        var listener = factory.Services.GetRequiredService<CacheListenerService>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (listener.BackendPid is null && DateTime.UtcNow < deadline)
            await Task.Yield();
        listener.BackendPid.Should().NotBeNull($"the cache listener connects (last error: {listener.LastError ?? "none"})");
    }

    private static async Task<PostDto> CreatePostAsync(HttpClient admin, string title)
    {
        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "## A short body"));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        var published = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1)));
        published.EnsureSuccessStatusCode();
        return (await published.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task UpdatePostAsync(HttpClient admin, PostDto post, string title)
    {
        var response = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(title, post.MarkdownContent));
        response.EnsureSuccessStatusCode();
    }

    private BlogApiFactory CreateFactory(DbCommandInterceptor interceptor, TimeProvider time, bool listener)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        var primary = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = PrimaryName };

        return new BlogApiFactory(
            primary.ConnectionString,
            storagePath,
            configuration: new Dictionary<string, string?> { ["Database:ReadHost"] = primary.Host },
            configureServices: services =>
            {
                services.RemoveAll<IHostedService>();
                if (listener)
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());
                services.Replace(ServiceDescriptor.Singleton(time));
            },
            commandInterceptor: interceptor);
    }
}
