using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AwesomeAssertions;
using Croft.Web.Tests.Fixtures;

namespace Croft.Web.Tests;

/// <summary>
/// The circuit client's failure paths against a scripted hub: each failure must surface at once and
/// say what the client was waiting for, never as a bare transport exception or a timeout.
/// </summary>
public class BlazorCircuitClientTests
{
    private const string HandshakeAck = "{}\u001e";

    [Fact]
    public async Task ConnectAsync_FirstPollAnswers204_ThrowsConnectionClosedBeforeTheHandshake()
    {
        var hub = new ScriptedHub(Poll(HttpStatusCode.NoContent));

        var connect = () => BlazorCircuitClient.ConnectAsync(hub.Client());

        await connect.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("the server closed the connection before the handshake");
        hub.Sends.Should().BeEmpty("the handshake must not be sent on a closed connection");
    }

    [Fact]
    public async Task ConnectAsync_FirstPollNeverAnswers_ThrowsTimeoutNamingTheFirstPoll()
    {
        // No scripted poll: the first long poll stays open until the client gives up.
        var hub = new ScriptedHub();

        var connect = () => BlazorCircuitClient.ConnectAsync(hub.Client(), TimeSpan.FromMilliseconds(200));

        await connect.Should().ThrowAsync<TimeoutException>()
            .WithMessage("the first long poll did not complete within 0.2 s");
        hub.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ServerClosesTheConnection_FailsWithWhatItWasWaitingFor()
    {
        var hub = new ScriptedHub(Poll(HttpStatusCode.OK), Poll(HttpStatusCode.OK, HandshakeAck), Poll(HttpStatusCode.NoContent));
        await using var client = await BlazorCircuitClient.ConnectAsync(hub.Client());

        var invoke = () => client.InvokeAsync("StartCircuit");

        var thrown = await invoke.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().StartWith(
            "the server closed the connection while waiting for the completion of StartCircuit; messages seen meanwhile: ;");
    }

    [Fact]
    public async Task InvokeAsync_PollFailsWith500_FailsAtOnceWithThePollsError()
    {
        var hub = new ScriptedHub(Poll(HttpStatusCode.OK), Poll(HttpStatusCode.OK, HandshakeAck), Poll(HttpStatusCode.InternalServerError));
        // A wait limit far beyond the test's run time: only a fail-fast path can end the call.
        await using var client = await BlazorCircuitClient.ConnectAsync(hub.Client(), TimeSpan.FromMinutes(5));

        var invoke = () => client.InvokeAsync("StartCircuit");

        var thrown = await invoke.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().StartWith("the long poll failed while waiting for the completion of StartCircuit;");
        thrown.Which.InnerException.Should().BeOfType<HttpRequestException>()
            .Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    private static Func<HttpResponseMessage> Poll(HttpStatusCode status, string body = "")
        => () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };

    /// <summary>
    /// A SignalR long-polling endpoint from a script: negotiate succeeds, every send is accepted
    /// and recorded, and each poll gets the next scripted answer. A poll beyond the script stays
    /// open until the client cancels it, like a server with nothing to say.
    /// </summary>
    private sealed class ScriptedHub(params Func<HttpResponseMessage>[] polls) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpResponseMessage>> _polls = new(polls);

        public ConcurrentQueue<string> Sends { get; } = new();

        public HttpClient Client() => new(this) { BaseAddress = new Uri("http://hub.test") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/_blazor/negotiate")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"negotiateVersion":1,"connectionId":"c","connectionToken":"t"}""", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                Sends.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (_polls.TryDequeue(out var answer))
            {
                return answer();
            }

            var open = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => open.TrySetCanceled(cancellationToken)))
            {
                return await open.Task;
            }
        }
    }
}
