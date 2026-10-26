using System.IO.Compression;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14c item 5: the responses the output cache may store (an anonymous reader's reading pages,
/// the feed and the sitemap) are compressed, once, inside the cache; nothing else is. They carry no
/// secret (no antiforgery token, no user data), so compressing them opens no BREACH-style leak.
/// </summary>
public class ResponseCompressionTests
{
    public static TheoryData<string> Documents =>
    [
        "/",
        $"/post/{CacheHost.PostSlug}",
        "/feed.xml",
        "/sitemap.xml",
    ];

    private static async Task<(HttpResponseMessage Response, byte[] Raw)> GetAsync(
        HttpClient client, string path, string? acceptEncoding, bool staff = false)
    {
        using var request = CacheHost.Get(path, staff);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        var response = await client.SendAsync(request);
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    private static byte[] WithoutBlazorState(byte[] body) =>
        System.Text.Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Replace(
            System.Text.Encoding.UTF8.GetString(body), "<!--Blazor-Server-Component-State:[^>]*-->", "<!--state-->"));

    private static byte[] Decompress(string encoding, byte[] body)
    {
        using var input = new MemoryStream(body);
        using Stream decoder = encoding switch
        {
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData("/", "br")]
    [InlineData("/", "gzip")]
    [InlineData($"/post/{CacheHost.PostSlug}", "br")]
    [InlineData("/feed.xml", "br")]
    [InlineData("/feed.xml", "gzip")]
    [InlineData("/sitemap.xml", "br")]
    [InlineData("/sitemap.xml", "gzip")]
    public async Task ADocumentTheCacheMayStore_IsCompressed_AndDecompressesToTheUncompressedBytes(string path, string encoding)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (plain, plainBody) = await GetAsync(client, path, null);
        var (compressed, compressedBody) = await GetAsync(client, path, encoding);

        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        plain.Content.Headers.ContentEncoding.Should().BeEmpty();
        compressed.StatusCode.Should().Be(HttpStatusCode.OK);
        compressed.Content.Headers.ContentEncoding.Should().Equal(encoding);
        // Two independent renders of a page differ in one comment: the data-protected state Blazor
        // appends (a fresh IV each time). Everything else must be the same bytes.
        WithoutBlazorState(Decompress(encoding, compressedBody)).Should().Equal(WithoutBlazorState(plainBody),
            "compression changes no byte of the document");
        compressedBody.Length.Should().BeLessThanOrEqualTo((int)(plainBody.Length * 0.35),
            $"{path} is {plainBody.Length} bytes uncompressed, {compressedBody.Length} with {encoding}");
        string.Join(", ", compressed.Headers.GetValues("Vary")).Split(',').Select(v => v.Trim()).Count(v => v == "Accept-Encoding")
            .Should().Be(1, "Vary: Accept-Encoding once, not once from each layer");
        compressed.Content.Headers.ContentType.Should().Be(plain.Content.Headers.ContentType);
    }

    [Theory]
    [InlineData("/feed.xml")]
    [InlineData("/sitemap.xml")]
    public async Task TheFeedAndTheSitemap_KeepTheirCacheControl(string path)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (response, _) = await GetAsync(client, path, "br");

        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=300");
        response.Content.Headers.ContentEncoding.Should().Equal("br");
    }

    [Fact]
    public async Task AfterABrotliRequestFilledTheCache_AnIdentityRequest_GetsPlainHtml_AndGzipGetsGzip()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (brotli, _) = await GetAsync(client, "/", "br");
        var (none, noneBody) = await GetAsync(client, "/", null);
        var (identity, identityBody) = await GetAsync(client, "/", "identity");
        var (gzip, gzipBody) = await GetAsync(client, "/", "gzip");
        var (brotliAgain, _) = await GetAsync(client, "/", "gzip;q=0.5, br");

        brotli.Content.Headers.ContentEncoding.Should().Equal("br");
        none.Content.Headers.ContentEncoding.Should().BeEmpty();
        System.Text.Encoding.UTF8.GetString(noneBody).Should().StartWith("<!DOCTYPE html>", "plain HTML, not the Brotli bytes of the entry before");
        identity.Content.Headers.ContentEncoding.Should().BeEmpty();
        identityBody.Should().Equal(noneBody);
        gzip.Content.Headers.ContentEncoding.Should().Equal("gzip");
        System.Text.Encoding.UTF8.GetString(Decompress("gzip", gzipBody)).Should().StartWith("<!DOCTYPE html>");
        brotliAgain.Content.Headers.ContentEncoding.Should().Equal(["br"], "br is preferred when both are accepted");
        none.Headers.Vary.Should().Contain("Accept-Encoding", "an identity answer varies on the encoding too");
        host.Store.Writes.Select(w => w.Key).Distinct().Should().HaveCount(3, "one entry per normalised encoding, never one per raw header");
    }

    [Theory]
    [InlineData("br;q=0, gzip")]
    [InlineData("zstd, deflate")]
    [InlineData("br;q=0, gzip;q=0")]
    [InlineData("*")]
    public async Task AnAcceptEncodingTheServerCannotUse_IsNormalisedToTheEncodingItServes(string acceptEncoding)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (response, body) = await GetAsync(client, "/", acceptEncoding);

        var served = response.Content.Headers.ContentEncoding.SingleOrDefault();
        served.Should().Be(acceptEncoding switch { "br;q=0, gzip" => "gzip", "*" => "br", _ => null });
        var html = served is null ? body : Decompress(served, body);
        System.Text.Encoding.UTF8.GetString(html).Should().StartWith("<!DOCTYPE html>");
    }

    [Fact]
    public async Task ASignedInRequest_IsNotCompressed()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (response, body) = await GetAsync(client, "/", "br", staff: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty("a signed-in render is never shared and never compressed here");
        System.Text.Encoding.UTF8.GetString(body).Should().StartWith("<!DOCTYPE html>");
    }

    [Theory]
    [InlineData("/admin/posts", true, HttpStatusCode.OK)]
    [InlineData("/not-found", false, HttpStatusCode.OK)]
    [InlineData("/post/no-such-post", false, HttpStatusCode.NotFound)]
    [InlineData("/search?q=cache", false, HttpStatusCode.OK)]
    // The cache host has no API behind it, so readiness answers its own 503 (not ready): still a deliberate answer.
    [InlineData("/health/ready", false, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/robots.txt", false, HttpStatusCode.OK)]
    public async Task EverythingElse_IsNotCompressedByThisCode(string path, bool staff, HttpStatusCode status)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (response, _) = await GetAsync(client, path, "br", staff);

        response.StatusCode.Should().Be(status, $"{path} must answer for real: a 500 is not 'uncompressed'");
        response.Content.Headers.ContentEncoding.Should().BeEmpty($"{path} is not something the output cache may store");
    }

    [Fact]
    public async Task AnImage_IsNotCompressed()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (response, body) = await GetAsync(client, $"/api/media/{Guid.NewGuid()}", "br");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Content.Headers.ContentEncoding.Should().BeEmpty("images are already compressed");
        body.Should().HaveCount(2048);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task TwoRequestsForOnePage_RunTheCompressorOnce(string encoding)
    {
        var compressor = new CompressorRuns();
        using var host = CacheHost.Create(configureServices: services => CountingProviders.Register(services, compressor));
        using var client = host.Client();

        var (first, firstBody) = await GetAsync(client, "/", encoding);
        var (second, secondBody) = await GetAsync(client, "/", encoding);

        first.Content.Headers.ContentEncoding.Should().Equal(encoding);
        second.Content.Headers.ContentEncoding.Should().Equal(encoding);
        secondBody.Should().Equal(firstBody, "the cached entry holds the compressed bytes");
        compressor.Streams(encoding).Should().Be(1, "the first request compressed the page, the second came out of the cache as it was stored");
        host.Store.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task ACacheHit_StillCarriesTheEnhancedNavigationHeaderAsAllow()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        var (first, _) = await GetAsync(client, $"/post/{CacheHost.PostSlug}", "br");
        var (second, _) = await GetAsync(client, $"/post/{CacheHost.PostSlug}", "br");

        second.Content.Headers.ContentEncoding.Should().Equal("br");
        first.Headers.GetValues("blazor-enhanced-nav").Should().Equal("allow");
        second.Headers.GetValues("blazor-enhanced-nav").Should().Equal(["allow"], "W-098 holds for a compressed entry");
    }

    /// <summary>How many compression streams each encoding opened.</summary>
    private sealed class CompressorRuns
    {
        private readonly Dictionary<string, int> _streams = [];

        public void Opened(string encoding)
        {
            lock (_streams)
            {
                _streams[encoding] = _streams.GetValueOrDefault(encoding) + 1;
            }
        }

        public int Streams(string encoding)
        {
            lock (_streams)
            {
                return _streams.GetValueOrDefault(encoding);
            }
        }
    }

    private static class CountingProviders
    {
        /// <summary>Wraps the host's own compression providers in ones that count the streams they open.</summary>
        public static void Register(IServiceCollection services, CompressorRuns runs)
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(ICompressionProvider)).ToList())
            {
                services.Remove(descriptor);
                services.AddSingleton<ICompressionProvider>(sp => new Counting(
                    (ICompressionProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!), runs));
            }
        }
    }

    private sealed class Counting(ICompressionProvider inner, CompressorRuns runs) : ICompressionProvider
    {
        public string EncodingName => inner.EncodingName;

        public bool SupportsFlush => inner.SupportsFlush;

        public Stream CreateStream(Stream outputStream)
        {
            runs.Opened(EncodingName);
            return inner.CreateStream(outputStream);
        }
    }
}
