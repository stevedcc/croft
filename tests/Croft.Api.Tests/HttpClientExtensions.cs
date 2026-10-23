namespace Croft.Api.Tests;

public static class HttpClientExtensions
{
    public static HttpClient WithRole(this HttpClient client, string role)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.RoleHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return client;
    }

    /// <summary>
    /// A client for a distinct user: its own <c>sub</c> (and name) and role, so the author-only
    /// rules can be tested with two Editors, an Admin and a no-role user side by side.
    /// </summary>
    public static HttpClient WithUser(this HttpClient client, string sub, string role, string? name = null)
    {
        client.WithRole(role);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.SubHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, sub);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.NameHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name ?? sub);
        return client;
    }

    /// <summary>A client whose requests are genuinely unauthenticated (no user at all).</summary>
    public static HttpClient AsAnonymous(this HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.AnonymousHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }
}
