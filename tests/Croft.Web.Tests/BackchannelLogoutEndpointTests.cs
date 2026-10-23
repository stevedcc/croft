using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Croft.Web.Authentication;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Tests;

/// <summary>
/// The POST /backchannel-logout endpoint's HTTP behaviour against the real app host: the form
/// field name, anonymous access, the 200 + Cache-Control: no-store / 400 contract, the session
/// actually ending, and the token never being logged.
/// </summary>
public class BackchannelLogoutEndpointTests : IDisposable
{
    private const string Authority = "http://idp.example.com";
    private const string ClientId = "blog-web";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa = RSA.Create(2048);

    public void Dispose()
    {
        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Post_WithValidLogoutToken_FromAnonymousCaller_Returns200NoStoreAndEndsTheSession()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600), Sid: "idp-sid-1", Sub: "admin-id"));
        await store.SetAsync("s2", new TokenEntry("at-2", "rt-2", Now.AddSeconds(3600), Sid: "idp-sid-2", Sub: "admin-id"));
        var client = factory.CreateClient();

        var response = await PostTokenAsync(client, CreateLogoutToken(sid: "idp-sid-1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.ToString().Should().Be("no-store",
            "the spec requires the accepted answer to be uncached");
        (await store.GetAsync("s1")).Should().BeNull("the logged-out session's tokens are gone");
        (await store.GetAsync("s2")).Should().NotBeNull("another session keeps its tokens");
    }

    [Fact]
    public async Task Post_WithInvalidToken_Returns400AndKeepsSessions()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("s1", new TokenEntry("at-1", "rt-1", Now.AddSeconds(3600), Sid: "idp-sid-1", Sub: "admin-id"));
        var client = factory.CreateClient();

        var response = await PostTokenAsync(client, "not-a-valid-token");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an invalid logout token must be answered with 400");
        (await store.GetAsync("s1")).Should().NotBeNull("an invalid token must change nothing");
    }

    [Fact]
    public async Task Post_WithoutTokenField_Returns400()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/backchannel-logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["something_else"] = "x" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a request without a logout_token is not a valid logout request");
    }

    [Fact]
    public async Task Post_MalformedForm_Returns400()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Content-Type says form, but the body is not a readable form: the form reader throws
        // instead of producing one, and an unparseable form is an invalid logout request.
        using var noBoundary = new ByteArrayContent("junk"u8.ToArray());
        noBoundary.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        using var truncatedMultipart = new ByteArrayContent(
            "--xyz\r\nContent-Disposition: form-data; name=\"logout_token\"\r\n\r\nabc"u8.ToArray());
        truncatedMultipart.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=xyz");

        (await client.PostAsync("/backchannel-logout", noBoundary)).StatusCode.Should()
            .Be(HttpStatusCode.BadRequest, "a form the reader cannot parse must be answered with 400");
        (await client.PostAsync("/backchannel-logout", truncatedMultipart)).StatusCode.Should()
            .Be(HttpStatusCode.BadRequest, "a multipart body without its closing boundary is not a form");
    }

    [Fact]
    public async Task Post_NeverLogsTheLogoutToken()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = CreateFactory(logs);
        var client = factory.CreateClient();
        var token = CreateLogoutToken(sid: "idp-sid-1");

        var response = await PostTokenAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        logs.Messages.Should().NotContain(message => message.Contains(token),
            "the logout token is a credential and must never reach the logs");
    }

    private static Task<HttpResponseMessage> PostTokenAsync(HttpClient client, string logoutToken) =>
        client.PostAsync("/backchannel-logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["logout_token"] = logoutToken }));

    // The host runs the real endpoint and the real BackchannelLogoutService; only the signing
    // keys are swapped for a test key so the signature checks run against something known.
    private WebApplicationFactory<Program> CreateFactory(ILoggerProvider? logSink = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Startup reads it when a request creates an HttpClient; these tests make none.
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", ClientId);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackchannelLogoutTokenValidator>();
                services.AddSingleton(CreateValidator());
            });
            if (logSink is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logSink));
            }
        });

    private BackchannelLogoutTokenValidator CreateValidator()
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.Issuer = Authority;
        configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "test-key" });
        return new BackchannelLogoutTokenValidator(
            ClientId,
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            new FakeTimeProvider(Now));
    }

    private string CreateLogoutToken(string? sid = null, string? sub = "admin-id")
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Authority,
            ["aud"] = ClientId,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = "test-jti-1",
            ["events"] = new Dictionary<string, object>
            {
                [BackchannelLogoutTokenValidator.LogoutEvent] = new Dictionary<string, object>()
            }
        };
        if (sid is not null)
        {
            payload["sid"] = sid;
        }

        if (sub is not null)
        {
            payload["sub"] = sub;
        }

        return SignedTestJwt.Create(_rsa, "test-key", payload);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Captures everything a reader of the logs could see of an entry: the formatted message, every
        /// structured value, the exception's text, and the values of every scope begun on the logger. A
        /// credential logged as a structured value, an exception detail or a scope never shows in the
        /// formatted message alone, so asserting on the message alone proves less than it says.
        /// </summary>
        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                messages.Enqueue("scope: " + Describe(state));
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(string.Join(' ', formatter(state, exception), Describe(state), exception?.ToString() ?? string.Empty));

            private static string Describe<TState>(TState state) =>
                state is IEnumerable<KeyValuePair<string, object?>> values
                    ? string.Join(' ', values.Select(pair => $"{pair.Key}={pair.Value}"))
                    : state?.ToString() ?? string.Empty;
        }
    }
}
