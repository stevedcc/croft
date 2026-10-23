using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

public class TagsControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task CreateDuplicateTag_ReturnsExistingTag()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var first = await admin.PostAsJsonAsync("/api/tags", "Dedupe Check");
        first.EnsureSuccessStatusCode();
        var second = await admin.PostAsJsonAsync("/api/tags", "Dedupe Check");
        second.EnsureSuccessStatusCode();

        var firstTag = await first.Content.ReadFromJsonAsync<TagDto>();
        var secondTag = await second.Content.ReadFromJsonAsync<TagDto>();

        secondTag!.Slug.Should().Be(firstTag!.Slug);
    }

    [Fact]
    public async Task RenameTag()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/tags", "Rename Source");
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        var update = await admin.PutAsJsonAsync($"/api/tags/{tag!.Id}", new UpdateTagRequest("K8s"));
        update.EnsureSuccessStatusCode();
        var renamed = await update.Content.ReadFromJsonAsync<TagDto>();
        renamed!.Name.Should().Be("K8s");

        var list = await admin.GetFromJsonAsync<List<TagDto>>("/api/tags");
        var persisted = list!.Single(t => t.Id == tag.Id);
        persisted.Name.Should().Be("K8s", "the rename must be persisted");
        persisted.Slug.Should().Be("k8s", "the slug follows the name");
    }

    [Fact]
    public async Task RenameTag_BlankName_Returns400()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/tags", "Blank Name");
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        var update = await admin.PutAsJsonAsync($"/api/tags/{tag!.Id}", new UpdateTagRequest(" "));

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Name");
    }

    [Fact]
    public async Task DeleteTag_InUse_Returns409()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/tags", "In Use");
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        var post = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Tagged Post", "content", Tags: ["In Use"]));
        post.EnsureSuccessStatusCode();

        var delete = await admin.DeleteAsync($"/api/tags/{tag!.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var list = await admin.GetFromJsonAsync<List<TagDto>>("/api/tags");
        list.Should().ContainSingle(t => t.Id == tag.Id, "the tag must survive a refused delete");
    }

    [Fact]
    public async Task ListTags_IncludesPublishedPostCount()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/tags", "Counted");
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        var draft = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Draft with tag", "content", Tags: ["Counted"]));
        draft.EnsureSuccessStatusCode();
        var draftPost = await draft.Content.ReadFromJsonAsync<PostDto>();

        var list = await admin.GetFromJsonAsync<List<TagDto>>("/api/tags");
        list!.Single(t => t.Id == tag!.Id).PostCount.Should().Be(0,
            "PostCount is published posts carrying the tag (same rule as CategoryDto.PostCount)");

        var publish = await admin.PostAsJsonAsync($"/api/posts/{draftPost!.Id}/publish", new PublishRequest());
        publish.EnsureSuccessStatusCode();

        list = await admin.GetFromJsonAsync<List<TagDto>>("/api/tags");
        list!.Single(t => t.Id == tag!.Id).PostCount.Should().Be(1,
            "publishing the tagged post must raise the count");
    }

    [Fact]
    public async Task ListTags_WithPagingParameters_ReturnsAPagedResultOrderedByName()
    {
        // The class database is shared by this class's tests (03h item 2), so the page is scoped
        // to this test's own tags through the q filter; the exact counts stay exact.
        var token = $"paged{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        for (var n = 1; n <= 15; n++)
        {
            var create = await admin.PostAsJsonAsync("/api/tags", $"{token} {n:00}");
            create.EnsureSuccessStatusCode();
        }

        var page2 = await admin.GetFromJsonAsync<PagedResult<TagDto>>($"/api/tags?q={token}&page=2&pageSize=10");

        page2!.Page.Should().Be(2);
        page2.PageSize.Should().Be(10);
        page2.TotalCount.Should().Be(15);
        page2.Items.Should().HaveCount(5, "15 tags fill one full page and half of the second");
        page2.Items.Select(t => t.Name).Should().BeInAscendingOrder("the admin list is name-ordered");
        page2.Items[0].Name.Should().Be($"{token} 11", "page 2 continues after the first ten names");
    }

    [Fact]
    public async Task ListTags_WithQ_ReturnsOnlyTheMatchingTagsAsAPagedResult()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        foreach (var name in new[] { "Kubernetes", "K3s", "Hiking" })
        {
            var create = await admin.PostAsJsonAsync("/api/tags", name);
            create.EnsureSuccessStatusCode();
        }

        var filtered = await admin.GetFromJsonAsync<PagedResult<TagDto>>("/api/tags?q=ube");

        filtered!.TotalCount.Should().Be(1);
        filtered.Items.Should().ContainSingle().Which.Name.Should().Be("Kubernetes",
            "the filter is a case-insensitive substring of the name");
    }

    [Fact]
    public async Task ListTags_WithQContainingAWildcard_LooksForItLiterally()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        foreach (var name in new[] { "50% off", "half off" })
        {
            var create = await admin.PostAsJsonAsync("/api/tags", name);
            create.EnsureSuccessStatusCode();
        }

        var filtered = await admin.GetFromJsonAsync<PagedResult<TagDto>>("/api/tags?q=%25");

        filtered!.Items.Should().ContainSingle().Which.Name.Should().Be("50% off",
            "a % in the filter searches for a literal percent sign, never as a wildcard");
    }

    [Fact]
    public async Task CreateTag_CountsTowardsRelatedDefaultsToTrue()
    {
        var name = $"tag-related-default-{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/tags", name);
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        tag!.CountsTowardsRelated.Should().BeTrue("a new tag counts towards related posts");
    }

    [Fact]
    public async Task UpdateTag_CountsTowardsRelatedFalse_IsStoredReturned_AndListed()
    {
        var name = $"tag-related-off-{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/tags", name);
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        var update = await admin.PutAsJsonAsync($"/api/tags/{tag!.Id}", new UpdateTagRequest(name, false));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<TagDto>();
        updated!.CountsTowardsRelated.Should().BeFalse("the PUT returns the stored value");

        var list = await admin.GetFromJsonAsync<List<TagDto>>("/api/tags");
        var persisted = list!.Single(t => t.Id == tag.Id);
        persisted.CountsTowardsRelated.Should().BeFalse("the field is returned in the tag list and must be persisted");
    }

    [Fact]
    public async Task UpdateTag_WithoutCountsTowardsRelated_LeavesItUnchanged()
    {
        // The raw body without the field is what a Web of the previous release sends during a
        // rolling deploy: Name alone must rename and leave CountsTowardsRelated as it was.
        var name = $"tag-related-keep-{Guid.NewGuid():N}";
        var renamed = $"tag-related-kept-{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/tags", name);
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();
        var off = await admin.PutAsJsonAsync($"/api/tags/{tag!.Id}", new UpdateTagRequest(name, false));
        off.EnsureSuccessStatusCode();

        using var body = new StringContent(
            $"{{\"Name\":\"{renamed}\"}}", System.Text.Encoding.UTF8, "application/json");
        var update = await admin.PutAsync($"/api/tags/{tag.Id}", body);
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<TagDto>();

        updated!.Name.Should().Be(renamed, "Name stays required and behaves as now");
        updated.CountsTowardsRelated.Should().BeFalse("a request without the field leaves it unchanged");
    }

    [Fact]
    public async Task GetTagBySlug_ReturnsTheTag_AndAnUnknownSlugIs404()
    {
        // Unique name: the class database is shared, and a literal "Kubernetes" would both
        // collide with ListTags_WithQ's tag and break its q=ube TotalCount of 1.
        var name = $"tag-lookup-{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/tags", name);
        create.EnsureSuccessStatusCode();
        var tag = await create.Content.ReadFromJsonAsync<TagDto>();

        var found = await admin.GetFromJsonAsync<TagDto>($"/api/tags/{tag!.Slug}");

        found!.Name.Should().Be(name, "a slug lookup returns the one tag without listing the table");

        var missing = await admin.GetAsync($"/api/tags/no-such-tag-{Guid.NewGuid():N}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTag_NameOf129Characters_Returns400WithAMessage_AndOf128IsAccepted()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var tooLong = await admin.PostAsJsonAsync("/api/tags", new string('a', 129));

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a name over the column length is a validation error, never a 500");
        var problem = await tooLong.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("128");

        var atTheLimit = await admin.PostAsJsonAsync("/api/tags", new string('b', 128));
        atTheLimit.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTag_BlankName_Returns400(string name)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/tags", name);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Name");
    }

    [Fact]
    public async Task CreateTag_NameWithNothingASlugCanCarry_Returns400_InsteadOfJoiningTheUntitledOne()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/tags", "!!! ???");

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
    public async Task CreateTag_NameWithNoLatinLetterOrDigit_Returns400WithTheHonestMessage_AndWritesNothing(string name)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var before = (await admin.GetFromJsonAsync<PagedResult<TagDto>>("/api/tags?page=1&pageSize=100"))!.TotalCount;

        var response = await admin.PostAsJsonAsync("/api/tags", name);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be(TaxonomyNameMessage, "{0} has letters, but none an ASCII slug can carry", name);
        (await admin.GetFromJsonAsync<PagedResult<TagDto>>("/api/tags?page=1&pageSize=100"))!.TotalCount.Should().Be(before,
            "a refused name is not stored");
    }

    [Fact]
    public async Task UpdateTag_Hashtags_AreStoredInOrder_KeptByARenameAndTheRelatedSwitch_UntilCleared()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var tag = await CreateTagAsync(admin, $"hashtag-keep-{Guid.NewGuid():N}");

        var put = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, null,
            [new HashtagDto("#Homelab", HashtagNetworks.Both), new HashtagDto("SelfHosted", HashtagNetworks.MastodonOnly)]));
        put.EnsureSuccessStatusCode();
        (await GetTagAsync(admin, tag.Id)).Hashtags.Should().Equal(
            [new HashtagDto("Homelab", HashtagNetworks.Both), new HashtagDto("SelfHosted", HashtagNetworks.MastodonOnly)],
            "the leading # is stripped and the order is kept");

        var renamed = $"hashtag-renamed-{Guid.NewGuid():N}";
        (await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(renamed))).EnsureSuccessStatusCode();
        (await GetTagAsync(admin, tag.Id)).Hashtags.Select(h => h.Name).Should().Equal(["Homelab", "SelfHosted"], "a rename without Hashtags keeps the list");

        (await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(renamed, false))).EnsureSuccessStatusCode();
        var afterSwitch = await GetTagAsync(admin, tag.Id);
        afterSwitch.CountsTowardsRelated.Should().BeFalse();
        afterSwitch.Hashtags.Select(h => h.Name).Should().Equal(["Homelab", "SelfHosted"], "the Related switch's save keeps the list");

        (await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(renamed, null, []))).EnsureSuccessStatusCode();
        (await GetTagAsync(admin, tag.Id)).Hashtags.Should().BeEmpty("an empty list clears them");
    }

    [Fact]
    public async Task UpdateTag_HashtagsOnlyChange_ShowsInTheAnonymousCachedRead()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var anonymous = factory.CreateClient();
        var tag = await CreateTagAsync(admin, $"hashtag-cache-{Guid.NewGuid():N}");
        (await anonymous.GetFromJsonAsync<TagDto>($"/api/tags/{tag.Slug}"))!.Hashtags.Should().BeEmpty();

        (await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, null,
            [new HashtagDto("Homelab", HashtagNetworks.Both)]))).EnsureSuccessStatusCode();

        (await anonymous.GetFromJsonAsync<TagDto>($"/api/tags/{tag.Slug}"))!.Hashtags.Should().ContainSingle()
            .Which.Name.Should().Be("Homelab", "the cached read is invalidated by an update that changes only the hashtags");
    }

    [Theory]
    [MemberData(nameof(BadHashtagLists))]
    public async Task UpdateTag_BadHashtags_Return400NamingTheFieldAndTheHashtag(HashtagDto[] hashtags, string named)
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var tag = await CreateTagAsync(admin, $"hashtag-bad-{Guid.NewGuid():N}");

        var update = await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new UpdateTagRequest(tag.Name, null, hashtags));

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Hashtags").And.Contain(named);
        (await GetTagAsync(admin, tag.Id)).Hashtags.Should().BeEmpty("a refused update stores nothing");
    }

    public static TheoryData<HashtagDto[], string> BadHashtagLists() => new()
    {
        { [new("#9", HashtagNetworks.Both)], "#9" },
        { [new("two words", HashtagNetworks.Both)], "two words" },
        { [new("Homelab", HashtagNetworks.Both), new("homelab", HashtagNetworks.BlueskyOnly)], "homelab" },
        { [.. Enumerable.Range(0, 11).Select(i => new HashtagDto("Tag" + (char)('a' + i), HashtagNetworks.Both))], "Tagk" },
    };

    private static async Task<TagDto> CreateTagAsync(HttpClient admin, string name)
    {
        var create = await admin.PostAsJsonAsync("/api/tags", name);
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<TagDto>())!;
    }

    private static async Task<TagDto> GetTagAsync(HttpClient admin, Guid id)
    {
        var list = await admin.GetFromJsonAsync<List<TagDto>>("/api/tags");
        return list!.Single(t => t.Id == id);
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
