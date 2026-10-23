using System.Net;
using System.Text;
using System.Text.Json;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13a review: forwarded headers are processed exactly once. In k8s the proxy appends the
/// real client to X-Forwarded-For; ForwardLimit 1 takes that last entry. A second pass (the
/// environment variable's startup filter plus an explicit UseForwardedHeaders) would take the
/// entry before it, which the client wrote itself, and make the client address forgeable: the
/// per-client limiter, the API's visitor key and the analytics IP hash all rest on it. The
/// factory has the k8s base's setting (ForwardedHeaders:KnownNetworks = the k3s pod network) and a
/// peer inside it. Brief 13b: only the trusted networks decide the client address, so a peer
/// outside them, or an empty setting, leaves the headers ignored.
/// </summary>
public class ForwardedHeadersOnceTests
{
    private const string Forged = "6.6.6.6, 7.7.7.7";

    [Fact]
    public async Task AMultiEntryForwardedFor_YieldsTheLastEntry_AsTheVisitorAddressSentToTheApi()
    {
        var api = new RecordingHandler();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new PageViewRequest(Guid.NewGuid(), null), JsonSerializerOptions.Web);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-Forwarded-For", Forged);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        api.VisitorAddresses.Should().ContainSingle().Which.Should().Be("7.7.7.7",
            "the address the proxy appended is the client; the entry before it is client-written");
    }

    [Fact]
    public async Task ThePerClientLimiter_KeysOnTheLastForwardedForEntry()
    {
        var api = new RecordingHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentRequestsPerClient", "1"),
            ("Croft:Web:ClientQueueLimit", "0"));
        using var client = factory.CreateClient();

        try
        {
            var held = Get(client, "/post/held", "6.6.6.6, 7.7.7.7");
            await api.HeldArrival;

            // Same real client (7.7.7.7) behind a different forged prefix: still that client.
            using var sameClient = await Get(client, "/", "9.9.9.9, 7.7.7.7").WaitAsync(TimeSpan.FromSeconds(5));
            sameClient.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            using var otherClient = await Get(client, "/", "6.6.6.6, 8.8.8.8").WaitAsync(TimeSpan.FromSeconds(5));
            otherClient.StatusCode.Should().Be(HttpStatusCode.OK);

            api.Release();
            (await held.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task APeerInsideTheKnownNetwork_IsTrusted_ItsForwardedForIsTheVisitor()
    {
        (await VisitorAddressAsync("203.0.113.7", peer: "10.42.1.5", networks: "10.42.0.0/16")).Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task APeerOutsideTheKnownNetwork_IsNotTrusted_ItsForwardedForIsIgnored()
    {
        (await VisitorAddressAsync("203.0.113.7", peer: "192.0.2.9", networks: "10.42.0.0/16")).Should().Be("192.0.2.9",
            "any other workload reaching the pod must not be able to choose a client address");
    }

    [Fact]
    public async Task WithNoKnownNetworks_NoProxyIsTrusted_TheForwardedForIsIgnored()
    {
        (await VisitorAddressAsync("203.0.113.7", peer: "10.42.1.5", networks: null)).Should().Be("10.42.1.5");
        (await VisitorAddressAsync("203.0.113.7", peer: "127.0.0.1", networks: null)).Should().Be("127.0.0.1",
            "not even loopback is trusted by default");
    }

    [Fact]
    public void AnEntryThatIsNotACidr_FailsStartup_NamingIt()
    {
        using var factory = CreateFactory(new RecordingHandler(), ("ForwardedHeaders:KnownNetworks:0", "10.42.0.0"));

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*'10.42.0.0' is not a CIDR*");
    }

    private static async Task<string?> VisitorAddressAsync(string forwardedFor, string peer, string? networks)
    {
        var api = new RecordingHandler();
        using var factory = CreateFactory(api, peer, networks is null ? [] : [("ForwardedHeaders:KnownNetworks:0", networks)]);
        using var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new PageViewRequest(Guid.NewGuid(), null), JsonSerializerOptions.Web);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return api.VisitorAddresses.Single();
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> CreateFactory(RecordingHandler api, params (string Key, string Value)[] settings) =>
        CreateFactory(api, "10.42.1.5", [("ForwardedHeaders:KnownNetworks:0", "10.42.0.0/16"), .. settings]);

    private static WebApplicationFactory<Program> CreateFactory(
        RecordingHandler api, string peer, (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                // The peer the test host presents: the TestServer has no socket address.
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(peer)));
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler())
                    .AddHttpMessageHandler(() => api);
            });
        });

    /// <summary>Records the X-Visitor-IP of every call and holds the post page "held" until released.</summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> VisitorAddresses { get; } = [];

        public Task HeldArrival => _arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("X-Visitor-IP", out var values))
            {
                lock (VisitorAddresses) { VisitorAddresses.AddRange(values); }
            }

            if (request.RequestUri!.AbsolutePath == "/api/posts/held")
            {
                _arrived.TrySetResult();
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
