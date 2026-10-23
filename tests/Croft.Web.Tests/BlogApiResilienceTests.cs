using System.Diagnostics;
using System.Net;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13c item 1: the "BlogApi" client fails fast. A hanging API is abandoned after the attempt
/// timeout and the total timeout, idempotent reads are retried, writes never are, and a stalled
/// API trips a circuit breaker. The tests use the real registration over a scripted primary handler
/// and short configured timeouts.
/// </summary>
public class BlogApiResilienceTests
{
    private static ServiceProvider Build(ScriptedApiHandler api, Dictionary<string, string?>? settings = null, ILoggerProvider? logs = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ApiBaseUrl"] = "http://blog-api.test/",
            ["Croft:Web:Api:AttemptTimeoutSeconds"] = "0.2",
            ["Croft:Web:Api:TotalTimeoutSeconds"] = "0.5",
            ["Croft:Web:Api:RetryDelayMilliseconds"] = "1",
        };
        foreach (var (key, value) in settings ?? [])
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (logs is not null)
                logging.AddProvider(logs);
        });
        services.AddBlogApiClient(configuration).ConfigurePrimaryHttpMessageHandler(() => api);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AHangingApi_IsAbandonedAfterTheAttemptTimeout_AndTheTotalTimeout()
    {
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.Hang);
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        // The guard only keeps a missing policy from hanging the suite: the policy must give up first.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        var act = () => client.GetAsync("/api/posts", guard.Token);
        await act.Should().ThrowAsync<TimeoutRejectedException>("the call is abandoned by the policy, not awaited");
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the total timeout (0.5 s) bounds the call");
        api.Attempts.Should().BeGreaterThan(1,
            "the 0.2 s attempt timeout abandoned the first attempt and a GET was retried within the 0.5 s total");
    }

    [Fact]
    public async Task AHealthyCall_WritesNoInformationLinesFromThePolicy()
    {
        // Every API call of every page goes through the policy: its per-attempt "execution attempt"
        // line must not be an Information line of the operator's log.
        var logs = new InformationCounter();
        var api = new ScriptedApiHandler((_, _) => HttpStatusCode.OK);
        await using var provider = Build(api, logs: logs);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        logs.PolicyLines.Should().BeEmpty("a successful call is not worth a log line at Information");
    }

    [Fact]
    public async Task AGet_ThatFailsTwiceThenSucceeds_SucceedsWithExactlyThreeAttempts()
    {
        var api = new ScriptedApiHandler((_, attempt) => attempt <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        await using var provider = Build(api, new() { ["Croft:Web:Api:AttemptTimeoutSeconds"] = "5", ["Croft:Web:Api:TotalTimeoutSeconds"] = "10" });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.Attempts.Should().Be(3, "the original attempt and the two retries");
    }

    [Fact]
    public async Task AGet_ThatKeepsFailing_StopsAfterTwoRetries_AndReturnsTheLastResponse()
    {
        var api = new ScriptedApiHandler((_, _) => HttpStatusCode.BadGateway);
        await using var provider = Build(api, new() { ["Croft:Web:Api:AttemptTimeoutSeconds"] = "5", ["Croft:Web:Api:TotalTimeoutSeconds"] = "10" });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        api.Attempts.Should().Be(3, "at most two retries");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AGet_IsNotRetried_ForAnAnswerThatIsNotTransient(HttpStatusCode status)
    {
        var api = new ScriptedApiHandler((_, _) => status);
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(status);
        api.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AGet_IsRetried_ForARequestTimeoutAndForTooManyRequests(HttpStatusCode status)
    {
        var api = new ScriptedApiHandler((_, attempt) => attempt == 1 ? status : HttpStatusCode.OK);
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task AGet_WhoseConnectionFails_IsRetried()
    {
        var api = new ScriptedApiHandler((_, attempt) =>
            attempt == 1 ? ScriptedApiHandler.ConnectionError : HttpStatusCode.OK);
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task ARetriedGet_HonoursTheRetryAfterHeader_OfATooManyRequestsAnswer()
    {
        var api = new ScriptedApiHandler((_, attempt) => attempt == 1
            ? ScriptedApiHandler.WithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1))
            : HttpStatusCode.OK);
        await using var provider = Build(api, new() { ["Croft:Web:Api:AttemptTimeoutSeconds"] = "5", ["Croft:Web:Api:TotalTimeoutSeconds"] = "10" });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/posts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        api.AttemptTimes.Should().HaveCount(2);
        (api.AttemptTimes[1] - api.AttemptTimes[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900),
            "the configured backoff is 1 ms: only the Retry-After of 1 s explains a longer pause");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AWrite_IsNeverRetried(string method)
    {
        var api = new ScriptedApiHandler((_, _) => HttpStatusCode.ServiceUnavailable);
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/posts"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        api.Attempts.Should().Be(1, "a write that failed may have happened; repeating it is not safe");
    }

    [Fact]
    public async Task AWrite_WhoseConnectionFails_IsNeverRetried()
    {
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.ConnectionError);
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        var act = () => client.PostAsync("/api/posts", new StringContent("{}"));

        await act.Should().ThrowAsync<HttpRequestException>();
        api.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task TheBreaker_OpensAfterTheConfiguredFailures_AndShortCircuitsFurtherCalls()
    {
        var api = new ScriptedApiHandler((_, _) => HttpStatusCode.ServiceUnavailable);
        await using var provider = Build(api, new()
        {
            ["Croft:Web:Api:AttemptTimeoutSeconds"] = "5",
            ["Croft:Web:Api:TotalTimeoutSeconds"] = "10",
            ["Croft:Web:Api:CircuitBreakerMinimumCalls"] = "4",
        });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        for (var i = 0; i < 4; i++)
        {
            using var failed = await client.PostAsync("/api/posts", new StringContent("{}"));
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        var act = () => client.PostAsync("/api/posts", new StringContent("{}"));
        await act.Should().ThrowAsync<BrokenCircuitException>("four failures out of four calls is past the 0.5 ratio");
        api.Attempts.Should().Be(4, "the open breaker never reaches the API");
    }

    [Fact]
    public async Task TheBreaker_StaysClosed_BelowTheMinimumNumberOfCalls()
    {
        var api = new ScriptedApiHandler((_, _) => HttpStatusCode.ServiceUnavailable);
        await using var provider = Build(api, new()
        {
            ["Croft:Web:Api:AttemptTimeoutSeconds"] = "5",
            ["Croft:Web:Api:TotalTimeoutSeconds"] = "10",
            ["Croft:Web:Api:CircuitBreakerMinimumCalls"] = "4",
        });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        for (var i = 0; i < 3; i++)
        {
            using var failed = await client.PostAsync("/api/posts", new StringContent("{}"));
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        api.Attempts.Should().Be(3, "every call reached the API while the breaker was closed");
    }

    [Fact]
    public async Task AResponseBody_MayStreamLongerThanTheTotalTimeout_WhenTheHeadersArrivedInTime()
    {
        // The media proxy reads with ResponseHeadersRead: the timeout covers getting the headers,
        // not the body stream (a large image may take longer to stream).
        var api = new ScriptedApiHandler((_, _) => ScriptedApiHandler.SlowBody(chunks: 4, chunkDelay: TimeSpan.FromMilliseconds(250)));
        await using var provider = Build(api);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("BlogApi");

        using var response = await client.GetAsync("/api/media/1", HttpCompletionOption.ResponseHeadersRead);
        var clock = Stopwatch.StartNew();
        var body = await response.Content.ReadAsStringAsync();
        clock.Stop();

        body.Should().Be("abcd");
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500), "the body took longer than the 0.5 s total timeout");
    }

    private sealed class InformationCounter : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> PolicyLines
        {
            get { lock (_lines) return [.. _lines]; }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(InformationCounter owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Information && category.StartsWith("Polly", StringComparison.Ordinal))
                {
                    lock (owner._lines)
                        owner._lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
