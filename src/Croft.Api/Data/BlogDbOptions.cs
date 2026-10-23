using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Data;

/// <summary>The one place the API's DbContext options are built, so the app and the test factory agree.</summary>
public static class BlogDbOptions
{
    /// <summary>The Npgsql connection for this request (primary or read replica, see
    /// <see cref="BlogDbConnectionString.ForRequest"/>) and the interceptor that sends a refill that must
    /// not read behind a write to the primary (<see cref="PrimaryRefill"/>).</summary>
    public static DbContextOptionsBuilder Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options
            .UseNpgsql(BlogDbConnectionString.ForRequest(
                serviceProvider.GetRequiredService<IConfiguration>(),
                serviceProvider.GetRequiredService<IHttpContextAccessor>()))
            .AddInterceptors(serviceProvider.GetRequiredService<PrimaryRefillConnectionInterceptor>());
}
