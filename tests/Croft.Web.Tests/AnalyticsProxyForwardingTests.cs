using System.Net;
using System.Text;
using System.Text.Json;
using Croft.Shared.Contracts;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// The Web app's page-view beacon proxy is what makes per-visitor deduplication work: it forwards
/// the browser's address (<c>X-Visitor-IP</c>) with the shared ingest key (<c>X-Analytics-Key</c>), and
/// without them the API keys every view on the Web pod's own address, so one view per post per day
/// across all readers. Nothing else tests it: E2E runs the browser and the Web pod on 127.0.0.1, where
/// the forwarded address and the socket address are the same string (review 2026-10-19, I-15).
/// </summary>
public class AnalyticsProxyForwardingTests
{
    private const string BeaconPath = "/api/analytics/pageview";
    private const string VisitorAddress = "203.0.113.9";
    private const string IngestKey = "test-ingest-key";

    [Fact]
    public async Task ABeacon_IsForwardedWithTheVisitorsAddress_TheIngestKey_TheBodyAndTheUserAgent()
    {
        var api = new CapturingApiHandler();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();
        var beacon = new PageViewRequest(Guid.NewGuid(), "https://referrer.example/page");
        using var request = new HttpRequestMessage(HttpMethod.Post, BeaconPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(beacon, JsonSerializerOptions.Web), Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "BeaconTest/1.0");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var forwarded = api.Forwarded.Should().ContainSingle(r => r.Path == BeaconPath).Subject;
        forwarded.Method.Should().Be(HttpMethod.Post);
        forwarded.Headers["X-Visitor-IP"].Should().Be(VisitorAddress,
            "the API dedupes per visitor: it needs the browser's address, not the Web pod's");
        forwarded.Headers["X-Analytics-Key"].Should().Be(IngestKey,
            "the API honours X-Visitor-IP only beside the shared ingest key");
        forwarded.Headers["User-Agent"].Should().Be("BeaconTest/1.0");
        using var body = JsonDocument.Parse(forwarded.Body);
        body.RootElement.GetProperty("postId").GetGuid().Should().Be(beacon.PostId);
        body.RootElement.GetProperty("referrer").GetString().Should().Be(beacon.Referrer);
    }

    [Fact]
    public async Task ABeaconFromSignedInStaff_IsNotForwardedAtAll()
    {
        var api = new CapturingApiHandler();
        using var factory = CreateFactory(api);
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("staff-session", new TokenEntry("at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1)));
        var cookie = await AuthCookie.SignInAsync(factory.Services, "staff-session", "Admin");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, BeaconPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(new PageViewRequest(Guid.NewGuid()), JsonSerializerOptions.Web), Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "analytics never breaks a page: the beacon is answered either way");
        api.Forwarded.Should().BeEmpty("staff previewing their own posts must not inflate the counts");
    }

    private static WebApplicationFactory<Program> CreateFactory(CapturingApiHandler api) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("Analytics:IngestKey", IngestKey);
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(VisitorAddress)));
            });
        });

    /// <summary>Records every request the Web app forwards to the API, with its headers and body.</summary>
    private sealed class CapturingApiHandler : HttpMessageHandler
    {
        private readonly List<ForwardedRequest> _forwarded = [];

        public IReadOnlyList<ForwardedRequest> Forwarded
        {
            get { lock (_forwarded) { return [.. _forwarded]; } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath == BeaconPath)
            {
                lock (_forwarded)
                {
                    _forwarded.Add(new ForwardedRequest(request.Method, request.RequestUri.AbsolutePath, headers, body));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed record ForwardedRequest(HttpMethod Method, string Path, Dictionary<string, string> Headers, string Body);
}
