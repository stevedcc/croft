using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using System.Net;

namespace Croft.Api.Tests;

public class AuthTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AdminEndpoint_WithNoRole_ReturnsForbidden()
    {
        await using var factory = CreateFactoryAsync();
        var client = factory.CreateClient(); // Test auth handler adds no role

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("x", "y"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
