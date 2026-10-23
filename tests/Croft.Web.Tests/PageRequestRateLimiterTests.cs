using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09b item 6: beyond Croft:Web:MaxConcurrentRequests in flight plus
/// Croft:Web:RequestQueueLimit waiting, page requests get a fast 503 with Retry-After instead of
/// queueing until croft-web runs out of memory — and health probes and static files stay served
/// while the shed is happening.
/// </summary>
public class PageRequestRateLimiterTests
{
    [Fact]
    public async Task ABurstBeyondTheLimit_SurfacesFast503sWithRetryAfter_WhileHealthAndStaticFilesStayUp()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new BlockingApiHandler(released.Task);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Croft:Web:MaxConcurrentRequests", "1");
            builder.UseSetting("Croft:Web:RequestQueueLimit", "0");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler())
                    .AddHttpMessageHandler(() => api);
            });
        });
        using var client = factory.CreateClient();

        try
        {
            // One page request sits inside the handler (its API call is blocked), holding the
            // single permit with no queue behind it.
            var held = client.GetAsync("/");
            await api.Arrived;

            var rejected = await client.GetAsync("/").WaitAsync(TimeSpan.FromSeconds(1));
            rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            rejected.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(5),
                "the shed request tells the reader when to come back");

            using var health = await client.GetAsync("/health/ready");
            health.StatusCode.Should().Be(HttpStatusCode.OK, "health probes are exempt from the shed");

            using var font = await client.GetAsync("/fonts/CroftSerif-Regular.woff2");
            font.StatusCode.Should().Be(HttpStatusCode.OK, "static files are exempt from the shed");

            released.TrySetResult();
            (await held.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.Should().Be(HttpStatusCode.OK,
                "the held request completes once its API call returns");

            // The permit frees as the held request's response unwinds, so wait for the condition
            // instead of racing it.
            await WaitUntilAsync(
                async () => (await client.GetAsync("/")).StatusCode == HttpStatusCode.OK,
                TimeSpan.FromSeconds(5),
                "after release a new page request succeeds");
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

    /// <summary>
    /// Blocks every BlogApi call except the health one until the gate opens, so a page request
    /// sits inside the handler with its permit held.
    /// </summary>
    private sealed class BlockingApiHandler(Task gate) : DelegatingHandler
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Healthy") };

            _arrived.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
