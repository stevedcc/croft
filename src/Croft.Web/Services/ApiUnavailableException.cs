using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Croft.Web.Services;

/// <summary>
/// The API could not answer in time (the attempt or total timeout) or the circuit breaker is open
/// and short-circuited the call. It is an <see cref="HttpRequestException"/> so every page that
/// handles a failed API call handles this one, and the exception handler answers 503 for it.
/// </summary>
public sealed class ApiUnavailableException(string message, Exception innerException)
    : HttpRequestException(message, innerException, HttpStatusCode.ServiceUnavailable)
{
    /// <summary>The exceptions the resilience handler throws when it gives up on a call.</summary>
    public static bool IsResilienceFailure(Exception exception) =>
        exception is TimeoutRejectedException or BrokenCircuitException;

    /// <summary>Whether a failed API call means "the API is unavailable" (answer 503), as opposed to
    /// a failure of this app: our own <see cref="ApiUnavailableException"/>, a connection that could
    /// not be made, or the API answering 502, 503 or 504.</summary>
    public static bool IsUnavailable(Exception exception) => exception switch
    {
        ApiUnavailableException => true,
        // Callers that use the client directly (feed, sitemap) see the policy's own exceptions unwrapped.
        _ when IsResilienceFailure(exception) => true,
        SessionExpiredException => false,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
        _ => false,
    };
}
