using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Croft.Api.Tests;

/// <summary>
/// A page is a kind of post (brief 07g item 1): create/update carry Kind (fixed after creation),
/// NavigationPlacement and NavigationOrder; a page cannot carry a category, tags or a banner; a page
/// whose slug is reserved is rejected; everything that lists posts returns posts only while the
/// admin list shows both kinds.
/// </summary>
public class PostKindTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task CreateAndUpdate_CarryKindAndNavigationFields()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Nav Round Trip",
            "content",
            Kind: PostKind.Page,
            NavigationPlacement: NavigationPlacement.Footer,
            NavigationOrder: 3));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();

        post!.Kind.Should().Be(PostKind.Page);
        post.NavigationPlacement.Should().Be(NavigationPlacement.Footer);
        post.NavigationOrder.Should().Be(3);

        var update = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(
            "07g Nav Round Trip",
            "edited content",
            Kind: PostKind.Page,
            NavigationPlacement: NavigationPlacement.None,
            NavigationOrder: 1));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PostDto>();

        updated!.Kind.Should().Be(PostKind.Page, "the kind is carried through updates");
        updated.NavigationPlacement.Should().Be(NavigationPlacement.None);
        updated.NavigationOrder.Should().Be(1);
    }

    [Fact]
    public async Task Update_PageWithAnotherKind_Returns400NamingKind_AndLeavesTheRowUnchanged()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "07g Fixed Kind Page", PostKind.Page);

        var response = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(
            "07g Fixed Kind Page",
            "content",
            Kind: PostKind.Post));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the kind is fixed after creation");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Kind");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.Posts.AsNoTracking().SingleAsync(p => p.Id == post.Id)).Kind.Should().Be(PostKind.Page,
            "a rejected update leaves the row unchanged");
    }

    [Fact]
    public async Task Update_PostWithAnotherKind_Returns400NamingKind()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "07g Fixed Kind Post", PostKind.Post);

        var response = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(
            "07g Fixed Kind Post",
            "content",
            Kind: PostKind.Page));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Kind");
    }

    [Fact]
    public async Task Create_PageWithCategory_Returns400NamingCategory_AndStoresNoRow()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var category = await CreateCategoryAsync(admin, "07g Page Category");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Page With Category",
            "content",
            CategoryId: category!.Id,
            Kind: PostKind.Page));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "pages carry no category");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Category", "the validation problem must name the field");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.Posts.CountAsync(p => p.Title == "07g Page With Category")).Should().Be(0,
            "a rejected create stores nothing");
    }

    [Fact]
    public async Task Create_PageWithTags_Returns400NamingTags_AndStoresNoRow()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Page With Tags",
            "content",
            Tags: ["07g-page-tag"],
            Kind: PostKind.Page));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "pages carry no tags");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Tags", "the validation problem must name the field");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.Posts.CountAsync(p => p.Title == "07g Page With Tags")).Should().Be(0,
            "a rejected create stores nothing");
    }

    [Fact]
    public async Task Create_PageWithBanner_Returns400NamingBanner_AndStoresNoRow()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var bannerId = await SeedBannerAsync(factory);

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Page With Banner",
            "content",
            BannerImageId: bannerId,
            Kind: PostKind.Page));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "pages carry no banner");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Banner", "the validation problem must name the field");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.Posts.CountAsync(p => p.Title == "07g Page With Banner")).Should().Be(0,
            "a rejected create stores nothing");
    }

    [Fact]
    public async Task Update_PageWithBanner_Returns400NamingBanner()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var bannerId = await SeedBannerAsync(factory);
        var post = await CreateAsync(admin, "07g Page Banner Update", PostKind.Page);

        var response = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest(
            "07g Page Banner Update",
            "content",
            BannerImageId: bannerId,
            Kind: PostKind.Page));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Banner");
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Editor")]
    [InlineData(null)]
    public async Task PublicViewList_ForAnyCaller_IsThePublicList_NoPagesDraftsOrScheduledPosts(string? role)
    {
        // Brief 10b item 8: the reading pages ask for publicView=true and get the public list
        // whoever is signed in; the admin grid (no flag) keeps the admin variant.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var page = await CreateAsync(admin, "10b Reading Page", PostKind.Page);
        await PublishAsync(admin, page.Id);
        var draft = await CreateAsync(admin, "10b Reading Draft", PostKind.Post);
        var scheduled = await CreateAsync(admin, "10b Reading Scheduled", PostKind.Post);
        var scheduledResponse = await admin.PostAsJsonAsync($"/api/posts/{scheduled.Id}/publish",
            new PublishRequest(DateTime.UtcNow.AddDays(30)));
        scheduledResponse.EnsureSuccessStatusCode();
        var post = await CreateAsync(admin, "10b Reading Post", PostKind.Post);
        await PublishAsync(admin, post.Id);

        var caller = role is null ? factory.CreateClient() : factory.CreateClient().WithRole(role);
        var response = await caller.GetAsync("/api/posts?publicView=true&pageSize=50");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        result!.Items.Should().Contain(p => p.Slug == post.Slug);
        result.Items.Should().NotContain(p => p.Slug == page.Slug, "a page is not a post");
        result.Items.Should().NotContain(p => p.Slug == draft.Slug, "a draft is not public");
        result.Items.Should().NotContain(p => p.Slug == scheduled.Slug, "a scheduled post is not public yet");

        var grid = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?pageSize=50");
        grid!.Items.Should().Contain(p => p.Slug == page.Slug).And.Contain(p => p.Slug == draft.Slug,
            "without the flag the admin variant is unchanged");
    }

    [Fact]
    public async Task PublicList_ExcludesPublishedPages()
    {
        // The home list (GET /api/posts, public) is the post feed: a published page lives at the
        // Web root and must never appear among posts.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var page = await CreateAsync(admin, "07g Home List Page", PostKind.Page);
        await PublishAsync(admin, page.Id);
        var post = await CreateAsync(admin, "07g Home List Post", PostKind.Post);
        await PublishAsync(admin, post.Id);

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/posts");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        result!.Items.Should().Contain(p => p.Slug == post.Slug);
        result.Items.Should().NotContain(p => p.Slug == page.Slug,
            "the public list returns posts only");
    }

    [Fact]
    public async Task Related_ExcludesPages()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreateAsync(admin, "07g Related Source", PostKind.Post);
        await PublishAsync(admin, post.Id);
        var page = await CreateAsync(admin, "07g Related Page", PostKind.Page);
        await PublishAsync(admin, page.Id);

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/posts/{post.Slug}/related?count=10");
        response.EnsureSuccessStatusCode();
        var related = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>();

        related.Should().NotContain(p => p.Slug == page.Slug,
            "related posts are posts only — a page must never fill the slots");
    }

    [Fact]
    public async Task Search_ExcludesPages()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Searchable Page",
            "A body about xylophone maintenance.",
            Kind: PostKind.Page));
        create.EnsureSuccessStatusCode();
        var page = await create.Content.ReadFromJsonAsync<PostDto>();
        await PublishAsync(admin, page!.Id);

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/search?q=xylophone");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        result!.Items.Should().NotContain(p => p.Slug == page.Slug,
            "search returns posts only");
    }

    [Fact]
    public async Task DraftPage_IsAbsentFromPublicListSearchAndRelated()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07g Draft Page",
            "A body about zebraphone tuning.",
            Kind: PostKind.Page));
        create.EnsureSuccessStatusCode();
        var page = (await create.Content.ReadFromJsonAsync<PostDto>())!;
        var post = await CreateAsync(admin, "07g Draft Page Sibling", PostKind.Post);
        await PublishAsync(admin, post.Id);

        var anonymous = factory.CreateClient();
        var list = await (await anonymous.GetAsync("/api/posts")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        list!.Items.Should().NotContain(p => p.Slug == page.Slug, "a draft page is not public");

        var search = await (await anonymous.GetAsync("/api/search?q=zebraphone")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        search!.Items.Should().NotContain(p => p.Slug == page.Slug, "a draft page is not public");

        var related = await (await anonymous.GetAsync($"/api/posts/{post.Slug}/related?count=10")).Content
            .ReadFromJsonAsync<List<PostSummaryDto>>();
        related.Should().NotContain(p => p.Slug == page.Slug, "a draft page is not public");
    }

    [Fact]
    public async Task AdminList_ShowsBothKinds_AndTheKindFilterSelectsOne()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var page = await CreateAsync(admin, "07g Kind Filter Page", PostKind.Page);
        var post = await CreateAsync(admin, "07g Kind Filter Post", PostKind.Post);

        var all = await (await admin.GetAsync("/api/posts?pageSize=50")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        all!.Items.Should().Contain(p => p.Slug == page.Slug && p.Kind == PostKind.Page,
            "the admin list shows both kinds and the rows carry their kind");
        all.Items.Should().Contain(p => p.Slug == post.Slug && p.Kind == PostKind.Post);

        var pages = await (await admin.GetAsync("/api/posts?kind=Page&pageSize=50")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        pages!.Items.Should().Contain(p => p.Slug == page.Slug);
        pages.Items.Should().NotContain(p => p.Slug == post.Slug, "the kind filter selects one kind");

        var posts = await (await admin.GetAsync("/api/posts?kind=Post&pageSize=50")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();
        posts!.Items.Should().Contain(p => p.Slug == post.Slug);
        posts.Items.Should().NotContain(p => p.Slug == page.Slug);
    }

    [Fact]
    public async Task AdminPageList_CarriesPlacementAndOrder_SortedSidebarThenFooterThenNone_ThenOrderThenTitle()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        foreach (var (title, placement, order) in new[]
        {
            ("10f Sort None A", NavigationPlacement.None, 0),
            ("10f Sort Footer B", NavigationPlacement.Footer, 2),
            ("10f Sort Side Z", NavigationPlacement.Sidebar, 5),
            ("10f Sort Footer A", NavigationPlacement.Footer, 2),
            ("10f Sort Side Y", NavigationPlacement.Sidebar, 1),
            ("10f Sort Footer First", NavigationPlacement.Footer, 1),
        })
        {
            var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
                title, "content", Kind: PostKind.Page, NavigationPlacement: placement, NavigationOrder: order));
            create.EnsureSuccessStatusCode();
        }

        var pages = await (await admin.GetAsync("/api/posts?kind=Page&pageSize=50")).Content
            .ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        var mine = pages!.Items.Where(p => p.Title.StartsWith("10f Sort ", StringComparison.Ordinal)).ToList();
        mine.Select(p => p.Title).Should().Equal(
            "10f Sort Side Y", "10f Sort Side Z", "10f Sort Footer First", "10f Sort Footer A", "10f Sort Footer B", "10f Sort None A");
        mine.Select(p => (p.NavigationPlacement, p.NavigationOrder)).Should().Equal(
            (NavigationPlacement.Sidebar, 1), (NavigationPlacement.Sidebar, 5), (NavigationPlacement.Footer, 1),
            (NavigationPlacement.Footer, 2), (NavigationPlacement.Footer, 2), (NavigationPlacement.None, 0));
    }

    [Fact]
    public async Task Create_Page_WhoseSlugIsAReservedWord_GetsTheUsualSuffix()
    {
        // A page lives at /<slug>; a slug the Web app serves for itself (here "login") would be
        // shadowed, so generation treats reserved words like taken ones and applies the usual
        // collision suffix.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Login",
            "content",
            Kind: PostKind.Page));
        response.EnsureSuccessStatusCode();
        var post = await response.Content.ReadFromJsonAsync<PostDto>();

        post!.Slug.Should().Be("login-1",
            "a generated slug that hits a reserved word gets the usual numeric suffix");
    }

    [Fact]
    public async Task Create_Post_WhoseSlugIsAReservedWord_KeepsIt()
    {
        // Posts live under /post/{slug} and never shadow a Web route: their slug generation is
        // unchanged and a reserved word is just a word.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Admin",
            "content",
            Kind: PostKind.Post));
        response.EnsureSuccessStatusCode();
        var post = await response.Content.ReadFromJsonAsync<PostDto>();

        post!.Slug.Should().Be("admin");
    }

    [Fact]
    public async Task Update_PageWhoseSlugIsReserved_Returns400NamingTheField()
    {
        // Generation keeps reserved words away from page slugs, so a page carrying one can only
        // predate the rule (or be seeded): any further write must refuse it with 400.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var page = new Post
            {
                Slug = "search",
                Title = "Seeded Reserved Page",
                MarkdownContent = "content",
                Status = PostStatus.Draft,
                Kind = PostKind.Page,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AuthorId = "test-user-id",
                AuthorName = "Test User"
            };
            db.Posts.Add(page);
            await db.SaveChangesAsync();
            id = page.Id;
        }

        var response = await admin.PutAsJsonAsync($"/api/posts/{id}", new UpdatePostRequest(
            "Seeded Reserved Page",
            "edited content",
            Kind: PostKind.Page));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a page whose slug is reserved is 400");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Slug", "the validation problem must name the field")
            .And.Contain("reserved");
    }

    private static async Task<PostDto> CreateAsync(HttpClient client, string title, PostKind kind)
    {
        var create = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            title, "content", Kind: kind));
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task PublishAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsJsonAsync($"/api/posts/{id}/publish", new PublishRequest());
        response.EnsureSuccessStatusCode();
    }

    private static async Task<CategoryDto?> CreateCategoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/categories", name);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CategoryDto>();
    }

    private static async Task<Guid> SeedBannerAsync(BlogApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = "07g-banner.png",
            OriginalName = "07g-banner.png",
            ContentType = "image/png",
            FilePath = "07g-banner.png",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "test-user-id",
            Scope = MediaAssetScope.Global
        };
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private BlogApiFactory CreateFactoryAsync(Action<IServiceCollection>? configureServices = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath, configureServices: configureServices);
    }
}
