using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// The page endpoints (brief 07g item 1): GET /api/pages/navigation is the sidebar's anonymous
/// read — published pages marked for navigation, ordered by NavigationOrder then title — and
/// GET /api/pages/{slug} is the public page fetch (published only; drafts for signed-in editors,
/// like GET /api/posts/{slug} does for posts).
/// </summary>
public class PagesControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Navigation_ReturnsOnlySidebarPublishedPages_InNavigationOrderThenTitle()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateAsync(admin, "07g Nav Delta", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 2);
        await CreateAsync(admin, "07g Nav Yankee", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 1);
        await CreateAsync(admin, "07g Nav Alpha", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 2);
        await CreateAsync(admin, "07g Nav Hidden", PostKind.Page, NavigationPlacement.None, NavigationOrder: 0);
        await CreateAsync(admin, "07g Nav Footer", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 0);
        var draft = await CreateAsync(admin, "07g Nav Draft", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 0);
        await PublishAsync(admin, "07g Nav Delta");
        await PublishAsync(admin, "07g Nav Yankee");
        await PublishAsync(admin, "07g Nav Alpha");
        await PublishAsync(admin, "07g Nav Hidden");
        await PublishAsync(admin, "07g Nav Footer");

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/pages/navigation");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the navigation read is anonymous");
        var items = await response.Content.ReadFromJsonAsync<List<PageNavItemDto>>();
        items.Should().NotBeNull();
        PageNavItemDto[] expected =
        [
            new("07g Nav Yankee", "07g-nav-yankee"),
            new("07g Nav Alpha", "07g-nav-alpha"),
            new("07g Nav Delta", "07g-nav-delta"),
        ];
        items!.Where(i => i.Title.StartsWith("07g Nav ", StringComparison.Ordinal)).Should().Equal(expected,
            "the entries are title/slug pairs ordered by NavigationOrder, then title");
        items.Should().NotContain(i => i.Title == "07g Nav Hidden", "a page placed nowhere is not listed");
        items.Should().NotContain(i => i.Title == "07g Nav Footer", "a footer page is not in the sidebar list");
        items.Should().NotContain(i => i.Title == draft.Title,
            "a draft page is in none of the public reads");
    }

    [Fact]
    public async Task Navigation_Footer_ReturnsOnlyFooterPublishedPages_InNavigationOrderThenTitle()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateAsync(admin, "10f Foot Zulu", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 2);
        await CreateAsync(admin, "10f Foot Mike", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 2);
        await CreateAsync(admin, "10f Foot First", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 1);
        await CreateAsync(admin, "10f Foot Side", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 0);
        await CreateAsync(admin, "10f Foot None", PostKind.Page, NavigationPlacement.None, NavigationOrder: 0);
        var draft = await CreateAsync(admin, "10f Foot Draft", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 0);
        await PublishAsync(admin, "10f Foot Zulu");
        await PublishAsync(admin, "10f Foot Mike");
        await PublishAsync(admin, "10f Foot First");
        await PublishAsync(admin, "10f Foot Side");
        await PublishAsync(admin, "10f Foot None");

        var response = await factory.CreateClient().GetAsync("/api/pages/navigation?placement=Footer");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the footer read is anonymous");
        var items = await response.Content.ReadFromJsonAsync<List<PageNavItemDto>>();
        PageNavItemDto[] expected =
        [
            new("10f Foot First", "10f-foot-first"),
            new("10f Foot Mike", "10f-foot-mike"),
            new("10f Foot Zulu", "10f-foot-zulu"),
        ];
        items!.Where(i => i.Title.StartsWith("10f Foot ", StringComparison.Ordinal)).Should().Equal(expected,
            "the footer entries are ordered by NavigationOrder, then title, and hold footer pages only");
        items.Should().NotContain(i => i.Title == draft.Title, "a draft page is in none of the public reads");
    }

    [Fact]
    public async Task NavigationAll_ReturnsTheSidebarAndFooterLists_InOneRead()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateAsync(admin, "10f All Side B", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 2);
        await CreateAsync(admin, "10f All Side A", PostKind.Page, NavigationPlacement.Sidebar, NavigationOrder: 2);
        await CreateAsync(admin, "10f All Foot B", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 1);
        await CreateAsync(admin, "10f All Foot A", PostKind.Page, NavigationPlacement.Footer, NavigationOrder: 3);
        await CreateAsync(admin, "10f All None", PostKind.Page, NavigationPlacement.None);
        await CreateAsync(admin, "10f All Draft", PostKind.Page, NavigationPlacement.Footer);
        foreach (var title in new[] { "10f All Side B", "10f All Side A", "10f All Foot B", "10f All Foot A", "10f All None" })
            await PublishAsync(admin, title);

        var response = await factory.CreateClient().GetAsync("/api/pages/navigation/all");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the combined read is anonymous");
        var all = await response.Content.ReadFromJsonAsync<PageNavigationDto>();
        all!.Sidebar.Where(i => i.Title.StartsWith("10f All ", StringComparison.Ordinal)).Select(i => i.Title)
            .Should().Equal("10f All Side A", "10f All Side B");
        all.Footer.Where(i => i.Title.StartsWith("10f All ", StringComparison.Ordinal)).Select(i => i.Title)
            .Should().Equal("10f All Foot B", "10f All Foot A");
    }

    [Fact]
    public async Task Navigation_NonePlacement_IsABadRequest()
    {
        await using var factory = CreateFactoryAsync();

        var response = await factory.CreateClient().GetAsync("/api/pages/navigation?placement=None");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "None is not a list");
    }

    [Fact]
    public async Task Get_PublishedPage_IsServedToAnonymous()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateAsync(admin, "07g Public Page", PostKind.Page);
        await PublishAsync(admin, "07g Public Page");

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/pages/07g-public-page");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PostDto>();
        page!.Title.Should().Be("07g Public Page");
        page.Kind.Should().Be(PostKind.Page);
    }

    [Fact]
    public async Task Get_DraftPage_Is404ForAnonymous_ButServedToSignedInEditors()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateAsync(admin, "07g Draft Only Page", PostKind.Page);

        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/pages/07g-draft-only-page")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "drafts are not public — 404 otherwise");

        var editor = factory.CreateClient().WithRole("Editor");
        (await editor.GetAsync("/api/pages/07g-draft-only-page")).StatusCode
            .Should().Be(HttpStatusCode.OK, "signed-in editors see draft pages, as with posts");
    }

    [Fact]
    public async Task Get_PostSlug_Returns404()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Not A Page",
            "content",
            Kind: PostKind.Post));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await PublishAsync(admin, "07g Not A Page");

        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync($"/api/pages/{post!.Slug}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the page fetch serves pages, not posts");
    }

    [Fact]
    public async Task GetPostBySlug_KeepsWorkingForPagesForTheEditor()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await CreateAsync(admin, "07g Editor Page", PostKind.Page);

        var editor = factory.CreateClient().WithRole("Editor");
        var response = await editor.GetAsync("/api/posts/07g-editor-page");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the editor's existing read endpoint keeps working for pages");
        var page = await response.Content.ReadFromJsonAsync<PostDto>();
        page!.Kind.Should().Be(PostKind.Page);
    }

    [Fact]
    public async Task List_ReturnsPublishedPagesOnly_WithUpdatedAt_AndNeverPosts()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, "07g List About", PostKind.Page);
        await CreateAsync(admin, "07g List Draft", PostKind.Page);
        await CreateAsync(admin, "07g List Post", PostKind.Post);
        var publish = await admin.PostAsJsonAsync($"/api/posts/{created.Id}/publish", new PublishRequest());
        publish.EnsureSuccessStatusCode();
        var published = await publish.Content.ReadFromJsonAsync<PostDto>();
        await PublishAsync(admin, "07g List Post");
        var scheduled = await CreateAsync(admin, "07g List Later", PostKind.Page);
        var future = await admin.PostAsJsonAsync($"/api/posts/{scheduled.Id}/publish",
            new PublishRequest(DateTime.UtcNow.AddDays(7)));
        future.EnsureSuccessStatusCode();

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/pages?page=1&pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the pages listing is anonymous");
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PageSummaryDto>>();
        result.Should().NotBeNull();
        // The expected row comes from the public page fetch: both reads map the same database
        // row (Postgres timestamps have microsecond precision, so an in-memory value with .NET
        // ticks must not be compared against one round-tripped through the column).
        var page = await (await anonymous.GetAsync($"/api/pages/{published!.Slug}")).Content
            .ReadFromJsonAsync<PostDto>();
        var rows = result!.Items.Where(i => i.Title.StartsWith("07g List ", StringComparison.Ordinal)).ToList();
        rows.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PageSummaryDto(page!.Id, page.Slug, page.Title,
                    PostStatus.Published, page.UpdatedAt),
                "the listing serves published pages with their UpdatedAt for the sitemap's lastmod");
        rows.Should().NotContain(i => i.Title == "07g List Draft", "drafts are not public");
        rows.Should().NotContain(i => i.Title == "07g List Later", "scheduled pages are not public yet");
        rows.Should().NotContain(i => i.Title == "07g List Post", "pages only — posts never appear here");
    }

    [Fact]
    public async Task List_IsPaged_LikeEveryOtherListRead()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        for (var n = 1; n <= 3; n++)
        {
            await CreateAsync(admin, $"07g Page Row {n}", PostKind.Page);
        }

        foreach (var row in new[] { "07g Page Row 1", "07g Page Row 2", "07g Page Row 3" })
        {
            await PublishAsync(admin, row);
        }

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/pages?page=1&pageSize=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PageSummaryDto>>();

        result!.TotalCount.Should().BeGreaterThanOrEqualTo(3);
        result.Items.Should().HaveCount(2, "the listing honours the paged contract (page/pageSize)");
    }

    private static async Task<PostDto> CreateAsync(HttpClient client, string title, PostKind kind,
        NavigationPlacement placement = NavigationPlacement.None, int NavigationOrder = 0)
    {
        var create = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            title, "content", Kind: kind, NavigationPlacement: placement, NavigationOrder: NavigationOrder));
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task PublishAsync(HttpClient client, string title)
    {
        // The publish route takes the id; the slug routes each test's own row to it.
        var list = await (await client.GetAsync("/api/posts?pageSize=50")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        var post = list!.Items.Single(p => p.Title == title);
        var response = await client.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest());
        response.EnsureSuccessStatusCode();
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }
}
