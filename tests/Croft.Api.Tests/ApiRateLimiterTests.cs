using Croft.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Data.Common;
using System.Net;
using AwesomeAssertions;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09b item 6: the API sheds load the same way — beyond Croft:Api:MaxConcurrentRequests in
/// flight plus Croft:Api:RequestQueueLimit waiting, /api/* requests get a fast 503 with
/// Retry-After while /health stays served.
/// </summary>
public class ApiRateLimiterTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task ABurstBeyondTheLimit_GetsAFast503WithRetryAfter_WhileHealthStaysUp()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new HeldReaderInterceptor(released.Task);
        await using var factory = CreateFactory(gate);
        using var client = factory.CreateClient();

        try
        {
            // One request sits inside the handler (its SQL is held), holding the single permit
            // with no queue behind it.
            var held = client.GetAsync("/api/posts?page=1&pageSize=10");
            await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            var rejected = await client.GetAsync("/api/posts?page=1&pageSize=10").WaitAsync(TimeSpan.FromSeconds(1));
            rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            rejected.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(5),
                "the shed request tells the caller when to come back");

            using var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK, "health probes are exempt from the shed");

            released.TrySetResult();
            (await held.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.Should().Be(HttpStatusCode.OK,
                "the held request completes once its SQL returns");

            // The permit frees as the held request's response unwinds, so wait for the condition
            // instead of racing it.
            await WaitUntilAsync(
                async () => (await client.GetAsync("/api/posts?page=1&pageSize=10")).StatusCode == HttpStatusCode.OK,
                TimeSpan.FromSeconds(5),
                "after release a new request succeeds");
        }
        finally
        {
            released.TrySetResult();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        (await condition()).Should().BeTrue($"condition should have been met within {timeout.TotalSeconds:0} s: {what}");
    }

    private BlogApiFactory CreateFactory(DbCommandInterceptor gate)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configuration: new Dictionary<string, string?>
            {
                ["Croft:Api:MaxConcurrentRequests"] = "1",
                ["Croft:Api:RequestQueueLimit"] = "0"
            },
            // The hosted jobs would run their own SQL on their own schedule; this test holds
            // every read, so only the request under test may be waiting on one.
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: gate);
    }

    /// <summary>
    /// Holds every SQL read until the gate opens, so the first request sits inside the handler
    /// with its permit held (the HeldCommandInterceptor pattern, for reads).
    /// </summary>
    private sealed class HeldReaderInterceptor(Task gate) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes as soon as the first held read has been reached.</summary>
        public Task Arrived => _arrived.Task;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _arrived.TrySetResult();
            // Bounded so a gate that never releases surfaces as a failed test, not a hung run.
            await gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }
}
