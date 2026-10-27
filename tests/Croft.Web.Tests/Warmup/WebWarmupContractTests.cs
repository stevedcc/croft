using System.Net;
using AwesomeAssertions;
using Croft.Shared.Warmup;
using Croft.Warmup.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Warmup;

/// <summary>The warm-up contract on the Web host: readiness also needs the API's /health (stubbed 200 here).</summary>
public sealed class WebWarmupContractTests : WarmupContractTests
{
    protected override WarmupTestHost Start(WarmupHostSetup setup)
    {
        var configured = setup.UseRealKestrelOnRoot(new WebApplicationFactory<Program>()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.ConfigureServices(services => services.AddHttpClient("BlogApi")
                .ConfigurePrimaryHttpMessageHandler(() => new ScriptedWarmupHandler()));
            setup.Apply(builder);
        });
        return setup.Launch(configured);
    }

    [Fact]
    public async Task ReadyAnswersWarmingUp_AndChecksNothingElse_WhileTheGateIsClosed()
    {
        var hold = new TaskCompletionSource();
        var handler = new ScriptedWarmupHandler(async (_, ct) =>
        {
            await hold.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new ScriptedWarmupHandler();
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get("/held")], Handler = handler };
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.ConfigureServices(services => services.AddHttpClient("BlogApi")
                .ConfigurePrimaryHttpMessageHandler(() => api));
            setup.Apply(builder);
        });
        using var client = factory.CreateClient();
        await handler.ArrivedAsync("/held");

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("Warming up");
        response.Headers.CacheControl!.ToString().Should().Be("no-store", "the health headers stay");
        response.Headers.GetValues("X-Robots-Tag").Should().Contain("noindex");
        api.Requests.Should().BeEmpty("the closed gate is checked first and nothing else is asked");
        factory.Dispose();
    }

    [Fact]
    public async Task WithTelemetryOn_TheWarmupIsExportedAsOneCroftWarmupSpan()
    {
        var capture = new Fixtures.TelemetryCapture();
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get("/a")], Handler = new ScriptedWarmupHandler() };
        using var factory = WebTelemetryTests.CreateFactory(capture).WithWebHostBuilder(setup.Apply);
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<WarmupGate>().WhenOpen.WaitAsync(TimeSpan.FromSeconds(30));

        var span = await capture.WaitForSpanAsync(s => s.OperationName == CroftActivity.WarmupName,
            "the croft.warmup span the orchestrator reads production by");

        span.Source.Name.Should().Be(CroftActivity.SourceName);
    }
}
