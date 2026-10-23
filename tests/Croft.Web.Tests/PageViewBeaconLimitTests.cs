using System.Net;
using System.Text;
using System.Text.Json;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13a item 3: the page-view beacon takes at most 4096 bytes of body; more is refused with
/// 413 before anything is parsed or forwarded to the API.
/// </summary>
public class PageViewBeaconLimitTests
{
    private const string BeaconPath = "/api/analytics/pageview";

    [Fact]
    public async Task ABodyOver4096Bytes_IsRefusedWith413_AndNothingReachesTheApi()
    {
        var api = new StubBlogApiHandler();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();
        var body = Beacon(referrer: new string('r', 5 * 1024));

        using var response = await client.PostAsync(BeaconPath, new StringContent(body, Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        api.Requests.Should().NotContain(r => r.PathAndQuery.StartsWith(BeaconPath, StringComparison.Ordinal),
            "an oversized beacon is never forwarded");
    }

    [Fact]
    public async Task AnOversizedBodyWithNoContentLength_IsRefusedWith413_AndNothingReachesTheApi()
    {
        var api = new StubBlogApiHandler();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();
        var bytes = Encoding.UTF8.GetBytes(Beacon(referrer: new string('r', 5 * 1024)));

        // A stream with no known length is sent chunked: the limit must hold without a header.
        using var content = new StreamContent(new MemoryStream(bytes));
        content.Headers.ContentType = new("text/plain");
        using var response = await client.PostAsync(BeaconPath, content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        api.Requests.Should().NotContain(r => r.PathAndQuery.StartsWith(BeaconPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABodyOfExactly4096Bytes_IsAccepted_AndForwarded()
    {
        var api = new StubBlogApiHandler();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient();
        var empty = Beacon(referrer: "");
        var body = Beacon(referrer: new string('r', 4096 - Encoding.UTF8.GetByteCount(empty)));
        Encoding.UTF8.GetByteCount(body).Should().Be(4096);

        using var response = await client.PostAsync(BeaconPath, new StringContent(body, Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        api.Requests.Should().Contain(r => r.PathAndQuery.StartsWith(BeaconPath, StringComparison.Ordinal),
            "a beacon within the limit is forwarded as before");
    }

    private static string Beacon(string referrer) =>
        JsonSerializer.Serialize(new PageViewRequest(Guid.NewGuid(), referrer), JsonSerializerOptions.Web);

    private static WebApplicationFactory<Program> CreateFactory(StubBlogApiHandler api) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api));
        });
}
