using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests;

/// <summary>
/// Holds the API's real endpoint list (the endpoint data source) against
/// <see cref="EndpointAuthorizationTable"/>, so an endpoint cannot be added, opened up or lose
/// its <c>[Authorize]</c> without a test failing: a gated endpoint with no row in the table has
/// no denial test; a row whose roles differ from the attribute's is stale; an endpoint that is
/// neither gated, nor a pinned <c>[AllowAnonymous]</c> one, nor a pinned open one is open by
/// omission (what removing an <c>[Authorize]</c> attribute produces).
/// </summary>
public class EndpointAuthorizationGuardTests(AuthorizationApiFixture api) : IClassFixture<AuthorizationApiFixture>
{
    [Fact]
    public void EveryGatedEndpoint_HasADenialTestRow_WithTheRolesItsAttributeDeclares()
    {
        var actual = Endpoints()
            .Where(e => e.Gated)
            .ToDictionary(e => e.Key, e => e.Roles);
        var expected = EndpointAuthorizationTable.Endpoints.ToDictionary(e => e.Key, e => e.Roles.Order().ToArray());

        actual.Keys.Should().BeEquivalentTo(expected.Keys,
            "every role-gated endpoint needs a row in EndpointAuthorizationTable (and so denial tests), and no row may outlive its endpoint");

        foreach (var (key, roles) in actual)
        {
            roles.Should().Equal(expected[key], $"{key}: the table's roles must be the ones its [Authorize] attribute declares");
        }
    }

    [Fact]
    public void EveryAnonymousEndpoint_IsPinned()
    {
        var actual = Endpoints().Where(e => e.Anonymous).Select(e => e.Key);

        actual.Should().BeEquivalentTo(EndpointAuthorizationTable.AnonymousEndpoints,
            "an [AllowAnonymous] endpoint is a decision: list it in EndpointAuthorizationTable.AnonymousEndpoints");
    }

    [Fact]
    public void NoEndpointIsOpenByOmission()
    {
        var actual = Endpoints().Where(e => !e.Gated && !e.Anonymous).Select(e => e.Key);

        actual.Should().BeEquivalentTo(EndpointAuthorizationTable.OpenEndpoints,
            "an endpoint with no [Authorize] and no [AllowAnonymous] is open to everyone: it either lost its attribute or is a new probe to pin; "
            + "and a pinned row whose endpoint is gone or protected now is stale");
    }

    private IEnumerable<EndpointFacts> Endpoints() =>
        api.Factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
                var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                return new EndpointFacts(
                    methods.Count == 0 ? "ANY" : string.Join('|', methods),
                    endpoint.RoutePattern.RawText!,
                    anonymous,
                    !anonymous && authorize.Count > 0,
                    [.. authorize.SelectMany(a => (a.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Order()]);
            });

    private sealed record EndpointFacts(string Method, string Pattern, bool Anonymous, bool Gated, string[] Roles)
    {
        public string Key => $"{Method} {Pattern}";
    }
}
