using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using System.Net.Http.Json;
using System.Text.Json;

namespace Croft.Api.Tests;

public class SearchControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Search_ReturnsMatchingPublishedPosts()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Kubernetes Tips",
            "Running workloads on kubernetes is fun.",
            Tags: ["k8s"]));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());
        // A draft that matches the same term: search must not find it, for anyone.
        var draftCreate = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Kubernetes Draft Notes", "Unfinished notes on kubernetes."));
        draftCreate.EnsureSuccessStatusCode();
        var draft = await draftCreate.Content.ReadFromJsonAsync<PostDto>();

        var anonymous = factory.CreateClient();
        var search = await anonymous.GetAsync("/api/search?q=kubernetes");
        search.EnsureSuccessStatusCode();
        var result = await search.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        result!.Items.Should().ContainSingle(p => p.Slug == "kubernetes-tips");
        result.Items.Should().NotContain(p => p.Slug == draft!.Slug, "a draft is not searchable");
        var adminResult = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=kubernetes");
        adminResult!.Items.Should().NotContain(p => p.Slug == draft!.Slug, "not even for the admin: search is the public search");

        var empty = await anonymous.GetAsync("/api/search?q=azure");
        empty.EnsureSuccessStatusCode();
        var emptyResult = await empty.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        emptyResult!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_PublicView_HidesTheAdminCountsAnAdminOtherwiseGets()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Countable Publicview Post", "Searching for publicviewmarker."));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var asAdmin = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=publicviewmarker");
        var asPublic = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/search?q=publicviewmarker&publicView=true");

        asAdmin!.Items.Should().ContainSingle().Which.ViewCount.Should().NotBeNull("an admin's search carries the admin counts");
        asPublic!.Items.Should().ContainSingle().Which.ViewCount.Should().BeNull(
            "publicView asks for what a visitor would see, whoever is signed in");
    }

    [Fact]
    public async Task Search_ReturnsSummaries()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Findable Post",
            "# Heading\n\nSearching for widgets is easy.\n\nSecond paragraph."));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/search?q=widgets");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("items").EnumerateArray().Single();

        item.TryGetProperty("markdownContent", out _).Should().BeFalse(
            "search returns summaries, not full posts");
        item.GetProperty("summary").GetString().Should().Be("Searching for widgets is easy.");

        var summary = item.Deserialize<PostSummaryDto>(WebJson);
        summary!.Slug.Should().Be("findable-post");
        summary.ReadingMinutes.Should().Be(1);
    }

    [Fact]
    public async Task Search_ClampPagingParameters()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Searchable", "content"));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient();
        var search = await anonymous.GetAsync($"/api/search?q=Searchable&page=-1&pageSize={PagingHelper.MaxPageSize + 50}");
        search.EnsureSuccessStatusCode();
        var result = await search.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        result!.Page.Should().Be(1);
        result.PageSize.Should().Be(PagingHelper.MaxPageSize);
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
