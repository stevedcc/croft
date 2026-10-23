using Polly;
using Polly.Retry;
using Polly.Telemetry;

namespace Croft.Web.Services;

/// <summary>
/// Registers the "BlogApi" HttpClient: the base address and the resilience policy (brief 13c
/// item 1) that keeps a stalled API from turning into a site-wide outage.
/// <list type="bullet">
/// <item>A per-attempt timeout (<c>Croft:Web:Api:AttemptTimeoutSeconds</c>, 5 s) and a total timeout
/// (<c>TotalTimeoutSeconds</c>, 10 s). The total covers getting a response's headers: a caller that
/// reads with <see cref="HttpCompletionOption.ResponseHeadersRead"/> (the media proxy) streams the
/// body outside it.</item>
/// <item>Retries for GET and HEAD only, at most 2, exponential backoff with jitter (first delay
/// <c>RetryDelayMilliseconds</c>, 250), on a connection error, an attempt timeout, 408, 429 (its
/// Retry-After honoured, within the total timeout) and 5xx except 501. A write is never retried.</item>
/// <item>A circuit breaker: failure ratio 0.5 over 30 s with at least
/// <c>CircuitBreakerMinimumCalls</c> (20) calls, open for <c>CircuitBreakerBreakSeconds</c> (15) s.</item>
/// </list>
/// </summary>
public static class BlogApiHttpClient
{
    public const string Name = "BlogApi";

    public static IHttpClientBuilder AddBlogApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Croft:Web:Api");
        var attemptTimeout = TimeSpan.FromSeconds(section.GetValue("AttemptTimeoutSeconds", 5.0));
        var totalTimeout = TimeSpan.FromSeconds(section.GetValue("TotalTimeoutSeconds", 10.0));
        var retryDelay = TimeSpan.FromMilliseconds(section.GetValue("RetryDelayMilliseconds", 250.0));
        var minimumCalls = section.GetValue("CircuitBreakerMinimumCalls", 20);
        var breakDuration = TimeSpan.FromSeconds(section.GetValue("CircuitBreakerBreakSeconds", 15.0));

        // The pipeline reports a timeout, an exhausted retry or an open breaker at Error severity, and
        // every attempt of every call at Information. For this client the first are expected outages the
        // app handles (the page answers 503 and the exception handler logs the Warning) and the second
        // would put a line in the operator's log for every API call of every page view: Error becomes
        // Warning, Information becomes Debug.
        services.Configure<TelemetryOptions>(options => options.SeverityProvider = args => args.Event.Severity switch
        {
            > ResilienceEventSeverity.Warning => ResilienceEventSeverity.Warning,
            ResilienceEventSeverity.Information => ResilienceEventSeverity.Debug,
            var severity => severity,
        });

        var builder = services.AddHttpClient(Name, client =>
        {
            client.BaseAddress = new Uri(configuration["ApiBaseUrl"]!);
            // The resilience handler owns the time budget; the client's own 100 s default must not
            // cut a streamed body short or outlive it.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = attemptTimeout;
            options.TotalRequestTimeout.Timeout = totalTimeout;

            options.Retry.MaxRetryAttempts = 2;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = retryDelay;
            options.Retry.ShouldRetryAfterHeader = true;
            options.Retry.ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args));

            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.MinimumThroughput = minimumCalls;
            options.CircuitBreaker.BreakDuration = breakDuration;
        });

        return builder;
    }

    private static bool ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var method = args.Context.GetRequestMessage()?.Method;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            return false;

        if (args.Outcome.Exception is { } exception)
        {
            return exception is HttpRequestException or Polly.Timeout.TimeoutRejectedException;
        }

        var status = args.Outcome.Result?.StatusCode;
        return status is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
            || (status >= System.Net.HttpStatusCode.InternalServerError && status != System.Net.HttpStatusCode.NotImplemented);
    }
}
