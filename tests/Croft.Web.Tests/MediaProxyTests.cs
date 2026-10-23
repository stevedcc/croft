using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// The Web app's <c>/api/media/{id}</c> proxy: the browser asks the Web app, the Web app asks the API
/// (test-suite review 2026-10-19, PR #4 I-7/I-8 test gaps): the API's <c>Cache-Control</c> decides how long
/// a browser and a CDN keep a published image and that a draft's is never stored, so it must reach the
/// browser unchanged; the visitor's address goes to the API for its per-client limit; and the API
/// connection and its permit are released when the copy ends.
/// </summary>
public class MediaProxyTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData("public, max-age=86400")]
    [InlineData("no-store")]
    public async Task TheApisCacheControl_ContentType_AndBody_ReachTheBrowserUnchanged(string cacheControl)
    {
        var api = new MediaApiHandler(_ => Image(cacheControl));
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        using var response = await client.GetAsync($"/api/media/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be(cacheControl, "the proxy must not decide what a browser may cache");
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
        api.Requests.Should().ContainSingle().Which.Path.Should().Be($"/api/media/{id}");
    }

    [Fact]
    public async Task AnApiRefusal_IsPassedOn_WithNoBody()
    {
        var api = new MediaApiHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret detail") });
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/media/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("the API's error body is not echoed to the browser");
    }

    [Fact]
    public async Task TheRequestToTheApi_CarriesTheVisitorsAddressAndTheIngestKey()
    {
        var api = new MediaApiHandler(_ => Image("public, max-age=86400"));
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/media/{Guid.NewGuid()}");

        response.EnsureSuccessStatusCode();
        var request = api.Requests.Should().ContainSingle().Subject;
        request.Headers["X-Visitor-IP"].Should().Be("203.0.113.9", "the API's per-client limit counts the visitor, not this pod");
        request.Headers["X-Analytics-Key"].Should().Be("test-ingest-key");
    }

    [Fact]
    public async Task TheApiResponse_IsDisposed_WhenTheCopyHasEnded()
    {
        var content = new TrackedContent(Png);
        var api = new MediaApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/media/{Guid.NewGuid()}");
        _ = await response.Content.ReadAsByteArrayAsync();

        content.Disposed.Should().BeTrue("the API connection is held until the body is copied, then released with its permit");
    }

    private static HttpResponseMessage Image(string cacheControl)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        response.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
        return response;
    }

    private static WebApplicationFactory<Program> CreateFactory(MediaApiHandler api) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse("203.0.113.9")));
            });
        });

    private sealed class MediaApiHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private readonly List<(string Path, Dictionary<string, string> Headers)> _requests = [];

        public IReadOnlyList<(string Path, Dictionary<string, string> Headers)> Requests
        {
            get { lock (_requests) { return [.. _requests]; } }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add((request.RequestUri!.AbsolutePath,
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            }

            return Task.FromResult(answer(request));
        }
    }

    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
