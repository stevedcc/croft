using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13a item 1: slow readers can't exhaust the request permits. The global limiter is
/// chained with a per-client one (a client is the forwarded address), /api/media/ has its own
/// partition, and Kestrel drops connections that read or send below a minimum data rate.
/// </summary>
public class ClientRequestLimitTests
{
    private static readonly Guid HeldMediaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FastMediaId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task OneClientHoldingItsPermits_IsRefusedFast_WhileAnotherClientIsStillServed()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentRequestsPerClient", "2"),
            ("Croft:Web:ClientQueueLimit", "0"),
            ("Croft:Web:RequestQueueLimit", "0"));
        using var client = factory.CreateClient();

        try
        {
            // Client A's two slow requests sit inside the handler, each holding a permit.
            var heldOne = Get(client, "/post/held", "203.0.113.1");
            var heldTwo = Get(client, "/post/held", "203.0.113.1");
            await api.HeldArrivals(2);

            using var refused = await Get(client, "/", "203.0.113.1").WaitAsync(TimeSpan.FromSeconds(5));
            refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "a client over its own ceiling is refused, whatever the global count");
            refused.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(5));

            using var other = await Get(client, "/", "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            other.StatusCode.Should().Be(HttpStatusCode.OK, "another client is served while the first one is over its ceiling");

            api.Release();
            (await heldOne.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
            (await heldTwo.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task AClientBeyondItsPermits_QueuesUpToItsQueueLimit_ThenIsRefused_WhileAnotherClientIsServed()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentRequestsPerClient", "1"),
            ("Croft:Web:ClientQueueLimit", "1"));
        using var client = factory.CreateClient();

        try
        {
            var held = Get(client, "/post/held", "203.0.113.1");
            await api.HeldArrivals(1);
            // Two more requests: one takes the queue's single slot and waits, the other finds
            // permits and queue full and is refused at once (which one is a race, so both are
            // watched).
            var one = Get(client, "/", "203.0.113.1");
            var two = Get(client, "/", "203.0.113.1");
            var refusedTask = await Task.WhenAny(one, two).WaitAsync(TimeSpan.FromSeconds(5));
            using var refused = await refusedTask;
            refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "permits and queue are both full");
            var queued = refusedTask == one ? two : one;
            queued.IsCompleted.Should().BeFalse("the queued request waits for a permit");

            using var other = await Get(client, "/", "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            other.StatusCode.Should().Be(HttpStatusCode.OK, "a client holding all its permits and its queue blocks nobody else");

            api.Release();
            (await held.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
            (await queued.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.Should().Be(HttpStatusCode.OK,
                "the queued request is served once a permit frees");
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task OneClientFiring40ConcurrentMediaRequests_GetsAllServed_SomeQueued_NoneRefused()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        try
        {
            // Every request is held upstream until released: 16 hold the client's media permits,
            // the other 24 wait in its queue (defaults 16 permits + 32 queue).
            var requests = Enumerable.Range(0, 40)
                .Select(_ => Get(client, $"/api/media/{HeldMediaId}", "203.0.113.1")).ToList();
            await api.HeldArrivals(16);
            requests.Count(r => r.IsCompleted).Should().Be(0, "nothing is served or refused while the permits are held");

            api.Release();
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60));
            responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.OK,
                "an image-heavy page's own images are queued, never refused");
            foreach (var response in responses)
                response.Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task APagesImages_DoNotCompeteWithThePageForTheClientsPermits()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentMediaRequestsPerClient", "1"),
            ("Croft:Web:MaxConcurrentRequestsPerClient", "1"),
            ("Croft:Web:ClientQueueLimit", "0"));
        using var client = factory.CreateClient();

        try
        {
            var heldMedia = Get(client, $"/api/media/{HeldMediaId}", "203.0.113.1");
            await api.HeldArrivals(1);

            using var page = await Get(client, "/", "203.0.113.1").WaitAsync(TimeSpan.FromSeconds(5));
            page.StatusCode.Should().Be(HttpStatusCode.OK, "the same client's page has its own permits");

            api.Release();
            (await heldMedia.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task APageRequest_IsServed_WhileEveryMediaPermitIsHeld()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentRequests", "1"),
            ("Croft:Web:RequestQueueLimit", "0"),
            ("Croft:Web:MaxConcurrentMediaRequests", "1"));
        using var client = factory.CreateClient();

        try
        {
            var heldMedia = Get(client, $"/api/media/{HeldMediaId}", "203.0.113.1");
            await api.HeldArrivals(1);

            using var page = await Get(client, "/", "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            page.StatusCode.Should().Be(HttpStatusCode.OK,
                "a page does not need a page permit-partition slot held by a slow image");

            using var secondMedia = await Get(client, $"/api/media/{FastMediaId}", "203.0.113.3").WaitAsync(TimeSpan.FromSeconds(5));
            secondMedia.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "the media partition itself is bounded");

            api.Release();
            (await heldMedia.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task AMediaRequest_IsServed_WhileEveryPagePermitIsHeld()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentRequests", "1"),
            ("Croft:Web:RequestQueueLimit", "0"),
            ("Croft:Web:MaxConcurrentMediaRequests", "1"));
        using var client = factory.CreateClient();

        try
        {
            var heldPage = Get(client, "/post/held", "203.0.113.1");
            await api.HeldArrivals(1);

            using var media = await Get(client, $"/api/media/{FastMediaId}", "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            media.StatusCode.Should().Be(HttpStatusCode.OK, "images are not starved by slow pages");
            (await media.Content.ReadAsByteArrayAsync()).Should().Equal(HoldingApiHandler.ImageBytes);

            api.Release();
            (await heldPage.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public async Task AnAvatarRequest_CountsAsMedia_AndIsServed_WhileEveryPagePermitIsHeld()
    {
        var api = new HoldingApiHandler();
        using var factory = CreateFactory(api,
            ("Croft:Web:MaxConcurrentRequests", "1"),
            ("Croft:Web:RequestQueueLimit", "0"),
            ("Croft:Web:MaxConcurrentMediaRequests", "1"));
        using var client = factory.CreateClient();

        try
        {
            var heldPage = Get(client, "/post/held", "203.0.113.1");
            await api.HeldArrivals(1);

            using var avatar = await Get(client, $"/social/avatar/{new string('c', 64)}.webp", "203.0.113.2").WaitAsync(TimeSpan.FromSeconds(5));
            avatar.StatusCode.Should().Be(HttpStatusCode.OK, "an avatar is an image: it has the media partition, not the pages'");
            (await avatar.Content.ReadAsByteArrayAsync()).Should().Equal(HoldingApiHandler.ImageBytes);

            api.Release();
            (await heldPage.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        }
        finally
        {
            api.Release();
        }
    }

    [Fact]
    public void KestrelLimits_DefaultToTheBriefsValues_AndAreConfigurable()
    {
        using var defaults = CreateFactory(new HoldingApiHandler());
        var limits = defaults.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        limits.MinResponseDataRate!.BytesPerSecond.Should().Be(2048);
        limits.MinResponseDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
        limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(2048);
        limits.MinRequestBodyDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
        limits.MaxConcurrentConnections.Should().Be(512);

        using var custom = CreateFactory(new HoldingApiHandler(),
            ("Croft:Web:MinResponseDataRate", "4096"),
            ("Croft:Web:MinRequestBodyDataRate", "1024"),
            ("Croft:Web:MinDataRateGracePeriodSeconds", "3"),
            ("Croft:Web:MaxConcurrentConnections", "99"));
        var configured = custom.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        configured.MinResponseDataRate!.BytesPerSecond.Should().Be(4096);
        configured.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(1024);
        configured.MinResponseDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(3));
        configured.MaxConcurrentConnections.Should().Be(99);
    }

    [Fact]
    public async Task AResponseReadSlowerThanTheMinimumRate_IsAborted_AndTheUpstreamReleased()
    {
        var api = new HoldingApiHandler();
        // A pending write is allowed its size at the minimum rate plus the grace; 1 MiB/s keeps
        // that to about two seconds for the proxy's 80 KiB copy buffer.
        using var factory = CreateFactory(api,
            ("Croft:Web:MinResponseDataRate", "1048576"),
            ("Croft:Web:MinDataRateGracePeriodSeconds", "2"));
        StartOnRandomPort(factory);

        using var socket = new TcpClient();
        var address = ServerUri(factory);
        await socket.ConnectAsync(address.Host, address.Port);
        var stream = socket.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET /api/media/{HoldingApiHandler.EndlessMediaId} HTTP/1.1\r\nHost: {address.Authority}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        // The client never reads: no bytes drain, so after the grace period Kestrel aborts the
        // response, the proxy's copy fails and the upstream response is disposed.
        await api.EndlessStreamDisposed.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ARequestBodySentSlowerThanTheMinimumRate_IsAborted()
    {
        using var factory = CreateFactory(new HoldingApiHandler(),
            ("Croft:Web:MinDataRateGracePeriodSeconds", "2"));
        StartOnRandomPort(factory);

        using var socket = new TcpClient();
        var address = ServerUri(factory);
        await socket.ConnectAsync(address.Host, address.Port);
        var stream = socket.GetStream();
        // Promises a body, then sends none of it.
        var request = Encoding.ASCII.GetBytes(
            $"POST /api/analytics/pageview HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var buffer = new byte[1024];
        var received = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            received.Append(Encoding.ASCII.GetString(buffer, 0, read));

        received.ToString().Should().Contain("408", "Kestrel answers a stalled request body with 408 and closes");
    }

    private static void StartOnRandomPort(WebApplicationFactory<Program> factory)
    {
        factory.UseKestrel();
        factory.StartServer();
    }

    private static Uri ServerUri(WebApplicationFactory<Program> factory) =>
        new(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string clientAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", clientAddress);
        return client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        HoldingApiHandler api, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            // As in the k8s base: forwarded headers from the proxy in front (here the loopback peers of the Kestrel tests).
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "127.0.0.0/8");
            // Only the Kestrel tests bind a socket; a random loopback port keeps them off any running stack.
            builder.UseSetting("urls", "http://127.0.0.1:0");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler())
                    .AddHttpMessageHandler(() => api);
            });
        });

    /// <summary>
    /// Answers the media proxy's upstream calls itself and holds the "held" ones: the post page
    /// for the slug "held" and the media asset <see cref="HeldMediaId"/> wait until released.
    /// </summary>
    private sealed class HoldingApiHandler : DelegatingHandler
    {
        public static readonly byte[] ImageBytes = [1, 2, 3, 4];
        public static readonly Guid EndlessMediaId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _endlessDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TaskCompletionSource> _arrivals = [];
        private int _held;

        public Task EndlessStreamDisposed => _endlessDisposed.Task;

        public void Release() => _released.TrySetResult();

        public Task HeldArrivals(int count)
        {
            lock (_arrivals)
            {
                while (_arrivals.Count < count)
                    _arrivals.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                return _arrivals[count - 1].Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/health")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Healthy") };

            if (path == "/api/posts/held" || path == $"/api/media/{HeldMediaId}")
            {
                MarkArrived();
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            if (path.StartsWith("/api/social/avatars/", StringComparison.Ordinal))
            {
                var avatar = new ByteArrayContent(ImageBytes);
                avatar.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = avatar };
            }

            if (path.StartsWith("/api/media/", StringComparison.Ordinal))
            {
                HttpContent content = path == $"/api/media/{EndlessMediaId}"
                    ? new StreamContent(new EndlessStream(() => _endlessDisposed.TrySetResult()))
                    : new ByteArrayContent(ImageBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return await base.SendAsync(request, cancellationToken);
        }

        private void MarkArrived()
        {
            var index = Interlocked.Increment(ref _held) - 1;
            lock (_arrivals)
            {
                while (_arrivals.Count <= index)
                    _arrivals.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                _arrivals[index].TrySetResult();
            }
        }
    }

    /// <summary>A body that never ends (zeros); reports when the proxy lets go of it.</summary>
    private sealed class EndlessStream(Action disposed) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                disposed();
            base.Dispose(disposing);
        }
    }
}
