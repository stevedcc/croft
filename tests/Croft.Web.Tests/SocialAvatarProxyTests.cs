using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 12b item 2: <c>GET /social/avatar/{hash}.webp</c>, the Web's proxy to the API's stored 48 x 48 avatars.
/// Only a 64-character lower-case hex hash is asked for; the bytes and the two headers that matter are replayed;
/// an API failure is a bare 503; a public avatar is kept in the pod like an image (brief 14c).
/// </summary>
public class SocialAvatarProxyTests
{
    private static readonly string Hash = new string('a', 32) + new string('0', 32);
    private static readonly byte[] Webp = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50];
    private const string ImmutableYear = "public, max-age=31536000, immutable";

    [Fact]
    public async Task AValidHash_IsProxied_WithTheBytesAndTheHeadersReplayed()
    {
        var api = new Api(_ => Avatar());
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/social/avatar/{Hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Webp);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
        response.Headers.CacheControl!.ToString().Should().Be(ImmutableYear);
        api.Requests.Should().ContainSingle().Which.Path.Should().Be($"/api/social/avatars/{Hash}.webp");
    }

    [Fact]
    public async Task TheRequestToTheApi_CarriesTheVisitorsAddressAndTheIngestKey()
    {
        var api = new Api(_ => Avatar());
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/social/avatar/{Hash}.webp");

        response.EnsureSuccessStatusCode();
        var request = api.Requests.Should().ContainSingle().Subject;
        request.Headers["X-Visitor-IP"].Should().Be("203.0.113.9", "the API's per-client limit counts the visitor, not this pod");
        request.Headers["X-Analytics-Key"].Should().Be("test-ingest-key");
    }

    [Theory]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD")] // 64 characters, upper case
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 63
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65
    [InlineData("..%2Fx")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")] // 64, not hex
    public async Task ABadHash_Is404_AndTheApiIsNotCalled(string hash)
    {
        var api = new Api(_ => Avatar());
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/social/avatar/{hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        api.Requests.Should().BeEmpty("a value that is not a hash is never looked up");
    }

    [Fact]
    public async Task AnApi404_Is404()
    {
        var api = new Api(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/social/avatar/{Hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnApiThatIsDown_Is503()
    {
        var api = new Api(_ => throw new HttpRequestException("connection refused"));
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/social/avatar/{Hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("an image request gets a bare 503, not the HTML error page");
    }

    [Fact]
    public async Task TwoAnonymousRequests_AreOneApiCall()
    {
        var api = new Api(_ => Avatar());
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();

        using var first = await client.GetAsync($"/social/avatar/{Hash}.webp");
        using var second = await client.GetAsync($"/social/avatar/{Hash}.webp");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsByteArrayAsync()).Should().Equal(Webp);
        second.Headers.CacheControl!.ToString().Should().Be(ImmutableYear);
        api.Requests.Should().HaveCount(1, "the second request is the pod's copy");
    }

    private static HttpResponseMessage Avatar()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Webp) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        response.Headers.CacheControl = CacheControlHeaderValue.Parse(ImmutableYear);
        return response;
    }

    private static WebApplicationFactory<Program> CreateFactory(Api api) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse("203.0.113.9")));
            });
        });

    private sealed class Api(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
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
}
