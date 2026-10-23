using System.Net;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Croft.Api.Tests;

public class StartupConfigurationTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Startup_WithoutAudience_OutsideDevelopment_Fails()
    {
        var connectionString = fixture.ConnectionString;
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        // Analytics keys are configured so the audience guard is the only thing left to refuse.
        await using var factory = new BlogApiFactory(
                connectionString,
                storagePath,
                new Dictionary<string, string?>
                {
                    ["Analytics:HashKey"] = "hash-key",
                    ["Analytics:IngestKey"] = "ingest-key",
                    ["Oidc:Audience"] = ""
                })
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var act = () => factory.CreateClient();

        var exception = act.Should().Throw<Exception>().Which;
        var messages = new List<string>();
        for (var error = exception; error is not null; error = error.InnerException)
        {
            messages.Add(error.Message);
        }

        string.Join("\n", messages).Should().Contain("Oidc:Audience");
    }

    [Fact]
    public async Task Startup_WithPlaceholderAudience_OutsideDevelopment_ServesHealth()
    {
        var connectionString = fixture.ConnectionString;
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        // The deploy-time placeholder in blog-api-config is obviously wrong on purpose; the API
        // must still start with it and fail token validation (not startup) until it is replaced.
        await using var factory = new BlogApiFactory(
                connectionString,
                storagePath,
                new Dictionary<string, string?>
                {
                    ["Analytics:HashKey"] = "hash-key",
                    ["Analytics:IngestKey"] = "ingest-key",
                    ["Oidc:Audience"] = "SET-TO-OIDC-CLIENT-ID"
                })
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
