using Croft.Web.Data;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Croft.Web.Tests;

/// <summary>
/// The Web database is optional: it is used only when ConnectionStrings:WebDb is set. Without it
/// the app must start exactly as before — no <see cref="CroftWebDbContext"/> anywhere in DI —
/// because the single-instance default must not need a database.
/// </summary>
public class WebDbRegistrationTests
{
    [Fact]
    public void WebDbNotConfigured_AppStarts_WithoutACroftWebDbContext()
    {
        using var factory = CreateFactory(webDb: null);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<CroftWebDbContext>().Should().BeNull(
            "without ConnectionStrings:WebDb the app keeps its database-less behaviour");
    }

    [Fact]
    public void WebDbConfigured_RegistersACroftWebDbContext()
    {
        using var factory = CreateFactory(webDb: "Host=localhost;Database=croft_web_tests");

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<CroftWebDbContext>().Should().NotBeNull(
            "with ConnectionStrings:WebDb the app registers its database");
    }

    [Fact]
    public void WebDbNotConfigured_UsesTheInMemoryTokenStore()
    {
        using var factory = CreateFactory(webDb: null);

        factory.Services.GetRequiredService<ITokenStore>().Should().BeOfType<InMemoryTokenStore>(
            "without ConnectionStrings:WebDb the app keeps its single-instance in-memory sessions");
        factory.Services.GetRequiredService<ISessionLock>().Should().BeOfType<InMemoryTokenStore>(
            "the refresh lock's in-memory implementation is the store itself");
        factory.Services.GetServices<IHostedService>().Should().NotContain(service => service is SessionPurgeHostedService,
            "with no database there is nothing to purge");
    }

    [Fact]
    public void WebDbConfigured_UsesTheSharedPostgresTokenStore()
    {
        using var factory = CreateFactory(webDb: "Host=localhost;Database=croft_web_tests");

        factory.Services.GetRequiredService<ITokenStore>().Should().BeOfType<PostgresTokenStore>(
            "with ConnectionStrings:WebDb the sessions are shared by every Web instance");
        factory.Services.GetRequiredService<ISessionLock>().Should().BeOfType<PostgresTokenStore>(
            "the refresh's row lock lives where the rows live");
        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is SessionPurgeHostedService,
            "the shared store's expired sessions are purged by a background job");
    }

    private static WebApplicationFactory<Program> CreateFactory(string? webDb) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Startup reads it when an HttpClient is created; these tests make no HTTP requests.
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            if (webDb is not null)
            {
                builder.UseSetting("ConnectionStrings:WebDb", webDb);
            }
        });
}
