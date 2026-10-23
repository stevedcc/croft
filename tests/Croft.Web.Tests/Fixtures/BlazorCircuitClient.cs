// WORKAROUND-FILE(W-058, Blazor): hand-rolled blazorpack client: depends on the StartCircuit signature, the prerender component markers and the MessagePack framing
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Croft.Web.Tests.Fixtures;

/// <summary>
/// Opens a real Blazor Server circuit without a browser: speaks the hub's "blazorpack" protocol
/// (SignalR's MessagePack encoding with limited types, VarInt-framed) over long polling to
/// /_blazor and invokes StartCircuit — the same call blazor.server.js makes after connecting. A
/// circuit that opens through here is a real circuit, so the circuit metrics fire exactly as they
/// do for a reader's browser. No extra packages: the wire format is small and written out here
/// against the SignalR hub protocol spec.
/// </summary>
internal sealed class BlazorCircuitClient : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';

    private readonly HttpClient _http;
    private readonly string _connectionToken;
    private readonly Channel<HubMessage> _messages = Channel.CreateUnbounded<HubMessage>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pollLoop;
    private readonly MemoryStream _inbox = new();
    private long _invocationId;
    private bool _handshakeComplete;

    private BlazorCircuitClient(HttpClient http, string connectionToken)
    {
        _http = http;
        _connectionToken = connectionToken;
        _pollLoop = PollLoopAsync();
    }

    /// <summary>Server-side "JS.Error" invocations, surfaced when something fails.</summary>
    public List<string> ServerErrors { get; } = [];

    /// <summary>
    /// Collects the component markers a prerendered page carries. The descriptors inside them are
    /// data-protected by the app that rendered the page, which is exactly what StartCircuit needs —
    /// blazor.server.js reads these same comments out of the document. Closing markers
    /// ("&lt;!--Blazor:end--&gt;") are not component records and are skipped.
    /// </summary>
    public static string CollectSsrComponentMarkers(string html)
    {
        var markers = new List<string>();
        var index = 0;
        while (true)
        {
            var start = html.IndexOf("<!--Blazor:", index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = html.IndexOf("-->", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            index = end + "-->".Length;
            var marker = html[(start + "<!--Blazor:".Length)..end].Trim();
            if (!marker.StartsWith('{'))
            {
                continue;
            }

            // Start markers carry a type ("server"/"auto"); closing markers only carry the
            // prerender id and are not component records.
            using var document = JsonDocument.Parse(marker);
            if (document.RootElement.TryGetProperty("type", out _))
            {
                markers.Add(marker);
            }
        }

        return $"[{string.Join(',', markers)}]";
    }

    public static async Task<BlazorCircuitClient> ConnectAsync(HttpClient http)
    {
        using var negotiate = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);
        negotiate.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync());
        var connectionToken = document.RootElement.GetProperty("connectionToken").GetString()!;

        var client = new BlazorCircuitClient(http, connectionToken);
        try
        {
            // The handshake is always text, whatever the negotiated protocol is.
            await client.SendAsync(
                Encoding.UTF8.GetBytes($"{{\"protocol\":\"blazorpack\",\"version\":2}}{RecordSeparator}"));
            var ack = await client.WaitForMessageAsync(
                message => message.Kind == HubMessageKind.Handshake,
                "the SignalR handshake acknowledgement");
            if (ack.Error is not null)
            {
                throw new InvalidOperationException($"the hub refused the blazorpack protocol: {ack.Error}");
            }

            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Invokes a hub method and returns its string result.</summary>
    public async Task<string> InvokeAsync(string target, params string[] arguments)
    {
        var invocationId = Interlocked.Increment(ref _invocationId).ToString(CultureInfo.InvariantCulture);
        await SendAsync(EncodeInvocation(invocationId, target, arguments));

        var completion = await WaitForMessageAsync(
            message => message.Kind == HubMessageKind.Completion && message.InvocationId == invocationId,
            $"the completion of {target}");
        if (completion.Error is not null)
        {
            throw new InvalidOperationException($"{target} failed: {completion.Error}");
        }

        return completion.Result ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _pollLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // Teardown of a connection that is being closed anyway: the abort surfaces as a
            // transport error here and carries no information.
        }

        _stop.Dispose();
    }

    private Task SendAsync(byte[] payload)
        => _http.PostAsync($"/_blazor?id={_connectionToken}", new ByteArrayContent(payload));

    private async Task<HubMessage> WaitForMessageAsync(Func<HubMessage, bool> predicate, string because)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var skipped = new List<string>();
        try
        {
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(timeout.Token);
                if (predicate(message))
                {
                    return message;
                }

                skipped.Add(message.ToString());
            }
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"timed out waiting for {because}; messages seen meanwhile: {string.Join(" | ", skipped)};"
                + $" server errors: {string.Join(" | ", ServerErrors)}");
        }
    }

    private async Task PollLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var response = await _http.GetAsync(
                $"/_blazor?id={_connectionToken}", HttpCompletionOption.ResponseHeadersRead, _stop.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                break;
            }

            var payload = await response.Content.ReadAsByteArrayAsync(_stop.Token);
            _inbox.Position = _inbox.Length;
            _inbox.Write(payload);
            DrainInbox();
        }
    }

    private void DrainInbox()
    {
        var buffer = _inbox.ToArray();
        var consumed = 0;
        while (consumed < buffer.Length)
        {
            if (!_handshakeComplete)
            {
                var end = Array.IndexOf(buffer, (byte)RecordSeparator, consumed);
                if (end < 0)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, consumed, end - consumed);
                consumed = end + 1;
                _handshakeComplete = true;
                var error = text.Contains("\"error\"", StringComparison.Ordinal)
                    ? text
                    : null;
                _messages.Writer.TryWrite(HubMessage.Handshake(error));
                continue;
            }

            if (!TryReadFrame(buffer.AsSpan(consumed), out var frame, out var frameLength))
            {
                break;
            }

            consumed += frameLength;
            _messages.Writer.TryWrite(DecodeMessage(frame));
        }

        // Keep whatever was not yet a complete frame.
        _inbox.SetLength(0);
        if (consumed < buffer.Length)
        {
            _inbox.Write(buffer, consumed, buffer.Length - consumed);
        }
    }

    // ---- VarInt framing ([Length][Body] per message) ----

    private static bool TryReadFrame(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        var length = 0;
        var shift = 0;
        var index = 0;
        while (index < input.Length)
        {
            var b = input[index++];
            length |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (input.Length - index < length)
                {
                    return false;
                }

                frame = input.Slice(index, length);
                consumed = index + length;
                return true;
            }
        }

        return false;
    }

    private static byte[] EncodeInvocation(string invocationId, string target, string[] arguments)
    {
        using var body = new MemoryStream();
        body.WriteByte(0x96); // 6-element array
        body.WriteByte(0x01); // Invocation
        body.WriteByte(0x80); // no headers
        WriteString(body, invocationId);
        WriteString(body, target);
        body.WriteByte((byte)(0x90 | arguments.Length)); // arguments array
        foreach (var argument in arguments)
        {
            WriteString(body, argument);
        }

        body.WriteByte(0x90); // no stream ids

        var bodyBytes = body.ToArray();
        using var framed = new MemoryStream();
        WriteVarInt(framed, (uint)bodyBytes.Length);
        framed.Write(bodyBytes);
        return framed.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        switch (bytes.Length)
        {
            case < 32:
                stream.WriteByte((byte)(0xA0 | bytes.Length));
                break;
            case < 256:
                stream.WriteByte(0xD9);
                stream.WriteByte((byte)bytes.Length);
                break;
            case < 65536:
                stream.WriteByte(0xDA);
                stream.WriteByte((byte)(bytes.Length >> 8));
                stream.WriteByte((byte)bytes.Length);
                break;
            default:
                stream.WriteByte(0xDB);
                stream.WriteByte((byte)(bytes.Length >> 24));
                stream.WriteByte((byte)(bytes.Length >> 16));
                stream.WriteByte((byte)(bytes.Length >> 8));
                stream.WriteByte((byte)bytes.Length);
                break;
        }

        stream.Write(bytes);
    }

    private static void WriteVarInt(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    // ---- a minimal MessagePack reader for the messages the server sends ----

    private static HubMessage DecodeMessage(ReadOnlySpan<byte> frame)
    {
        var reader = new MsgPackReader(frame);
        var elementCount = reader.ReadArrayHeader();
        var type = reader.ReadInt();

        switch (type)
        {
            case 1: // Invocation from the server (JS.Error and friends)
            {
                _ = reader.ReadMapHeader();
                var invocationId = reader.TryReadString(out var id) ? id : null;
                var target = reader.ReadString();
                reader.ReadArrayHeader();
                var error = reader.TryReadString(out var firstArgument) ? firstArgument : null;
                return HubMessage.Invocation(invocationId, target, error);
            }

            case 3: // Completion
            {
                _ = reader.ReadMapHeader();
                var invocationId = reader.ReadString();
                var resultKind = reader.ReadInt();
                return resultKind switch
                {
                    1 => HubMessage.Completion(invocationId, error: reader.ReadString()),
                    3 => HubMessage.Completion(invocationId, result: reader.ReadString()),
                    _ => HubMessage.Completion(invocationId, result: null),
                };
            }

            default:
                return HubMessage.Other(type, elementCount);
        }
    }

    private ref struct MsgPackReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public int ReadArrayHeader()
        {
            var b = Next();
            return b switch
            {
                >= 0x90 and <= 0x9F => b & 0x0F,
                0xDC => (Next() << 8) + Next(),
                _ => throw new InvalidOperationException($"expected an array header, got 0x{b:X2}"),
            };
        }

        public int ReadMapHeader()
        {
            var b = Next();
            return b switch
            {
                >= 0x80 and <= 0x8F => b & 0x0F,
                _ => throw new InvalidOperationException($"expected a map header, got 0x{b:X2}"),
            };
        }

        public int ReadInt()
        {
            var b = Next();
            return b switch
            {
                >= 0x00 and <= 0x7F => b, // positive fixint
                >= 0xE0 => b - 0x100, // negative fixint
                0xCC => Next(),
                0xCD => (Next() << 8) + Next(),
                _ => throw new InvalidOperationException($"expected an int, got 0x{b:X2}"),
            };
        }

        public string ReadString()
        {
            if (!TryReadString(out var value))
            {
                throw new InvalidOperationException("expected a string");
            }

            return value;
        }

        public bool TryReadString(out string value)
        {
            if (_data.IsEmpty)
            {
                value = "";
                return false;
            }

            var b = _data[0];
            var length = b switch
            {
                >= 0xA0 and <= 0xBF => b & 0x1F, // fixstr
                0xD9 => -1, // str8
                0xDA => -2, // str16
                _ => int.MinValue, // not a string (nil, bool, ...)
            };

            if (length == int.MinValue)
            {
                if (b == 0xC0)
                {
                    // nil: stands for "no value here", consumed either way.
                    _data = _data[1..];
                    value = "";
                    return true;
                }

                value = "";
                return false;
            }

            _data = _data[1..];
            if (length == -1)
            {
                length = Next();
            }
            else if (length == -2)
            {
                length = (Next() << 8) + Next();
            }

            value = Encoding.UTF8.GetString(_data[..length]);
            _data = _data[length..];
            return true;
        }

        private byte Next()
        {
            var b = _data[0];
            _data = _data[1..];
            return b;
        }
    }
}

internal enum HubMessageKind
{
    Handshake,
    Invocation,
    Completion,
    Other,
}

internal sealed record HubMessage(HubMessageKind Kind, string? InvocationId, string? Target, string? Result, string? Error, int OtherType = 0)
{
    public static HubMessage Handshake(string? error) => new(HubMessageKind.Handshake, null, null, null, error);

    public static HubMessage Invocation(string? invocationId, string? target, string? firstArgument)
        => new(HubMessageKind.Invocation, invocationId, target, firstArgument, null);

    public static HubMessage Completion(string? invocationId, string? result = null, string? error = null)
        => new(HubMessageKind.Completion, invocationId, null, result, error);

    public static HubMessage Other(int type, int elementCount) => new(HubMessageKind.Other, null, null, null, null, type);
}
