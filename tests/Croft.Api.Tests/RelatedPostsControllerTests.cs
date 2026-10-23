using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// The related endpoint ranks every published post in the database, so these tests' exact-set
/// assertions only hold when no other posts exist. They live in their own class — and so, since
/// the per-class database is shared by the tests inside it, their own database — and each test
/// starts by clearing the posts table so it sees exactly the posts it creates below.
/// </summary>
public class RelatedPostsControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Related_RanksBySharedTagsThenCategoryThenNewest()
    {
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var categoryResponse = await admin.PostAsJsonAsync("/api/categories", "Cat C");
        categoryResponse.EnsureSuccessStatusCode();
        var category = await categoryResponse.Content.ReadFromJsonAsync<CategoryDto>();

        // Creation order is deliberately A, D, C, B so a result in insertion order cannot
        // accidentally match; D carries the newest PublishedAt.
        await CreatePublishedAsync(admin, "A Post", ["x", "y"], category!.Id, DateTime.UtcNow.AddDays(-4));
        await CreatePublishedAsync(admin, "D Post", [], null, DateTime.UtcNow.AddDays(-1));
        await CreatePublishedAsync(admin, "C Post", [], category.Id, DateTime.UtcNow.AddDays(-2));
        await CreatePublishedAsync(admin, "B Post", ["x", "y"], null, DateTime.UtcNow.AddDays(-3));

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/posts/a-post/related?count=3");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();

        // B: 2 shared tags -> score 4; C: same category -> 1; D: newest unrelated -> 0 (filler).
        related!.Select(p => p.Slug).Should().Equal("b-post", "c-post", "d-post");
    }

    [Fact]
    public async Task Related_ExcludesSelfAndUnpublished()
    {
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var categoryResponse = await admin.PostAsJsonAsync("/api/categories", "Cat C");
        categoryResponse.EnsureSuccessStatusCode();
        var category = await categoryResponse.Content.ReadFromJsonAsync<CategoryDto>();

        await CreatePublishedAsync(admin, "A Post", ["x", "y"], category!.Id, DateTime.UtcNow.AddDays(-3));
        await CreatePublishedAsync(admin, "B Post", ["x", "y"], null, DateTime.UtcNow.AddDays(-2));

        // The draft and the scheduled post would out-rank B, but must never appear.
        var draft = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Draft Post", "content", Tags: ["x", "y"], CategoryId: category.Id));
        draft.EnsureSuccessStatusCode();
        var scheduled = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Scheduled Post", "content", Tags: ["x", "y"]));
        scheduled.EnsureSuccessStatusCode();
        var scheduledPost = await scheduled.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{scheduledPost!.Id}/publish", new PublishRequest(DateTime.UtcNow.AddDays(1)));

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/posts/a-post/related?count=3");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();

        related!.Select(p => p.Slug).Should().Equal(["b-post"],
            "only published posts other than the source may be related, including as filler");
    }

    [Fact]
    public async Task Related_CountClampedTo1to10()
    {
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        await CreatePublishedAsync(admin, "Self Post", [], null, DateTime.UtcNow.AddDays(-13));
        for (var i = 1; i <= 12; i++)
        {
            await CreatePublishedAsync(admin, $"Filler {i}", [], null, DateTime.UtcNow.AddDays(-13 + i));
        }

        var anonymous = factory.CreateClient();

        var tooSmall = await anonymous.GetAsync("/api/posts/self-post/related?count=0");
        tooSmall.EnsureSuccessStatusCode();
        var smallResult = await tooSmall.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        smallResult.Should().HaveCount(1, "count is clamped to a minimum of 1");

        var tooLarge = await anonymous.GetAsync("/api/posts/self-post/related?count=99");
        tooLarge.EnsureSuccessStatusCode();
        var largeResult = await tooLarge.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        largeResult.Should().HaveCount(10, "count is clamped to a maximum of 10");
    }

    [Fact]
    public async Task Related_UnknownSlug_Returns404()
    {
        await using var factory = CreateFactoryAsync();
        var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync("/api/posts/no-such-post/related");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Related_UnpublishedSource_Returns404ForAnonymous()
    {
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        // A draft or scheduled source slug must not be discoverable by anonymous callers.
        var draft = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Draft Source", "content", Tags: ["x"]));
        draft.EnsureSuccessStatusCode();
        var scheduledResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Scheduled Source", "content", Tags: ["x"]));
        scheduledResponse.EnsureSuccessStatusCode();
        var scheduled = await scheduledResponse.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{scheduled!.Id}/publish", new PublishRequest(DateTime.UtcNow.AddDays(1)));

        var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/posts/draft-source/related")).StatusCode.Should().Be(
            HttpStatusCode.NotFound, "an unpublished draft slug must not leak through the related endpoint");
        (await anonymous.GetAsync("/api/posts/scheduled-source/related")).StatusCode.Should().Be(
            HttpStatusCode.NotFound, "a scheduled slug must not leak through the related endpoint");
    }

    [Fact]
    public async Task Related_DraftSource_VisibleToAdminAndEditor()
    {
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        // Draft preview keeps the rail: Admin and Editor resolve a draft source's related posts.
        var draft = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Draft Source", "content", Tags: ["x"]));
        draft.EnsureSuccessStatusCode();
        await CreatePublishedAsync(admin, "Related Post", ["x"], null, DateTime.UtcNow.AddDays(-1));

        var adminResponse = await admin.GetAsync("/api/posts/draft-source/related");
        adminResponse.EnsureSuccessStatusCode();
        var adminRelated = await adminResponse.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        adminRelated!.Select(p => p.Slug).Should().Equal(["related-post"]);

        var editor = factory.CreateClient().WithRole("Editor");
        var editorResponse = await editor.GetAsync("/api/posts/draft-source/related");
        editorResponse.EnsureSuccessStatusCode();
        var editorRelated = await editorResponse.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        editorRelated!.Select(p => p.Slug).Should().Equal(["related-post"]);
    }

    [Fact]
    public async Task Related_SameCategoryBonus_RequiresSharedCategory()
    {
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var categoryResponse = await admin.PostAsJsonAsync("/api/categories", "Other Cat");
        categoryResponse.EnsureSuccessStatusCode();
        var category = await categoryResponse.Content.ReadFromJsonAsync<CategoryDto>();

        // The source has no category, so no candidate may earn the "same category" bonus
        // and the two candidates are tied at 0: the newest must come first.
        await CreatePublishedAsync(admin, "Self Post", [], null, DateTime.UtcNow.AddDays(-3));
        await CreatePublishedAsync(admin, "Uncategorized", [], null, DateTime.UtcNow.AddDays(-2));
        await CreatePublishedAsync(admin, "Categorized", [], category!.Id, DateTime.UtcNow.AddDays(-1));

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/posts/self-post/related?count=2");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();

        related!.Select(p => p.Slug).Should().Equal(["categorized", "uncategorized"],
            "without a shared category or tags every candidate scores 0 and the newest comes first");
    }

    [Fact]
    public async Task Related_FlagOffTag_IsNeutral_ThePostRanksLikeOneSharingNothing()
    {
        var token = $"r07ka{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var tag = await CreateTagAsync(admin, $"{token}-prov");
        var category = await CreateCategoryAsync(admin, $"{token}-cat");
        var off = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, false));
        off.EnsureSuccessStatusCode();

        // A and B share only the flag-off tag; C shares A's category; D and E share nothing with A.
        // D carries exactly B's PublishedAt and E an older one: a flag-off tag is neutral, so B
        // must land where a post sharing nothing lands — tied with D under the unchanged
        // tie-break, above the older E, below C's category point — and never as if the tag
        // counted (above C) or as if it demoted B (below E).
        var bPublishedAt = DateTime.UtcNow.AddDays(-2);
        var a = await CreatePublishedAsync(admin, $"A {token}", [tag.Name], category.Id, DateTime.UtcNow.AddDays(-4));
        var b = await CreatePublishedAsync(admin, $"B {token}", [tag.Name], null, bPublishedAt);
        var c = await CreatePublishedAsync(admin, $"C {token}", [], category.Id, DateTime.UtcNow.AddDays(-1));
        var d = await CreatePublishedAsync(admin, $"D {token}", [], null, bPublishedAt);
        var e = await CreatePublishedAsync(admin, $"E {token}", [], null, DateTime.UtcNow.AddDays(-3));

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/posts/{a.Slug}/related?count=4");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        var slugs = related!.Select(p => p.Slug).ToList();

        slugs.Should().HaveCount(4,
            "every candidate is listed: a flag-off tag is neutral, never a demotion");
        slugs[0].Should().Be(c.Slug,
            "the shared category's point outranks the zero-scoring posts");
        slugs[^1].Should().Be(e.Slug,
            "the oldest zero-scorer fills last — B must not sink below it");
        slugs.Skip(1).Take(2).Should().BeEquivalentTo([b.Slug, d.Slug],
            "B scores exactly like D, which shares nothing with A and has B's PublishedAt: the unchanged tie-break sorts them together");
    }

    [Fact]
    public async Task Related_TagWithCountsTowardsRelatedOn_RanksTheTagSharingPostAboveTheCategoryPost()
    {
        var token = $"r07kb{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var tag = await CreateTagAsync(admin, $"{token}-prov");
        var category = await CreateCategoryAsync(admin, $"{token}-cat");

        var a = await CreatePublishedAsync(admin, $"A {token}", [tag.Name], category.Id, DateTime.UtcNow.AddDays(-3));
        var b = await CreatePublishedAsync(admin, $"B {token}", [tag.Name], null, DateTime.UtcNow.AddDays(-2));
        var c = await CreatePublishedAsync(admin, $"C {token}", [], category.Id, DateTime.UtcNow.AddDays(-1));

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/posts/{a.Slug}/related?count=3");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();

        related!.Select(p => p.Slug).Should().Equal([b.Slug, c.Slug],
            "with the flag on the shared tag scores 2 and beats the category's 1, as today");
    }

    [Fact]
    public async Task Related_FlagOffTag_StillShowsOnThePostAndItsTagPageListsIt()
    {
        var token = $"r07kc{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var tag = await CreateTagAsync(admin, $"{token}-prov");
        var off = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, false));
        off.EnsureSuccessStatusCode();
        var a = await CreatePublishedAsync(admin, $"A {token}", [tag.Name], null, DateTime.UtcNow.AddDays(-1));

        var anonymous = factory.CreateClient();
        var post = await anonymous.GetFromJsonAsync<PostDto>($"/api/posts/{a.Slug}");
        post!.Tags.Select(t => t.Name).Should().Contain(tag.Name,
            "the flag-off tag still shows in the post's tag list");

        var byTag = await anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>($"/api/posts?tagSlug={tag.Slug}");
        byTag!.Items.Select(p => p.Slug).Should().Contain(a.Slug,
            "the tag page still lists the post");
    }

    [Fact]
    public async Task Related_FlagOffTag_AddsNoPoints_ButItsPostStillCountsThroughTheCategory()
    {
        var token = $"r07kd{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var tag = await CreateTagAsync(admin, $"{token}-prov");
        var category = await CreateCategoryAsync(admin, $"{token}-cat");
        var off = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, false));
        off.EnsureSuccessStatusCode();

        // D shares the flag-off tag AND the category (1 point), C shares only the category
        // (1 point): the tie-break (newest first) must decide, so the newer C comes first.
        var a = await CreatePublishedAsync(admin, $"A {token}", [tag.Name], category.Id, DateTime.UtcNow.AddDays(-3));
        var d = await CreatePublishedAsync(admin, $"D {token}", [tag.Name], category.Id, DateTime.UtcNow.AddDays(-2));
        var c = await CreatePublishedAsync(admin, $"C {token}", [], category.Id, DateTime.UtcNow.AddDays(-1));

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/posts/{a.Slug}/related?count=3");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();

        related!.Select(p => p.Slug).Should().Equal([c.Slug, d.Slug],
            "the flag-off tag adds no points (D ties with C at 1) and its post still counts through the category");
    }

    [Fact]
    public async Task UpdateTag_TogglingCountsTowardsRelatedBackOn_IsStored_AndTheTagCountsAgain()
    {
        var token = $"r07kf{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        await RemoveAllPostsAsync(factory);
        var admin = factory.CreateClient().WithRole("Admin");

        var tag = await CreateTagAsync(admin, $"{token}-prov");
        var category = await CreateCategoryAsync(admin, $"{token}-cat");
        var a = await CreatePublishedAsync(admin, $"A {token}", [tag.Name], category.Id, DateTime.UtcNow.AddDays(-3));
        var b = await CreatePublishedAsync(admin, $"B {token}", [tag.Name], null, DateTime.UtcNow.AddDays(-2));
        var c = await CreatePublishedAsync(admin, $"C {token}", [], category.Id, DateTime.UtcNow.AddDays(-1));

        var off = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, false));
        off.EnsureSuccessStatusCode();
        var anonymous = factory.CreateClient();
        var during = await anonymous.GetAsync($"/api/posts/{a.Slug}/related?count=2");
        during.EnsureSuccessStatusCode();
        var whileOff = await during.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        whileOff!.Select(p => p.Slug).Should().Equal([c.Slug, b.Slug],
            "while the flag is off B scores like a post sharing nothing and C's point wins");

        var on = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, true));
        on.EnsureSuccessStatusCode();
        var restored = await on.Content.ReadFromJsonAsync<TagDto>();
        restored!.CountsTowardsRelated.Should().BeTrue("the toggle back on is stored and returned");

        var after = await anonymous.GetAsync($"/api/posts/{a.Slug}/related?count=2");
        after.EnsureSuccessStatusCode();
        var whileOn = await after.Content.ReadFromJsonAsync<List<PostSummaryDto>>();
        whileOn!.Select(p => p.Slug).Should().Equal([b.Slug, c.Slug],
            "the tag counts again and B ranks above C");
    }

    // The ranking sees every published post in the class database (also the scheduled/draft
    // leftovers of this class's earlier tests), so each test empties the posts table first and
    // asserts on a database that contains exactly its own posts.
    private static async Task RemoveAllPostsAsync(BlogApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        await db.Posts.ExecuteDeleteAsync();
    }

    private static async Task<TagDto> CreateTagAsync(HttpClient client, string name)
    {
        var create = await client.PostAsJsonAsync("/api/tags", name);
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<TagDto>())!;
    }

    private static async Task<CategoryDto> CreateCategoryAsync(HttpClient client, string name)
    {
        var create = await client.PostAsJsonAsync("/api/categories", name);
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static async Task<PostDto> CreatePublishedAsync(
        HttpClient client, string title, string[] tags, Guid? categoryId, DateTime publishedAt)
    {
        var create = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            title,
            "content",
            Tags: tags,
            CategoryId: categoryId));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();

        var publish = await client.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest(publishedAt));
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
