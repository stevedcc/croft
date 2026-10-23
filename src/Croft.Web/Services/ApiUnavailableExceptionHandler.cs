using Microsoft.AspNetCore.Diagnostics;

namespace Croft.Web.Services;

/// <summary>
/// Logs an expected, already-handled API outage as a Warning (brief 13c item 1) and leaves the
/// answer to the exception handler's error page, which carries the 503.
/// </summary>
public sealed partial class ApiUnavailableExceptionHandler(ILogger<ApiUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (ApiUnavailableException.IsUnavailable(exception))
        {
            LogApiUnavailable(httpContext.Request.Path, exception.GetType().Name, exception.Message);
        }

        return ValueTask.FromResult(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The API is unavailable for {Path}: {ExceptionType}: {Reason}")]
    private partial void LogApiUnavailable(string path, string exceptionType, string reason);
}

internal static partial class ApiOutageLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The API is unavailable for media {Id}: {ExceptionType}")]
    public static partial void MediaUnavailable(ILogger logger, string id, string exceptionType);
}
