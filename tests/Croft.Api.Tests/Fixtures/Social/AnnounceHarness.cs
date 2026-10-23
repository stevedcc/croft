using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Social;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>
/// Everything a sender test needs: a scratch database of its own, a fake clock, stub announcers for the two networks (the real ones are tested on their own),
/// a logger that captures what is logged and a spy on the cache notifier. A "replica" is one <see cref="SocialAnnounceHostedService"/>; two of them tick against
/// the same database like two API pods.
/// </summary>
internal sealed class AnnounceHarness : IAsyncDisposable
{
    public const string BaseUrl = "https://blog.example";

    private AnnounceHarness(ScratchDatabase database)
    {
        Database = database;
        Time = new Fixtures.FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));
        Settings = new SocialOptions
        {
            Enabled = true,
            BaseUrl = BaseUrl,
            Mastodon = new MastodonSocialOptions { Server = SocialTestSettings.Server, Account = SocialTestSettings.Account, AccessToken = "test-token-0f3a" },
            Bluesky = new BlueskySocialOptions { Did = SocialTestSettings.Did, AppPassword = "test-pass-77c1" },
        };
    }

    public ScratchDatabase Database { get; }

    public Fixtures.FakeTimeProvider Time { get; }

    public SocialOptions Settings { get; }

    public StubAnnouncer Mastodon { get; } = new(SocialPlatform.Mastodon);

    public StubAnnouncer Bluesky { get; } = new(SocialPlatform.Bluesky);

    public LogCollector Logs { get; } = new();

    public FakeStorage Storage { get; } = new();

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    public static async Task<AnnounceHarness> CreateAsync(PostgresDatabaseFixture fixture) =>
        new(await ScratchDatabase.CreateAsync(fixture.ConnectionString));

    public SocialAnnounceReplica NewReplica(SocialPlatformGates? gates = null, params ISocialAnnouncer[] announcers)
    {
        var spy = new SpyNotifier();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(Logs));
        var service = new SocialAnnounceHostedService(
            loggers.CreateLogger<SocialAnnounceHostedService>(),
            new ServiceCollection().AddDbContext<BlogDbContext>(options => options.UseNpgsql(Database.ConnectionString)).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Time,
            spy,
            Options.Create(Settings),
            announcers.Length > 0 ? announcers : [Mastodon, Bluesky],
            gates ?? new SocialPlatformGates(),
            Storage,
            new NoHandles());
        return new SocialAnnounceReplica(service, spy, this);
    }

    /// <summary>A post with its announcement rows pending now (a published post just after the Publish click), or only the post.</summary>
    public async Task<Guid> SeedPostAsync(
        string title = "Seeded post", string? subtitle = "A subtitle", string slug = "", string? announcementText = null,
        PostStatus status = PostStatus.Published, bool queued = true, bool mastodon = true, bool bluesky = true, Guid? bannerId = null)
    {
        await using var db = Database.CreateContext();
        var id = Guid.CreateVersion7();
        var now = Now;
        db.Posts.Add(new Post
        {
            Id = id,
            Slug = string.IsNullOrEmpty(slug) ? $"post-{id:N}" : slug,
            Title = title,
            Subtitle = subtitle,
            MarkdownContent = "body",
            Status = status,
            PublishedAt = status == PostStatus.Draft ? null : now,
            CreatedAt = now,
            UpdatedAt = now,
            ContentEditedAt = now,
            AuthorId = "a",
            AuthorName = "A",
            AnnouncementText = announcementText,
            AnnounceMastodon = true,
            AnnounceBluesky = true,
            BannerImageId = bannerId,
        });
        if (queued && mastodon)
            db.SocialAnnouncements.Add(new SocialAnnouncement { PostId = id, Platform = SocialPlatform.Mastodon, State = AnnouncementState.Pending, QueuedAt = now, NextAttemptAt = now });
        if (queued && bluesky)
            db.SocialAnnouncements.Add(new SocialAnnouncement { PostId = id, Platform = SocialPlatform.Bluesky, State = AnnouncementState.Pending, QueuedAt = now, NextAttemptAt = now });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task<Guid> SeedBannerAsync(string path, byte[] bytes)
    {
        await using var db = Database.CreateContext();
        var asset = new MediaAsset { FileName = "b.png", OriginalName = "b.png", ContentType = "image/png", FilePath = path, UploadedAt = Now, UploadedBy = "a", Scope = MediaAssetScope.Global };
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        Storage.Files[path] = bytes;
        return asset.Id;
    }

    public async Task<SocialAnnouncement> RowAsync(Guid postId, SocialPlatform platform)
    {
        await using var db = Database.CreateContext();
        return await db.SocialAnnouncements.AsNoTracking().SingleAsync(a => a.PostId == postId && a.Platform == platform);
    }

    public async Task<Post> PostAsync(Guid postId)
    {
        await using var db = Database.CreateContext();
        return await db.Posts.AsNoTracking().SingleAsync(p => p.Id == postId);
    }

    public async Task ChangeRowAsync(Guid postId, SocialPlatform platform, Action<SocialAnnouncement> change)
    {
        await using var db = Database.CreateContext();
        var row = await db.SocialAnnouncements.SingleAsync(a => a.PostId == postId && a.Platform == platform);
        change(row);
        await db.SaveChangesAsync();
    }

    public async Task ChangePostAsync(Guid postId, Action<Post> change)
    {
        await using var db = Database.CreateContext();
        var post = await db.Posts.SingleAsync(p => p.Id == postId);
        change(post);
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

/// <summary>One replica of the sender and what it told its cache.</summary>
internal sealed class SocialAnnounceReplica(SocialAnnounceHostedService service, SpyNotifier notifier, AnnounceHarness harness)
{
    public SpyNotifier Notifier => notifier;

    public async Task<AnnounceTickResult> TickAsync(CancellationToken ct = default)
    {
        await using var db = harness.Database.CreateContext();
        return await service.TickAsync(db, ct);
    }
}

/// <summary>A network stand-in: records what it was asked and answers with a function of it (by default: posted, with a new id).</summary>
internal sealed class StubAnnouncer(SocialPlatform platform) : ISocialAnnouncer
{
    private int _counter;

    public SocialPlatform Platform => platform;

    public List<AnnouncementMessage> Sends { get; } = [];

    public List<AnnouncementMessage> Checks { get; } = [];

    public Func<AnnouncementMessage, CancellationToken, Task<AnnounceOutcome>>? OnSend { get; set; }

    public Func<AnnouncementMessage, CancellationToken, Task<AnnounceOutcome>>? OnCheck { get; set; }

    public AnnounceOutcome.Posted NewPost()
    {
        var number = Interlocked.Increment(ref _counter);
        return platform == SocialPlatform.Mastodon
            ? new AnnounceOutcome.Posted($"1150000000000000{number:00}", $"https://{SocialTestSettings.Server}/@{SocialTestSettings.Account}/1150000000000000{number:00}")
            : new AnnounceOutcome.Posted($"at://{SocialTestSettings.Did}/app.bsky.feed.post/3m5aaaaaaaaa{number:00}", $"https://bsky.app/profile/{SocialTestSettings.Did}/post/3m5aaaaaaaaa{number:00}");
    }

    public Task<AnnounceOutcome> FindEarlierAsync(AnnouncementMessage message, CancellationToken ct)
    {
        lock (Checks)
            Checks.Add(message);
        return OnCheck is null ? Task.FromResult<AnnounceOutcome>(new AnnounceOutcome.NotFound()) : OnCheck(message, ct);
    }

    public Task<AnnounceOutcome> SendAsync(AnnouncementMessage message, CancellationToken ct)
    {
        lock (Sends)
            Sends.Add(message);
        return OnSend is null ? Task.FromResult<AnnounceOutcome>(NewPost()) : OnSend(message, ct);
    }
}

/// <summary>Files in memory, for banners.</summary>
internal sealed class FakeStorage : IFileStorage
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<string> SaveAsync(Stream data, string fileName, string contentType, string storagePath, CancellationToken ct = default) => throw new NotSupportedException();

    public Stream OpenRead(string relativePath) =>
        Files.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes) : throw new FileNotFoundException(relativePath);

    public void Delete(string relativePath) => Files.Remove(relativePath);
}

/// <summary>The sender never resolves handles (only the link service's validation does).</summary>
internal sealed class NoHandles : IBlueskyHandleResolver
{
    public Task<string> ResolveDidAsync(string handle, CancellationToken ct) => throw new NotSupportedException();
}
