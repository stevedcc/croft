using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Services;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14b item 1: the Web's staff write paths evict the <c>content</c> tag after the API call
/// succeeds, so the pod that handled the write shows it at once; other pods catch up when their
/// entries expire.
/// </summary>
public class OutputCacheEvictionTests
{
    [Fact]
    public async Task AStaffUpdate_EvictsThisPodsEntries_SoTheNextReaderSeesTheNewTitle()
    {
        using var host = CacheHost.Create();
        using var client = host.Client();
        var path = $"/post/{CacheHost.PostSlug}";
        using (var first = await client.GetAsync(path))
        {
            (await first.Content.ReadAsStringAsync()).Should().Contain("Cache Post");
        }

        var detail = host.Api.PostDetails.Single(p => p.Slug == CacheHost.PostSlug);
        host.Api.PostDetails[host.Api.PostDetails.IndexOf(detail)] = detail with { Title = "A Renamed Post" };
        var staff = StaffApiClient.Create(host.Api, host.Services.GetRequiredService<IOutputCacheStore>());
        await staff.UpdatePostAsync(detail.Id, new UpdatePostRequest("A Renamed Post", "## A short body"));

        using var after = await client.GetAsync(path);

        (await after.Content.ReadAsStringAsync()).Should().Contain("A Renamed Post",
            "the Web pod that handled the write shows it at once");
        host.Store.Evictions.Should().Equal("content");
    }

    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    public static TheoryData<string> Writes =>
    [
        nameof(IBlogApiClient.CreatePostAsync),
        nameof(IBlogApiClient.UpdatePostAsync),
        nameof(IBlogApiClient.PublishPostAsync),
        nameof(IBlogApiClient.UnpublishPostAsync),
        nameof(IBlogApiClient.DeletePostAsync),
        nameof(IBlogApiClient.CreateTagAsync),
        nameof(IBlogApiClient.UpdateTagAsync),
        nameof(IBlogApiClient.DeleteTagAsync),
        nameof(IBlogApiClient.CreateCategoryAsync),
        nameof(IBlogApiClient.UpdateCategoryAsync),
        nameof(IBlogApiClient.DeleteCategoryAsync),
        nameof(IBlogApiClient.UpdateMediaAsync),
        nameof(IBlogApiClient.DeleteMediaAsync),
    ];

    private static Task CallAsync(IBlogApiClient client, string write) => write switch
    {
        nameof(IBlogApiClient.CreatePostAsync) => client.CreatePostAsync(new CreatePostRequest("T", "body")),
        nameof(IBlogApiClient.UpdatePostAsync) => client.UpdatePostAsync(Id, new UpdatePostRequest("T", "body")),
        nameof(IBlogApiClient.PublishPostAsync) => client.PublishPostAsync(Id, new PublishRequest()),
        nameof(IBlogApiClient.UnpublishPostAsync) => client.UnpublishPostAsync(Id),
        nameof(IBlogApiClient.DeletePostAsync) => client.DeletePostAsync(Id),
        nameof(IBlogApiClient.CreateTagAsync) => client.CreateTagAsync("Tag"),
        nameof(IBlogApiClient.UpdateTagAsync) => client.UpdateTagAsync(Id, new UpdateTagRequest("Tag")),
        nameof(IBlogApiClient.DeleteTagAsync) => client.DeleteTagAsync(Id),
        nameof(IBlogApiClient.CreateCategoryAsync) => client.CreateCategoryAsync("Category"),
        nameof(IBlogApiClient.UpdateCategoryAsync) => client.UpdateCategoryAsync(Id, new UpdateCategoryRequest("Category", null)),
        nameof(IBlogApiClient.DeleteCategoryAsync) => client.DeleteCategoryAsync(Id),
        nameof(IBlogApiClient.UpdateMediaAsync) => client.UpdateMediaAsync(Id, new UpdateMediaRequest("alt")),
        nameof(IBlogApiClient.DeleteMediaAsync) => client.DeleteMediaAsync(Id),
        _ => throw new ArgumentOutOfRangeException(nameof(write), write, null),
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task EveryStaffWrite_EvictsTheContentTag_AfterTheApiAnswersSuccess(string write)
    {
        var store = new RecordingStore(TimeProvider.System);
        var api = new WriteApi(HttpStatusCode.OK);
        var client = StaffApiClient.Create(api, store);

        await CallAsync(client, write);

        api.Requests.Should().ContainSingle("one write is one API call");
        store.Evictions.Should().Equal("content");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AFailedStaffWrite_EvictsNothing(string write)
    {
        var store = new RecordingStore(TimeProvider.System);
        var api = new WriteApi(HttpStatusCode.InternalServerError);
        var client = StaffApiClient.Create(api, store);

        var call = () => CallAsync(client, write);

        await call.Should().ThrowAsync<Exception>();
        store.Evictions.Should().BeEmpty("nothing changed, so nothing is dropped");
    }

    [Fact]
    public async Task UploadingAFile_EvictsNothing_ItChangesNoPublishedPage()
    {
        var store = new RecordingStore(TimeProvider.System);
        var client = StaffApiClient.Create(new WriteApi(HttpStatusCode.OK), store);

        await client.UploadMediaAsync(new MemoryStream([1, 2, 3]), "a.png", "image/png");

        store.Evictions.Should().BeEmpty();
    }

    /// <summary>The API answering every write with one status and a body of the shape the call reads.</summary>
    private sealed class WriteApi(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var response = new HttpResponseMessage(status);
            if (status != HttpStatusCode.OK)
            {
                return Task.FromResult(response);
            }

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                response.StatusCode = HttpStatusCode.NoContent;
            }
            else if (path.StartsWith("/api/posts", StringComparison.Ordinal))
            {
                response.Content = JsonContent.Create(new PostDto(Id, "slug", "T", null, "body", null, null,
                    PostStatus.Draft, null, DateTime.UtcNow, DateTime.UtcNow, null, "A", null, [], null, 1));
            }
            else if (path.StartsWith("/api/tags", StringComparison.Ordinal))
            {
                response.Content = JsonContent.Create(new TagDto(Id, "Tag", "tag", 0, true));
            }
            else if (path.StartsWith("/api/categories", StringComparison.Ordinal))
            {
                response.Content = JsonContent.Create(new CategoryDto(Id, "Category", "category", null, 0));
            }
            else
            {
                response.Content = JsonContent.Create(new MediaAssetDto(Id, "a.png", "a.png", "image/png",
                    "/api/media/" + Id, MediaAssetScope.Global, null, null));
            }

            return Task.FromResult(response);
        }
    }
}
