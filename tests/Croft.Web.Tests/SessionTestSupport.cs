using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>An OIDC edge for tests that run <c>UserAccessTokenService</c> against a fake token endpoint.</summary>
internal static class SessionTestSupport
{
    public const string TokenEndpoint = "https://idp.example/token";

    public static OpenIdConnectOptions OidcOptions() => new()
    {
        ClientId = "blog-web",
        ClientSecret = "secret",
        ConfigurationManager = new StaticConfigurationManager()
    };

    internal sealed class StaticConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(new OpenIdConnectConfiguration { TokenEndpoint = TokenEndpoint });

        public void RequestRefresh() { }
    }

    internal sealed class StaticOptionsMonitor(OpenIdConnectOptions options) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;

        public OpenIdConnectOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }

    internal sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    /// <summary>A token endpoint that answers every request with one canned response.</summary>
    internal sealed class CannedTokenEndpoint(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
