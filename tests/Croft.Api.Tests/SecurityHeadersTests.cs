using System.Net;
using System.Net.Http.Headers;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Croft.Api.Services;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests;

/// <summary>
/// The plain security headers plus the API's own CSP on every response: the exact values on
/// the JSON responses, the health check, an unknown URL and a media file download. HSTS is
/// sent outside Development on any scheme (TLS terminates at the ingress), exactly once;
/// Kestrel's Server header is off (the option is asserted because TestServer never sends one).
/// The API serves no HTML anywhere (JSON and ProblemDetails, plain-text health, media files,
/// and the dev-only OpenAPI document which is JSON), so the CSP may forbid everything.
/// </summary>
public class SecurityHeadersTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly List<string> _storagePaths = [];

    public void Dispose()
    {
        foreach (var path in _storagePaths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("/api/posts?page=1&pageSize=10", HttpStatusCode.OK, "a JSON response")]
    [InlineData("/health", HttpStatusCode.OK, "the health check")]
    [InlineData("/api/no-such-endpoint", HttpStatusCode.NotFound, "an unknown URL")]
    public async Task PlainHeadersAndCsp_HaveTheExactValues_OnEveryResponseShape(
        string path, HttpStatusCode status, string shape)
    {
        await using var factory = CreateFactory();
        using var response = await factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(status, shape);
        AssertSecurityHeaders(response);
        AssertHsts(response);
    }

    // Responses answered by UseExceptionHandler (a 400 from a ValidationException, a 500) carry every
    // header too: the middleware applies them in OnStarting, because the handler clears the response before it writes.
    [Fact]
    public async Task AValidationErrorAnsweredByTheExceptionHandler_StillCarriesEveryHeader()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");

        using var badRequest = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("", "x"));

        badRequest.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSecurityHeaders(badRequest);
        AssertHsts(badRequest);
    }

    [Fact]
    public async Task AnUnhandledException_AnsweredByTheExceptionHandler_StillCarriesEveryHeader()
    {
        await using var factory = CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(new UnreadableFileStorage(new LocalFileStorage(_storagePaths[^1])));
        });
        var admin = factory.CreateClient().WithRole("Admin");
        var upload = await admin.PostAsync("/api/media", new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "unreadable.png" },
            { new StringContent("global"), "scope" },
        });
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        using var serverError = await factory.CreateClient().GetAsync(asset!.Url);

        serverError.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the storage throws when the file is read");
        AssertSecurityHeaders(serverError);
        AssertHsts(serverError);
    }

    [Fact]
    public async Task JsonResponses_AreJson_NotHtml()
    {
        await using var factory = CreateFactory();
        using var response = await factory.CreateClient().GetAsync("/api/posts?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task MediaDownload_AFileResponse_StillCarriesEveryHeaderIncludingCsp()
    {
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");

        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "test.png" },
            { new StringContent("global"), "scope" }
        };
        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        using var download = await factory.CreateClient().GetAsync(asset!.Url);

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        AssertSecurityHeaders(download);
        AssertHsts(download);
    }

    [Fact]
    public async Task OpenApiDocument_IsJsonNotHtml_AndCarriesTheHeaders()
    {
        // MapOpenApi is mapped in Development only — the one endpoint that answers a document
        // rather than data, and it is JSON like everything else.
        await using var factory = CreateFactory(production: false);
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Hsts_IsNotSent_InDevelopment()
    {
        await using var factory = CreateFactory(production: false);
        using var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse(
            "HSTS is for real deployments; the dev server answers plain HTTP and must not pin it");
    }

    [Fact]
    public void ServerHeader_IsTurnedOffOnKestrel()
    {
        using var factory = CreateFactory();

        // TestServer never sends a Server header, so the Kestrel option is where the production
        // behaviour can be proven; the response shapes above prove the header is absent.
        factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader
            .Should().BeFalse("Kestrel's default Server: Kestrel names the server for no benefit");
    }

    private WebApplicationFactory<Program> CreateFactory(bool production = true, Action<IServiceCollection>? configureServices = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        _storagePaths.Add(storagePath);

        var factory = new BlogApiFactory(fixture.ConnectionString, storagePath, new Dictionary<string, string?>
        {
            // Outside Development the app refuses to start without these.
            ["Analytics:HashKey"] = "hash-key",
            ["Analytics:IngestKey"] = "ingest-key",
            ["Oidc:Audience"] = "audience"
        }, configureServices);
        return production
            ? factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"))
            : factory;
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        AssertSingleHeader(response, "X-Content-Type-Options", "nosniff");
        AssertSingleHeader(response, "Referrer-Policy", "strict-origin-when-cross-origin");
        AssertSingleHeader(response, "Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        AssertSingleHeader(response, "Cross-Origin-Opener-Policy", "same-origin");
        AssertSingleHeader(response, "Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
        response.Headers.Contains("Server").Should().BeFalse("the server must not be named");
    }

    private static void AssertHsts(HttpResponseMessage response) =>
        AssertSingleHeader(response, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");

    private static void AssertSingleHeader(HttpResponseMessage response, string name, string value)
    {
        response.Headers.Contains(name).Should().BeTrue($"every response carries {name}");
        response.Headers.GetValues(name).Should().ContainSingle(
            $"{name} must appear exactly once").Which.Should().Be(value);
    }

    /// <summary>Stores files normally and fails every read: the unhandled exception behind the 500 case.</summary>
    private sealed class UnreadableFileStorage(IFileStorage inner) : IFileStorage
    {
        public Task<string> SaveAsync(Stream data, string fileName, string contentType, string storagePath, CancellationToken ct = default) =>
            inner.SaveAsync(data, fileName, contentType, storagePath, ct);

        public Stream OpenRead(string relativePath) => throw new InvalidOperationException("the storage cannot be read (test)");

        public void Delete(string relativePath) => inner.Delete(relativePath);
    }

    private static ByteArrayContent CreatePngContent()
    {
        // The smallest valid PNG the media endpoint's magic-byte check accepts.
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        return new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };
    }
}
