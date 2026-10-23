using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests;

public class BlogDbContextTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task CanApplyMigrations()
    {
        var connectionString = fixture.ConnectionString;
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        var services = new ServiceCollection();
        services.AddDbContext<BlogDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddSingleton<Croft.Api.Services.IFileStorage>(
            _ => new Croft.Api.Services.LocalFileStorage(storagePath));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();

        (await db.Database.CanConnectAsync()).Should().BeTrue();

        await db.Database.MigrateAsync();

        var pending = await db.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();
    }
}
