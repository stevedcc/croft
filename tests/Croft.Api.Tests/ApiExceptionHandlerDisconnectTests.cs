using AwesomeAssertions;
using Croft.Api.Errors;
using Croft.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests;

/// <summary>
/// A client that disconnects mid-request cancels the request's token and the work in flight
/// throws OperationCanceledException: that is not an unhandled server error, so it must not be
/// an Error log with a stack trace (brief 13e item 6, review M-13). A cancellation the client did
/// not cause (its token is not cancelled) is still a real error.
/// </summary>
public class ApiExceptionHandlerDisconnectTests : IDisposable
{
    private readonly LogCollector _logs = new();

    public void Dispose()
    {
        _logs.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OperationCanceled_WithTheRequestAborted_IsNotLoggedAsAnErrorOrAWarning()
    {
        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        await aborted.CancelAsync();

        var handled = await Handler().TryHandleAsync(context, new OperationCanceledException(aborted.Token), CancellationToken.None);

        handled.Should().BeTrue("the exception is dealt with here: nobody is left to answer");
        _logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Information,
            "a disconnect is routine: Debug or nothing, never an Error with a stack trace");
        context.Response.StatusCode.Should().Be(499, "the status the access log shows for a client that went away");
    }

    [Fact]
    public async Task OperationCanceled_WhileTheClientIsStillThere_IsStillAnError()
    {
        var context = new DefaultHttpContext();

        await Handler().TryHandleAsync(context, new OperationCanceledException("a timeout inside the server"), CancellationToken.None);

        _logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error,
            "only a client's own disconnect is excused: any other cancellation is a server fault");
        context.Response.StatusCode.Should().Be(500);
    }

    private ApiExceptionHandler Handler() =>
        new(new Logger<ApiExceptionHandler>(LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs))));
}
