using System.Net;
using System.Net.Http.Headers;

namespace Croft.Web.Tests;

/// <summary>
/// A primary handler that answers each attempt from a script: a status, a hang (until the request
/// is cancelled), a connection error or a response with a slow body. Counts the attempts that
/// reached it.
/// </summary>
internal sealed class ScriptedApiHandler(Func<HttpRequestMessage, int, ScriptedApiHandler.Step> script) : HttpMessageHandler
{
    private int _attempts;

    public static readonly Step Hang = new(Kind.Hang);

    public static readonly Step ConnectionError = new(Kind.ConnectionError);

    public int Attempts => Volatile.Read(ref _attempts);

    public List<DateTimeOffset> AttemptTimes { get; } = [];

    public static Step WithRetryAfter(HttpStatusCode status, TimeSpan retryAfter) => new(Kind.Status, status, retryAfter);

    public static Step SlowBody(int chunks, TimeSpan chunkDelay) => new(Kind.SlowBody, Chunks: chunks, ChunkDelay: chunkDelay);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        lock (AttemptTimes)
            AttemptTimes.Add(DateTimeOffset.UtcNow);

        var step = script(request, attempt);
        switch (step.Kind)
        {
            case Kind.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            case Kind.ConnectionError:
                throw new HttpRequestException("Connection refused (test)", new IOException("refused"));
            case Kind.SlowBody:
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SlowContent(step.Chunks, step.ChunkDelay),
                };
            default:
                var response = new HttpResponseMessage(step.Status) { RequestMessage = request };
                if (step.RetryAfter is { } retryAfter)
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                return response;
        }
    }

    public enum Kind { Status, Hang, ConnectionError, SlowBody }

    public readonly record struct Step(Kind Kind, HttpStatusCode Status = HttpStatusCode.OK, TimeSpan? RetryAfter = null,
        int Chunks = 0, TimeSpan ChunkDelay = default)
    {
        public static implicit operator Step(HttpStatusCode status) => new(Kind.Status, status);
    }

    private sealed class SlowContent(int chunks, TimeSpan chunkDelay) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var i = 0; i < chunks; i++)
            {
                await Task.Delay(chunkDelay);
                await stream.WriteAsync(new[] { (byte)('a' + i) });
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
