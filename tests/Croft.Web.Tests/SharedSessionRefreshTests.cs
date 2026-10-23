using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Croft.Web.Data;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Two Web instances refreshing one session at once — the race that breaks a session when the
/// refresh token rotates: if both spend the same refresh token, the IdP revokes it and the user
/// is signed out. Exactly one refresh request must reach the token endpoint (which revokes a
/// refresh token on second use), both callers must get the new access token, and the session
/// must survive. Two WebApplicationFactory hosts on one database stand in for the two instances.
/// </summary>
[Trait("Category", "Container")]
public class SharedSessionRefreshTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private const string Authority = "http://idp.example.com";
    private const string ClientId = "blog-web";
    private const string TokenEndpoint = "https://idp.example/token";

    private readonly RevokingTokenEndpointHandler _tokenEndpoint = new();
    private readonly RowReadWatcher _rowReads = new();

    public void Dispose()
    {
        _tokenEndpoint.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ConcurrentRefresh_OnTwoInstances_SendsOneRefreshRequestAndBothCallersGetTheNewToken()
    {
        using var a = CreateFactory();
        using var b = CreateFactory();
        var user = UserWithSid("s1");
        await a.Services.GetRequiredService<ITokenStore>().SetAsync("s1", new TokenEntry(
            "at-old", "rt-old", DateTimeOffset.UtcNow.AddSeconds(30), "idt-1", "idp-sid-1", "admin-id"));

        var callerA = a.Services.GetRequiredService<IUserAccessTokenService>().GetAccessTokenAsync(user);
        // A's answer waits until B is inside the refresh's session-row read (the row lock's
        // acquisition point), so the two instances are guaranteed to race the same session
        // instead of racing on task scheduling. When nothing serialises the two refreshes, B
        // reaches the token endpoint itself and that arrival ends the wait instead.
        await _tokenEndpoint.FirstRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var callerB = b.Services.GetRequiredService<IUserAccessTokenService>().GetAccessTokenAsync(user);
        await Task.WhenAny(_rowReads.BothRowReadsIssued.Task, _tokenEndpoint.SecondRequestArrived.Task)
            .WaitAsync(TimeSpan.FromSeconds(30));
        _tokenEndpoint.ReleaseFirstResponse();

        var tokenA = await callerA.WaitAsync(TimeSpan.FromSeconds(30));
        var tokenB = await callerB.WaitAsync(TimeSpan.FromSeconds(30));

        _tokenEndpoint.RequestCount.Should().Be(1,
            "a second refresh request would spend the rotated refresh token again and have it revoked at the IdP");
        tokenA.Should().Be("at-new",
            "the instance that refreshed gets the new token");
        tokenB.Should().Be("at-new",
            "the loser of the refresh race takes the winner's new token instead of spending the rotated refresh token twice");
        var survivor = (await b.Services.GetRequiredService<ITokenStore>().GetAsync("s1"))!;
        survivor.AccessToken.Should().Be("at-new", "the session survives the race with the refreshed token");
        survivor.RefreshToken.Should().Be("rt-new", "the rotated refresh token is the one now stored");
        survivor.IdToken.Should().Be("idt-1", "the ID token from login survives refreshes for id_token_hint");
    }

    // The host runs the real app (shared store registration included); only the IdP edge is
    // faked: the token endpoint (which revokes a refresh token on second use) and the
    // session-row read watcher that tells the test when both instances are in the race.
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("ConnectionStrings:WebDb", fixture.ConnectionString);
            builder.UseSetting("Oidc:Authority", Authority);
            builder.UseSetting("Oidc:ClientId", ClientId);
            builder.ConfigureTestServices(services =>
            {
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.ClientId = ClientId;
                    options.ClientSecret = "secret";
                    options.ConfigurationManager = new FakeConfigurationManager(TokenEndpoint);
                });
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(_tokenEndpoint));
                services.RemoveAll<IDbContextFactory<CroftWebDbContext>>();
                services.AddSingleton<IDbContextFactory<CroftWebDbContext>>(
                    new WatcherDbContextFactory(fixture.ConnectionString, _rowReads));
            });
        });

    private static ClaimsPrincipal UserWithSid(string? sid)
    {
        var claims = new List<Claim> { new("sub", "admin-id") };
        if (sid != null)
        {
            claims.Add(new Claim("blog_sid", sid));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>
    /// A token endpoint that rotates refresh tokens and revokes a refresh token on second use:
    /// the racing instance's duplicate request comes back 400 invalid_grant, exactly as a real
    /// IdP would answer it.
    /// </summary>
    private sealed class RevokingTokenEndpointHandler : HttpMessageHandler
    {
        private readonly HashSet<string> _spentRefreshTokens = [];
        private readonly TaskCompletionSource _releaseFirstResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => _requestCount;
        public TaskCompletionSource FirstRequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstResponse() => _releaseFirstResponse.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sequence = Interlocked.Increment(ref _requestCount);
            if (sequence == 1)
            {
                FirstRequestArrived.TrySetResult();
            }
            else
            {
                SecondRequestArrived.TrySetResult();
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var refreshToken = ParseFormValue(body, "refresh_token");

            // The refresh token is marked spent on ARRIVAL: a second use of it (the racing
            // instance's duplicate) is answered with the revocation below even while the first
            // request is still in flight.
            var alreadySpent = !_spentRefreshTokens.Add(refreshToken);
            if (sequence == 1)
            {
                // The first answer waits for the racing instance to reach the session row, so
                // the two refreshes genuinely overlap.
                await _releaseFirstResponse.Task.WaitAsync(cancellationToken);
            }

            return alreadySpent
                ? JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
                : JsonResponse(HttpStatusCode.OK, """{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}""");
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

        private static string ParseFormValue(string body, string name)
        {
            foreach (var pair in body.Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == name)
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// Signals when both instances have issued the refresh's session-row read (the raw
    /// <c>SELECT * FROM "UserSessions"</c> that plain reads never issue): at that point both
    /// refreshes are decided and the race is genuinely on.
    /// </summary>
    private sealed class RowReadWatcher : DbCommandInterceptor
    {
        private int _rowReads;

        public TaskCompletionSource BothRowReadsIssued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT * FROM \"UserSessions\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref _rowReads) >= 2)
            {
                BothRowReadsIssued.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class WatcherDbContextFactory(string connectionString, DbCommandInterceptor watcher)
        : IDbContextFactory<CroftWebDbContext>
    {
        public CroftWebDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<CroftWebDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(watcher)
                .Options);
    }

    private sealed class FakeConfigurationManager(string? tokenEndpoint) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(new OpenIdConnectConfiguration { TokenEndpoint = tokenEndpoint });

        public void RequestRefresh() { }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        // The handler is shared by both instances' clients; disposing a client must not take it away.
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
