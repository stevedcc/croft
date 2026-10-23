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
using System.Net.Http.Json;
using System.Text.Json;

namespace Croft.Api.Tests;

public class PostsControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task CreatePost_WithDuplicateTitle_GeneratesUniqueSlug()
    {
        await using var factory = CreateFactoryAsync();
        var client = factory.CreateClient().WithRole("Admin");

        var token = Token();
        var request = new CreatePostRequest($"Hello World {token}", "content");
        var first = await client.PostAsJsonAsync("/api/posts", request);
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/posts", request);
        second.EnsureSuccessStatusCode();
        var post = await second.Content.ReadFromJsonAsync<PostDto>();

        post!.Slug.Should().Be($"hello-world-{token}-1", "the second post with the same title gets the collision suffix");
    }

    [Fact]
    public async Task PublicList_OnlyReturnsPublishedPosts()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var token = Token();
        var draftResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Draft Post {token}", "draft"));
        var draft = await draftResponse.Content.ReadFromJsonAsync<PostDto>();
        var publishedResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Published Post {token}", "published"));
        var published = await publishedResponse.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{published!.Id}/publish", new PublishRequest());

        // Both posts match the search term; only the published one may be listed.
        var result = await ListAsync(factory.CreateClient(), token);

        result.Items.Should().ContainSingle(p => p.Slug == published.Slug);
        result.Items.Should().NotContain(p => p.Slug == draft!.Slug, "a draft is not in the public list");
    }

    [Fact]
    public async Task PublicList_ItemsCarryExcerptAndAuthorName()
    {
        // The Atom feed's entry summary (SeoDescription falls back through the excerpt) and
        // author are built from the listing rows, so the wire format the feed deserialises is
        // asserted as JSON: the projection must carry the excerpt and the author's name.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var token = Token();

        var create = await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest($"Described {token}", "content", Subtitle: "A subtitle", Excerpt: "The excerpt."));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var listResponse = await factory.CreateClient().GetAsync($"/api/posts?pageSize=100&q={token}");
        listResponse.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("slug").GetString() == post.Slug);
        item.GetProperty("excerpt").GetString().Should().Be("The excerpt.",
            "the feed entry's summary falls back through the excerpt");
        item.GetProperty("authorName").GetString().Should().Be("Test User",
            "the feed entry's author is the post's author");
    }

    [Fact]
    public async Task PublicList_PostAppearsOnlyAfterInjectedClockPassesPublishedAt()
    {
        // The post's PublishedAt sits ahead of the fake clock on purpose: its visibility in the
        // public listing must follow the injected clock, not the wall clock.
        var token = Token();
        var slug = $"clock-gated-post-{token}";
        var publishedAt = DateTime.UtcNow.AddDays(1);
        var clock = new FakeTimeProvider(new DateTimeOffset(publishedAt).AddHours(-1));
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            db.Posts.Add(new Post
            {
                Slug = slug,
                Title = $"Clock Gated Post {token}",
                MarkdownContent = "content",
                Status = PostStatus.Published,
                PublishedAt = publishedAt,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                UpdatedAt = clock.GetUtcNow().UtcDateTime,
                AuthorId = "author-id",
                AuthorName = "Author"
            });
            await db.SaveChangesAsync();
        }

        var anonymous = factory.CreateClient();

        var earlyList = await ListAsync(anonymous, token);
        earlyList.Items.Should().NotContain(p => p.Slug == slug,
            "the injected clock has not reached PublishedAt yet");

        clock.Advance(TimeSpan.FromHours(2));

        var lateList = await ListAsync(anonymous, token);
        lateList.Items.Should().Contain(p => p.Slug == slug,
            "the injected clock has passed PublishedAt");
    }

    [Fact]
    public async Task PublicList_SummaryCarriesUpdatedAt()
    {
        // The sitemap's lastmod and the feed's dates come from UpdatedAt, so the summaries the
        // public listing returns must carry it — distinguishably from CreatedAt and PublishedAt.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactoryAsync(s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");

        var token = Token();
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Timestamped {token}", "content"));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        clock.Advance(TimeSpan.FromHours(2));
        var update = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest($"Timestamped {token}", "edited content"));
        update.EnsureSuccessStatusCode();

        var result = await ListAsync(factory.CreateClient(), token);

        result.Items.Single(p => p.Slug == post.Slug).UpdatedAt
            .Should().Be(clock.GetUtcNow().UtcDateTime, "the summary carries UpdatedAt for the sitemap's lastmod");
    }

    [Fact]
    public async Task Editor_CannotUpdatePublishedPost()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Published", "content"));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var editor = factory.CreateClient().WithRole("Editor");
        var update = await editor.PutAsJsonAsync($"/api/posts/{post.Id}", new UpdatePostRequest("Updated", "content"));

        update.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreatePost_StoresAuthenticatedUserName()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Authored", "content"));
        response.EnsureSuccessStatusCode();
        var post = await response.Content.ReadFromJsonAsync<PostDto>();

        post!.AuthorName.Should().Be("Test User");
        post.AuthorId.Should().Be("test-user-id");
    }

    [Fact]
    public async Task CreatePost_WithTitleTooLong_ReturnsBadRequest()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var title = new string('a', 600);
        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "content"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePost_DedupesTagsBySlug()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Tagged Post",
            "content",
            Tags: ["C#", "C Sharp"]));

        response.EnsureSuccessStatusCode();
        var post = await response.Content.ReadFromJsonAsync<PostDto>();

        post!.Tags.Should().ContainSingle();
        post.Tags.Single().Slug.Should().Be("c-sharp");
    }

    [Fact]
    public async Task Get_ListsPostTagsSortedByName()
    {
        // A post's tags must come back by name (case-insensitive ordinal) on every fetch: the
        // join's row order is undefined and did drift between requests. Five fetches prove the
        // order is stable, not luck.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Tag Order Post",
            "content",
            Tags: ["zeta", "mid", "Alpha"]));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient();
        for (var fetch = 0; fetch < 5; fetch++)
        {
            var get = await anonymous.GetAsync($"/api/posts/{post.Slug}");
            get.EnsureSuccessStatusCode();
            var fetched = await get.Content.ReadFromJsonAsync<PostDto>();

            fetched!.Tags.Select(t => t.Name).Should().Equal("Alpha", "mid", "zeta");
        }
    }

    [Fact]
    public async Task PublicList_ListsPostTagsSortedByName()
    {
        // The same name order (case-insensitive ordinal) must hold in the listing summaries, on
        // every fetch: the projected tag list's SQL order is undefined and did drift between
        // requests. Five fetches prove the order is stable, not luck.
        var token = Token();
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            $"Tag Order List Post {token}",
            "content",
            Tags: ["zeta", "mid", "Alpha"]));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient();
        for (var fetch = 0; fetch < 5; fetch++)
        {
            var result = await ListAsync(anonymous, token);

            var summary = result.Items.Single(p => p.Slug == post.Slug);
            summary.Tags.Select(t => t.Name).Should().Equal("Alpha", "mid", "zeta");
        }
    }

    [Fact]
    public async Task CreatePost_SetsSummaryAndReadingTime()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Summary Post",
            "# Title\n\nHello *world*."));
        response.EnsureSuccessStatusCode();
        var post = await response.Content.ReadFromJsonAsync<PostDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var entity = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == post!.Id);

        entity.Summary.Should().Be("Hello world.");
        entity.ReadingMinutes.Should().Be(1);
    }

    [Fact]
    public async Task CreatePost_ReturnsSummaryInDto()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Summary Dto Post",
            "# Title\n\nHello *world*."));
        response.EnsureSuccessStatusCode();
        var post = await response.Content.ReadFromJsonAsync<PostDto>();

        // The Web app's meta-description fallback reads the stored summary through the
        // DTO, so the read model must carry it.
        post!.Summary.Should().Be("Hello world.");
    }

    [Fact]
    public async Task UpdatePost_RecomputesSummaryAndReadingTime()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Editable Post", "short start"));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();

        var updatedMarkdown = string.Join(' ', Enumerable.Repeat("word", 221));
        var update = await admin.PutAsJsonAsync($"/api/posts/{post!.Id}", new UpdatePostRequest("Editable Post", updatedMarkdown));
        update.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var entity = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == post.Id);

        entity.Summary.Should().Be(string.Join(' ', Enumerable.Repeat("word", 60)) + "…");
        entity.ReadingMinutes.Should().Be(2);
    }

    [Fact]
    public async Task List_ReturnsSummaries_WithoutMarkdown()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var token = Token();
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            $"Summary List Post {token}",
            "# Heading\n\nHello *world*.\n\nSecond paragraph."));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/posts?pageSize=100&q={token}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(p => p.GetProperty("slug").GetString() == post.Slug);

        item.TryGetProperty("markdownContent", out _).Should().BeFalse(
            "the list returns summaries and must never ship the markdown body");
        item.GetProperty("summary").GetString().Should().Be("Hello world.");
    }

    [Fact]
    public async Task List_AdminCounts_NullForAnonymous_SetForAdmin()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var token = Token();
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Counted Post {token}", "content"));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
            var entity = await db.Posts.SingleAsync(p => p.Id == post.Id);
            entity.Views = new PostViewCount { ViewCount = 42 };
            db.Feedback.Add(new Feedback
            {
                Id = Guid.NewGuid(),
                PostId = post.Id,
                AuthorId = "author",
                AuthorName = "Author",
                QuotedText = "q1",
                Comment = "unresolved one",
                CreatedAt = DateTime.UtcNow
            });
            db.Feedback.Add(new Feedback
            {
                Id = Guid.NewGuid(),
                PostId = post.Id,
                AuthorId = "author",
                AuthorName = "Author",
                QuotedText = "q2",
                Comment = "unresolved two",
                CreatedAt = DateTime.UtcNow
            });
            db.Feedback.Add(new Feedback
            {
                Id = Guid.NewGuid(),
                PostId = post.Id,
                AuthorId = "author",
                AuthorName = "Author",
                QuotedText = "q3",
                Comment = "resolved",
                CreatedAt = DateTime.UtcNow,
                ResolvedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var anonymous = factory.CreateClient();
        var anonymousResult = await ListAsync(anonymous, token);
        var anonymousItem = anonymousResult.Items.Single(p => p.Slug == post.Slug);
        anonymousItem.ViewCount.Should().BeNull("view and feedback counts are admin-only");
        anonymousItem.OpenFeedbackCount.Should().BeNull("view and feedback counts are admin-only");

        var adminResult = await ListAsync(admin, token);
        var adminItem = adminResult.Items.Single(p => p.Slug == post.Slug);
        adminItem.ViewCount.Should().Be(42);
        adminItem.OpenFeedbackCount.Should().Be(2, "resolved feedback must not count as open");
    }

    [Fact]
    public async Task Create_WithSubtitleAndCategoryId_RoundTrips()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var categoryName = $"Tech {Token()}";
        var categoryResponse = await admin.PostAsJsonAsync("/api/categories", categoryName);
        categoryResponse.EnsureSuccessStatusCode();
        var category = await categoryResponse.Content.ReadFromJsonAsync<CategoryDto>();

        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Subtitle Post",
            "First paragraph.",
            Subtitle: "A short subtitle",
            CategoryId: category!.Id));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();

        post!.Subtitle.Should().Be("A short subtitle");
        post.Category.Should().NotBeNull();
        post.Category!.Id.Should().Be(category.Id);
        post.Category.Name.Should().Be(categoryName);
        post.Category.PostCount.Should().Be(0, "the post is still a draft; PostCount counts published posts");

        var fetchedResponse = await admin.GetAsync($"/api/posts/{post.Slug}");
        fetchedResponse.EnsureSuccessStatusCode();
        var fetched = await fetchedResponse.Content.ReadFromJsonAsync<PostDto>();
        fetched!.Subtitle.Should().Be("A short subtitle");
        fetched.Category!.Id.Should().Be(category.Id);

        await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest());
        var publishedResponse = await admin.GetAsync($"/api/posts/{post.Slug}");
        publishedResponse.EnsureSuccessStatusCode();
        var published = await publishedResponse.Content.ReadFromJsonAsync<PostDto>();
        published!.Category!.PostCount.Should().Be(1, "PostCount counts the category's published posts");
    }

    [Fact]
    public async Task Create_SubtitleOver300_Returns400()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Long Subtitle",
            "content",
            Subtitle: new string('s', 301)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Subtitle");
    }

    [Fact]
    public async Task Create_ContentOver200000_Returns400NamesTheField_AndStoresNoRow()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07e Over Limit Create",
            new string('a', 200_001)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "content over the shared limit is a validation problem, like a too-long title");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Content", "the validation problem must name the field")
            .And.Contain("200,000", "the validation problem must state the limit");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        (await db.Posts.CountAsync(p => p.Title == "07e Over Limit Create")).Should().Be(0,
            "a rejected create stores nothing");
    }

    [Fact]
    public async Task Create_ContentAt200000_IsStored()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07e At Limit",
            new string('a', 200_000)));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "exactly at the limit is allowed");
        var post = await response.Content.ReadFromJsonAsync<PostDto>();
        post!.MarkdownContent.Length.Should().Be(200_000);
    }

    [Fact]
    public async Task Update_ContentOver200000_Returns400_AndLeavesTheRowUnchanged()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "07e Over Limit Update",
            "Original body."));
        create.EnsureSuccessStatusCode();
        var post = await create.Content.ReadFromJsonAsync<PostDto>();

        var response = await admin.PutAsJsonAsync($"/api/posts/{post!.Id}", new UpdatePostRequest(
            "07e Over Limit Update",
            new string('a', 200_001)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Content");

        var get = await admin.GetAsync($"/api/posts/{post.Id}");
        get.EnsureSuccessStatusCode();
        var unchanged = await get.Content.ReadFromJsonAsync<PostDto>();
        unchanged!.MarkdownContent.Should().Be("Original body.",
            "an update over the limit stores nothing and leaves the row unchanged");
    }

    [Fact]
    public async Task Create_UnknownCategoryId_Returns400()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(
            "Orphan Category",
            "content",
            CategoryId: Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown CategoryId must fail as a ValidationException");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Category");
    }

    [Fact]
    public async Task Unpublish_SetsDraft_ClearsPublishedAt_DisappearsFromPublicList()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var token = Token();
        var post = await CreatePublishedAsync(admin, $"Unpublish Me {token}", [], null, DateTime.UtcNow.AddHours(-1));
        (await ListAsync(factory.CreateClient(), token)).Items.Should().Contain(p => p.Slug == post.Slug,
            "the post is listed while it is published, so its absence below is the unpublish, not a page limit");

        var response = await admin.PostAsync($"/api/posts/{post.Id}/unpublish", null);
        response.EnsureSuccessStatusCode();
        var unpublished = await response.Content.ReadFromJsonAsync<PostDto>();

        unpublished!.Status.Should().Be(PostStatus.Draft);
        unpublished.PublishedAt.Should().BeNull();

        var list = await ListAsync(factory.CreateClient(), token);

        list.Items.Should().NotContain(p => p.Slug == post.Slug,
            "an unpublished post must disappear from the public list");
    }

    [Fact]
    public async Task Unpublish_RequiresAdmin()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedAsync(admin, "Stays Published", [], null, DateTime.UtcNow.AddHours(-1));

        var editor = factory.CreateClient().WithRole("Editor");
        var response = await editor.PostAsync($"/api/posts/{post.Id}/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anonymous = factory.CreateClient();
        var get = await anonymous.GetAsync($"/api/posts/{post.Slug}");
        get.EnsureSuccessStatusCode();
        var stillVisible = await get.Content.ReadFromJsonAsync<PostDto>();
        stillVisible!.Status.Should().Be(PostStatus.Published, "a rejected unpublish must leave the post published");
    }

    [Fact]
    public async Task Get_WithThePostId_ReturnsThePost()
    {
        // The admin editor routes by id (/admin/posts/{id}/edit), so the read endpoint must
        // resolve an id as well as a slug; the visibility rules stay the same.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("By Id", "body"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<PostDto>();

        var get = await admin.GetAsync($"/api/posts/{created!.Id}");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var post = await get.Content.ReadFromJsonAsync<PostDto>();
        post!.Id.Should().Be(created.Id);
        post.Slug.Should().Be(created.Slug);
    }

    [Fact]
    public async Task Get_WithThePostId_StillHidesDraftsFromAnonymousCallers()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Hidden By Id", "body"));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<PostDto>();

        var anonymous = factory.CreateClient();
        var get = await anonymous.GetAsync($"/api/posts/{created!.Id}");

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_WhenASlugCollidesWithAnotherPostsId_TheSlugWins()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Id Holder", "body"));
        create.EnsureSuccessStatusCode();
        var other = await create.Content.ReadFromJsonAsync<PostDto>();

        // A title whose slug is exactly the other post's id: the two lookups collide on the same
        // route value, and the slug must decide which post comes back.
        var colliding = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(other!.Id.ToString(), "body"));
        colliding.EnsureSuccessStatusCode();
        var slugPost = await colliding.Content.ReadFromJsonAsync<PostDto>();
        slugPost!.Slug.Should().Be(other.Id.ToString(), "the slug is generated from the title");

        var get = await admin.GetAsync($"/api/posts/{other.Id}");

        get.EnsureSuccessStatusCode();
        var post = await get.Content.ReadFromJsonAsync<PostDto>();
        post!.Id.Should().Be(slugPost.Id, "a route value that is a live slug resolves to that slug's post");
    }

    /// <summary>A word no other test's posts contain: put it in the titles of the posts a test creates.</summary>
    private static string Token() => $"t{Guid.NewGuid():N}";

    /// <summary>
    /// The posts the caller sees whose text matches <paramref name="token"/> (one page of up to 100): the
    /// class's database accumulates every test's posts, so an unscoped list is order-dependent.
    /// </summary>
    private static async Task<PagedResult<PostSummaryDto>> ListAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/posts?pageSize=100&q={token}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>())!;
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

    private BlogApiFactory CreateFactoryAsync(Action<IServiceCollection>? configureServices = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath, configureServices: configureServices);
    }
}
