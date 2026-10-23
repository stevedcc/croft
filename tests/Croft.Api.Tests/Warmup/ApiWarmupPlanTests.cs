using System.Net;
using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Warmup;
using Croft.Warmup.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests.Warmup;

/// <summary>
/// Brief 14a item 3: the API's warm-up over a real database. The requests reach the very API host
/// that sends them (the TestServer's handler stands in for loopback), anonymously, and read the
/// content they need (the newest post, its category and tag, the first page) from the API's own
/// first answers.
/// </summary>
public sealed class ApiWarmupPlanTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Seeded_EveryWarmupRequestIs2xx_AndThePathsAreTheExpectedSet()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using (var db = scratch.CreateContext())
        {
            await ContentSeed.SeedAsync(db);
        }

        await using var run = await WarmUpAsync(scratch.ConnectionString, seedBannerFile: true);

        run.Paths.Should().Equal(
        [
            "/api/posts?page=1&pageSize=10&publicView=true",
            "/api/categories?top=12",
            "/api/pages/navigation/all",
            "/api/pages?page=1&pageSize=100",
            "/api/posts/hello-croft",
            "/api/posts/hello-croft/related?count=3",
            "/api/posts/hello-croft/discussion",
            "/api/media/00000000-0000-0000-0000-0000000000e1",
            "/api/posts?categorySlug=engineering&page=1&pageSize=10&publicView=true",
            "/api/posts?tagSlug=ai-assisted&page=1&pageSize=10&publicView=true",
            "/api/categories/engineering",
            "/api/tags/ai-assisted",
            "/api/categories?page=1&pageSize=24",
            "/api/tags?page=1&pageSize=100",
            "/api/search?q=Hello&page=1&pageSize=10&publicView=true",
            "/api/pages/about",
            "/api/posts?page=1&pageSize=20",
            "/api/sitemap",
        ], "the same query shapes the Web asks for, the content-dependent ones from the newest post and the first page");
        run.Responses.Where(r => (int)r.Status is < 200 or > 299).Should().BeEmpty("every warm-up request succeeds");
        run.Logs.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyDatabase_WarmsOnlyTheContentFreeRoutes_AndLogsNoWarning()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);

        await using var run = await WarmUpAsync(scratch.ConnectionString, seedBannerFile: false);

        run.Paths.Should().Equal(
        [
            "/api/posts?page=1&pageSize=10&publicView=true",
            "/api/categories?top=12",
            "/api/pages/navigation/all",
            "/api/pages?page=1&pageSize=100",
            "/api/categories?page=1&pageSize=24",
            "/api/tags?page=1&pageSize=100",
            "/api/search?q=blog&page=1&pageSize=10&publicView=true",
            "/api/posts?page=1&pageSize=20",
            "/api/sitemap",
        ], "an empty database has no post, category, tag, page or image to warm");
        run.Responses.Where(r => (int)r.Status is < 200 or > 299).Should().BeEmpty();
        run.Logs.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty("missing content is skipped, not failed");
    }

    private static async Task<WarmRun> WarmUpAsync(string connectionString, bool seedBannerFile)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(storagePath, "banners"));
        if (seedBannerFile)
        {
            // The seeded banner's row points at banners/e1.png: the media GET must find the file.
            await File.WriteAllBytesAsync(Path.Combine(storagePath, "banners", "e1.png"), [0x89, 0x50, 0x4E, 0x47]);
        }

        var recorder = new RecordingHandler();
        var loopback = new LateBoundHandler();
        var setup = new WarmupHostSetup();
        var factory = new BlogApiFactory(connectionString, storagePath).WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddHttpClient(WarmupRunner.ClientName)
                .AddHttpMessageHandler(() => new AnonymousHandler())
                .AddHttpMessageHandler(() => recorder)
                .ConfigurePrimaryHttpMessageHandler(() => loopback));
            setup.Apply(builder);
        });
        loopback.Ready(factory.Server.CreateHandler());
        await factory.Services.GetRequiredService<WarmupGate>().WhenOpen.WaitAsync(TimeSpan.FromSeconds(60));
        return new WarmRun(factory, storagePath, recorder, setup.Logs);
    }

    private sealed class WarmRun(IDisposable factory, string storagePath, RecordingHandler recorder, WarmupLogCapture logs) : IAsyncDisposable
    {
        public IReadOnlyList<string> Paths => [.. recorder.Paths];

        public IReadOnlyList<(string Path, HttpStatusCode Status)> Responses => [.. recorder.Responses];

        public IReadOnlyList<CapturedLog> Logs => [.. logs.FromRunner];

        public ValueTask DisposeAsync()
        {
            factory.Dispose();
            Directory.Delete(storagePath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The test host authenticates every request as a test user unless told otherwise; the
    /// warm-up's requests carry no session at all, so they say so.</summary>
    private sealed class AnonymousHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(TestAuthHandler.AnonymousHeader, "true");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
