using System.Net;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 4: <c>GET /api/social/avatars/{hash}.webp</c>, anonymous, content-addressed and so cacheable for a year.</summary>
public class SocialAvatarEndpointTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly string Hash = new('a', 64);

    private static async Task<byte[]> SeedAsync(BlogApiFactory factory, string hash, bool withFile = true)
    {
        var bytes = TestImages.Stripes(SKEncodedImageFormat.Webp);
        await using var scope = factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var path = withFile
            ? await storage.SaveAsync(new MemoryStream(bytes), $"{hash}.webp", "image/webp", "social-avatars")
            : $"social-avatars/{Guid.NewGuid():N}.webp";
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        db.SocialAvatars.Add(new SocialAvatar { Hash = hash, SourceUrl = $"https://avatars.example/{hash[..8]}", StoragePath = path, FetchedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return bytes;
    }

    [Fact]
    public async Task AKnownAvatar_IsServedToAnAnonymousCaller_WithTheImmutableCacheHeaders()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var bytes = await SeedAsync(factory, Hash);

        var response = await factory.CreateClient().AsAnonymous().GetAsync($"/api/social/avatars/{Hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public async Task AnUnknownHash_Is404(string hash)
    {
        await using var factory = SocialApiFactory.Create(fixture);

        var response = await factory.CreateClient().AsAnonymous().GetAsync($"/api/social/avatars/{hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("..%2f..%2fappsettings")]
    [InlineData("%2e%2e")]
    public async Task AMalformedHash_Is404_AndNeverTouchesTheDatabaseOrTheFiles(string hash)
    {
        await using var factory = SocialApiFactory.Create(fixture);

        var response = await factory.CreateClient().AsAnonymous().GetAsync($"/api/social/avatars/{hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARowWhoseFileIsGone_Is404_NotAnError()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        var hash = new string('b', 64);
        await SeedAsync(factory, hash, withFile: false);

        var response = await factory.CreateClient().AsAnonymous().GetAsync($"/api/social/avatars/{hash}.webp");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHashWithoutTheWebpSuffix_IsNotARoute()
    {
        await using var factory = SocialApiFactory.Create(fixture);
        await SeedAsync(factory, new string('c', 64));

        var response = await factory.CreateClient().AsAnonymous().GetAsync($"/api/social/avatars/{new string('c', 64)}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
