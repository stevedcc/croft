using Croft.Api.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>The owner's identity on the two networks as the social tests configure it (invented values).</summary>
internal static class SocialTestSettings
{
    public const string Server = "mastodon.example";
    public const string Account = "owner";
    public const string Did = "did:plc:ownerowner00000000000000";

    public static Dictionary<string, string?> Configuration(bool enabled = true, string server = Server, string account = Account, string did = Did) =>
        new()
        {
            ["Social:Enabled"] = enabled ? "true" : "false",
            ["Social:Mastodon:Server"] = server,
            ["Social:Mastodon:Account"] = account,
            ["Social:Bluesky:Did"] = did,
        };
}

/// <summary>A test API host with the social settings on and no background fetch loop (the tests drive ticks themselves).</summary>
internal static class SocialApiFactory
{
    public static BlogApiFactory Create(
        PostgresDatabaseFixture fixture,
        IBlueskyHandleResolver? resolver = null,
        bool enabled = true,
        TimeProvider? time = null,
        Action<IServiceCollection>? configureServices = null,
        Dictionary<string, string?>? configuration = null,
        Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? commandInterceptor = null) =>
        Create(fixture.ConnectionString, resolver, enabled, time, configureServices, configuration, commandInterceptor);

    /// <summary>The same, on a database of the caller's own (a <see cref="ScratchDatabase"/>: tests that count blocks or threads need every row to be theirs).</summary>
    public static BlogApiFactory Create(
        string connectionString,
        IBlueskyHandleResolver? resolver = null,
        bool enabled = true,
        TimeProvider? time = null,
        Action<IServiceCollection>? configureServices = null,
        Dictionary<string, string?>? configuration = null,
        Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? commandInterceptor = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        var settings = SocialTestSettings.Configuration(enabled);
        if (configuration is not null)
        {
            foreach (var (key, value) in configuration)
            {
                settings[key] = value;
            }
        }

        return new BlogApiFactory(connectionString, storagePath, settings, services =>
        {
            // The loop that fetches replies is not what these tests are about: they drive a tick themselves (SocialFetchHarness).
            // The same for the loop that announces posts (brief 12d): no test ever lets a real loop reach for a network.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType is { } type && (type == typeof(SocialFetchHostedService) || type == typeof(SocialAnnounceHostedService))).ToList())
                services.Remove(descriptor);

            if (resolver is not null)
            {
                services.RemoveAll<IBlueskyHandleResolver>();
                services.AddSingleton(resolver);
            }

            if (time is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
            }

            configureServices?.Invoke(services);
        }, commandInterceptor);
    }
}
