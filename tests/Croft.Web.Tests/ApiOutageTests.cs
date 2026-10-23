using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13c item 1: with the API down or hanging, a reading page answers the error page with 503
/// quickly (the error page needs no API data), and the outage is logged as a Warning, not as an
/// unhandled Error.
/// </summary>
public class ApiOutageTests
{
    private const string Authority = "https://idp.example.com";

    private static WebApplicationFactory<Program> CreateFactory(ScriptedApiHandler api, LogCapture logs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Analytics:IngestKey", "test-ingest-key");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", "blog-web");
            builder.UseSetting("Croft:Web:Api:AttemptTimeoutSeconds", "0.2");
            builder.UseSetting("Croft:Web:Api:TotalTimeoutSeconds", "0.5");
            builder.UseSetting("Croft:Web:Api:RetryDelayMilliseconds", "1");
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => api);
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration { Issuer = Authority, AuthorizationEndpoint = $"{Authority}/authorize" }));
            });
        });

    [Theory]
    [InlineData("hang")]
    [InlineData("refused")]
    public async Task AReadingPage_WithTheApiDown_Answers503WithTheErrorCopy_Quickly_AndLogsNoError(string outage)
    {
        var logs = new LogCapture();
        var api = new ScriptedApiHandler((_, _) => outage == "hang" ? ScriptedApiHandler.Hang : ScriptedApiHandler.ConnectionError);
        using var factory = CreateFactory(api, logs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var clock = Stopwatch.StartNew();
        using var response = await client.GetAsync("/categories");
        var html = await response.Content.ReadAsStringAsync();
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the API is the reason: 503, not a bare or generic 500");
        html.Should().Contain("Something went wrong", "the error page's copy is served, not the server's bare status");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the page fails fast, it does not wait for the API");
        logs.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).Should().BeEmpty(
            "an API outage the app handles is a Warning, not an unhandled exception");
        logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("API", StringComparison.Ordinal),
            "the outage is still visible to the operator");
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/feed.xml")]
    [InlineData("/sitemap.xml")]
    public async Task ADirectApiCall_WhoseResiliencePolicyGivesUp_Answers503_AndLogsNoError(string path)
    {
        // The policy's own exceptions (a timeout here, an open breaker alike) are not HttpRequestExceptions:
        // the readiness probe, the feed and the sitemap call the client directly and must treat them as an outage.
        var logs = new LogCapture();
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.Hang);
        using var factory = CreateFactory(api, logs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        if (path == "/health/ready")
            body.Should().Be("Unhealthy", "the documented readiness answer");
        logs.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).Should().BeEmpty();
    }

    [Fact]
    public async Task TheErrorPage_NeedsNoApiData()
    {
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.ConnectionError);
        using var factory = CreateFactory(api, new LogCapture());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/Error");
        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("Something went wrong");
        api.Attempts.Should().Be(0, "the error page's layout must not call the API it is reporting on");
    }

    [Fact]
    public async Task TheMediaProxy_WithAHangingApi_Answers503WithNoBody_Quickly()
    {
        var logs = new LogCapture();
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.Hang);
        using var factory = CreateFactory(api, logs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var clock = Stopwatch.StartNew();
        using var response = await client.GetAsync($"/api/media/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().BeEmpty("an image request gets a status, not an HTML error page");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        logs.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).Should().BeEmpty();
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

        public void Dispose()
        {
        }

        internal sealed record Entry(LogLevel Level, string Message);

        private sealed class CaptureLogger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                    owner._entries.Add(new Entry(logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception.GetType().Name)));
            }
        }
    }
}
