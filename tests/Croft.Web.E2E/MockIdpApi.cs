using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Direct API calls as the mock IdP's admin — the same authorization-code token flow as
/// dev/seed-dev-data.sh — so fixtures can seed data the admin editor cannot create yet without
/// ever driving the browser UI. Promoted from PhoneDrawerScrollTests when brief 07g item 3's
/// page fixtures needed it before the pages editor exists.
/// </summary>
internal static class MockIdpApi
{
    public const string OAuthAuthority = "http://localhost:5556/default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true });

    /// <summary>The mock admin's access token, through the mock IdP's authorization-code flow.</summary>
    public static async Task<string> GetAdminAccessTokenAsync(HttpClient client)
    {
        var callback = "http://localhost:9876/callback";
        var authorize = $"{OAuthAuthority}/authorize?response_type=code&client_id=blog-web" +
                        $"&redirect_uri={Uri.EscapeDataString(callback)}&scope=openid&state=seed";

        await client.GetAsync(authorize);

        var claims = "{\"sub\":\"admin-id\",\"name\":\"Admin User\",\"email\":\"admin@example.com\"," +
                     "\"email_verified\":true,\"groups\":[\"Admin\"]}";
        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin@example.com",
            ["claims"] = claims,
        });
        var loginResponse = await client.PostAsync(authorize, loginForm);
        var location = loginResponse.Headers.Location
            ?? throw new InvalidOperationException("Mock IdP login returned no redirect with an authorization code.");
        var code = location.Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .First(pair => pair[0] == "code")[1];

        using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = "blog-web",
            ["client_secret"] = "secret",
            ["redirect_uri"] = callback,
        });
        var tokenResponse = await client.PostAsync($"{OAuthAuthority}/token", tokenForm);
        tokenResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>POSTs raw JSON to the API with the admin's bearer token.</summary>
    public static async Task<JsonDocument> PostJsonAsync(HttpClient client, string apiUrl, string path, string json, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>POSTs a request record (Web defaults: camelCase, enums as numbers) to the API.</summary>
    public static Task<JsonDocument> PostJsonAsync(HttpClient client, string apiUrl, string path, object payload, string token) =>
        PostJsonAsync(client, apiUrl, path, JsonSerializer.Serialize(payload, JsonOptions), token);

    /// <summary>Uploads an image as the admin (multipart, as the editor's banner picker does)
    /// and returns the new media asset's id.</summary>
    public static async Task<Guid> UploadImageAsync(HttpClient client, string apiUrl, string fileName, byte[] png, string token)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/media") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}
