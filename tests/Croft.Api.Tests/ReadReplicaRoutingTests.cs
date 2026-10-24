using System.Net;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Net.Http.Headers;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09d item 3: with Database:ReadHost set, a public read (GET/HEAD with no signed-in
/// user) runs its SQL on the read connection (Application Name croft-api-read,
/// default_transaction_read_only=on); everything else — admin, editor, a signed-in reader,
/// every write, background services — runs on the primary, so an editor always sees what
/// they just saved. Unset: today's behaviour, everything on the primary.
/// </summary>
public class ReadReplicaRoutingTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string ReadApplicationName = "croft-api-read";

    [Fact]
    public async Task WithReadHost_PublicReadsRunOnTheReadConnection_WritesAndAuthenticatedRunOnThePrimary()
    {
        var capture = new ConnectionApplicationNameCapture();
        await using var factory = CreateFactory(readHost: Host(), capture);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var post = await CreatePostAsync(admin, "Routing Probe Post");

        capture.Clear();
        var list = await anonymous.GetAsync("/api/posts?pageSize=43");
        list.EnsureSuccessStatusCode();
        capture.Names.Should().Contain(ReadApplicationName,
            "the anonymous GET runs its SQL on the read connection");

        capture.Clear();
        var adminList = await admin.GetAsync("/api/posts?pageSize=43");
        adminList.EnsureSuccessStatusCode();
        capture.Names.Should().NotContain(ReadApplicationName,
            "an authenticated GET runs on the primary, so an editor sees what they just saved");
        capture.Names.Should().OnlyContain(name => name == PrimaryName, "and touches nothing else");

        capture.Clear();
        var view = await anonymous.PostAsJsonAsync("/api/analytics/pageview", new PageViewRequest(post.Id));
        view.StatusCode.Should().Be(HttpStatusCode.NoContent, "the anonymous write goes to the primary and succeeds");
        capture.Names.Should().OnlyContain(name => name == PrimaryName,
            "every POST runs on the primary — the read connection is read-only");

        await InsertScheduledDuePostAsync("routing-due-post");
        capture.Clear();
        await RunSchedulerOnceAsync(factory);
        capture.Names.Should().OnlyContain(name => name == PrimaryName,
            "the scheduler's publish runs on the primary (it has no HttpContext)");
    }

    [Fact]
    public async Task WithoutReadHost_EverythingRunsOnThePrimaryConnection()
    {
        var capture = new ConnectionApplicationNameCapture();
        await using var factory = CreateFactory(readHost: null, capture);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        await CreatePostAsync(admin, "Routing Unset Post");

        capture.Clear();
        var list = await anonymous.GetAsync("/api/posts?pageSize=43");
        list.EnsureSuccessStatusCode();

        capture.Names.Should().NotBeEmpty("the read ran SQL");
        capture.Names.Should().OnlyContain(name => name == PrimaryName,
            "with Database:ReadHost unset everything uses ConnectionStrings:BlogDb exactly as before");
    }

    [Fact]
    public async Task WithReadHost_NoAllowAnonymousGetEndpoint_WritesOnTheReadConnection()
    {
        var capture = new ConnectionApplicationNameCapture();
        await using var factory = CreateFactory(readHost: Host(), capture);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        var ids = await SeedAsync(factory, admin);

        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(IsAnonymousGet)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Order()
            .ToArray();

        // Pinned so a new [AllowAnonymous] GET endpoint cannot go unswept: add it here with
        // valid inputs and the loop below must see it succeed on the read connection.
        routes.Should().BeEquivalentTo(
            [
                "api/categories", "api/categories/{slug}", "api/media/{id:guid}", "api/pages",
                "api/pages/navigation", "api/pages/navigation/all", "api/pages/{slug}", "api/posts", "api/posts/{slug}",
                "api/posts/{slug}/discussion", "api/posts/{slug}/related", "api/search", "api/sitemap", "api/social/avatars/{hash}.webp", "api/tags", "api/tags/{slug}",
            ],
            "every [AllowAnonymous] GET endpoint is enumerated from the endpoint data source");

        foreach (var route in routes)
        {
            var url = UrlFor(route, ids);
            url.Should().NotContain("{", $"every parameter of '{route}' must be substituted with a valid input");
            var response = await anonymous.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"{url} must serve its public read on the read connection without writing (a write there fails like on a real replica)");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("read-only transaction", $"{url} must not hit a read-only-transaction error");
        }
    }

    [Fact]
    public async Task MultiHostReadHost_WithUnresolvableFirstHost_StillServesPublicReads()
    {
        var capture = new ConnectionApplicationNameCapture();
        await using var factory = CreateFactory(readHost: $"does-not-exist.invalid,{Host()}", capture);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        await CreatePostAsync(admin, "Routing MultiHost Post");

        capture.Clear();
        var list = await anonymous.GetAsync("/api/posts?pageSize=43");
        list.EnsureSuccessStatusCode();
        capture.Names.Should().Contain(ReadApplicationName,
            "Npgsql's multi-host read string falls over from the unresolvable host to the test server");
    }

    [Fact]
    public async Task EmptyReadHost_IsTreatedAsUnset_EverythingRunsOnThePrimary()
    {
        var capture = new ConnectionApplicationNameCapture();
        await using var factory = CreateFactory(readHost: " ", capture);
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient().AsAnonymous();
        await CreatePostAsync(admin, "Routing Blank Host Post");

        capture.Clear();
        var list = await anonymous.GetAsync("/api/posts?pageSize=43");
        list.EnsureSuccessStatusCode();

        capture.Names.Should().OnlyContain(name => name == PrimaryName,
            "a blank Database:ReadHost means unset: everything uses ConnectionStrings:BlogDb");
    }

    [Fact]
    public async Task Startup_LogsWhetherReadsAreRouted_AndNeverThePassword()
    {
        // A distinctive per-run password keeps the "never the password" oracle honest: the
        // shared fixture password ("blog") can appear in a log line for a harmless reason
        // (a path like .../newblog/...) and fail this test by accident. The factories never
        // open a connection here (hosted services off, no requests), so the password does
        // not have to authenticate.
        var routedPassword = $"pw-{Guid.NewGuid():N}";
        var logs = new TestLogCapture();
        await using var routed = CreateFactory(readHost: Host(), logs: logs, password: routedPassword);
        routed.CreateClient();

        logs.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Information
                && entry.Message.Contains("route to read host", StringComparison.Ordinal)
                && entry.Message.Contains(Host(), StringComparison.Ordinal),
            "startup logs at Information which host public reads route to");
        logs.Entries.Should().NotContain(
            entry => entry.Message.Contains(routedPassword, StringComparison.Ordinal),
            "the log never carries the password of the connection string in play");
        logs.Entries.Should().NotContain(
            entry => entry.Message.Contains("Password=", StringComparison.OrdinalIgnoreCase),
            "the log never carries a connection string's Password keyword");

        var plainPassword = $"pw-{Guid.NewGuid():N}";
        var unset = new TestLogCapture();
        await using var plain = CreateFactory(readHost: null, logs: unset, password: plainPassword);
        plain.CreateClient();

        unset.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("primary", StringComparison.Ordinal),
            "without Database:ReadHost the log says reads use the primary connection");
        unset.Entries.Should().NotContain(
            entry => entry.Message.Contains(plainPassword, StringComparison.Ordinal),
            "the log never carries the password of the connection string in play");
    }

    private string Host() => new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Host ?? string.Empty;

    private static string PrimaryName => "routing-primary";

    private BlogApiFactory CreateFactory(
        string? readHost, DbCommandInterceptor? interceptor = null, TestLogCapture? logs = null, string? password = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = PrimaryName
        };
        if (password is not null)
            builder.Password = password;
        var connectionString = builder.ConnectionString;

        // These tests are about which requests route to the replica. The refill window of brief 13c
        // (a refill within Croft:Api:Cache:PrimaryAfterInvalidation of an invalidation reads the
        // primary) would send the first read after the test's own seeding writes to the primary, so
        // it is off here; PrimaryAfterInvalidationTests covers it.
        var settings = new Dictionary<string, string?> { ["Croft:Api:Cache:PrimaryAfterInvalidation"] = "00:00:00" };
        if (readHost is not null)
            settings["Database:ReadHost"] = readHost;

        return new BlogApiFactory(
            connectionString,
            storagePath,
            configuration: settings,
            configureServices: services =>
            {
                // These tests drive the scheduler themselves and the cache listener is not
                // what routing is about.
                services.RemoveAll<IHostedService>();
                if (logs is not null)
                    services.AddSingleton<ILoggerProvider>(logs);
            },
            commandInterceptor: interceptor);
    }

    private static bool IsAnonymousGet(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return methods is not null
            && (methods.Contains("GET") || methods.Contains("HEAD"))
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
    }

    /// <summary>
    /// RoutePattern.RawText is the combined template without a leading slash. Every route
    /// parameter is substituted by name with the seeded input of the route's own collection,
    /// and an unknown parameter fails loudly instead of sweeping a URL that 404s.
    /// </summary>
    private static string UrlFor(string route, SeededInputs ids)
    {
        var slugValue = route.StartsWith("api/posts", StringComparison.Ordinal) ? ids.PostSlug
            : route.StartsWith("api/pages", StringComparison.Ordinal) ? ids.PageSlug
            : route.StartsWith("api/categories", StringComparison.Ordinal) ? ids.CategorySlug
            : ids.TagSlug;

        var parts = route.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            if (!parts[index].StartsWith('{'))
                continue;

            // A segment may carry a literal after its parameter ({hash}.webp): only the braces are replaced.
            var close = parts[index].IndexOf('}', StringComparison.Ordinal);
            var name = parts[index][1..close].Split(':')[0].TrimEnd('?');
            var value = name switch
            {
                "slug" => slugValue,
                "id" => ids.BannerId.ToString(),
                "hash" => ids.AvatarHash,
                _ => throw new InvalidOperationException($"No seeded input for route parameter '{name}' in '{route}'."),
            };
            parts[index] = value + parts[index][(close + 1)..];
        }

        var url = $"/{string.Join('/', parts)}";
        return route == "api/search" ? $"{url}?q=publicreadcache" : url;
    }

    private sealed record SeededInputs(Guid BannerId, string PostSlug, string PageSlug, string CategorySlug, string TagSlug, string AvatarHash);

    private static async Task<SeededInputs> SeedAsync(BlogApiFactory factory, HttpClient admin)
    {
        var category = await CreateCategoryAsync(admin, "Routing Cat");
        var post = await CreatePostAsync(admin, "Routing Sweep Post", categoryId: category.Id, tags: ["routing-tag"]);
        var page = await CreatePageAsync(admin, "Routing Sweep Page");
        var bannerId = await UploadBannerAsync(admin);
        return new SeededInputs(bannerId, post.Slug, page.Slug, category.Slug, "routing-tag", await SeedAvatarAsync(factory));
    }

    /// <summary>A stored social avatar (the public read of brief 12a): a row and a file, as a refresh leaves them.</summary>
    private static async Task<string> SeedAvatarAsync(BlogApiFactory factory)
    {
        var hash = new string('d', 64);
        await using var scope = factory.Services.CreateAsyncScope();
        var path = await scope.ServiceProvider.GetRequiredService<IFileStorage>()
            .SaveAsync(new MemoryStream([1, 2, 3]), $"{hash}.webp", "image/webp", "social-avatars");
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        db.SocialAvatars.Add(new SocialAvatar { Hash = hash, SourceUrl = "https://avatars.example/routing", StoragePath = path, FetchedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return hash;
    }

    private static async Task<CategoryDto> CreateCategoryAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/categories", name);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static async Task<PostDto> CreatePostAsync(
        HttpClient admin, string title, Guid? categoryId = null, IReadOnlyList<string>? tags = null)
    {
        var created = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "## A short routing body", Tags: tags, CategoryId: categoryId));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        var published = await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish",
            new PublishRequest(DateTime.UtcNow.AddMinutes(-1)));
        published.EnsureSuccessStatusCode();
        return (await published.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<PostDto> CreatePageAsync(HttpClient admin, string title)
    {
        var created = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest(title, "## A short routing page", Kind: PostKind.Page, NavigationPlacement: NavigationPlacement.Sidebar));
        created.EnsureSuccessStatusCode();
        var page = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        var published = await admin.PostAsJsonAsync($"/api/posts/{page.Id}/publish",
            new PublishRequest(DateTime.UtcNow.AddMinutes(-1)));
        published.EnsureSuccessStatusCode();
        return (await published.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<Guid> UploadBannerAsync(HttpClient admin)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var content = new MultipartFormDataContent
        {
            { file, "file", "routing-banner.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<MediaAssetDto>())!.Id;
    }

    private async Task InsertScheduledDuePostAsync(string slug)
    {
        var publishedAt = DateTime.UtcNow.AddMinutes(-5);
        await using var db = new BlogDbContext(
            new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        db.Posts.Add(new Post
        {
            Slug = slug,
            Title = slug,
            MarkdownContent = "## A short routing body",
            Status = PostStatus.Scheduled,
            Kind = PostKind.Post,
            PublishedAt = publishedAt,
            CreatedAt = publishedAt,
            UpdatedAt = publishedAt,
            ContentEditedAt = publishedAt,
            AuthorId = "routing-author",
            AuthorName = "Routing Test"
        });
        await db.SaveChangesAsync();
    }

    private static async Task RunSchedulerOnceAsync(BlogApiFactory factory)
    {
        var scheduler = new ScheduledPublisherHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScheduledPublisherHostedService>.Instance,
            TimeProvider.System,
            factory.Services.GetRequiredService<IScheduledPublishWakeup>(),
            TestCache.NewNotifier());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        await scheduler.PublishDuePostsAsync(db);
    }
}
