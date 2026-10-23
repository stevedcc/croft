using System.Net;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Croft.Api.Tests.Fixtures;

namespace Croft.Api.Tests;

/// <summary>
/// A denial test for every role-gated endpoint (C-1 of the 2026-10-19 test-suite review): no user
/// at all is 401, a user whose roles are not in the endpoint's list is 403 and the row it aimed
/// at is untouched, and each role the endpoint names gets past authorization. The endpoints and
/// their expected roles are <see cref="EndpointAuthorizationTable"/>; the guard keeps that table
/// complete.
/// </summary>
public class EndpointAuthorizationTests(AuthorizationApiFixture api) : IClassFixture<AuthorizationApiFixture>
{
    private static readonly string[] AllRoles = ["", "Reader", "Editor", "Admin"];

    public static IEnumerable<object[]> Keys => EndpointAuthorizationTable.Keys;

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Endpoint_RefusesAnonymousAndWrongRoles_WithoutSideEffects_AndAdmitsItsRoles(string key)
    {
        var endpoint = EndpointAuthorizationTable.Find(key);
        var before = await StateAsync();

        using var scope = new AssertionScope(key);

        var anonymous = api.Factory.CreateClient().AsAnonymous();
        using (var response = await anonymous.SendAsync(endpoint.Build(api.Seed, true)))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no user at all");
        }

        foreach (var role in AllRoles.Where(r => !endpoint.Roles.Contains(r)))
        {
            var client = api.Factory.CreateClient().WithUser($"denied-{(role.Length == 0 ? "none" : role)}", role);
            using var response = await client.SendAsync(endpoint.Build(api.Seed, true));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                $"role '{(role.Length == 0 ? "(none)" : role)}' is not one of {string.Join(", ", endpoint.Roles)}");
        }

        (await StateAsync()).Should().Be(before, "a refused request must not change anything");

        foreach (var role in endpoint.Roles)
        {
            var client = api.Factory.CreateClient().WithUser($"allowed-{role}", role);
            using var response = await client.SendAsync(endpoint.Build(api.Seed, false));
            response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, $"{role} is let in")
                .And.NotBe(HttpStatusCode.Forbidden, $"{role} is let in");
        }
    }

    /// <summary>Everything the table's requests could change, as the Admin sees it.</summary>
    private async Task<string> StateAsync()
    {
        var admin = api.Factory.CreateClient().WithUser("state-reader", "Admin");
        var parts = new List<string>();
        foreach (var url in new[]
        {
            "/api/posts?pageSize=100", $"/api/posts/{api.Seed.PostId}/feedback", "/api/media",
            "/api/categories", "/api/tags", $"/api/posts/{api.Seed.PostId}/media",
            "/api/discussion/blocks", $"/api/posts/{api.Seed.PostId}/discussion/admin",
        })
        {
            var response = await admin.GetAsync(url);
            response.EnsureSuccessStatusCode();
            parts.Add($"{url}\n{await response.Content.ReadAsStringAsync()}");
        }

        return string.Join("\n---\n", parts);
    }
}
