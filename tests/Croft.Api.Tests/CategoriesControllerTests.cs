using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace Croft.Api.Tests;

public class CategoriesControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task UpdateCategory_SetsNameAndIcon()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "Icon Check");

        var update = await admin.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("Technology", "Folder"));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<CategoryDto>();
        updated!.Name.Should().Be("Technology");
        updated.Icon.Should().Be("Folder");

        var list = await admin.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        var persisted = list!.Single(c => c.Id == category.Id);
        persisted.Name.Should().Be("Technology", "the rename must be persisted");
        persisted.Icon.Should().Be("Folder", "the icon must be persisted");
        persisted.Slug.Should().Be("technology", "the slug follows the name");
    }

    [Fact]
    public async Task UpdateCategory_InvalidIcon_Returns400()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "Invalid Icon");

        var update = await admin.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("Technology", "bad icon!"));

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Icon");
    }

    [Fact]
    public async Task UpdateCategory_BlankName_Returns400()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "Blank Name");

        var update = await admin.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("", "Folder"));

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Name");
    }

    [Fact]
    public async Task DeleteCategory_InUse_Returns409()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "In Use");

        var create = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Categorized Post", "content", CategoryId: category.Id));
        create.EnsureSuccessStatusCode();

        var delete = await admin.DeleteAsync($"/api/categories/{category.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var list = await admin.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        list.Should().ContainSingle(c => c.Id == category.Id, "the category must survive a refused delete");
    }

    [Fact]
    public async Task DeleteCategory_Unused_Returns204()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "Lonely");

        var delete = await admin.DeleteAsync($"/api/categories/{category.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await admin.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        // The class database carries the other tests' categories too, so assert on this
        // test's own category rather than on the list being empty.
        list.Should().NotContain(c => c.Id == category.Id, "the deleted category must be gone");
    }

    [Fact]
    public async Task ListCategories_IncludesIconAndPublishedCount()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "Tech");

        var update = await admin.PutAsJsonAsync($"/api/categories/{category.Id}",
            new UpdateCategoryRequest("Tech", "Code"));
        update.EnsureSuccessStatusCode();

        var draft = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Draft Post", "content", CategoryId: category.Id));
        draft.EnsureSuccessStatusCode();
        var published = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Published Post", "content", CategoryId: category.Id));
        published.EnsureSuccessStatusCode();
        var publishedPost = await published.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{publishedPost!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient();
        var list = await anonymous.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var item = list!.Single(c => c.Id == category.Id);
        item.Icon.Should().Be("Code");
        item.PostCount.Should().Be(1, "PostCount counts published posts only");
    }

    [Fact]
    public async Task ListCategories_WithPagingParameters_ReturnsAPagedResultOrderedByName()
    {
        // The class database is shared by this class's tests (03h item 2), so the page is scoped
        // to this test's own categories through the q filter; the exact counts stay exact.
        var token = $"cat{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        for (var n = 1; n <= 12; n++)
        {
            await CreateCategoryAsync(admin, $"{token} {n:00}");
        }

        var page2 = await admin.GetFromJsonAsync<PagedResult<CategoryDto>>($"/api/categories?q={token}&page=2&pageSize=10");

        page2!.Page.Should().Be(2);
        page2.TotalCount.Should().Be(12);
        page2.Items.Should().HaveCount(2, "12 categories fill one page and two slots of the second");
        page2.Items.Select(c => c.Name).Should().BeInAscendingOrder("the admin list is name-ordered");
    }

    [Fact]
    public async Task ListCategories_WithQ_ReturnsOnlyTheMatchingCategoriesAsAPagedResult()
    {
        // Unique-token names: the class database is shared (03h item 2), and a literal
        // "Technology" here would collide on slug "technology" with the rename in
        // UpdateCategory_SetsNameAndIcon. "chnology-{token}" is a case-insensitive substring
        // of exactly one of the two names ("Technology-{token}"; "Travel-{token}" has no 'c'),
        // so the one-match claim is unchanged.
        var token = $"{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateCategoryAsync(admin, $"Technology-{token}");
        await CreateCategoryAsync(admin, $"Travel-{token}");

        var filtered = await admin.GetFromJsonAsync<PagedResult<CategoryDto>>($"/api/categories?q=chnology-{token}");

        filtered!.TotalCount.Should().Be(1);
        filtered.Items.Should().ContainSingle().Which.Name.Should().Be($"Technology-{token}");
    }

    [Fact]
    public async Task ListCategories_WithTop_ReturnsInUseCategoriesByPublishedPostCount()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var busy = await CreateCategoryAsync(admin, "Busy");
        var quiet = await CreateCategoryAsync(admin, "Quiet");
        var unused = await CreateCategoryAsync(admin, "Unused");
        foreach (var title in new[] { "Top One", "Top Two" })
        {
            var create = await admin.PostAsJsonAsync("/api/posts",
                new CreatePostRequest(title, "content", CategoryId: busy.Id));
            create.EnsureSuccessStatusCode();
            var post = await create.Content.ReadFromJsonAsync<PostDto>();
            var publish = await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());
            publish.EnsureSuccessStatusCode();
        }

        var one = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Top Three", "content", CategoryId: quiet.Id));
        one.EnsureSuccessStatusCode();
        var quietPost = await one.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{quietPost!.Id}/publish", new PublishRequest());

        var top = await admin.GetFromJsonAsync<List<CategoryDto>>("/api/categories?top=12");

        // The class database is shared (03h item 2), so other tests' used categories are in the
        // list too: assert this test's own claims — used categories only, most posts first, and
        // never the unused one — rather than the whole list.
        top!.Select(c => c.Name).Should().ContainInOrder(["Busy", "Quiet"],
            "top returns used categories most posts first");
        top.Should().OnlyContain(c => c.PostCount > 0, "top returns only categories with published posts");
        top.Should().NotContain(c => c.Id == unused.Id, "an unused category never makes the top list");
    }

    [Fact]
    public async Task GetCategoryBySlug_ReturnsTheCategory_AndAnUnknownSlugIs404()
    {
        // Unique name: the class database is shared (03h item 2), and a literal "Tech" would
        // collide with the category another test in this class already created.
        var name = $"cat-lookup-{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, name);

        var found = await admin.GetFromJsonAsync<CategoryDto>($"/api/categories/{category.Slug}");

        found!.Name.Should().Be(name, "a slug lookup returns the one category without listing the table");

        var missing = await admin.GetAsync($"/api/categories/no-such-category-{Guid.NewGuid():N}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<CategoryDto> CreateCategoryAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/categories", name);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    [Fact]
    public async Task CreateCategory_NameOf129Characters_Returns400WithAMessage_AndOf128IsAccepted()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var tooLong = await admin.PostAsJsonAsync("/api/categories", new string('a', 129));

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a name over the column length is a validation error, never a 500");
        var problem = await tooLong.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("128");

        var atTheLimit = await admin.PostAsJsonAsync("/api/categories", new string('b', 128));
        atTheLimit.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCategory_BlankName_Returns400(string name)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/categories", name);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Name");
    }

    [Fact]
    public async Task CreateCategory_NameWithNothingASlugCanCarry_Returns400_InsteadOfJoiningTheUntitledOne()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/categories", "!!! ???");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "such a name would slug to the shared fallback and silently return another row");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be(TaxonomyNameMessage);
    }

    private const string TaxonomyNameMessage =
        "The name needs at least one Latin letter or digit (a-z, 0-9) to make its address.";

    [Theory]
    [InlineData("日本語")]
    [InlineData("🙂🙂")]
    public async Task CreateCategory_NameWithNoLatinLetterOrDigit_Returns400WithTheHonestMessage_AndWritesNothing(string name)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var before = (await admin.GetFromJsonAsync<PagedResult<CategoryDto>>("/api/categories?page=1&pageSize=100"))!.TotalCount;

        var response = await admin.PostAsJsonAsync("/api/categories", name);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be(TaxonomyNameMessage, "{0} has letters, but none an ASCII slug can carry", name);
        (await admin.GetFromJsonAsync<PagedResult<CategoryDto>>("/api/categories?page=1&pageSize=100"))!.TotalCount.Should().Be(before,
            "a refused name is not stored");
    }

    [Fact]
    public async Task UpdateCategory_Hashtags_AreStoredInOrder_WithTheirNetworks_AndKeptByARenameWithoutThem_UntilCleared()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, $"Hashtag Keep {Guid.NewGuid():N}");

        var put = await admin.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(category.Name, null,
            [new HashtagDto("#Homelab", HashtagNetworks.Both), new HashtagDto("SelfHosted", HashtagNetworks.BlueskyOnly)]));
        put.EnsureSuccessStatusCode();

        (await GetCategoryAsync(admin, category.Id)).Hashtags.Should().Equal(
            [new HashtagDto("Homelab", HashtagNetworks.Both), new HashtagDto("SelfHosted", HashtagNetworks.BlueskyOnly)],
            "the leading # is stripped and the order is kept");

        var renamed = $"Hashtag Renamed {Guid.NewGuid():N}";
        (await admin.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(renamed, null))).EnsureSuccessStatusCode();
        var afterRename = await GetCategoryAsync(admin, category.Id);
        afterRename.Name.Should().Be(renamed);
        afterRename.Hashtags.Select(h => h.Name).Should().Equal(["Homelab", "SelfHosted"], "a request without Hashtags leaves the list unchanged");

        (await admin.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(renamed, null, []))).EnsureSuccessStatusCode();
        (await GetCategoryAsync(admin, category.Id)).Hashtags.Should().BeEmpty("an empty list clears them");
    }

    [Fact]
    public async Task UpdateCategory_HashtagsOnlyChange_ShowsInTheAnonymousCachedRead()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient();
        var category = await CreateCategoryAsync(admin, $"Hashtag Cache {Guid.NewGuid():N}");
        (await anonymous.GetFromJsonAsync<CategoryDto>($"/api/categories/{category.Slug}"))!.Hashtags.Should().BeEmpty();

        (await admin.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(category.Name, null,
            [new HashtagDto("Homelab", HashtagNetworks.Both)]))).EnsureSuccessStatusCode();

        (await anonymous.GetFromJsonAsync<CategoryDto>($"/api/categories/{category.Slug}"))!.Hashtags.Should().ContainSingle()
            .Which.Name.Should().Be("Homelab", "the cached read is invalidated by an update that changes only the hashtags");
    }

    [Theory]
    [MemberData(nameof(BadHashtagLists))]
    public async Task UpdateCategory_BadHashtags_Return400NamingTheFieldAndTheHashtag(HashtagDto[] hashtags, string named)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, $"Hashtag Bad {Guid.NewGuid():N}");

        var update = await admin.PutAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(category.Name, null, hashtags));

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Hashtags").And.Contain(named);
        (await GetCategoryAsync(admin, category.Id)).Hashtags.Should().BeEmpty("a refused update stores nothing");
    }

    public static TheoryData<HashtagDto[], string> BadHashtagLists() => new()
    {
        { [new("#9", HashtagNetworks.Both)], "#9" },
        { [new("two words", HashtagNetworks.Both)], "two words" },
        { [new("Homelab", HashtagNetworks.Both), new("homelab", HashtagNetworks.BlueskyOnly)], "homelab" },
        { [.. Enumerable.Range(0, 11).Select(i => new HashtagDto("Tag" + (char)('a' + i), HashtagNetworks.Both))], "Tagk" },
    };

    private static async Task<CategoryDto> GetCategoryAsync(HttpClient admin, Guid id)
    {
        var list = await admin.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        return list!.Single(c => c.Id == id);
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
