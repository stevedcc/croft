using System.Net;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>An <see cref="IHttpClientFactory"/> whose every client talks to one stub handler.</summary>
internal sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
}

/// <summary>A handler that answers each request with a function of it and records what was asked.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    /// <summary>What the stub answers; a test may replace it between requests.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

    public List<Uri> Requests { get; } = [];

    public List<HttpRequestMessage> Messages { get; } = [];

    /// <summary>The Mastodon thread (<c>.../context</c>) requests: since brief 17e a fetch also asks for the status itself, which these tests do not count.</summary>
    public IReadOnlyList<Uri> ContextRequests
    {
        get
        {
            lock (Requests)
                return [.. Requests.Where(r => r.AbsolutePath.EndsWith("/context", StringComparison.Ordinal))];
        }
    }

    public static StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });

    public static StubHandler Status(HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
            Messages.Add(request);
        }

        return Task.FromResult(Respond(request));
    }
}

/// <summary>A handler that throws instead of answering.</summary>
internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(exception);
}
