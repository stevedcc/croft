using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests.Fixtures;

public class BlogApiFactory(
    string connectionString,
    string storagePath,
    IReadOnlyDictionary<string, string?>? configuration = null,
    Action<IServiceCollection>? configureServices = null,
    DbCommandInterceptor? commandInterceptor = null,
    string? environment = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // WebApplicationFactory hosts run in Development unless told otherwise; the JWT tests
        // (JwtBearerApi) ask for Production, where the app validates the token's audience.
        if (environment is not null)
        {
            builder.UseEnvironment(environment);
        }

        builder.ConfigureAppConfiguration(config =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["FileStorage:BasePath"] = storagePath,
                // Production supplies this from the environment; tests must too, so anything
                // reading the configured connection string (the cache listener) hits the test
                // database instead of finding no connection string at all.
                ["ConnectionStrings:BlogDb"] = connectionString
            };
            if (configuration is not null)
            {
                foreach (var (key, value) in configuration)
                {
                    settings[key] = value;
                }
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            services.Remove(services.Single(d => d.ServiceType == typeof(DbContextOptions<Croft.Api.Data.BlogDbContext>)));
            services.AddDbContext<Croft.Api.Data.BlogDbContext>(
                (serviceProvider, options) =>
                {
                    // The app's own options callback builds every connection string through
                    // Croft.Api.Data.BlogDbConnectionString (pool settings and the read-replica
                    // routing); the factory re-runs the same builder and adds its interceptor.
                    Croft.Api.Data.BlogDbOptions.Configure(serviceProvider, options);
                    if (commandInterceptor is not null)
                    {
                        options.AddInterceptors(commandInterceptor);
                    }
                },
                optionsLifetime: ServiceLifetime.Scoped);

            configureServices?.Invoke(services);
        });
    }
}
