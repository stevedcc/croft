using System.Net;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>
/// Everything a fetcher test needs: a database of its own (a migrated scratch database, so a tick sees only this test's threads),
/// a fake clock, stub platforms that answer from the recorded fixtures, and a spy on the cache notifier. A "replica" is one
/// <see cref="SocialFetchHostedService"/>; two of them tick against the same database like two API pods.
/// </summary>
internal sealed class SocialFetchHarness : IAsyncDisposable
{
    public const string MastodonServer = "mastodon.example";

    private SocialFetchHarness(ScratchDatabase database, FakeTimeProvider time, SocialOptions settings)
    {
        Database = database;
        Time = time;
        Settings = settings;
        Mastodon = new StubHandler(request => MastodonAnswerFor(request));
        Bluesky = new StubHandler(_ => BlueskyAnswer());
        _mastodonActive = Mastodon;
        _blueskyActive = Bluesky;
    }

    private HttpMessageHandler _mastodonActive;
    private HttpMessageHandler _blueskyActive;

    public ScratchDatabase Database { get; }

    public FakeTimeProvider Time { get; }

    public SocialOptions Settings { get; }

    /// <summary>The Mastodon server: answers the recorded context; a test changes <see cref="StubHandler.Respond"/>, or swaps the handler with <see cref="UseMastodon"/>.</summary>
    public StubHandler Mastodon { get; }

    public StubHandler Bluesky { get; }

    public ISocialAvatars Avatars { get; set; } = new SocialNoAvatars();

    public static async Task<SocialFetchHarness> CreateAsync(PostgresDatabaseFixture fixture, DateTimeOffset? now = null, bool enabled = true)
    {
        var settings = new SocialOptions
        {
            Enabled = enabled,
            Mastodon = new MastodonSocialOptions { Server = MastodonServer, Account = "owner" },
            Bluesky = new BlueskySocialOptions { Did = BlueskyThreadBuilder.OwnerDid },
        };
        return new SocialFetchHarness(
            await ScratchDatabase.CreateAsync(fixture.ConnectionString),
            new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)),
            settings);
    }

    public void UseMastodon(HttpMessageHandler handler) => _mastodonActive = handler;

    public void UseBluesky(HttpMessageHandler handler) => _blueskyActive = handler;

    public static HttpResponseMessage MastodonAnswer(string? json = null, int remaining = 299)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json ?? SocialFixtures.Read("mastodon-context.json"), System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    /// <summary>The recorded context, rooted at the status the request asks about (the recording's replies answer the recorded status).</summary>
    public static HttpResponseMessage MastodonAnswerFor(HttpRequestMessage request, int remaining = 299)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/');
        var statusId = segments[^2]; // /api/v1/statuses/{id}/context
        return MastodonAnswer(SocialFixtures.Read("mastodon-context.json").Replace(SocialFixtures.MastodonRootId, statusId, StringComparison.Ordinal), remaining);
    }

    public static HttpResponseMessage BlueskyAnswer(string? json = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            json ?? BlueskyThreadBuilder.Thread([BlueskyThreadBuilder.Node("alpha", null, BlueskyThreadBuilder.Node("alpha1")), BlueskyThreadBuilder.Node("beta")]),
            System.Text.Encoding.UTF8, "application/json"),
    };

    /// <summary>One replica: its own hosted-service instance over the harness's sources, with its own cache spy.</summary>
    public SocialFetchReplica NewReplica(ISocialAvatars? avatars = null, SocialPlatformGates? gates = null, ILogger<SocialFetchHostedService>? logger = null)
    {
        var factory = new RoutingClientFactory(_mastodonActive, _blueskyActive);
        var spy = new SpyNotifier();
        var service = new SocialFetchHostedService(
            logger ?? NullLogger<SocialFetchHostedService>.Instance,
            new ServiceCollection()
                .AddDbContext<BlogDbContext>(options => options.UseNpgsql(Database.ConnectionString))
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Time,
            spy,
            Options.Create(Settings),
            [new MastodonThreadSource(factory, Options.Create(Settings), Time), new BlueskyThreadSource(factory, Time)],
            gates ?? new SocialPlatformGates(),
            avatars ?? Avatars);
        return new SocialFetchReplica(service, spy, this);
    }

    public async Task<Guid> SeedPostAsync(
        DateTime publishedAt, string? mastodonStatusId = SocialFixtures.MastodonRootId, string? blueskyUri = null,
        DateTime? linkedAt = null, PostStatus status = PostStatus.Published, DateTime? nextDueAt = null, string slug = "")
    {
        await using var db = Database.CreateContext();
        var id = Guid.CreateVersion7();
        var post = new Post
        {
            Id = id,
            Slug = string.IsNullOrEmpty(slug) ? $"post-{id:N}" : slug,
            Title = "Seeded",
            MarkdownContent = "body",
            Status = status,
            PublishedAt = publishedAt,
            CreatedAt = publishedAt,
            UpdatedAt = publishedAt,
            ContentEditedAt = publishedAt,
            AuthorId = "a",
            AuthorName = "A",
            MastodonUrl = mastodonStatusId is null ? null : $"https://{MastodonServer}/@owner/{mastodonStatusId}",
            MastodonStatusId = mastodonStatusId,
            BlueskyUrl = blueskyUri is null ? null : "https://bsky.app/profile/owner.example/post/x",
            BlueskyUri = blueskyUri,
            DiscussionLinkedAt = linkedAt ?? publishedAt,
        };
        db.Posts.Add(post);
        var due = nextDueAt ?? Time.GetUtcNow().UtcDateTime;
        if (mastodonStatusId is not null)
            db.SocialThreads.Add(new SocialThread { PostId = id, Platform = SocialPlatform.Mastodon, NextDueAt = due });
        if (blueskyUri is not null)
            db.SocialThreads.Add(new SocialThread { PostId = id, Platform = SocialPlatform.Bluesky, NextDueAt = due });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task<SocialThread> ThreadAsync(Guid postId, SocialPlatform platform)
    {
        await using var db = Database.CreateContext();
        return await db.SocialThreads.AsNoTracking().SingleAsync(t => t.PostId == postId && t.Platform == platform);
    }

    public async Task<List<SocialReply>> RepliesAsync(Guid postId, SocialPlatform platform)
    {
        await using var db = Database.CreateContext();
        return await db.SocialReplies.AsNoTracking().Where(r => r.PostId == postId && r.Platform == platform).OrderBy(r => r.ExternalId).ToListAsync();
    }

    public async Task UpdateThreadAsync(Guid postId, SocialPlatform platform, Action<SocialThread> change)
    {
        await using var db = Database.CreateContext();
        var thread = await db.SocialThreads.SingleAsync(t => t.PostId == postId && t.Platform == platform);
        change(thread);
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    /// <summary>The DateTime the database stores for an instant: UTC, microsecond resolution.</summary>
    public static DateTime Utc(DateTimeOffset instant) => instant.UtcDateTime;
}

/// <summary>One replica of the fetcher and what it told its cache.</summary>
internal sealed class SocialFetchReplica(SocialFetchHostedService service, SpyNotifier notifier, SocialFetchHarness harness)
{
    public SocialFetchHostedService Service => service;

    public SpyNotifier Notifier => notifier;

    public async Task<SocialTickResult> TickAsync()
    {
        await using var db = harness.Database.CreateContext();
        return await service.TickAsync(db);
    }

    /// <summary>A tick on a context the test built (to watch its SQL).</summary>
    public Task<SocialTickResult> TickAsync(BlogDbContext db) => service.TickAsync(db);
}

/// <summary>An <see cref="ICacheNotifier"/> that records what was notified and cleared locally, and still sends the real NOTIFY.</summary>
internal sealed class SpyNotifier : ICacheNotifier
{
    private readonly CacheNotifier _real = new(new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions())));
    private readonly Lock _gate = new();
    private readonly List<CacheGroup> _notified = [];
    private readonly List<CacheGroup> _cleared = [];

    public IReadOnlyList<CacheGroup> Notified
    {
        get
        {
            lock (_gate)
                return [.. _notified];
        }
    }

    public IReadOnlyList<CacheGroup> ClearedLocally
    {
        get
        {
            lock (_gate)
                return [.. _cleared];
        }
    }

    public Task SaveAndNotifyAsync(BlogDbContext db, CacheGroup changed, CancellationToken ct = default) => _real.SaveAndNotifyAsync(db, changed, ct);

    public Task NotifyAsync(DbContext db, CacheGroup changed, CancellationToken ct = default)
    {
        lock (_gate)
            _notified.Add(changed);
        return _real.NotifyAsync(db, changed, ct);
    }

    public Task InvalidateLocalAsync(CacheGroup changed)
    {
        lock (_gate)
            _cleared.Add(changed);
        return _real.InvalidateLocalAsync(changed);
    }
}

/// <summary>Client factory that answers each named social client with its own stub handler.</summary>
internal sealed class RoutingClientFactory(HttpMessageHandler mastodon, HttpMessageHandler bluesky, HttpMessageHandler? avatars = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(name switch
    {
        SocialHttp.Mastodon => mastodon,
        SocialHttp.Bluesky => bluesky,
        SocialHttp.Avatars => avatars ?? throw new InvalidOperationException("no avatar handler in this test"),
        _ => throw new InvalidOperationException($"unexpected client {name}"),
    }, disposeHandler: false);
}

/// <summary>The avatar step of a fetcher test that is not about avatars: every reply shows the fallback.</summary>
internal sealed class SocialNoAvatars : ISocialAvatars
{
    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(BlogDbContext db, IReadOnlyCollection<string> sourceUrls, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public Task SweepAsync(BlogDbContext db, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A handler that holds its first request until the test releases it: a fetch that is "in flight" while another replica ticks.</summary>
internal sealed class GatedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public Task Arrived => _arrived.Task;

    public int Requests => Volatile.Read(ref _count);

    public void Release() => _release.TrySetResult();

    /// <summary>
    /// Waits until the first request is in flight; if the tick that should have sent it ends first (it failed), that failure is
    /// thrown instead of waiting for ever.
    /// </summary>
    public async Task ArrivedOrTickEndedAsync(Task tick)
    {
        await Task.WhenAny(Arrived, tick);
        if (tick.IsCompleted)
            await tick;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _count);
        if (number == 1)
        {
            _arrived.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return respond(request);
    }
}
