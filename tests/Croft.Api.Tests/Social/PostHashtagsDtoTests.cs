using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12e item 5: the editor gets the hashtags of a post's category and tags with the post (staff only), so its previews need no further reads.</summary>
public class PostHashtagsDtoTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AStaffRead_CarriesTheHashtagsOfTheCategoryAndTags_AnAnonymousReadDoesNot()
    {
        var run = Guid.NewGuid().ToString("N")[..10];
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        await using var factory = new BlogApiFactory(fixture.ConnectionString, storagePath);
        var admin = factory.CreateClient().WithRole("Admin");
        var category = (await (await admin.PostAsJsonAsync("/api/categories", $"Hash Cat {run}")).Content.ReadFromJsonAsync<CategoryDto>())!;
        (await admin.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(category.Name, null,
            [new HashtagDto("Homelab", HashtagNetworks.Both)]))).EnsureSuccessStatusCode();
        var tag = (await (await admin.PostAsJsonAsync("/api/tags", $"hash-tag-{run}")).Content.ReadFromJsonAsync<TagDto>())!;
        (await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, null,
            [new HashtagDto("DotNet", HashtagNetworks.MastodonOnly)]))).EnsureSuccessStatusCode();
        var created = (await (await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest($"Hash post {run}", "Body.", CategoryId: category.Id, Tags: [tag.Name]))).Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{created.Id}/publish", new PublishRequest())).EnsureSuccessStatusCode();

        var staff = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{created.Slug}"))!;
        var anonymous = (await factory.CreateClient().GetFromJsonAsync<PostDto>($"/api/posts/{created.Slug}"))!;

        staff.Category!.Hashtags.Should().Equal([new HashtagDto("Homelab", HashtagNetworks.Both)]);
        staff.Tags.Single().Hashtags.Should().Equal([new HashtagDto("DotNet", HashtagNetworks.MastodonOnly)]);
        anonymous.Category!.Hashtags.Should().BeEmpty("the public post carries no hashtags");
        anonymous.Tags.Single().Hashtags.Should().BeEmpty();
    }
}
