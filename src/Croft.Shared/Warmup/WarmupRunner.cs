using System.Globalization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Croft.Shared.Warmup;

/// <summary>
/// Warms the pod before it takes readers (brief 14a): once Kestrel is listening
/// (<see cref="IHostApplicationLifetime.ApplicationStarted"/>) it sends the app's
/// <see cref="IWarmupPlan"/> to itself over loopback, one anonymous GET at a time through the
/// <c>"CroftWarmup"</c> client, inside the <see cref="WarmupSettings.Timeout"/> budget and the
/// per-request <see cref="WarmupSettings.RequestTimeout"/>. Whatever happens (done, failed, out of
/// time, stopping) it opens the <see cref="WarmupGate"/> in a <c>finally</c>: the gate never
/// blocks forever, and a failed request is logged and skipped past.
/// </summary>
public sealed partial class WarmupRunner(
    WarmupGate gate,
    IWarmupPlan plan,
    IOptions<WarmupSettings> settings,
    IHttpClientFactory httpClientFactory,
    IServer server,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<WarmupRunner> logger) : IHostedService
{
    /// <summary>The named client the requests go through; tests replace its primary handler.</summary>
    public const string ClientName = "CroftWarmup";

    private Task _run = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsOpen)
        {
            lifetime.ApplicationStarted.Register(() => _run = Task.Run(() => RunAsync(lifetime.ApplicationStopping)));
        }

        return Task.CompletedTask;
    }

    // The run's token is ApplicationStopping, cancelled before this is called, so it is already
    // unwinding. Observing it must not throw: the host's stop token firing first is a normal
    // shutdown, and the run itself never faults past its own catch.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _run.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host stopped waiting; the run carries on unwinding by itself.
        }
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        var options = settings.Value;
        var started = time.GetTimestamp();
        var total = 0;
        var succeeded = 0;
        List<string> failures = [];
        List<string> notReached = [];
        try
        {
            using var activity = CroftActivity.Source.StartActivity(CroftActivity.WarmupName);
            using var budget = new CancellationTokenSource(options.Timeout, time);
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, stopping);
            using var client = httpClientFactory.CreateClient(ClientName);
            var baseAddress = LoopbackAddress();
            var results = new WarmupResults();

            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                if (overall.IsCancellationRequested)
                {
                    notReached.Add(step.Name);
                    continue;
                }

                var path = step.ResolvePath(results);
                if (path is null)
                {
                    // The content this step warms does not exist on this site: skipped, not failed.
                    continue;
                }

                total++;
                using var request = new CancellationTokenSource(options.RequestTimeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, overall.Token);
                try
                {
                    using var response = await client.GetAsync(new Uri(baseAddress, path), linked.Token);
                    var body = step.KeepBody
                        ? await response.Content.ReadAsStringAsync(linked.Token)
                        : await DrainAsync(response, linked.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        succeeded++;
                        if (step.KeepBody)
                        {
                            results.Keep(step.Name, body);
                        }
                    }
                    else
                    {
                        failures.Add($"{path} ({(int)response.StatusCode})");
                    }
                }
                catch (OperationCanceledException) when (overall.IsCancellationRequested)
                {
                    // The budget ran out (or the pod is stopping) while this request was in flight.
                    notReached.Add($"{path} (in flight)");
                }
                catch (OperationCanceledException)
                {
                    failures.Add($"{path} (no answer within {options.RequestTimeout.TotalSeconds:0.#} s)");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException)
                {
                    failures.Add($"{path} ({ex.GetType().Name})");
                }
            }
        }
        catch (Exception ex)
        {
            // The warm-up is an optimisation: whatever goes wrong in it, the pod still serves.
            failures.Add($"the warm-up itself ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
            LogFinished(logger, elapsed, succeeded, total);
            if (failures.Count > 0 || notReached.Count > 0)
            {
                LogIncomplete(logger, string.Join(", ", failures), string.Join(", ", notReached));
            }

            gate.Open();
        }
    }

    private static async Task<string> DrainAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.CopyToAsync(Stream.Null, ct);
        return string.Empty;
    }

    private Uri LoopbackAddress() =>
        LoopbackOf(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []);

    /// <summary>The process's own listening address with the host replaced by loopback; the
    /// plain-http address when there are several. No address (a test server) gives a
    /// placeholder: the tests' handler never opens a socket.</summary>
    internal static Uri LoopbackOf(IEnumerable<string> addresses)
    {
        var chosen = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault();
        return chosen is null ? new Uri("http://127.0.0.1/") : LoopbackOf(chosen);
    }

    internal static Uri LoopbackOf(string address)
    {
        // WORKAROUND(W-097, ASP.NET Core): Kestrel reports wildcard addresses ("http://+:8080", "http://[::]:8080") that Uri cannot parse
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        var scheme = address[..schemeEnd];
        var authority = address[(schemeEnd + 3)..];
        var slash = authority.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        var colon = authority.LastIndexOf(':');
        var port = colon > authority.LastIndexOf(']') && colon >= 0
            ? authority[(colon + 1)..]
            : (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "443" : "80");
        return new Uri(string.Create(CultureInfo.InvariantCulture, $"{scheme}://127.0.0.1:{port}/"));
        // END WORKAROUND(W-097)
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Warm-up finished in {ElapsedMs} ms: {Succeeded} of {Total} requests succeeded")]
    private static partial void LogFinished(ILogger logger, long elapsedMs, int succeeded, int total);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Warm-up incomplete. Failed: [{Failed}]. Not reached: [{NotReached}]")]
    private static partial void LogIncomplete(ILogger logger, string failed, string notReached);
}

public static class WarmupServiceCollectionExtensions
{
    /// <summary>Registers the gate, the settings, the <c>"CroftWarmup"</c> client and the runner;
    /// the app supplies its <see cref="IWarmupPlan"/>.</summary>
    public static IServiceCollection AddCroftWarmup<TPlan>(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        where TPlan : class, IWarmupPlan
    {
        services.Configure<WarmupSettings>(configuration.GetSection(WarmupSettings.SectionName));
        services.AddSingleton(sp => new WarmupGate(sp.GetRequiredService<IOptions<WarmupSettings>>().Value.Enabled));
        services.AddSingleton<IWarmupPlan, TPlan>();
        services.AddHttpClient(WarmupRunner.ClientName);
        services.AddHostedService<WarmupRunner>();
        return services;
    }
}
