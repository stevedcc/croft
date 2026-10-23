using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// A Web host over a seeded stub API for the output-cache tests (brief 14b). Every API request the
/// pages make is recorded by the stub (<see cref="Calls"/>); the output cache's store is a
/// <see cref="RecordingStore"/> on the host's clock, so expiry needs no sleeping; and a request that
/// carries <see cref="StaffHeader"/> is a signed-in admin (the header stands in for the session
/// cookie, which the host's real cookie handler would need a token store entry for).
/// </summary>
internal sealed class CacheHost : IDisposable
{
    public const string StaffHeader = "X-Test-Staff";
    public const string PostSlug = "cache-post";
    public const string PageSlug = "cache-about";
    public const string CategorySlug = "cache-category";
    public const string TagSlug = "cache-tag";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly MediaRoutingHandler _routing;

    private CacheHost(WebApplicationFactory<Program> factory, StubBlogApiHandler api, MediaRoutingHandler routing, RecordingStore store, FakeTimeProvider clock)
    {
        _factory = factory;
        Api = api;
        _routing = routing;
        Store = store;
        Clock = clock;
    }

    public StubBlogApiHandler Api { get; }

    public RecordingStore Store { get; }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _factory.Services;

    /// <summary>The image requests (<c>/api/media/{id}</c>) the API has seen so far, with the Authorization header each carried.</summary>
    public IReadOnlyList<(string Path, string? Authorization)> MediaRequests => _routing.Requests;

    /// <summary>The API requests the stub has seen so far (the images are counted apart, see <see cref="MediaRequests"/>).</summary>
    public int Calls => Api.Requests.Count;

    public static CacheHost Create(
        Dictionary<string, string?>? settings = null, bool staffHeader = true, int posts = 12,
        Action<IServiceCollection>? configureServices = null, Func<HttpRequestMessage, HttpResponseMessage>? media = null)
    {
        var stub = SeededApi(posts);
        var api = new MediaRoutingHandler(stub, media ?? (_ => PublishedImage()));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(clock);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            foreach (var (key, value) in settings ?? [])
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                services.AddSingleton<IOutputCacheStore>(store);
                configureServices?.Invoke(services);
                if (staffHeader)
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderStaffHandler>("HeaderStaff", _ => { });
                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultScheme = "HeaderStaff";
                        options.DefaultAuthenticateScheme = "HeaderStaff";
                    });
                    services.AddSingleton<IUserAccessTokenService, FixedToken>();
                }
            });
        });
        return new CacheHost(factory, stub, api, store, clock);
    }

    /// <summary>A client that follows no redirects (a 302 is an answer the tests look at).</summary>
    public HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static HttpRequestMessage Get(string path, bool staff = false, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (staff)
        {
            request.Headers.Add(StaffHeader, "1");
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return request;
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>The API seeded with <paramref name="posts"/> published posts (the first is
    /// <see cref="PostSlug"/>) in one category with one tag, plus one static page.</summary>
    private static StubBlogApiHandler SeededApi(int posts)
    {
        var category = new CategoryDto(Guid.CreateVersion7(), "Cache Category", CategorySlug, null, posts);
        var tag = new TagDto(Guid.CreateVersion7(), "Cache Tag", TagSlug, posts, CountsTowardsRelated: true);
        var at = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

        var summaries = Enumerable.Range(1, posts).Select(i => new PostSummaryDto(
            Guid.CreateVersion7(), i == 1 ? PostSlug : $"cache-post-{i}", i == 1 ? "Cache Post" : $"Cache Post {i}",
            null, "An excerpt", "A summary", 1, PostStatus.Published, at.AddMinutes(-i), at, "Cache Author",
            category, [tag], null, null, null)).ToList();

        PostDto Detail(string slug, string title, PostKind kind) => new(
            Guid.CreateVersion7(), slug, title, null, "## A short body", "An excerpt", "A summary",
            PostStatus.Published, at, at, at, "cache-author-id", "Cache Author", null, [tag], category, 1, kind);

        return new StubBlogApiHandler
        {
            Posts = summaries,
            Categories = [category],
            Tags = [tag],
            Pages = [new PageSummaryDto(Guid.CreateVersion7(), PageSlug, "Cache About", PostStatus.Published, at)],
            PostDetails =
            [
                Detail(PostSlug, "Cache Post", PostKind.Post),
                Detail(PageSlug, "Cache About", PostKind.Page),
            ],
        };
    }

    /// <summary>A published image as the API answers it: <c>public, max-age=86400</c>.</summary>
    public static HttpResponseMessage PublishedImage(int bytes = 2048) => Image("public, max-age=86400", bytes);

    /// <summary>A draft's image as the API answers it: <c>no-store</c>.</summary>
    public static HttpResponseMessage DraftImage(int bytes = 2048) => Image("no-store", bytes);

    private static HttpResponseMessage Image(string cacheControl, int bytes)
    {
        var body = new byte[bytes];
        Random.Shared.NextBytes(body);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        response.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
        return response;
    }

    /// <summary>Answers <c>/api/media/…</c> from the test's responder and records it; everything else goes to the stub.</summary>
    private sealed class MediaRoutingHandler(StubBlogApiHandler stub, Func<HttpRequestMessage, HttpResponseMessage> media) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(string Path, string? Authorization)> _requests = new();

        public IReadOnlyList<(string Path, string? Authorization)> Requests => [.. _requests];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/media/", StringComparison.Ordinal))
            {
                _requests.Enqueue((request.RequestUri.AbsolutePath, request.Headers.Authorization?.ToString()));
                return Task.FromResult(media(request));
            }

            return new HttpMessageInvoker(stub, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private sealed class FixedToken : IUserAccessTokenService
    {
        // As the real service: a signed-in user has a token, an anonymous reader has none.
        public Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<string?>(user.Identity?.IsAuthenticated == true ? "test-token" : null);
    }

    /// <summary>Signed in as an admin when the request carries <see cref="StaffHeader"/>, anonymous otherwise.</summary>
    private sealed class HeaderStaffHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(StaffHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin@example.com"), new Claim(ClaimTypes.Role, "Admin")], "HeaderStaff");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "HeaderStaff")));
        }
    }
}

/// <summary>
/// The output cache's store on a test clock: entries expire when the clock passes their lifetime,
/// tag eviction works, and every write and eviction is recorded. The framework's own store reads the
/// real clock and offers no hook, so it cannot be fast-forwarded.
/// </summary>
internal sealed class RecordingStore(TimeProvider clock) : IOutputCacheStore
{
    private readonly ConcurrentDictionary<string, (byte[] Value, string[] Tags, DateTimeOffset Expires)> _entries = new();
    private readonly ConcurrentQueue<(string Key, string[] Tags, TimeSpan ValidFor)> _writes = new();
    private readonly ConcurrentQueue<string> _evictions = new();

    /// <summary>Every <c>SetAsync</c>: key, tags and lifetime.</summary>
    public IReadOnlyList<(string Key, string[] Tags, TimeSpan ValidFor)> Writes => [.. _writes];

    /// <summary>Every tag evicted, in order.</summary>
    public IReadOnlyList<string> Evictions => [.. _evictions];

    /// <summary>The entries that have not expired.</summary>
    public int EntryCount => _entries.Count(e => e.Value.Expires > clock.GetUtcNow());

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        _evictions.Enqueue(tag);
        foreach (var (key, entry) in _entries)
        {
            if (entry.Tags.Contains(tag))
            {
                _entries.TryRemove(key, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_entries.TryGetValue(key, out var entry) && entry.Expires > clock.GetUtcNow()
            ? entry.Value
            : null);

    public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        _writes.Enqueue((key, tags ?? [], validFor));
        _entries[key] = (value, tags ?? [], clock.GetUtcNow() + validFor);
        return ValueTask.CompletedTask;
    }
}
