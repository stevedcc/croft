using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Net;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13a item 1, the API side: the global limiter is chained with a per-client one (the
/// visitor address Web forwards beside the ingest key), /api/media has its own partition, and
/// Kestrel gets minimum data rates and a connection ceiling.
/// </summary>
public class ApiClientRequestLimitTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string IngestKey = "test-ingest-key";
    private const string PostsRead = "/api/posts?page=1&pageSize=10";

    [Fact]
    public async Task OneVisitorHoldingItsPermits_IsRefusedFast_WhileAnotherVisitorIsStillServed()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new HoldFirstReadsInterceptor(2, released.Task);
        await using var factory = CreateFactory(gate,
            ("Croft:Api:MaxConcurrentRequestsPerClient", "2"),
            ("Croft:Api:ClientQueueLimit", "0"),
            ("Croft:Api:RequestQueueLimit", "0"));
        using var client = factory.CreateClient();

        try
        {
            var heldOne = Get(client, PostsRead, "203.0.113.1");
            var heldTwo = Get(client, PostsRead, "203.0.113.1");
            await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            using var refused = await Get(client, PostsRead, "203.0.113.1").WaitAsync(TimeSpan.FromSeconds(5));
            refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            using var other = await Get(client, PostsRead, "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            other.StatusCode.Should().Be(HttpStatusCode.OK, "another visitor is served while the first is over its ceiling");

            released.TrySetResult();
            (await heldOne.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
            (await heldTwo.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            released.TrySetResult();
        }
    }

    [Fact]
    public async Task AVisitorAddress_IsHonouredOnlyBesideTheIngestKey()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new HoldFirstReadsInterceptor(1, released.Task);
        await using var factory = CreateFactory(gate,
            ("Croft:Api:MaxConcurrentRequestsPerClient", "1"),
            ("Croft:Api:ClientQueueLimit", "0"),
            ("Croft:Api:RequestQueueLimit", "0"));
        using var client = factory.CreateClient();

        try
        {
            var held = Get(client, PostsRead, "203.0.113.1");
            await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            // Without the key the header is ignored: the request has no client to count against,
            // so it is not limited per client (only the global limiter applies).
            var request = new HttpRequestMessage(HttpMethod.Get, PostsRead);
            request.Headers.Add("X-Visitor-IP", "203.0.113.1");
            using var unkeyed = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            unkeyed.StatusCode.Should().Be(HttpStatusCode.OK);

            using var keyed = await Get(client, PostsRead, "203.0.113.1").WaitAsync(TimeSpan.FromSeconds(5));
            keyed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            released.TrySetResult();
            (await held.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            released.TrySetResult();
        }
    }

    [Fact]
    public async Task APageRead_IsServed_WhileEveryMediaPermitIsHeld_AndMediaIsBounded()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new HoldFirstReadsInterceptor(1, released.Task);
        await using var factory = CreateFactory(gate,
            ("Croft:Api:MaxConcurrentRequests", "1"),
            ("Croft:Api:MaxConcurrentMediaRequests", "1"),
            ("Croft:Api:RequestQueueLimit", "0"));
        using var client = factory.CreateClient();

        try
        {
            var media = $"/api/media/{Guid.NewGuid()}";
            var held = Get(client, media, "203.0.113.1");
            await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            using var page = await Get(client, PostsRead, "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            page.StatusCode.Should().Be(HttpStatusCode.OK, "a held media read does not occupy the page partition");

            using var secondMedia = await Get(client, $"/api/media/{Guid.NewGuid()}", "203.0.113.3").WaitAsync(TimeSpan.FromSeconds(5));
            secondMedia.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the media partition is bounded");

            released.TrySetResult();
            (await held.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            released.TrySetResult();
        }
    }

    [Fact]
    public async Task KestrelLimits_DefaultToTheBriefsValues_AndAreConfigurable()
    {
        await using var defaults = CreateFactory(new HoldFirstReadsInterceptor(0, Task.CompletedTask));
        var limits = defaults.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        limits.MinResponseDataRate!.BytesPerSecond.Should().Be(2048);
        limits.MinResponseDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
        limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(2048);
        limits.MinRequestBodyDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
        limits.MaxConcurrentConnections.Should().Be(512);

        await using var custom = CreateFactory(new HoldFirstReadsInterceptor(0, Task.CompletedTask),
            ("Croft:Api:MinResponseDataRate", "4096"),
            ("Croft:Api:MinRequestBodyDataRate", "1024"),
            ("Croft:Api:MinDataRateGracePeriodSeconds", "3"),
            ("Croft:Api:MaxConcurrentConnections", "99"));
        var configured = custom.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        configured.MinResponseDataRate!.BytesPerSecond.Should().Be(4096);
        configured.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(1024);
        configured.MinResponseDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(3));
        configured.MaxConcurrentConnections.Should().Be(99);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string visitor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Visitor-IP", visitor);
        request.Headers.Add("X-Analytics-Key", IngestKey);
        return client.SendAsync(request);
    }

    private BlogApiFactory CreateFactory(DbCommandInterceptor gate, params (string Key, string Value)[] settings)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        var configuration = new Dictionary<string, string?> { ["Analytics:IngestKey"] = IngestKey };
        foreach (var (key, value) in settings)
            configuration[key] = value;

        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configuration: configuration,
            // The hosted jobs would run their own SQL; only the requests under test may read.
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: gate);
    }

    /// <summary>Holds the first <c>count</c> SQL reads until the gate opens; later reads pass.</summary>
    private sealed class HoldFirstReadsInterceptor(int count, Task gate) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        /// <summary>Completes when the last of the held reads has been reached.</summary>
        public Task Arrived => _arrived.Task;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var number = Interlocked.Increment(ref _seen);
            if (number > count)
                return result;

            if (number == count)
                _arrived.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }
}
