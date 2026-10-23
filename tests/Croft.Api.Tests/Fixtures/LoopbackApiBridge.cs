// WORKAROUND-FILE(W-075, ASP.NET Core): WebApplicationFactory hosts have no socket, so HttpClient client spans need a real loopback bridge
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Gives an in-memory test server a real socket. The bridge accepts HTTP/1.1 on a loopback port and
/// relays every request through the target handler (the API's in-memory server), so the calling
/// side runs the real socket transport — where HttpClient creates client spans and injects the
/// W3C traceparent header, exactly like production's Web-to-API calls. WebApplicationFactory hosts
/// cannot be reached over sockets themselves, which is why this exists.
/// </summary>
internal sealed class LoopbackApiBridge : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public LoopbackApiBridge(HttpMessageHandler target)
    {
        _client = new HttpClient(target, disposeHandler: false);
        _listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Url { get; }

    /// <summary>Relay failures, asserted empty by the tests that use the bridge.</summary>
    public List<string> Errors { get; } = [];

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _client.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            _ = HandleConnectionAsync(socket);
        }
    }

    private async Task HandleConnectionAsync(Socket socket)
    {
        try
        {
            using (socket)
            {
                using var stream = new NetworkStream(socket, ownsSocket: false);
                while (!_stop.IsCancellationRequested)
                {
                    if (!await RelayOneRequestAsync(stream))
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            lock (Errors)
            {
                Errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Relays one request/response pair; returns false when the connection must close.</summary>
    private async Task<bool> RelayOneRequestAsync(NetworkStream stream)
    {
        var inbound = await ReadHeadAsync(stream);
        if (inbound is null)
        {
            return false;
        }

        var (head, leftover) = inbound.Value;
        var (startLine, headers) = ParseHead(head);
        var parts = startLine.Split(' ', 3);
        var method = parts[0];
        var pathAndQuery = parts[1];

        var body = leftover;
        if (headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            if (body.Length < length)
            {
                var rest = new byte[length - body.Length];
                await ReadExactlyAsync(stream, rest);
                body = [.. body, .. rest];
            }
            else
            {
                body = body[..length];
            }
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Url, pathAndQuery));
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
        }

        foreach (var (key, value) in headers)
        {
            if (key is "host")
            {
                request.Headers.Host = value;
            }
            else if (!request.Headers.TryAddWithoutValidation(key, value) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await _client.SendAsync(request, _stop.Token);
        var responseBody = await response.Content.ReadAsByteArrayAsync(_stop.Token);

        var responseHead = new StringBuilder();
        responseHead.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
            .Append(response.ReasonPhrase ?? "Unknown").Append("\r\n");
        foreach (var header in response.Headers)
        {
            responseHead.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
        }

        foreach (var header in response.Content.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            responseHead.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
        }

        responseHead.Append("Content-Length: ").Append(responseBody.Length).Append("\r\n\r\n");

        var headBytes = Encoding.ASCII.GetBytes(responseHead.ToString());
        await stream.WriteAsync(headBytes, _stop.Token);
        await stream.WriteAsync(responseBody, _stop.Token);
        await stream.FlushAsync(_stop.Token);

        return !headers.TryGetValue("connection", out var connection) ||
            !connection.Equals("close", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string Head, byte[] Leftover)?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var collected = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return null;
            }

            collected.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(collected.GetBuffer(), 0, (int)collected.Length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                // Any body bytes that arrived with the head are handed back to the caller.
                var head = text[..end];
                var bodyStart = end + 4;
                var leftover = collected.GetBuffer()[bodyStart..(int)collected.Length];
                return (head, [.. leftover]);
            }

            if (collected.Length > 64 * 1024)
            {
                throw new InvalidOperationException("request head exceeded 64 KiB");
            }
        }
    }

    private static (string StartLine, Dictionary<string, string> Headers) ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim().ToLowerInvariant()] = line[(separator + 1)..].Trim();
            }
        }

        return (lines[0], headers);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] body)
    {
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset));
            if (read == 0)
            {
                throw new InvalidOperationException("connection closed mid-body");
            }

            offset += read;
        }
    }
}
