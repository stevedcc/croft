using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 14b item 2 (architecture finding 12): <c>GET /api/sitemap</c>, the slim anonymous read the
/// Web's sitemap is built from. It replaces four paged walks over the posts, pages, categories and
/// tags lists, so what it returns must be exactly what those walks together produced.
/// </summary>
public class SitemapEndpointTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task ItListsOnlyWhatIsPublic_AndNeverADraftAScheduledPostOrAnEmptyCategoryOrTag()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, new SqlCommandCounter());
        var admin = factory.CreateClient().WithRole("Admin");
        var seeded = await SeedAsync(admin);

        var sitemap = await factory.CreateClient().AsAnonymous().GetFromJsonAsync<SitemapDto>("/api/sitemap");

        sitemap!.Posts.Select(p => p.Slug).Should().Equal(seeded.PublishedPostSlugsNewestFirst);
        sitemap.Posts.Should().OnlyContain(p => p.UpdatedAt > DateTime.UtcNow.AddHours(-1) && p.UpdatedAt.Kind == DateTimeKind.Utc);
        sitemap.Pages.Select(p => p.Slug).Should().Equal("sitemap-seed-about");
        sitemap.Categories.Should().Equal("sitemap-seed-category");
        sitemap.Tags.Should().Equal("sitemap-seed-tag");
        var all = sitemap.Posts.Select(p => p.Slug).Concat(sitemap.Pages.Select(p => p.Slug)).ToList();
        all.Should().NotContain([seeded.DraftSlug, seeded.ScheduledSlug, seeded.DraftPageSlug],
            "a draft and a scheduled post are not public");
        sitemap.Categories.Should().NotContain("sitemap-seed-empty-category");
        sitemap.Tags.Should().NotContain("sitemap-seed-draft-only-tag");
    }

    [Fact]
    public async Task ItIsExactlyWhatTheFourPagedListsItReplacesGave_InTheSameOrder()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, new SqlCommandCounter());
        var admin = factory.CreateClient().WithRole("Admin");
        await SeedAsync(admin);
        var anonymous = factory.CreateClient().AsAnonymous();

        var sitemap = await anonymous.GetFromJsonAsync<SitemapDto>("/api/sitemap");

        // The walk the Web's sitemap did until now: every row of the four anonymous lists, 100 per page,
        // then the published-only and count-above-zero rules it applied itself.
        var posts = await AllAsync<PostSummaryDto>(anonymous, p => $"/api/posts?page={p}&pageSize=100");
        var pages = await AllAsync<PageSummaryDto>(anonymous, p => $"/api/pages?page={p}&pageSize=100");
        var categories = await AllAsync<CategoryDto>(anonymous, p => $"/api/categories?page={p}&pageSize=100");
        var tags = await AllAsync<TagDto>(anonymous, p => $"/api/tags?page={p}&pageSize=100");

        sitemap!.Posts.Select(p => (p.Slug, p.UpdatedAt)).Should().Equal(
            posts.Where(p => p.Status == Croft.Shared.Enums.PostStatus.Published).Select(p => (p.Slug, p.UpdatedAt)));
        sitemap.Pages.Select(p => (p.Slug, p.UpdatedAt)).Should().Equal(
            pages.Where(p => p.Status == Croft.Shared.Enums.PostStatus.Published).Select(p => (p.Slug, p.UpdatedAt)));
        sitemap.Categories.Should().Equal(categories.Where(c => c.PostCount > 0).Select(c => c.Slug));
        sitemap.Tags.Should().Equal(tags.Where(t => t.PostCount > 0).Select(t => t.Slug));
        sitemap.Posts.Should().NotBeEmpty();
        sitemap.Categories.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TwoAnonymousCalls_RunOneSetOfQueries_OneQueryPerKind()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        await SeedAsync(factory.CreateClient().WithRole("Admin"));
        var anonymous = factory.CreateClient().AsAnonymous();

        counter.Reset();
        var first = await anonymous.GetFromJsonAsync<SitemapDto>("/api/sitemap");
        var queriesForOneBuild = counter.Commands;
        var second = await anonymous.GetFromJsonAsync<SitemapDto>("/api/sitemap");

        queriesForOneBuild.Should().Be(4, "posts, pages, categories and tags: one query each");
        counter.Commands.Should().Be(queriesForOneBuild, "the second call is answered from the cache");
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task AfterAPublish_TheNewPostIsInIt()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        await SeedAsync(admin);
        var anonymous = factory.CreateClient().AsAnonymous();
        var before = await anonymous.GetFromJsonAsync<SitemapDto>("/api/sitemap");

        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Sitemap Late Arrival", "## Body"));
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(DateTime.UtcNow.AddMinutes(-1))))
            .EnsureSuccessStatusCode();

        var after = await anonymous.GetFromJsonAsync<SitemapDto>("/api/sitemap");
        before!.Posts.Select(p => p.Slug).Should().NotContain(post.Slug);
        after!.Posts.Select(p => p.Slug).Should().Contain(post.Slug, "a write drops the cached sitemap");
    }

    [Fact]
    public async Task AStaffCaller_IsNeverServedFromTheCache()
    {
        var counter = new SqlCommandCounter();
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, counter);
        var admin = factory.CreateClient().WithRole("Admin");
        await SeedAsync(admin);

        counter.Reset();
        (await admin.GetAsync("/api/sitemap")).EnsureSuccessStatusCode();
        var oneBuild = counter.Commands;
        (await admin.GetAsync("/api/sitemap")).EnsureSuccessStatusCode();

        oneBuild.Should().Be(4);
        counter.Commands.Should().Be(2 * oneBuild, "staff read their own writes: the same gate as every other public read");
    }

    [Fact]
    public async Task ItAnswersAnAnonymousCaller()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var factory = CreateFactory(scratch, new SqlCommandCounter());

        using var response = await factory.CreateClient().AsAnonymous().GetAsync("/api/sitemap");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    private sealed record Seeded(
        IReadOnlyList<string> PublishedPostSlugsNewestFirst, string DraftSlug, string ScheduledSlug, string DraftPageSlug);

    /// <summary>Two published posts (one category, one tag), a draft with its own tag, a scheduled post,
    /// a published page and a draft page, and a category nobody uses.</summary>
    private static async Task<Seeded> SeedAsync(HttpClient admin)
    {
        var category = await (await admin.PostAsJsonAsync("/api/categories", "Sitemap Seed Category"))
            .Content.ReadFromJsonAsync<CategoryDto>();
        (await admin.PostAsJsonAsync("/api/categories", "Sitemap Seed Empty Category")).EnsureSuccessStatusCode();

        var older = await CreateAsync(admin, new CreatePostRequest("Sitemap Seed Older", "## Body", Tags: ["sitemap-seed-tag"], CategoryId: category!.Id));
        var newer = await CreateAsync(admin, new CreatePostRequest("Sitemap Seed Newer", "## Body", Tags: ["sitemap-seed-tag"], CategoryId: category.Id));
        await PublishAsync(admin, older, DateTime.UtcNow.AddDays(-2));
        await PublishAsync(admin, newer, DateTime.UtcNow.AddDays(-1));

        var draft = await CreateAsync(admin, new CreatePostRequest("Sitemap Seed Draft", "## Body", Tags: ["sitemap-seed-draft-only-tag"]));
        var scheduled = await CreateAsync(admin, new CreatePostRequest("Sitemap Seed Scheduled", "## Body"));
        await PublishAsync(admin, scheduled, DateTime.UtcNow.AddDays(3));

        var about = await CreateAsync(admin, new CreatePostRequest("Sitemap Seed About", "## About", Kind: Croft.Shared.Enums.PostKind.Page));
        await PublishAsync(admin, about, DateTime.UtcNow.AddDays(-1));
        var draftPage = await CreateAsync(admin, new CreatePostRequest("Sitemap Seed Draft Page", "## Draft", Kind: Croft.Shared.Enums.PostKind.Page));

        return new Seeded([newer.Slug, older.Slug], draft.Slug, scheduled.Slug, draftPage.Slug);
    }

    private static async Task<PostDto> CreateAsync(HttpClient admin, CreatePostRequest request)
    {
        var response = await admin.PostAsJsonAsync("/api/posts", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task PublishAsync(HttpClient admin, PostDto post, DateTime at) =>
        (await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest(at))).EnsureSuccessStatusCode();

    private static async Task<List<T>> AllAsync<T>(HttpClient client, Func<int, string> url)
    {
        List<T> rows = [];
        for (var page = 1; ; page++)
        {
            var result = (await client.GetFromJsonAsync<PagedResult<T>>(url(page)))!;
            rows.AddRange(result.Items);
            if (result.Items.Count == 0 || rows.Count >= result.TotalCount)
                return rows;
        }
    }

    private static BlogApiFactory CreateFactory(ScratchDatabase scratch, DbCommandInterceptor interceptor)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            scratch.ConnectionString,
            storagePath,
            configureServices: services => services.RemoveAll<IHostedService>(),
            commandInterceptor: interceptor);
    }
}
