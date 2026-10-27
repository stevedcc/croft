using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Croft.Warmup.Contract;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Croft.Api.Tests.Warmup;

/// <summary>The warm-up contract on the API host: readiness also asks the primary database (a real one here).</summary>
public sealed class ApiWarmupContractTests(PostgresDatabaseFixture fixture)
    : WarmupContractTests, IClassFixture<PostgresDatabaseFixture>
{
    protected override WarmupTestHost Start(WarmupHostSetup setup)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        var configured = new BlogApiFactory(fixture.ConnectionString, storagePath).WithWebHostBuilder(setup.Apply);
        return setup.Launch(configured);
    }

    [Fact]
    public async Task TheWarmupCheck_SaysWarmingUp_WhileTheGateIsClosed_AndLivenessIsNotAskedForIt()
    {
        var hold = new TaskCompletionSource();
        var handler = new ScriptedWarmupHandler(async (_, ct) =>
        {
            await hold.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var setup = new WarmupHostSetup { Plan = [Croft.Shared.Warmup.WarmupStep.Get("/held")], Handler = handler };
        await using var host = Start(setup);
        await handler.ArrivedAsync("/held");

        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Tags.Contains("ready"));
        using var ready = await host.Client.GetAsync("/health/ready");
        using var alias = await host.Client.GetAsync("/health");

        report.Entries["warmup"].Status.Should().Be(HealthStatus.Unhealthy);
        report.Entries["warmup"].Description.Should().Be("Warming up");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        alias.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "/health, the alias older Web pods read, carries the warm-up too");
    }
}
