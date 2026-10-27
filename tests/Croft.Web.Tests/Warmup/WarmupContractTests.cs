using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Croft.Shared.Warmup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// Compiled into BOTH test projects (Croft.Web.Tests owns it, Croft.Api.Tests links it): brief 14a
// item 1 asks for the same gate-and-runner behaviour from both apps, and one body of tests keeps
// them from drifting apart. Each project supplies only how to start its own host.
namespace Croft.Warmup.Contract;

/// <summary>How a test wants the app host: the warm-up's request list, the handler that answers
/// those requests, settings, the clock, and a log capture.</summary>
public sealed class WarmupHostSetup
{
    /// <summary>The warm-up's request list; null keeps the app's own plan.</summary>
    public IReadOnlyList<WarmupStep>? Plan { get; init; }

    /// <summary>Answers the warm-up's requests. Null: the requests go wherever the host sends them (real loopback).</summary>
    public HttpMessageHandler? Handler { get; init; }

    public Dictionary<string, string?> Config { get; } = new() { ["Warmup:Enabled"] = "true" };

    public TimeProvider? Time { get; init; }

    public WarmupLogCapture Logs { get; } = new();

    /// <summary>Real Kestrel on a loopback port instead of the TestServer, so the warm-up's
    /// requests are real HTTP and the HttpClient makes its client Activities.</summary>
    public bool Kestrel { get; init; }

    /// <summary>The part of host set-up every app needs the same way.</summary>
    public void Apply(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Config));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            if (Plan is not null)
            {
                services.RemoveAll<IWarmupPlan>();
                services.AddSingleton<IWarmupPlan>(new TestPlan(Plan));
            }

            if (Handler is not null)
            {
                services.AddHttpClient(WarmupRunner.ClientName).ConfigurePrimaryHttpMessageHandler(() => Handler);
            }

            if (Time is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(Time);
            }
        });
    }

    /// <summary>Switches a root factory (the one a test creates before <c>WithWebHostBuilder</c>) to real Kestrel on
    /// an OS-assigned port when the setup asks for it, and returns it. It must be called on the root:
    /// <c>WithWebHostBuilder</c> derives a new factory that does not carry the setting over, and a
    /// <c>UseKestrel</c> call on the derived one is ignored, so the host binds Kestrel's default
    /// localhost:5000 (and fails while E2E instance 1 holds that port).</summary>
    // WORKAROUND(W-106, ASP.NET Core): UseKestrel on a WithWebHostBuilder-derived factory is silently ignored, so it is called on the root
    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntry> UseRealKestrelOnRoot<TEntry>(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntry> root)
        where TEntry : class
    {
        if (Kestrel)
        {
            root.UseKestrel(0);
        }

        return root;
    }
    // END WORKAROUND(W-106)

    /// <summary>Starts the configured factory (on real Kestrel when asked) and wraps it for the tests.</summary>
    public WarmupTestHost Launch<TEntry>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntry> factory)
        where TEntry : class
    {
        if (!Kestrel)
        {
            return new WarmupTestHost(factory, factory.CreateClient(), factory.Services);
        }

        factory.StartServer();
        return new WarmupTestHost(factory, new HttpClient { BaseAddress = new Uri(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First()) }, factory.Services);
    }

    private sealed class TestPlan(IReadOnlyList<WarmupStep> steps) : IWarmupPlan
    {
        public IReadOnlyList<WarmupStep> Steps => steps;
    }
}

public sealed class WarmupTestHost(IDisposable factory, HttpClient client, IServiceProvider services) : IAsyncDisposable
{
    public HttpClient Client => client;

    public IServiceProvider Services => services;

    public WarmupGate Gate => services.GetRequiredService<WarmupGate>();

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        factory.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record CapturedLog(LogLevel Level, string Category, string Message);

public sealed class WarmupLogCapture : ILoggerProvider
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public IEnumerable<CapturedLog> FromRunner => Entries.Where(e => e.Category.EndsWith("WarmupRunner", StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(WarmupLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue(new CapturedLog(logLevel, category, formatter(state, exception)));
    }
}

/// <summary>The warm-up's requests, answered by the test: records every path, lets a test see a
/// request arrive, and answers each path as scripted (held on a gate, 500, an exception, 200).</summary>
public sealed class ScriptedWarmupHandler(Func<string, CancellationToken, Task<HttpResponseMessage>>? respond = null)
    : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _arrivals = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    public Task ArrivedAsync(string path) => Arrival(path).Task.WaitAsync(TimeSpan.FromSeconds(30));

    private TaskCompletionSource Arrival(string path) =>
        _arrivals.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Enqueue(path);
        Arrival(path).TrySetResult();
        return respond is null
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : await respond(path, cancellationToken);
    }
}

/// <summary>Records every request the warm-up client sends (path and query) and the status it got
/// back, in order, on its way to the real host.</summary>
public sealed class RecordingHandler : DelegatingHandler
{
    public ConcurrentQueue<string> Paths { get; } = new();

    public ConcurrentQueue<(string Path, HttpStatusCode Status)> Responses { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Paths.Enqueue(path);
        var response = await base.SendAsync(request, cancellationToken);
        Responses.Enqueue((path, response.StatusCode));
        return response;
    }
}

/// <summary>Sends through the TestServer's handler, which exists only after the host starts: the
/// warm-up's loopback requests reach the very host that sends them, with no socket.</summary>
public sealed class LateBoundHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource<HttpMessageInvoker> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called once the test has its server (the warm-up may start before that: the host
    /// raises ApplicationStarted while the factory is still building its server).</summary>
    public void Ready(HttpMessageHandler handler) => _ready.SetResult(new HttpMessageInvoker(handler));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var invoker = await _ready.Task.WaitAsync(cancellationToken);
        return await invoker.SendAsync(request, cancellationToken);
    }
}

/// <summary>A fake clock whose timers follow it, so the warm-up's budget runs on simulated time.</summary>
public sealed class FakeBudgetClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan delta)
    {
        List<Entry> due;
        lock (_gate)
        {
            _now += delta;
            due = [.. _timers.Where(t => !t.Disposed && t.Due <= _now)];
            foreach (var entry in due)
            {
                entry.Disposed = true;
            }
        }

        foreach (var entry in due)
        {
            entry.Callback(entry.State);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var entry = new Entry(callback, state, _now + dueTime);
            _timers.Add(entry);
            return entry;
        }
    }

    private sealed class Entry(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public DateTimeOffset Due = due;
        public bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Brief 14a item 1, for either app: the readiness gate, the runner's budget, failures and trace.</summary>
public abstract partial class WarmupContractTests
{
    protected abstract WarmupTestHost Start(WarmupHostSetup setup);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ReadyIs503WhileTheWarmupRuns_ThenOpensWhenItFinishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedWarmupHandler(async (path, ct) =>
        {
            if (path == "/held")
            {
                await release.Task.WaitAsync(ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get("/held"), WarmupStep.Get("/after")], Handler = handler };
        await using var host = Start(setup);
        await handler.ArrivedAsync("/held");

        using (var held = await host.Client.GetAsync("/health/ready"))
        {
            held.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "a pod that is still warming up takes no readers");
        }

        release.SetResult();
        await host.Gate.WhenOpen.WaitAsync(Patience);

        using var ready = await host.Client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK, "the warm-up finished, so the gate is open");
        handler.Requests.Should().Equal(["/held", "/after"], "each request is sent once, one at a time, in order");
    }

    [Fact]
    public async Task LivenessIgnoresTheWarmup()
    {
        var handler = new ScriptedWarmupHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get("/held")], Handler = handler };
        await using var host = Start(setup);
        await handler.ArrivedAsync("/held");

        using var live = await host.Client.GetAsync("/health/live");

        live.StatusCode.Should().Be(HttpStatusCode.OK, "a restart cannot speed a warm-up up: liveness never waits for it");
        host.Gate.IsOpen.Should().BeFalse("the warm-up is still held, so this proves liveness answered during it");
    }

    [Fact]
    public async Task WhenDisabled_ReadyIs200AtOnce_AndNoRequestIsSent()
    {
        var handler = new ScriptedWarmupHandler();
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get("/a")], Handler = handler };
        setup.Config["Warmup:Enabled"] = "false";
        await using var host = Start(setup);

        using var ready = await host.Client.GetAsync("/health/ready");

        ready.StatusCode.Should().Be(HttpStatusCode.OK, "with the warm-up off the gate starts open");
        host.Gate.IsOpen.Should().BeTrue();
        handler.Requests.Should().BeEmpty("a disabled warm-up sends nothing");
        host.Logs(setup).Should().NotContain(e => e.Message.StartsWith("Warm-up", StringComparison.Ordinal),
            "a disabled warm-up does not even log a finish line");
    }

    [Fact]
    public async Task ABudgetShorterThanARequest_OpensTheGate_AndOneWarningNamesWhatWasNotDone()
    {
        var clock = new FakeBudgetClock();
        var handler = new ScriptedWarmupHandler(async (path, ct) =>
        {
            if (path == "/slow")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var setup = new WarmupHostSetup
        {
            Plan = [WarmupStep.Get("/fast"), WarmupStep.Get("/slow"), WarmupStep.Get("/never-sent")],
            Handler = handler,
            Time = clock,
        };
        setup.Config["Warmup:Timeout"] = "00:00:10";
        setup.Config["Warmup:RequestTimeout"] = "00:05:00";
        await using var host = Start(setup);
        await handler.ArrivedAsync("/slow");

        clock.Advance(TimeSpan.FromSeconds(11));
        await host.Gate.WhenOpen.WaitAsync(Patience);

        var warnings = host.Logs(setup).Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle("one Warning for the whole warm-up");
        warnings[0].Message.Should().Contain("/slow").And.Contain("/never-sent")
            .And.NotContain("/fast", "the request that succeeded is not named");
        handler.Requests.Should().Equal(["/fast", "/slow"], "the request after the budget is never sent");
        host.Logs(setup).Should().ContainSingle(e => e.Level == LogLevel.Information
            && Regex.IsMatch(e.Message, @"^Warm-up finished in \d+ ms: 1 of 2 requests succeeded$"));
    }

    [Fact]
    public async Task FailedRequestsAreNamedInOneWarning_TheRestStillRun_AndTheGateOpens()
    {
        var handler = new ScriptedWarmupHandler((path, _) => path switch
        {
            "/err" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            "/boom" => throw new HttpRequestException("connection refused"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
        });
        var setup = new WarmupHostSetup
        {
            Plan = [WarmupStep.Get("/one"), WarmupStep.Get("/err"), WarmupStep.Get("/boom"), WarmupStep.Get("/two")],
            Handler = handler,
        };
        await using var host = Start(setup);
        await host.Gate.WhenOpen.WaitAsync(Patience);

        handler.Requests.Should().Equal(["/one", "/err", "/boom", "/two"], "a failure never stops the warm-up");
        var warnings = host.Logs(setup).Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("/err").And.Contain("500").And.Contain("/boom").And.Contain("HttpRequestException");
        host.Logs(setup).Should().ContainSingle(e => e.Level == LogLevel.Information
            && Regex.IsMatch(e.Message, @"^Warm-up finished in \d+ ms: 2 of 4 requests succeeded$"));
        using var ready = await host.Client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APathThatIsNotAUri_IsAFailureNamedInTheWarning_AndTheLaterStepsStillRun()
    {
        var handler = new ScriptedWarmupHandler();
        var setup = new WarmupHostSetup
        {
            Plan = [WarmupStep.Get("/one"), WarmupStep.Get("http://"), WarmupStep.Get("/two")],
            Handler = handler,
        };
        await using var host = Start(setup);
        await host.Gate.WhenOpen.WaitAsync(Patience);

        handler.Requests.Should().Equal(["/one", "/two"], "an unusable path fails its own step only");
        var warnings = host.Logs(setup).Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("http://").And.Contain("UriFormatException");
        host.Logs(setup).Should().ContainSingle(e => e.Message.EndsWith("2 of 3 requests succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsyncWithAnAlreadyCancelledToken_WhileAStepIsHeld_ReturnsWithoutThrowing()
    {
        var handler = new ScriptedWarmupHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get("/held")], Handler = handler };
        await using var host = Start(setup);
        await handler.ArrivedAsync("/held");
        var runner = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WarmupRunner>().Single();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var stop = () => runner.StopAsync(cancelled.Token);

        await stop.Should().NotThrowAsync("the host's stop token firing first is a normal shutdown, not a fault");
    }

    [Fact]
    public async Task AStepWithNoSource_IsSkippedNotFailed()
    {
        var handler = new ScriptedWarmupHandler();
        var setup = new WarmupHostSetup
        {
            Plan = [WarmupStep.Get("/a"), new WarmupStep("post", _ => null), WarmupStep.Get("/b")],
            Handler = handler,
        };
        await using var host = Start(setup);
        await host.Gate.WhenOpen.WaitAsync(Patience);

        host.Logs(setup).Should().NotContain(e => e.Level >= LogLevel.Warning, "missing content is not a failure");
        host.Logs(setup).Should().ContainSingle(e => e.Message.EndsWith("2 of 2 requests succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryWarmupRequest_RunsInsideTheCroftWarmupActivity()
    {
        // The HttpClient redacts query strings in its Activity (url.full ends "?*"), so a query
        // cannot carry a marker; this test's host has its own port, which is the marker instead.
        var path = "/health/live";
        ConcurrentQueue<Activity> started = new();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is CroftActivity.SourceName or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = started.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var setup = new WarmupHostSetup { Plan = [WarmupStep.Get(path)], Kestrel = true };
        await using var host = Start(setup);
        await host.Gate.WhenOpen.WaitAsync(Patience);

        var port = host.Client.BaseAddress!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var clientActivities = started
            .Where(a => a.Kind == ActivityKind.Client
                && a.Tags.Any(t => t is { Key: "url.full" } && t.Value!.Contains($":{port}/", StringComparison.Ordinal)))
            .ToList();
        clientActivities.Should().NotBeEmpty("the warm-up's request went out over real HTTP, so the HttpClient made its Activity");
        foreach (var client in clientActivities)
        {
            Ancestors(client).Should().Contain(a => a.OperationName == CroftActivity.WarmupName,
                "every warm-up request belongs to the one croft.warmup trace");
        }
    }

    private static IEnumerable<Activity> Ancestors(Activity activity)
    {
        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            yield return parent;
        }
    }
}

internal static class WarmupContractExtensions
{
    public static IReadOnlyList<CapturedLog> Logs(this WarmupTestHost _, WarmupHostSetup setup) => [.. setup.Logs.FromRunner];
}
