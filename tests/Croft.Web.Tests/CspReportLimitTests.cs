using System.Buffers;
using System.Net;
using System.Text;
using Croft.Web.Security.Csp;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13b item 6 (review finding 11): /csp-report is anonymous and exempt from the request
/// limiters, so its own limiter must not be rotated around (a client with a whole IPv6 /64 owns
/// 2^64 addresses), must not grow without bound (a bucket per client), must have a ceiling for the
/// whole pod, and must refuse before it allocates the body buffer. The limits are per pod.
/// </summary>
public class CspReportLimitTests
{
    private const string Report = """{"csp-report":{"violated-directive":"img-src"}}""";

    [Fact]
    public async Task SixtyOneReportsFromSixtyOneAddressesOfOneSlash64_The61stIsRefused()
    {
        using var factory = CreateFactory(new CountingPool());
        using var client = factory.CreateClient();

        for (var n = 1; n <= 60; n++)
        {
            (await PostAsync(client, $"2001:db8:1:2::{n:x}")).Should().Be(HttpStatusCode.NoContent, $"report {n} is within the limit");
        }

        (await PostAsync(client, "2001:db8:1:2::ffff")).Should().Be(HttpStatusCode.TooManyRequests,
            "the whole /64 is one client: rotating the address inside it buys no extra reports");
        (await PostAsync(client, "2001:db8:1:3::1")).Should().Be(HttpStatusCode.NoContent, "another /64 is another client");
    }

    [Fact]
    public async Task WhenTheBucketTableIsFull_ANewClientIsRefused_AnExistingOneIsNot()
    {
        using var factory = CreateFactory(new CountingPool(), ("Csp:ReportMaxClients", "3"));
        using var client = factory.CreateClient();

        foreach (var peer in new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" })
        {
            (await PostAsync(client, peer)).Should().Be(HttpStatusCode.NoContent);
        }

        (await PostAsync(client, "192.0.2.4")).Should().Be(HttpStatusCode.TooManyRequests,
            "with the table full a new client is refused until the sweep frees space");
        (await PostAsync(client, "192.0.2.1")).Should().Be(HttpStatusCode.NoContent, "a client already in the table keeps its budget");
    }

    [Fact]
    public async Task ReportsAcrossManyClients_StopAtTheGlobalCeiling_601stIsRefused()
    {
        using var factory = CreateFactory(new CountingPool());
        using var client = factory.CreateClient();

        for (var n = 1; n <= 600; n++)
        {
            (await PostAsync(client, PeerNumber(n))).Should().Be(HttpStatusCode.NoContent, $"report {n} is under the ceiling");
        }

        (await PostAsync(client, PeerNumber(601))).Should().Be(HttpStatusCode.TooManyRequests,
            "601 reports in a minute across clients exceed Csp:ReportGlobalPerMinute (600)");
    }

    [Fact]
    public async Task ARefusedReport_AllocatesNoBodyBuffer()
    {
        var pool = new CountingPool();
        using var factory = CreateFactory(pool, ("Csp:ReportRateLimitPerMinute", "1"), ("Csp:ReportGlobalPerMinute", "2"));
        using var client = factory.CreateClient();

        (await PostAsync(client, "192.0.2.1")).Should().Be(HttpStatusCode.NoContent);
        pool.Rents.Should().Be(1, "an accepted report buffers its body");

        (await PostAsync(client, "192.0.2.1")).Should().Be(HttpStatusCode.TooManyRequests, "over the client's limit");
        (await PostAsync(client, "192.0.2.2")).Should().Be(HttpStatusCode.NoContent);
        (await PostAsync(client, "192.0.2.3")).Should().Be(HttpStatusCode.TooManyRequests, "over the pod's ceiling");

        pool.Rents.Should().Be(2, "the two refused reports allocated no body buffer: the limiter runs first");
    }

    [Fact]
    public void TheDefaults_AreTenThousandClientsAndSixHundredReportsPerMinute()
    {
        var options = new CspOptions();

        options.ReportMaxClients.Should().Be(10_000);
        options.ReportGlobalPerMinute.Should().Be(600);
        options.ReportRateLimitPerMinute.Should().Be(60);
    }

    [Fact]
    public void TheTenThousandAndFirstDistinctClient_IsRefused_UntilTheSweepFreesSpace()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        // The global ceiling is lifted here: it is its own test.
        var limiter = new CspReportRateLimiter(
            Microsoft.Extensions.Options.Options.Create(new CspOptions { ReportGlobalPerMinute = int.MaxValue }), clock);

        for (var n = 0; n < 10_000; n++)
        {
            limiter.TryAccept($"client-{n}").Should().BeTrue($"client {n} fits in the table");
        }

        limiter.TryAccept("client-10000").Should().BeFalse("the 10 001st distinct client does not fit");
        limiter.TryAccept("client-0").Should().BeTrue("a client in the table keeps its budget");

        clock.UtcNow = clock.UtcNow.AddMinutes(1).AddSeconds(1);

        limiter.TryAccept("client-10000").Should().BeTrue("the sweep removed the buckets of the minute before");
    }

    private static string PeerNumber(int n) => $"10.{n / 250}.{n % 250}.1";

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string peer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/csp-report")
        {
            Content = new StringContent(Report, Encoding.UTF8, "application/csp-report"),
        };
        request.Headers.Add(PeerFromHeaderStartupFilter.Header, peer);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static WebApplicationFactory<Program> CreateFactory(CountingPool pool, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseEnvironment("Production");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, PeerFromHeaderStartupFilter>();
                services.AddSingleton<ArrayPool<byte>>(pool);
            });
        });

    /// <summary>Counts how often the endpoint asks for a buffer.</summary>
    private sealed class CountingPool : ArrayPool<byte>
    {
        private int _rents;

        public int Rents => Volatile.Read(ref _rents);

        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref _rents);
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
        }
    }

    /// <summary>Gives each test request the peer address named in a header (the TestServer has no socket address).</summary>
    private sealed class PeerFromHeaderStartupFilter : IStartupFilter
    {
        public const string Header = "X-Test-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[Header].ToString());
                return nextMiddleware();
            });
            next(app);
        };
    }
}
