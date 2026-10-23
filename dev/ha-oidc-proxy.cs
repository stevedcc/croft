// The HA stack's OIDC counting proxy (dev/ha-local.sh starts it; HaTests read its log).
//
// It forwards every request to the shared mock IdP untouched — EXCEPT the discovery document,
// whose token_endpoint it points at itself — so the counter sees every token request the Web
// instances make (the authorization-code exchange at login and every refresh) while the issuer
// stays the mock's own (the mock derives it from the forwarded Host, so tokens keep validating
// exactly as on the ordinary stack). This is the harness's request counter at the token
// endpoint (HaTests item 7 / ruling 3); the mock server has no observability of its own.
//
// Every /token POST appends one JSON line to the token log (3rd argument):
//   {"t":<unix ms>,"grant":"refresh_token","status":200,"expires_in":69,"rt":"<sha256[:16]>","rtOut":"<sha256[:16]>"}
// rt/rtOut are SHA-256 prefixes of the refresh token sent and received — tokens themselves are
// credentials and never land in a log file.
//
// Run (dev/ha-local.sh does this): dotnet publish dev/ha-oidc-proxy.cs -o <dir>, then
//   dotnet <dir>/ha-oidc-proxy.dll <listenPort> <targetBase> <tokenLog>
// e.g. dotnet ha-oidc-proxy.dll 5314 http://localhost:5556 /tmp/blog-ha-oidc-tokens.jsonl

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: ha-oidc-proxy <listenPort> <targetBase> <tokenLog>");
    return 2;
}

var listenPort = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
var targetBase = args[1].TrimEnd('/');
var tokenLog = args[2];
var logGate = new object();

File.WriteAllText(tokenLog, string.Empty); // a fresh stack gets a fresh counter log

using var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{listenPort}/");
listener.Prefixes.Add($"http://127.0.0.1:{listenPort}/");
listener.Start();
Console.WriteLine($"ha-oidc-proxy: listening on localhost:{listenPort} -> {targetBase}, token log {tokenLog}");

using var http = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    AutomaticDecompression = DecompressionMethods.None,
});

// Hop-by-hop headers belong to one connection and must not be forwarded.
var hopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Authenticate",
    "Proxy-Authorization", "TE", "Trailer", "Expect", "Host", "Content-Length",
};

while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(context));
}

async Task HandleAsync(HttpListenerContext context)
{
    var request = context.Request;
    var response = context.Response;
    try
    {
        var path = request.Url!.AbsolutePath;
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body);
        var requestBody = body.ToArray();

        var isToken = request.HttpMethod == "POST" && path.EndsWith("/token", StringComparison.Ordinal);
        var grant = isToken ? FormValue(requestBody, "grant_type") : null;
        var sentRefreshToken = isToken ? FormValue(requestBody, "refresh_token") : null;

        using var forward = new HttpRequestMessage(new HttpMethod(request.HttpMethod), $"{targetBase}{path}{request.Url.Query}");
        foreach (string name in request.Headers)
        {
            if (!hopByHop.Contains(name) && !forward.Headers.TryAddWithoutValidation(name, request.Headers[name]))
            {
                forward.Content?.Headers.TryAddWithoutValidation(name, request.Headers[name]);
            }
        }

        if (requestBody.Length > 0)
        {
            forward.Content = new ByteArrayContent(requestBody);
            forward.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType ?? "application/octet-stream");
        }

        using var upstream = await http.SendAsync(forward);
        var responseBody = await upstream.Content.ReadAsByteArrayAsync();

        // WORKAROUND(W-083, OIDC): only token_endpoint is rewritten; the mock derives the issuer from the forwarded Host
        // The one rewrite: the discovery document's token_endpoint points at this proxy, so the
        // Web's token traffic (login exchange and refreshes) is what the counter sees. Every
        // other URL — issuer, authorize, jwks — stays the mock's own.
        if (path.Contains("/.well-known/", StringComparison.Ordinal))
        {
            responseBody = RewriteTokenEndpoint(responseBody, path);
        }
        // END WORKAROUND(W-083)

        if (isToken)
        {
            var (expiresIn, newRefreshToken) = ReadTokenResponse(responseBody);
            var line = new JsonObject
            {
                ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["grant"] = grant,
                ["status"] = (int)upstream.StatusCode,
                ["expires_in"] = expiresIn,
                ["rt"] = Fingerprint(sentRefreshToken),
                ["rtOut"] = Fingerprint(newRefreshToken),
            }.ToJsonString();
            lock (logGate)
            {
                File.AppendAllText(tokenLog, line + "\n");
            }
            Console.WriteLine($"ha-oidc-proxy: {line}");
        }

        response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in upstream.Headers)
        {
            if (!hopByHop.Contains(header.Key))
            {
                response.Headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        response.ContentLength64 = responseBody.Length;
        await response.OutputStream.WriteAsync(responseBody);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ha-oidc-proxy: {request.HttpMethod} {request.Url} failed: {ex}");
        try
        {
            response.StatusCode = 502;
        }
        catch
        {
            // The response may already be started; the client sees a broken connection.
        }
    }
    finally
    {
        response.Close();
    }
}

byte[] RewriteTokenEndpoint(byte[] body, string path)
{
    var issuerId = path.Trim('/').Split('/')[0];
    var node = JsonNode.Parse(Encoding.UTF8.GetString(body));
    if (node?["token_endpoint"] is JsonValue)
    {
        node["token_endpoint"] = $"http://localhost:{listenPort}/{issuerId}/token";
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    return body;
}

(int? ExpiresIn, string? RefreshToken) ReadTokenResponse(byte[] body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt32()
            : (int?)null;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()
            : null;
        return (expiresIn, refreshToken);
    }
    catch (JsonException)
    {
        return (null, null);
    }
}

static string? FormValue(byte[] body, string name)
{
    foreach (var pair in Encoding.UTF8.GetString(body).Split('&'))
    {
        var parts = pair.Split('=', 2);
        if (parts.Length == 2 && Uri.UnescapeDataString(parts[0].Replace('+', ' ')) == name)
        {
            return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
        }
    }

    return null;
}

static string? Fingerprint(string? token) =>
    string.IsNullOrEmpty(token) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16].ToLowerInvariant();
