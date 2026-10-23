using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Croft.Api.Errors;

public sealed partial class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger,
    IProblemDetailsService? problemDetailsService = null) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // A client that disconnects mid-request cancels RequestAborted and the work in flight throws
        // OperationCanceledException: nobody is left to answer and nothing went wrong on the server,
        // so it is not an error. 499 is the status access logs show for a client that went away.
        // A cancellation the client did not cause (its token is not cancelled) is still a fault.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogClientDisconnected(logger, httpContext.Request.Path.Value ?? "");
            httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }

        var statusCode = exception switch
        {
            ValidationException => StatusCodes.Status400BadRequest,
            NotFoundException => StatusCodes.Status404NotFound,
            ForbiddenException => StatusCodes.Status403Forbidden,
            ConflictException => StatusCodes.Status409Conflict,
            UpstreamUnavailableException => StatusCodes.Status503ServiceUnavailable,
            DbUpdateException dbEx when dbEx.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        // 500s are real errors and keep the exception. The mapped statuses are routine traffic
        // outcomes (a bad link, a validation failure) — with alerts on error logs, logging them
        // as errors would page the owner for every bad link, so they log at Information without
        // the stack trace, saying what happened.
        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(logger, exception);
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            var path = httpContext.Request.Path.Value ?? "";
            var exceptionType = exception.GetType().Name;
            LogHandledRequest(logger, path, statusCode, exceptionType);
        }

        httpContext.Response.StatusCode = statusCode;

        // IProblemDetailsService is optional here: without it the response is still the mapped
        // status code with an empty body, exactly like the previous inline handler.
        if (problemDetailsService is not null)
        {
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails
                {
                    Title = statusCode switch
                    {
                        StatusCodes.Status400BadRequest => "Bad Request",
                        StatusCodes.Status404NotFound => "Not Found",
                        StatusCodes.Status403Forbidden => "Forbidden",
                        StatusCodes.Status409Conflict => "Conflict",
                        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
                        _ => "An error occurred"
                    },
                    Status = statusCode,
                    Detail = exception switch
                    {
                        ConflictException conflictException => conflictException.Message,
                        _ => statusCode switch
                        {
                            StatusCodes.Status500InternalServerError => "An unexpected error occurred.",
                            StatusCodes.Status409Conflict => "A conflict occurred.",
                            _ => exception.Message
                        }
                    }
                }
            });
        }

        return true;
    }

    // WORKAROUND(W-014, ASP.NET Core): the .NET 10 exception-handler middleware no longer logs handled exceptions, so the handler logs them itself
    // Unmapped exceptions are real errors; the .NET 10 middleware no longer logs handled
    // exceptions, so the handler owns that log with the exception attached.
    [LoggerMessage(Level = LogLevel.Error, Message = "An unhandled exception has occurred while executing the request.")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The client disconnected during the request to {Path}.")]
    private static partial void LogClientDisconnected(ILogger logger, string path);

    // Mapped statuses (400/403/404/409) are routine outcomes, logged without the stack trace.
    [LoggerMessage(Level = LogLevel.Information, Message = "Request to {Path} ended with {StatusCode} ({ExceptionType})")]
    private static partial void LogHandledRequest(ILogger logger, string path, int statusCode, string exceptionType);
    // END WORKAROUND(W-014)
}
