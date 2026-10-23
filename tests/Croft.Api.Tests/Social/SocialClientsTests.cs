using System.Net;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 4: the three named clients (SocialMastodon, SocialBluesky, SocialAvatars): the guarded primary handler (no redirects, no proxy,
/// no cookies), the standard resilience handler with no retries, the User-Agent, and no URL in the logs.
/// </summary>
public class SocialClientsTests
{
    public static TheoryData<string> Names => [SocialHttp.Mastodon, SocialHttp.Bluesky, SocialHttp.Avatars, SocialHttp.Announce];

    private static ServiceProvider Provider(Action<IServiceCollection>? configure = null, LogCollector? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (logs is not null)
                logging.AddProvider(logs);
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Blog:BaseUrl"] = "https://blog.example" }).Build();
        services.AddSocialClients(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ThePrimaryHandler_IsGuarded_FollowsNoRedirects_UsesNoProxyAndNoCookies(string name)
    {
        using var provider = Provider();

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name));

        var primary = chain.OfType<SocketsHttpHandler>().Single();
        primary.ConnectCallback.Should().NotBeNull("every connection goes through the SSRF guard");
        primary.AllowAutoRedirect.Should().BeFalse("a redirect would walk past the check of the first address");
        primary.UseProxy.Should().BeFalse("a proxy would be connected to instead of the checked address");
        primary.UseCookies.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheChain_HasTheStandardResilienceHandler(string name)
    {
        using var provider = Provider();

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name));

        chain.Select(h => h.GetType().Name).Should().Contain("ResilienceHandler");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AFailingAnswer_IsNotRetried_TheScheduleRetries(string name)
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var provider = Provider(services => services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub));

        var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient(name).GetAsync("https://example.test/x");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stub.Requests.Should().ContainSingle("the standard handler would retry a 503 three times; here the schedule retries");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task ANetworkError_IsNotRetried(string name)
    {
        var attempts = 0;
        var stub = new StubHandler(_ =>
        {
            attempts++;
            throw new HttpRequestException("connection reset");
        });
        using var provider = Provider(services => services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub));

        var act = () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(name).GetAsync("https://example.test/x");

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task EveryRequest_NamesTheSoftwareAndTheSite(string name)
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var provider = Provider(services => services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub));

        await provider.GetRequiredService<IHttpClientFactory>().CreateClient(name).GetAsync("https://example.test/x");

        stub.Messages.Single().Headers.UserAgent.ToString().Should().StartWith("Croft/").And.EndWith("(+https://blog.example)");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task NoLogLine_CarriesTheRequestUrl(string name)
    {
        var logs = new LogCollector();
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var provider = Provider(services => services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub), logs);

        await provider.GetRequiredService<IHttpClientFactory>().CreateClient(name).GetAsync("https://example.test/accounts/some-handle/avatar.png?token=abc");

        logs.Entries.Should().NotContain(e => e.Message.Contains("some-handle", StringComparison.Ordinal) || e.Message.Contains("example.test", StringComparison.Ordinal),
            "a handle in a URL must not reach the logs");
    }

    [Theory]
    [InlineData(SocialHttp.Mastodon, 10)]
    [InlineData(SocialHttp.Bluesky, 10)]
    [InlineData(SocialHttp.Avatars, 10)]
    [InlineData(SocialHttp.Announce, 30)]
    public void TheTimeLimit_IsTenSeconds_ExceptForAnnouncing_WhichUploadsBlobs(string name, int seconds)
    {
        using var provider = Provider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>>().Get($"{name}-standard");

        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(seconds));
        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public async Task APostToTheAnnounceClient_IsNeverRetried()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var provider = Provider(services => services.AddHttpClient(SocialHttp.Announce).ConfigurePrimaryHttpMessageHandler(() => stub));

        var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient(SocialHttp.Announce)
            .PostAsync("https://example.test/x", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stub.Requests.Should().ContainSingle("Polly must never send a POST twice");
    }

    private static List<HttpMessageHandler> Chain(HttpMessageHandler handler)
    {
        List<HttpMessageHandler> chain = [];
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
            chain.Add(current);
        return chain;
    }
}
