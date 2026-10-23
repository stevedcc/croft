using System.Security.Claims;
using Croft.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.OutputCaching;

namespace Croft.Web.Tests.OutputCache;

/// <summary>The Web's real <see cref="BlogApiClient"/> as a signed-in staff member's circuit would
/// hold it, over any API handler, with the output cache's store a test can look at.</summary>
internal static class StaffApiClient
{
    public static BlogApiClient Create(HttpMessageHandler api, IOutputCacheStore store) =>
        new(new Factory(api), new StaffState(), new FixedToken(), new Navigation(), new SessionExpiryHooks(), store);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:5000/"),
        };
    }

    private sealed class StaffState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "admin-id")], "test"))));
    }

    private sealed class FixedToken : IUserAccessTokenService
    {
        public Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost:5002/", "http://localhost:5002/");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException("a successful write never navigates");
    }
}
