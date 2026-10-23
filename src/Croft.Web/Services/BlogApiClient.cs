using System.Net.Http.Headers;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.WebUtilities;
using Croft.Web.Caching;

namespace Croft.Web.Services;

public class BlogApiClient(
    IHttpClientFactory httpClientFactory,
    AuthenticationStateProvider authenticationStateProvider,
    IUserAccessTokenService userAccessTokenService,
    NavigationManager navigationManager,
    SessionExpiryHooks sessionExpiryHooks,
    IOutputCacheStore outputCache) : IBlogApiClient
{
    /// <summary>
    /// After a staff write the API accepted: drops this pod's cached reading pages (brief 14b), so a
    /// reader of this pod sees the change at once. Other pods' entries expire within the cache
    /// duration. Not tied to the caller's token: the write has happened, so the eviction must too.
    /// </summary>
    private ValueTask EvictReadingPagesAsync() =>
        outputCache.EvictByTagAsync(ReadingPageCachePolicy.ContentTag, CancellationToken.None);

    // WORKAROUND(W-043, Blazor): inside a circuit no HTTP request runs cookie validation, so a 401 is the only sign the session is gone
    /// <summary>
    /// Every API call routes through here. It creates the client with the bearer token attached and
    /// turns a 401 for an authenticated user into an expired-session redirect: inside an interactive
    /// circuit no new HTTP request happens, so the cookie OnValidatePrincipal check never runs and a
    /// 401 is the only sign the session is gone. Anonymous 401s (and other status codes) fall
    /// through untouched.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<ApiCaller, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("BlogApi");
        var authState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var token = await userAccessTokenService.GetAccessTokenAsync(authState.User, ct);
        var authenticated = authState.User.Identity?.IsAuthenticated == true;
        if (authenticated && string.IsNullOrEmpty(token))
        {
            // The circuit still holds the cookie's principal, but the session behind it is gone
            // (purged, or lost with a restart): there is no token to send. An anonymous request
            // could be answered with a 404 for a draft and hide that, so the ended session is
            // detected here, before anything is sent.
            await EndSessionAsync();
        }

        // The token goes on each request, never on the client's default headers: a client that
        // were ever cached and shared would otherwise send one user's token for the next call.
        var caller = new ApiCaller(client, token);
        HttpResponseMessage response;
        try
        {
            response = await send(caller);
        }
        catch (Exception ex) when (ApiUnavailableException.IsResilienceFailure(ex))
        {
            // The resilience handler gave up (timeout, open breaker): one exception type for
            // "the API is not answering", which every page that handles a failed call already catches.
            throw new ApiUnavailableException("The API did not answer in time.", ex);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && authenticated)
        {
            response.Dispose();
            await EndSessionAsync();
        }

        return response;
    }

    /// <summary>
    /// The verbs the API client uses, each sent as its own <see cref="HttpRequestMessage"/> carrying
    /// this call's bearer token (when there is one). The call sites read like the
    /// <see cref="HttpClient"/> extension methods they replace.
    /// </summary>
    private sealed class ApiCaller(HttpClient client, string? token)
    {
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct) =>
            SendAsync(HttpMethod.Get, url, null, ct);

        public Task<HttpResponseMessage> DeleteAsync(string url, CancellationToken ct) =>
            SendAsync(HttpMethod.Delete, url, null, ct);

        public Task<HttpResponseMessage> PostAsync(string url, HttpContent? content, CancellationToken ct) =>
            SendAsync(HttpMethod.Post, url, content, ct);

        public Task<HttpResponseMessage> PostAsJsonAsync<T>(string url, T value, CancellationToken ct) =>
            SendAsync(HttpMethod.Post, url, JsonContent.Create(value), ct);

        public Task<HttpResponseMessage> PutAsJsonAsync<T>(string url, T value, CancellationToken ct) =>
            SendAsync(HttpMethod.Put, url, JsonContent.Create(value), ct);

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await client.SendAsync(request, ct);
        }
    }

    /// <summary>
    /// Pages back up unsaved work first, while the browser is still on them; then the browser
    /// goes to sign in and the caller stops cleanly with <see cref="SessionExpiredException"/>.
    /// </summary>
    private async Task EndSessionAsync()
    {
        var keptWork = await sessionExpiryHooks.RunAsync();
        NavigateToLogin(keptWork);
        throw new SessionExpiredException();
    }
    // END WORKAROUND(W-043)

    /// <summary>
    /// The query parameter that tells the editor it was reached by signing in again after its
    /// session ended with work kept in the draft backup: the backup is restored without asking.
    /// The editor removes it from the address bar after use.
    /// </summary>
    public const string SignedBackInParameter = "signedBackIn";

    private void NavigateToLogin(bool keptWork)
    {
        var returnUrl = "/" + navigationManager.ToBaseRelativePath(navigationManager.Uri);
        if (keptWork && LocalUrl.IsLocal(returnUrl))
        {
            returnUrl = QueryHelpers.AddQueryString(returnUrl, SignedBackInParameter, "1");
        }

        var target = LocalUrl.IsLocal(returnUrl)
            ? $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : "/login";
        navigationManager.NavigateTo(target, forceLoad: true);
    }

    public async Task<PagedResult<PostSummaryDto>> GetPostsAsync(PostListRequest request, CancellationToken ct = default)
    {
        var url = PublicReadRoutes.Posts(request);
        using var response = await SendAsync(http => http.GetAsync(url, ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>(ct);
        return result ?? new PagedResult<PostSummaryDto>([], 0, request.Page, request.PageSize);
    }

    public async Task<PostDto?> GetPostAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Post(slug), ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PostDto>(ct);
    }

    public async Task<PostDiscussionDto?> GetDiscussionAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Discussion(slug), ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PostDiscussionDto>(ct);
    }

    public async Task<PagedResult<TagDto>> GetTagsAsync(int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Tags(page, pageSize, q), ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<TagDto>>(ct);
        return result ?? new PagedResult<TagDto>([], 0, page, pageSize);
    }

    public async Task<IReadOnlyList<TagDto>> GetTagsByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0)
            return [];

        var query = string.Join('&', names.Select(name => $"names={Uri.EscapeDataString(name)}"));
        using var response = await SendAsync(http => http.GetAsync($"/api/tags?page=1&pageSize={Math.Max(names.Count, 1)}&{query}", ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<TagDto>>(ct);
        return result?.Items ?? [];
    }

    public async Task<TagDto?> GetTagBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Tag(slug), ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TagDto>(ct);
    }

    public async Task<PagedResult<CategoryDto>> GetCategoriesAsync(int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Categories(page, pageSize, q), ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<CategoryDto>>(ct);
        return result ?? new PagedResult<CategoryDto>([], 0, page, pageSize);
    }

    public async Task<CategoryDto?> GetCategoryBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Category(slug), ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CategoryDto>(ct);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetTopCategoriesAsync(int count, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.TopCategories(count), ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<CategoryDto>>(ct);
        return result ?? [];
    }

    /// <summary>The staff media lists' query shape: the paged contract the public lists share.</summary>
    private static string ListUrl(string path, int page, int pageSize, string? q) =>
        PublicReadRoutes.PagedList(path, page, pageSize, q);

    public async Task<PagedResult<PostSummaryDto>> SearchAsync(string query, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        var url = PublicReadRoutes.Search(query, page, pageSize);
        using var response = await SendAsync(http => http.GetAsync(url, ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>(ct);
        return result ?? new PagedResult<PostSummaryDto>([], 0, page, pageSize);
    }

    public async Task<IReadOnlyList<PostSummaryDto>> GetRelatedPostsAsync(string slug, int count = 3, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Related(slug, count), ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<PostSummaryDto>>(ct);
        return result ?? [];
    }

    public async Task<PostDto?> GetPageAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.Page(slug), ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PostDto>(ct);
    }

    public async Task<PageNavigationDto> GetPageNavigationsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(PublicReadRoutes.NavigationAll(), ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PageNavigationDto>(ct);
        return result ?? new PageNavigationDto([], []);
    }

    public async Task<PostDto> CreatePostAsync(CreatePostRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsJsonAsync("/api/posts", request, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<PostDto>(ct))!;
    }

    public async Task<PostDto> UpdatePostAsync(Guid id, UpdatePostRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PutAsJsonAsync($"/api/posts/{id}", request, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<PostDto>(ct))!;
    }

    public async Task<PostDto?> GetPostByIdAsync(Guid id, CancellationToken ct = default)
    {
        // The same read endpoint as GetPostAsync: it resolves an id as well as a slug.
        using var response = await SendAsync(http => http.GetAsync($"/api/posts/{id}", ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PostDto>(ct);
    }

    public async Task<PostDto> PublishPostAsync(Guid id, PublishRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsJsonAsync($"/api/posts/{id}/publish", request, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<PostDto>(ct))!;
    }

    public async Task<PostDto> UnpublishPostAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsync($"/api/posts/{id}/unpublish", null, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<PostDto>(ct))!;
    }

    public async Task DeletePostAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.DeleteAsync($"/api/posts/{id}", ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
    }

    /// <summary>
    /// The editor's error alert shows the API's ProblemDetails text verbatim, so a failed
    /// mutation carries that detail in the exception message (and the status code for callers
    /// that branch on it). A body that is not ProblemDetails falls back to the reason phrase.
    /// </summary>
    private static async Task EnsureSuccessWithDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = default(string);
        if (response.Content is not null)
        {
            try
            {
                detail = (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(ct))?.Detail;
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a ProblemDetails body.
            }
            catch (NotSupportedException)
            {
                // Not a JSON body at all.
            }
        }

        throw new HttpRequestException(detail ?? response.ReasonPhrase, null, response.StatusCode);
    }

    public async Task<MediaAssetDto> UploadMediaAsync(Stream fileStream, string fileName, string contentType, Guid? postId = null, string scope = "Global", CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "file", fileName);
        if (postId.HasValue)
            content.Add(new StringContent(postId.Value.ToString()), "postId");
        content.Add(new StringContent(scope), "scope");

        using var response = await SendAsync(http => http.PostAsync("/api/media", content, ct), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MediaAssetDto>(ct))!;
    }

    public async Task<TagDto> CreateTagAsync(string name, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsJsonAsync("/api/tags", name, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<TagDto>(ct))!;
    }

    public async Task<TagDto> UpdateTagAsync(Guid id, UpdateTagRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PutAsJsonAsync($"/api/tags/{id}", request, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<TagDto>(ct))!;
    }

    public Task DeleteTagAsync(Guid id, CancellationToken ct = default) =>
        DeleteRefusingInUseAsync($"/api/tags/{id}", ct);

    public async Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsJsonAsync("/api/categories", name, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(ct))!;
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PutAsJsonAsync($"/api/categories/{id}", request, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(ct))!;
    }

    public Task DeleteCategoryAsync(Guid id, CancellationToken ct = default) =>
        DeleteRefusingInUseAsync($"/api/categories/{id}", ct);

    /// <summary>
    /// DELETE for tags and categories: the API refuses with 409 ProblemDetails while posts still
    /// use the item, so surface that detail as <see cref="DeleteConflictException"/> for the UI.
    /// </summary>
    private async Task DeleteRefusingInUseAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(http => http.DeleteAsync(url, ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(ct);
            throw new DeleteConflictException(problem?.Detail ?? "Cannot delete: the item is still in use.");
        }

        response.EnsureSuccessStatusCode();
        await EvictReadingPagesAsync();
    }

    public async Task<PagedResult<MediaAssetDto>> GetGlobalMediaAsync(int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(ListUrl("/api/media", page, pageSize, q), ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<MediaAssetDto>>(ct);
        return result ?? new PagedResult<MediaAssetDto>([], 0, page, pageSize);
    }

    public async Task<PagedResult<MediaAssetDto>> GetPostMediaAsync(Guid postId, int page, int pageSize, string? q = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync(ListUrl($"/api/posts/{postId}/media", page, pageSize, q), ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<MediaAssetDto>>(ct);
        return result ?? new PagedResult<MediaAssetDto>([], 0, page, pageSize);
    }

    public async Task<MediaAssetDto> UpdateMediaAsync(Guid id, UpdateMediaRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PutAsJsonAsync($"/api/media/{id}", request, ct), ct);
        response.EnsureSuccessStatusCode();
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<MediaAssetDto>(ct))!;
    }

    public async Task DeleteMediaAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.DeleteAsync($"/api/media/{id}", ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // The API refuses to delete a banner image with 409 ProblemDetails naming the post(s);
            // surface that detail so the grid can show it verbatim — the same
            // DeleteConflictException as the tag/category refusals (one type for the one pattern).
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(ct);
            throw new DeleteConflictException(problem?.Detail ?? "Cannot delete: the image is still in use.");
        }

        response.EnsureSuccessStatusCode();
        await EvictReadingPagesAsync();
    }

    public async Task<IReadOnlyList<FeedbackDto>> GetFeedbackAsync(Guid postId, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync($"/api/posts/{postId}/feedback", ct), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<FeedbackDto>>(ct);
        return result ?? [];
    }

    public async Task CreateFeedbackAsync(Guid postId, CreateFeedbackRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsJsonAsync($"/api/posts/{postId}/feedback", request, ct), ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<FeedbackDto> ResolveFeedbackAsync(Guid postId, Guid feedbackId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            http => http.PostAsync($"/api/posts/{postId}/feedback/{feedbackId}/resolve", null, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(ct))!;
    }

    public async Task<AnalyticsSummaryDto> GetAnalyticsAsync(Guid postId, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync($"/api/analytics?postId={postId}", ct), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AnalyticsSummaryDto>(ct))!;
    }

    public async Task<PostDiscussionAdminDto?> GetDiscussionAdminAsync(Guid postId, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync($"/api/posts/{postId}/discussion/admin", ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PostDiscussionAdminDto>(ct);
    }

    public async Task RefreshDiscussionAsync(Guid postId, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsync($"/api/posts/{postId}/discussion/refresh", null, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
    }

    public async Task<PostAnnouncementsDto?> GetAnnouncementsAsync(Guid postId, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync($"/api/posts/{postId}/announcements", ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PostAnnouncementsDto>(ct);
    }

    public async Task<AnnouncementStatusDto> AnnounceAsync(Guid postId, SocialPlatform platform, CancellationToken ct = default)
    {
        var segment = platform.ToString().ToLowerInvariant();
        using var response = await SendAsync(http => http.PostAsync($"/api/posts/{postId}/announcements/{segment}", null, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AnnouncementStatusDto>(ct))!;
    }

    public async Task<IReadOnlyList<SocialBlockDto>> GetSocialBlocksAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.GetAsync("/api/discussion/blocks", ct), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SocialBlockDto>>(ct) ?? [];
    }

    public async Task<SocialBlockDto> CreateSocialBlockAsync(CreateSocialBlockRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.PostAsJsonAsync("/api/discussion/blocks", request, ct), ct);
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return (await response.Content.ReadFromJsonAsync<SocialBlockDto>(ct))!;
    }

    public async Task<bool> DeleteSocialBlockAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(http => http.DeleteAsync($"/api/discussion/blocks/{id}", ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessWithDetailAsync(response, ct);
        await EvictReadingPagesAsync();
        return true;
    }
}
