using System.Net;
using System.Security.Claims;
using System.Text;
using Croft.Web.Options;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Croft's own counters on the Croft.Web meter: CSP violations by their effective directive (the
/// blocked URI and the document path can carry reader data and are never attributes), and token
/// refreshes by outcome (refreshed | transient_failure | failed). Attribute values stay in those
/// fixed sets so the series count stays small.
/// </summary>
[Collection(nameof(CroftMetricsGroup))]
public class WebMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CspViolation_CountsOnceWithDirectiveOnly()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        using var factory = WebTelemetryTests.CreateFactory(capture: null);
        using var client = factory.CreateClient();

        const string json = """
            {
              "csp-report": {
                "document-uri": "https://example.com/post/hello?utm_source=news",
                "violated-directive": "img-src",
                "blocked-uri": "https://evil.example.com/pixel.gif?session=abc",
                "status-code": 200
              }
            }
            """;
        using var response = await client.PostAsync(
            "/csp-report", new StringContent(json, Encoding.UTF8, "application/csp-report"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var sample = metrics.Measurements.Should().ContainSingle(
                m => m.Instrument == "croft.csp.reports" && m.Value == 1 && m.HasTag("directive", "img-src"),
                "one reported violation is one event under its effective directive")
            .Which;
        sample.Tags.Should().ContainSingle(t => t.Key == "directive",
            "the blocked URI and the document path can carry reader data and must never be metric attributes");
    }

    [Fact]
    public async Task TokenRefresh_Stored_CountsOutcomeRefreshed()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new StubTokenEndpointHandler(
            HttpStatusCode.OK, """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}""");
        var service = NewService(store, handler);

        var token = await service.GetAccessTokenAsync(UserWithSid("s1"));

        token.Should().Be("at-new");
        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.token.refreshes" && m.Value == 1 && m.HasTag("outcome", "refreshed"),
            "a refresh that stores new tokens counts as refreshed");
    }

    [Fact]
    public async Task TokenRefresh_IdpOutage_CountsOutcomeTransientFailure()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new StubTokenEndpointHandler(HttpStatusCode.InternalServerError, "");
        var service = NewService(store, handler);

        await service.GetAccessTokenAsync(UserWithSid("s1"));

        metrics.Measurements.Should().ContainSingle(
                m => m.Instrument == "croft.token.refreshes" && m.Value == 1 && m.HasTag("outcome", "transient_failure"),
                "an IdP outage says nothing about the refresh token and counts as transient")
            .Which.Tags.Should().ContainSingle(t => t.Key == "outcome",
                "outcome is the only attribute: fixed sets keep the series count small");
    }

    [Fact]
    public async Task TokenRefresh_TokenRejected_CountsOutcomeFailed()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", new TokenEntry("at-old", "rt-old", Now.AddSeconds(30)));
        var handler = new StubTokenEndpointHandler(HttpStatusCode.BadRequest, "");
        var service = NewService(store, handler);

        await service.GetAccessTokenAsync(UserWithSid("s1"));

        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.token.refreshes" && m.Value == 1 && m.HasTag("outcome", "failed"),
            "a rejected refresh token ends the session and counts as failed");
    }

    [Fact]
    public async Task CspViolation_DirectiveWithSources_CountsUnderTheDirectiveName()
    {
        using var metrics = new MetricCapture("Croft.Web").Start();
        using var factory = WebTelemetryTests.CreateFactory(capture: null);
        using var client = factory.CreateClient();

        await PostCspReportAsync(client, """{ "csp-report": { "document-uri": "https://example.com/post/hello?utm_source=news", "violated-directive": "img-src 'self' https://cdn.example", "blocked-uri": "https://evil.example.com/pixel.gif?session=abc", "status-code": 200 } }""");

        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.csp.reports" && m.Value == 1 && m.HasTag("directive", "img-src"),
            "the attribute is the effective directive name, not the policy text a client sends");
    }

    [Fact]
    public async Task CspViolation_TrustedTypesReport_CountsUnderItsOwnDirective_NotOther()
    {
        // The reading pages' report-only Trusted Types policy (10c item 4) reports to the same
        // endpoint: its effective directive is part of the policy's vocabulary, so it is its own
        // metric series, not lumped into "other" with the made-up ones.
        using var metrics = new MetricCapture("Croft.Web").Start();
        using var factory = WebTelemetryTests.CreateFactory(capture: null);
        using var client = factory.CreateClient();

        await PostCspReportAsync(client, """{ "type": "csp-violation", "body": { "documentURL": "https://example.com/post/x", "effectiveDirective": "require-trusted-types-for", "blockedURL": "trusted-types-sink", "disposition": "report" } }""");

        metrics.Measurements.Should().ContainSingle(
            m => m.Instrument == "croft.csp.reports" && m.Value == 1 && m.HasTag("directive", "require-trusted-types-for"));
    }

    [Fact]
    public async Task CspViolation_UnknownDirectives_CollapseIntoOneBoundedSeries()
    {
        // The endpoint is anonymous: a stranger can post anything as a violated-directive, and
        // an unbounded tag would let them multiply the collector's series at will.
        using var metrics = new MetricCapture("Croft.Web").Start();
        using var factory = WebTelemetryTests.CreateFactory(capture: null);
        using var client = factory.CreateClient();

        await PostCspReportAsync(client, """{ "csp-report": { "document-uri": "https://example.com/p", "violated-directive": "made-up-directive-aaa", "blocked-uri": "https://evil.example.com/x" } }""");
        await PostCspReportAsync(client, """{ "csp-report": { "document-uri": "https://example.com/p", "violated-directive": "made-up-directive-bbb", "blocked-uri": "https://evil.example.com/x" } }""");

        var samples = metrics.Measurements.Where(m => m.Instrument == "croft.csp.reports").ToList();
        samples.Should().HaveCount(2, "both violations count");
        samples.Should().OnlyContain(m => m.HasTag("directive", "other"),
            "values outside the policy's directive set must collapse into one series");
    }

    private static async Task PostCspReportAsync(HttpClient client, string json)
    {
        using var response = await client.PostAsync(
            "/csp-report", new StringContent(json, Encoding.UTF8, "application/csp-report"));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static UserAccessTokenService NewService(InMemoryTokenStore store, StubTokenEndpointHandler handler)
    {
        var options = new OpenIdConnectOptions
        {
            ClientId = "blog-web",
            ClientSecret = "secret",
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { TokenEndpoint = "https://idp.example/token" }),
        };
        return new UserAccessTokenService(
            store,
            store,
            new StubHttpClientFactory(handler),
            new StubOidcOptionsMonitor(options),
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)),
            NullLogger<UserAccessTokenService>.Instance);
    }

    private static ClaimsPrincipal UserWithSid(string? sid)
    {
        var identity = new ClaimsIdentity("test");
        if (sid is not null)
        {
            identity.AddClaim(new Claim("blog_sid", sid));
        }

        return new ClaimsPrincipal(identity);
    }

    private sealed class StubTokenEndpointHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubOidcOptionsMonitor(OpenIdConnectOptions options) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;

        public OpenIdConnectOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }
}

/// <summary>
/// The Croft.Web counters are process-wide statics: this collection opts out of parallel
/// execution so exact-count assertions cannot be corrupted by another class's events.
/// </summary>
[CollectionDefinition(nameof(CroftMetricsGroup), DisableParallelization = true)]
public sealed class CroftMetricsGroup;
