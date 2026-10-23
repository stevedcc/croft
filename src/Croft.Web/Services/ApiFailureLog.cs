namespace Croft.Web.Services;

/// <summary>
/// The one Warning line a component writes when an API call it makes fails and the page carries on
/// (an error line, an empty sidebar). A session that ends is not an API fault: the sign-in
/// redirect is under way, so <see cref="SessionExpiredException"/> is not logged.
/// </summary>
internal static partial class ApiFailureLog
{
    public static void Failed(ILogger logger, string operation, HttpRequestException exception)
    {
        if (exception is SessionExpiredException)
        {
            return;
        }

        LogFailed(logger, operation, exception.GetType().Name, (int?)exception.StatusCode, exception.Message);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The API call for {Operation} failed: {ExceptionType} (status {StatusCode}): {Reason}")]
    private static partial void LogFailed(ILogger logger, string operation, string exceptionType, int? statusCode, string reason);
}
