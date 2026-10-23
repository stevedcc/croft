using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

public class MediaControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Upload_GlobalAsset_IsPubliclyAccessible()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "test.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        var anonymous = factory.CreateClient().AsAnonymous();
        var download = await anonymous.GetAsync(asset!.Url);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upload_PostSpecificDraftAsset_RequiresEditorOrAdmin()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();

        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "secret.png" },
            { new StringContent(post!.Id.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        };
        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        // No user at all, and a signed-in user with no role (a different request: CreateClient() alone is
        // authenticated, with no role): both are refused a draft's image.
        var anonymous = factory.CreateClient().AsAnonymous();
        var anonymousDownload = await anonymous.GetAsync(asset!.Url);
        anonymousDownload.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var noRole = factory.CreateClient();
        var noRoleDownload = await noRole.GetAsync(asset.Url);
        noRoleDownload.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var editor = factory.CreateClient().WithRole("Editor");
        var editorDownload = await editor.GetAsync(asset.Url);
        editorDownload.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upload_MaliciousFilename_IsSanitized()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "../../../etc/passwd.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        asset!.OriginalName.Should().Be("../../../etc/passwd.png");
        asset.FileName.Should().NotContain("..");

        // The DTO alone cannot show it (storage names files by GUID, so the DTO can never carry ".."
        // whatever the storage did): the outcome is where the bytes went. The stored path stays under
        // the storage root and exactly one file exists there, none of it written outside.
        var storagePath = GetStoragePath(factory);
        var row = await GetMediaAssetFromDb(factory, asset.Id);
        var stored = Path.GetFullPath(Path.Combine(storagePath, row!.FilePath));
        stored.Should().StartWith(Path.GetFullPath(storagePath) + Path.DirectorySeparatorChar,
            "the file landed under the storage root, not where the file name pointed");
        File.Exists(stored).Should().BeTrue();
        Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories).Should().ContainSingle()
            .Which.Should().Be(stored, "one upload wrote exactly one file");
    }

    [Fact]
    public async Task Upload_SvgFile_IsRejected()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var content = new MultipartFormDataContent
        {
            { new StringContent("<svg></svg>"), "file", "image.svg" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_FileWithMismatchedMagicBytes_IsRejected()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var content = new MultipartFormDataContent
        {
            { new StringContent("not a real png"), "file", "fake.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_GlobalAsset_SetsInlineDispositionAndCacheControl()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "test.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        var anonymous = factory.CreateClient().AsAnonymous();
        var download = await anonymous.GetAsync(asset!.Url);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition.Should().BeNull();
        download.Headers.CacheControl.Should().NotBeNull();
        download.Headers.CacheControl!.Public.Should().BeTrue();
        download.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        download.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle(v => v == "nosniff");
    }

    [Fact]
    public async Task Get_DraftPostSpecificAsset_SetsNoStoreCacheControl()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();

        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "secret.png" },
            { new StringContent(post!.Id.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        };
        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        var download = await admin.GetAsync(asset!.Url);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Headers.CacheControl.Should().NotBeNull();
        download.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Get_PostSpecificAsset_ReadsNoMarkdownOrSearchVector_AndKeepsTheAccessRules()
    {
        var recorder = new RecordingCommandInterceptor();
        await using var factory = new BlogApiFactory(fixture.ConnectionString, NewStoragePath(), commandInterceptor: recorder);
        var admin = factory.CreateClient().WithRole("Admin");

        var post = await (await admin.PostAsJsonAsync("/api/posts",
            new CreatePostRequest("Image owner", new string('x', 5_000)))).Content.ReadFromJsonAsync<PostDto>();
        var upload = await admin.PostAsync("/api/media", new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "asset.png" },
            { new StringContent(post!.Id.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        });
        upload.EnsureSuccessStatusCode();
        var asset = (await upload.Content.ReadFromJsonAsync<MediaAssetDto>())!;
        var anonymous = factory.CreateClient().AsAnonymous();

        recorder.Clear();
        var whileDraft = await anonymous.GetAsync(asset.Url);
        var draftSql = recorder.Commands;
        whileDraft.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a draft's image still needs staff");

        await admin.PostAsJsonAsync($"/api/posts/{post.Id}/publish", new PublishRequest());
        recorder.Clear();
        var whilePublished = await anonymous.GetAsync(asset.Url);
        var publishedSql = recorder.Commands;
        whilePublished.StatusCode.Should().Be(HttpStatusCode.OK, "a published post's image is public");
        whilePublished.Headers.CacheControl!.Public.Should().BeTrue();

        foreach (var sql in draftSql.Concat(publishedSql))
        {
            sql.Should().NotContain("MarkdownContent", "authorising an image needs the owning post's status, not its text");
            sql.Should().NotContain("SearchVector");
        }

        publishedSql.Should().Contain(sql => sql.Contains("\"Status\""), "the status is what is read");
    }

    [Fact]
    public async Task Upload_FileExceeding10MbLimit_IsRejected()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var pngHeader = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var oversized = new byte[11 * 1024 * 1024]; // 11 MB
        Array.Copy(pngHeader, oversized, pngHeader.Length);

        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(oversized) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "file", "big.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        // RequestFormLimits(MultipartBodyLengthLimit) causes the form reader to reject
        // oversized multipart bodies with 400 Bad Request.
        upload.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_1MbPng_Succeeds()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var pngHeader = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var oneMb = new byte[1024 * 1024];
        Array.Copy(pngHeader, oneMb, pngHeader.Length);

        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(oneMb) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "file", "test.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();
        asset.Should().NotBeNull();
        asset!.Url.Should().StartWith("/api/media/");
    }

    [Fact]
    public async Task DeletePost_WithPostSpecificAsset_RemovesFileFromStorage()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        // Create a post
        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("To Be Deleted", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();

        // Upload a post-specific asset
        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "asset.png" },
            { new StringContent(post!.Id.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        };
        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = await upload.Content.ReadFromJsonAsync<MediaAssetDto>();

        // Resolve the file path on disk
        var assetDb = await GetMediaAssetFromDb(factory, asset!.Id);
        var storagePath = GetStoragePath(factory);
        var filePath = Path.Combine(storagePath, assetDb!.FilePath);
        File.Exists(filePath).Should().BeTrue("file should exist after upload");

        // Delete the post
        var delete = await admin.DeleteAsync($"/api/posts/{post.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // File should be removed from storage
        File.Exists(filePath).Should().BeFalse("file should be removed when post is deleted");
    }

    [Fact]
    public async Task Download_WhenTheStoredFileIsMissingFromDisk_IsNotFound_AndLogsOneWarning_NothingAbove()
    {
        var logs = new LogCollector();
        var storagePath = NewStoragePath();
        await using var factory = new BlogApiFactory(fixture.ConnectionString, storagePath,
            configureServices: services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
        var admin = factory.CreateClient().WithRole("Admin");
        var content = new MultipartFormDataContent { { CreatePngContent(), "file", "vanishing.png" }, { new StringContent("global"), "scope" } };
        var upload = await admin.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        var asset = (await upload.Content.ReadFromJsonAsync<MediaAssetDto>())!;
        var row = await GetMediaAssetFromDb(factory, asset.Id);
        File.Delete(Path.Combine(storagePath, row!.FilePath));
        logs.Clear();

        var download = await factory.CreateClient().AsAnonymous().GetAsync(asset.Url);

        download.StatusCode.Should().Be(HttpStatusCode.NotFound, "a row whose file is gone is a missing image, not a server fault");
        logs.Entries.Where(e => e.Level > Microsoft.Extensions.Logging.LogLevel.Warning).Should().BeEmpty(
            "an image that is not there is not an error worth paging anyone for");
        logs.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Should().ContainSingle(
            "an operator should notice a row without its file, once").Which.Message
            .Should().Contain(asset.Id.ToString()).And.Contain(row.FilePath);
    }

    [Fact]
    public async Task Download_AnonymousRequestForADraftsAsset_WhoseFileIsMissing_IsForbidden_NotNotFound()
    {
        var storagePath = NewStoragePath();
        await using var factory = new BlogApiFactory(fixture.ConnectionString, storagePath);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await (await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft owner", "content")))
            .Content.ReadFromJsonAsync<PostDto>();
        var upload = await admin.PostAsync("/api/media", new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "draft-gone.png" },
            { new StringContent(post!.Id.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        });
        upload.EnsureSuccessStatusCode();
        var asset = (await upload.Content.ReadFromJsonAsync<MediaAssetDto>())!;
        var row = await GetMediaAssetFromDb(factory, asset.Id);
        File.Delete(Path.Combine(storagePath, row!.FilePath));

        var download = await factory.CreateClient().AsAnonymous().GetAsync(asset.Url);

        download.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the access rules come first: whether the file exists is not revealed to someone who may not see the asset");
    }

    [Fact]
    public async Task Upload_DbSaveFails_LeavesNoFile()
    {
        // Create a factory with a BlogDbContext that fails on SaveChangesAsync
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);

        // Both factories are disposed: left running, their hosted services keep working against the class
        // database while the class's later tests run.
        await using var factory = new BlogApiFactory(fixture.ConnectionString, storagePath);
        var seededSlug = $"test-post-{Guid.NewGuid():N}";

        // Seed a post directly so it exists in the class database
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Croft.Api.Data.BlogDbContext>();
            db.Posts.Add(new Croft.Api.Models.Post
            {
                Id = Guid.NewGuid(),
                Slug = seededSlug,
                Title = "Test Post",
                MarkdownContent = "content",
                Status = Croft.Shared.Enums.PostStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AuthorId = "test-user-id",
                AuthorName = "Test User"
            });
            await db.SaveChangesAsync();
        }

        // Now use a factory with a context that always fails on SaveChangesAsync
        await using var failingFactory = new FailingBlogApiFactory(fixture.ConnectionString, storagePath);
        var admin = failingFactory.CreateClient().WithRole("Admin");

        // Get the seeded post's ID (by its slug: the class database also holds the
        // other tests' posts, and this test must upload against its own)
        Guid postId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Croft.Api.Data.BlogDbContext>();
            postId = await db.Posts.Where(p => p.Slug == seededSlug).Select(p => p.Id).SingleAsync();
        }

        // Attempt upload — should fail because SaveChanges throws
        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "willfail.png" },
            { new StringContent(postId.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        };
        var upload = await admin.PostAsync("/api/media", content);
        upload.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // No file should remain in storage
        Directory.GetFiles(storagePath, "*", SearchOption.AllDirectories).Should().BeEmpty(
            "no file should remain after a failed upload");
    }

    [Fact]
    public async Task UpdateMedia_SetsAltText()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var asset = await UploadPngAsync(admin, "alt.png");

        var update = await admin.PutAsJsonAsync($"/api/media/{asset.Id}", new UpdateMediaRequest("A red bicycle"));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<MediaAssetDto>();
        updated!.AltText.Should().Be("A red bicycle");

        var list = await admin.GetFromJsonAsync<List<MediaAssetDto>>("/api/media");
        list!.Single(m => m.Id == asset.Id).AltText.Should().Be("A red bicycle", "the alt text must be persisted");
    }

    [Fact]
    public async Task UpdateMedia_AltTextOver300_Returns400()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var asset = await UploadPngAsync(admin, "alt.png");

        var update = await admin.PutAsJsonAsync($"/api/media/{asset.Id}", new UpdateMediaRequest(new string('a', 301)));

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Alt text");
    }

    [Fact]
    public async Task DeleteMedia_RemovesRowAndFile()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var asset = await UploadPngAsync(admin, "gone.png");
        var assetDb = await GetMediaAssetFromDb(factory, asset.Id);
        var filePath = Path.Combine(GetStoragePath(factory), assetDb!.FilePath);
        File.Exists(filePath).Should().BeTrue("file should exist after upload");

        var delete = await admin.DeleteAsync($"/api/media/{asset.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        File.Exists(filePath).Should().BeFalse("file should be removed from storage");
        var fetch = await admin.GetAsync($"/api/media/{asset.Id}");
        fetch.StatusCode.Should().Be(HttpStatusCode.NotFound, "the row should be gone too");
    }

    [Fact]
    public async Task DeleteMedia_UsedAsBanner_Returns409_WithPostTitles()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var asset = await UploadPngAsync(admin, "banner.png");
        foreach (var title in new[] { "Banner Post One", "Banner Post Two" })
        {
            var create = await admin.PostAsJsonAsync("/api/posts",
                new CreatePostRequest(title, "content", BannerImageId: asset.Id));
            create.EnsureSuccessStatusCode();
        }

        var delete = await admin.DeleteAsync($"/api/media/{asset.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await delete.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("Banner Post One").And.Contain("Banner Post Two");

        var list = await admin.GetFromJsonAsync<List<MediaAssetDto>>("/api/media");
        list.Should().ContainSingle(m => m.Id == asset.Id, "the asset must survive a refused delete");
    }

    [Fact]
    public async Task DeleteMedia_FileDeleteFailsWithIoError_StillRemovesRow()
    {
        await using var factory = CreateFactoryWithFailingFileDeleteAsync(new IOException("simulated disk failure"));
        var admin = factory.CreateClient().WithRole("Admin");

        var asset = await UploadPngAsync(admin, "gone.png");

        var delete = await admin.DeleteAsync($"/api/media/{asset.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "an IO error while deleting the file must not fail the delete the user asked for");
        var fetch = await admin.GetAsync($"/api/media/{asset.Id}");
        fetch.StatusCode.Should().Be(HttpStatusCode.NotFound, "the row should be gone too");
    }

    [Fact]
    public async Task DeleteMedia_FileDeleteThrowsUnexpectedly_Propagates()
    {
        await using var factory = CreateFactoryWithFailingFileDeleteAsync(
            new InvalidOperationException("path outside the storage root"));
        var admin = factory.CreateClient().WithRole("Admin");

        var asset = await UploadPngAsync(admin, "gone.png");

        var delete = await admin.DeleteAsync($"/api/media/{asset.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "an exception file deletion cannot actually throw must surface, not be swallowed");
    }

    [Fact]
    public async Task DeletePost_FileDeleteFailsWithIoError_StillDeletesPost()
    {
        await using var factory = CreateFactoryWithFailingFileDeleteAsync(new IOException("simulated disk failure"));
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("To Be Deleted", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        await UploadPostSpecificAssetAsync(admin, post!.Id);

        var delete = await admin.DeleteAsync($"/api/posts/{post.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "an IO error while deleting a media file must not fail the post delete the user asked for");
        var secondDelete = await admin.DeleteAsync($"/api/posts/{post.Id}");
        secondDelete.StatusCode.Should().Be(HttpStatusCode.NotFound, "the post should be gone");
    }

    [Fact]
    public async Task DeletePost_FileDeleteThrowsUnexpectedly_Propagates()
    {
        await using var factory = CreateFactoryWithFailingFileDeleteAsync(
            new InvalidOperationException("path outside the storage root"));
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("To Be Deleted", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        await UploadPostSpecificAssetAsync(admin, post!.Id);

        var delete = await admin.DeleteAsync($"/api/posts/{post.Id}");

        delete.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "an exception file deletion cannot actually throw must surface, not be swallowed");
    }

    [Fact]
    public async Task ListMedia_WithPagingParameters_ReturnsAPagedResultNewestFirst()
    {
        // The class database is shared by this class's tests (03h item 2), so the page is scoped
        // to this test's own uploads through the q filter; the exact counts stay exact.
        var token = $"med{Guid.NewGuid():N}";
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        foreach (var name in new[] { "first", "second", "third" })
        {
            await UploadPngAsync(admin, $"{token}-{name}.png");
        }

        var page = await admin.GetFromJsonAsync<PagedResult<MediaAssetDto>>($"/api/media?q={token}&page=1&pageSize=2");

        page!.TotalCount.Should().Be(3);
        page.Items.Should().HaveCount(2, "the page is bounded, never the whole table");
        string[] newestFirst = [$"{token}-third.png", $"{token}-second.png"];
        page.Items.Select(m => m.OriginalName).Should().Equal(newestFirst,
            "the listing is newest first");
    }

    [Fact]
    public async Task ListMedia_WithQ_ReturnsOnlyTheMatchingFileNames()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        await UploadPngAsync(admin, "holiday-sunset.png");
        await UploadPngAsync(admin, "contract-scan.png");

        var filtered = await admin.GetFromJsonAsync<PagedResult<MediaAssetDto>>("/api/media?q=sunset");

        filtered!.TotalCount.Should().Be(1);
        filtered.Items.Should().ContainSingle().Which.OriginalName.Should().Be("holiday-sunset.png",
            "the filename filter is a case-insensitive substring");
    }

    [Fact]
    public async Task ListPostMedia_WithPagingParameters_ReturnsAPagedResultNewestFirst()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Media Post", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        foreach (var name in new[] { "old.png", "new.png" })
        {
            var content = new MultipartFormDataContent
            {
                { CreatePngContent(), "file", name },
                { new StringContent(post!.Id.ToString()), "postId" },
                { new StringContent("PostSpecific"), "scope" }
            };
            var upload = await admin.PostAsync("/api/media", content);
            upload.EnsureSuccessStatusCode();
        }

        var page = await admin.GetFromJsonAsync<PagedResult<MediaAssetDto>>($"/api/posts/{post!.Id}/media?page=1&pageSize=1");

        page!.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle().Which.OriginalName.Should().Be("new.png",
            "the post's media pages newest first like the global library");
    }

    private static async Task<MediaAssetDto> UploadPngAsync(HttpClient client, string fileName)
    {
        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", fileName },
            { new StringContent("global"), "scope" }
        };

        var upload = await client.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<MediaAssetDto>())!;
    }

    private static async Task UploadPostSpecificAssetAsync(HttpClient client, Guid postId)
    {
        var content = new MultipartFormDataContent
        {
            { CreatePngContent(), "file", "asset.png" },
            { new StringContent(postId.ToString()), "postId" },
            { new StringContent("PostSpecific"), "scope" }
        };

        var upload = await client.PostAsync("/api/media", content);
        upload.EnsureSuccessStatusCode();
    }

    private static async Task<Croft.Api.Models.MediaAsset?> GetMediaAssetFromDb(BlogApiFactory factory, Guid assetId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Croft.Api.Data.BlogDbContext>();
        return await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == assetId);
    }

    private static string GetStoragePath(BlogApiFactory factory)
    {
        // The factory feeds the app its storage root through configuration, so read it back from
        // there rather than reaching into LocalFileStorage's internals.
        return factory.Services.GetRequiredService<IConfiguration>()["FileStorage:BasePath"]!;
    }

    private sealed class FailingBlogApiFactory(string connectionString, string storagePath) : BlogApiFactory(connectionString, storagePath)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // Remove the real BlogDbContext and replace with one that throws
                var descriptors = services.Where(
                    d => d.ServiceType == typeof(Croft.Api.Data.BlogDbContext)).ToList();
                foreach (var d in descriptors)
                    services.Remove(d);

                services.AddScoped<Croft.Api.Data.BlogDbContext, FailingBlogDbContext>();
            });
        }
    }

    // A BlogDbContext subclass that always throws on SaveChangesAsync
    private sealed class FailingBlogDbContext(DbContextOptions<Croft.Api.Data.BlogDbContext> options) : Croft.Api.Data.BlogDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("Simulated database failure",
                new InvalidOperationException("Test constraint violation"));
        }
    }

    private static ByteArrayContent CreatePngContent()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private static string NewStoragePath()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return storagePath;
    }

    /// <summary>Records the text of every query EF sends.</summary>
    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly Lock _gate = new();
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_gate)
                {
                    return [.. _commands];
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _commands.Clear();
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _commands.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private BlogApiFactory CreateFactoryAsync()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath);
    }

    private BlogApiFactory CreateFactoryWithFailingFileDeleteAsync(Exception deleteException)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath, configureServices: services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(new ThrowingDeleteFileStorage(new LocalFileStorage(storagePath), deleteException));
        });
    }

    // Storage that saves and reads normally but fails every delete, so tests can pin which
    // delete failures are handled and which surface to the caller.
    private sealed class ThrowingDeleteFileStorage(IFileStorage inner, Exception deleteException) : IFileStorage
    {
        public Task<string> SaveAsync(Stream data, string fileName, string contentType, string storagePath, CancellationToken ct = default) =>
            inner.SaveAsync(data, fileName, contentType, storagePath, ct);

        public Stream OpenRead(string relativePath) => inner.OpenRead(relativePath);

        public void Delete(string relativePath) => throw deleteException;
    }
}
