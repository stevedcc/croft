using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests;

/// <summary>
/// Audience validation on the API's real token-validation path (AGENTS.md rule 4: never disable
/// a security check unconditionally). Outside Development the app validates <c>aud</c> against
/// Oidc:Audience; Development relaxes it, and only Development does.
/// </summary>
public class JwtAudienceTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly Claim[] AdminClaims =
    [
        new("sub", "audience-admin"),
        new("name", "Audience Admin"),
        new("role", "Admin"),
    ];

    [Fact]
    public async Task Production_TokenForTheConfiguredAudience_IsAccepted()
    {
        await using var factory = CreateFactory("Production");
        var client = Authenticated(factory, TestTokens.Audience);

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Right audience", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the app's audience setting must be the one the token carries, or this control proves nothing");
    }

    [Fact]
    public async Task Production_TokenForAnotherAudience_IsUnauthorized()
    {
        await using var factory = CreateFactory("Production");
        var client = Authenticated(factory, "some-other-client");

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Wrong audience", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertNoPostStoredAsync(factory, "Wrong audience");
    }

    [Fact]
    public async Task Production_TokenWithNoAudience_IsUnauthorized()
    {
        await using var factory = CreateFactory("Production");
        var client = Authenticated(factory, audience: null);

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("No audience", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertNoPostStoredAsync(factory, "No audience");
    }

    [Fact]
    public async Task Production_ValidatesTheAudience_AndDevelopmentIsWhatRelaxesIt()
    {
        await using var production = CreateFactory("Production");
        await using var development = CreateFactory("Development");

        ValidateAudienceOf(production).Should().BeTrue("outside Development the audience is validated");
        ValidateAudienceOf(development).Should().BeFalse("Development is the only environment that relaxes it");

        var response = await Authenticated(development, "some-other-client")
            .PostAsJsonAsync("/api/posts", new CreatePostRequest("Development relaxation", "content"));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "Development accepts a token for another audience (the local mock IdP has none to match)");
    }

    private BlogApiFactory CreateFactory(string environment)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return JwtBearerApi.Create(fixture.ConnectionString, storagePath, environment);
    }

    private static HttpClient Authenticated(BlogApiFactory factory, string? audience)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.CreateFor(audience, AdminClaims));
        return client;
    }

    private static bool ValidateAudienceOf(BlogApiFactory factory) =>
        factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters.ValidateAudience;

    private static async Task AssertNoPostStoredAsync(BlogApiFactory factory, string title)
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(AdminClaims));
        var list = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>(
            $"/api/posts?q={Uri.EscapeDataString(title)}");
        list!.Items.Should().NotContain(p => p.Title == title, "a refused request writes nothing");
    }
}
