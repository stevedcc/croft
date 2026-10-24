using System.Security.Claims;
using Croft.Shared.Contracts;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Headers;

namespace Croft.Web.Tests;

public class BlogApiClientTests
{
    [Fact]
    public async Task GetPostsAsync_SendsCorrectRequestAndDeserializesResponse()
    {
        var expected = new PagedResult<PostSummaryDto>(
            [], 0, 1, 10);

        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.PathAndQuery.Should().StartWith("/api/posts");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new BlogApiClient(factory, new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.GetPostsAsync(new PostListRequest());

        result.TotalCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task GetPostsAsync_SendsPublicViewOnlyWhenAsked(bool publicView, bool expectedInQuery)
    {
        string? query = null;
        var handler = new TestHandler(request =>
        {
            query = request.RequestUri!.Query;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<PostSummaryDto>([], 0, 1, 10))
            };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.GetPostsAsync(new PostListRequest(PublicView: publicView));

        (query!.Contains("publicView=true")).Should().Be(expectedInQuery);
    }

    [Fact]
    public async Task CreateCategoryAsync_ARefusedName_CarriesTheApisProblemDetailsMessage()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = 400,
                Detail = "Name must be 128 characters or fewer.",
            }),
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var act = () => client.CreateCategoryAsync(new string('x', 129));

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Be("Name must be 128 characters or fewer.");
    }

    [Fact]
    public async Task CreateTagAsync_ARefusedName_CarriesTheApisProblemDetailsMessage()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = 400, Detail = "Name is required." }),
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var act = () => client.CreateTagAsync(" ");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Be("Name is required.");
    }

    [Fact]
    public async Task SearchAsync_AlwaysAsksForThePublicView()
    {
        string? query = null;
        var handler = new TestHandler(request =>
        {
            query = request.RequestUri!.Query;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<PostSummaryDto>([], 0, 1, 10))
            };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.SearchAsync("tartan");

        query.Should().Contain("publicView=true");
    }

    [Fact]
    public async Task CreatePostAsync_AddsBearerToken_WhenAvailable()
    {
        var tokenService = new FakeAccessTokenService { Token = () => "test-token" };
        var captured = default(AuthenticationHeaderValue?);
        var handler = new TestHandler(request =>
        {
            captured = request.Headers.Authorization;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new PostDto(
                    Guid.NewGuid(), "slug", "Title", null, "content", null, null,
                    Croft.Shared.Enums.PostStatus.Draft, null,
                    DateTime.UtcNow, DateTime.UtcNow, "id", "Name",
                    null, [], null, 1))
            };
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new BlogApiClient(factory, new FakeAuthenticationStateProvider(User()), tokenService, new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.CreatePostAsync(new CreatePostRequest("Title", "content"));

        captured.Should().NotBeNull();
        captured!.Scheme.Should().Be("Bearer");
        captured.Parameter.Should().Be("test-token");
    }

    [Fact]
    public async Task GetPostsAsync_OmitsAuthorizationHeader_WhenTokenMissing()
    {
        var tokenService = new FakeAccessTokenService { Token = () => null };
        var captured = default(AuthenticationHeaderValue?);
        var handler = new TestHandler(request =>
        {
            captured = request.Headers.Authorization;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<PostSummaryDto>([], 0, 1, 10))
            };
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new BlogApiClient(factory, new FakeAuthenticationStateProvider(AnonymousUser()), tokenService, new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.GetPostsAsync(new PostListRequest());

        captured.Should().BeNull();
    }

    [Fact]
    public async Task ApiCall_PassesAuthenticationStateUser_ToTokenService()
    {
        var user = AnonymousUser();
        var tokenService = new FakeAccessTokenService { Token = () => null };
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedResult<PostSummaryDto>([], 0, 1, 10))
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(user), tokenService, new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.GetPostsAsync(new PostListRequest());

        tokenService.SeenUsers.Should().ContainSingle().Which.Should().BeSameAs(user);
    }

    [Fact]
    public async Task EachApiCall_GetsTheCurrentTokenFromTheTokenService()
    {
        var tokens = new Queue<string?>(["token-1", "token-2"]);
        var tokenService = new FakeAccessTokenService { Token = () => tokens.Dequeue() };
        var captured = new List<AuthenticationHeaderValue?>();
        var handler = new TestHandler(request =>
        {
            captured.Add(request.Headers.Authorization);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<PostSummaryDto>([], 0, 1, 10))
            };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), tokenService, new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.GetPostsAsync(new PostListRequest());
        await client.GetPostsAsync(new PostListRequest());

        tokenService.SeenUsers.Should().HaveCount(2, "every API call re-resolves the token");
        captured.Select(h => h?.Parameter).Should().Equal("token-1", "token-2");
    }

    [Fact]
    public async Task AuthenticatedUser_Getting401_NavigatesToLoginWithReturnUrl_AndThrowsSessionExpiredException()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var navigation = new FakeNavigationManager(
            baseUri: "http://localhost:5002/",
            currentUri: "http://localhost:5002/posts/my-post?page=2");
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(User()),
            new FakeAccessTokenService(),
            navigation,
            new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostsAsync(new PostListRequest());

        await act.Should().ThrowAsync<SessionExpiredException>("the session is expired and the caller must stop cleanly");
        navigation.NavigatedTo.Should().Be($"/login?returnUrl={Uri.EscapeDataString("/posts/my-post?page=2")}");
        navigation.ForceLoad.Should().BeTrue("the app must reload so the cookie OnValidatePrincipal check runs");
    }

    [Fact]
    public async Task AuthenticatedUser_Getting401_RunsTheRegisteredHooksBeforeNavigating_AndMarksTheReturnUrlWhenOneKeptWork()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var navigation = new FakeNavigationManager(
            baseUri: "http://localhost:5002/",
            currentUri: "http://localhost:5002/admin/posts/new");
        var hooks = new SessionExpiryHooks();
        string? navigatedWhenTheHookRan = "not run";
        using var registration = hooks.Register(() =>
        {
            navigatedWhenTheHookRan = navigation.NavigatedTo;
            return Task.FromResult(true);
        });
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(User()),
            new FakeAccessTokenService(),
            navigation,
            hooks,
            new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostsAsync(new PostListRequest());

        await act.Should().ThrowAsync<SessionExpiredException>();
        navigatedWhenTheHookRan.Should().BeNull("unsaved work is backed up before the browser leaves");
        navigation.NavigatedTo.Should().Be(
            $"/login?returnUrl={Uri.EscapeDataString("/admin/posts/new?signedBackIn=1")}",
            "the editor's return URL carries the one-shot flag that restores the backup without asking");
    }

    [Fact]
    public async Task AuthenticatedUser_Getting401_WithHooksThatKeptNothing_LeavesTheReturnUrlUnmarked()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var navigation = new FakeNavigationManager(
            baseUri: "http://localhost:5002/",
            currentUri: "http://localhost:5002/admin/posts/new");
        var hooks = new SessionExpiryHooks();
        using var registration = hooks.Register(() => Task.FromResult(false));
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(User()),
            new FakeAccessTokenService(),
            navigation,
            hooks,
            new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostsAsync(new PostListRequest());

        await act.Should().ThrowAsync<SessionExpiredException>();
        navigation.NavigatedTo.Should().Be($"/login?returnUrl={Uri.EscapeDataString("/admin/posts/new")}");
    }

    [Fact]
    public async Task AuthenticatedUser_Getting401_WithAThrowingHook_StillRedirects()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var navigation = new FakeNavigationManager(
            baseUri: "http://localhost:5002/",
            currentUri: "http://localhost:5002/admin/posts/new");
        var hooks = new SessionExpiryHooks();
        using var registration = hooks.Register(() => throw new InvalidOperationException("the circuit is gone"));
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(User()),
            new FakeAccessTokenService(),
            navigation,
            hooks,
            new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostsAsync(new PostListRequest());

        await act.Should().ThrowAsync<SessionExpiredException>();
        navigation.NavigatedTo.Should().Be($"/login?returnUrl={Uri.EscapeDataString("/admin/posts/new")}",
            "a hook that cannot run must never keep the user from signing in");
    }

    [Fact]
    public void SessionExpiredException_IsAnHttpRequestException_SoEveryPageThatHandlesAFailedCallHandlesIt()
    {
        new SessionExpiredException().Should().BeAssignableTo<HttpRequestException>();
    }

    [Fact]
    public async Task AuthenticatedUser_WithNoAccessTokenAtAll_IsAnEndedSession_BeforeAnyRequestIsSent()
    {
        // The session is gone from the store (purged, pod restarted): the circuit still holds the
        // cookie's principal, but there is no token to send. A public endpoint would answer an
        // anonymous 404 for a draft and hide that; the ended session is detected here instead.
        var requests = 0;
        var handler = new TestHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var navigation = new FakeNavigationManager(
            baseUri: "http://localhost:5002/",
            currentUri: "http://localhost:5002/admin/posts/1/edit");
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(User()),
            new FakeAccessTokenService { Token = () => null },
            navigation,
            new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostByIdAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<SessionExpiredException>();
        requests.Should().Be(0, "an anonymous request would only be answered with a misleading 404");
        navigation.NavigatedTo.Should().Be($"/login?returnUrl={Uri.EscapeDataString("/admin/posts/1/edit")}");
    }

    [Fact]
    public async Task AnonymousUser_Getting401_DoesNotNavigate_AndStillFailsWithEnsureSuccessStatusCode()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var navigation = new FakeNavigationManager();
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(AnonymousUser()),
            new FakeAccessTokenService(),
            navigation,
            new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostsAsync(new PostListRequest());

        await act.Should().ThrowAsync<System.Net.Http.HttpRequestException>("that is what EnsureSuccessStatusCode did before this change");
        navigation.NavigatedTo.Should().BeNull("an anonymous 401 is not an expired session");
    }

    [Fact]
    public async Task AuthenticatedUser_Getting401_WithNonLocalCurrentUrl_NavigatesToLoginWithoutReturnUrl()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var navigation = new FakeNavigationManager(
            baseUri: "http://localhost:5002/",
            currentUri: "http://localhost:5002//evil.example/path");
        var client = new BlogApiClient(
            new FakeHttpClientFactory(handler),
            new FakeAuthenticationStateProvider(User()),
            new FakeAccessTokenService(),
            navigation,
            new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.GetPostsAsync(new PostListRequest());

        await act.Should().ThrowAsync<SessionExpiredException>();
        navigation.NavigatedTo.Should().Be("/login", "LocalUrl.IsLocal rejects the current URL, so no returnUrl parameter is attached");
        navigation.ForceLoad.Should().BeTrue();
    }

    [Fact]
    public async Task GetRelatedPostsAsync_SendsCorrectRequestAndDeserializesResponse()
    {
        // Task R3.4: the post page's Related panel (spec §5.4) consumes GET /api/posts/{slug}/related.
        var expected = new List<PostSummaryDto>
        {
            new(Guid.NewGuid(), "ef-core-migrations", "EF Core migrations", null, null, "Summary.", 5,
                Croft.Shared.Enums.PostStatus.Published, null,
                new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), "An Author", null, [], null, null, null),
        };
        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.PathAndQuery.Should().Be("/api/posts/my-post/related?count=3");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.GetRelatedPostsAsync("my-post");

        result.Should().ContainSingle().Which.Slug.Should().Be("ef-core-migrations");
    }

    [Fact]
    public async Task GetDiscussionAsync_AsksTheDiscussionRoute_AndDeserializesTheNestedReplies()
    {
        // Brief 12a item 6: the one client method of the discussion (12b renders it).
        var reply = new Croft.Shared.Contracts.Social.DiscussionReplyDto(
            Croft.Shared.Contracts.Social.SocialPlatform.Mastodon, "110054900866478823", "reader@social.example", "Reader", "https://social.example/@reader", "acct-1",
            null, new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), "https://social.example/@reader/1", "<p>Example reply text.</p>", null, false,
            [new Croft.Shared.Contracts.Social.DiscussionReplyDto(
                Croft.Shared.Contracts.Social.SocialPlatform.Bluesky, "at://did:plc:x/app.bsky.feed.post/y", "h.example", "H", "https://bsky.app/profile/h.example", "did:plc:x",
                new string('a', 64), new DateTimeOffset(2026, 10, 20, 9, 5, 0, TimeSpan.Zero), "https://bsky.app/profile/h.example/post/y", "<p>Nested.</p>", "CW", true, [])]);
        var expected = new Croft.Shared.Contracts.Social.PostDiscussionDto("https://mastodon.example/@owner/1", null, 2, [reply]);
        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.PathAndQuery.Should().Be("/api/posts/my-post/discussion");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.GetDiscussionAsync("my-post");

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetDiscussionAsync_IsNullFor404_AndThrowsForAServerError_AndReleasesEveryResponse()
    {
        List<TrackedResponse> responses = [];
        var handler = new TestHandler(request =>
        {
            var status = request.RequestUri!.AbsolutePath.Contains("/missing/", StringComparison.Ordinal)
                ? System.Net.HttpStatusCode.NotFound
                : System.Net.HttpStatusCode.InternalServerError;
            var response = new TrackedResponse(status) { Content = new StringContent("{}") };
            responses.Add(response);
            return response;
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        (await client.GetDiscussionAsync("missing/post")).Should().BeNull();
        var failing = () => client.GetDiscussionAsync("broken/post");
        await failing.Should().ThrowAsync<HttpRequestException>();

        responses.Should().HaveCount(2).And.OnlyContain(r => r.Disposed);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task UpdateMediaAsync_PutsAltText_AndReturnsUpdatedAsset()
    {
        var id = Guid.CreateVersion7();
        var expected = new MediaAssetDto(id, "f.png", "f.png", "image/png", $"/api/media/{id}",
            Croft.Shared.Enums.MediaAssetScope.Global, null, "New alt");
        var capturedBody = default(Croft.Shared.Contracts.UpdateMediaRequest);
        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.PathAndQuery.Should().Be($"/api/media/{id}");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBody = System.Text.Json.JsonSerializer.Deserialize<Croft.Shared.Contracts.UpdateMediaRequest>(body, JsonOptions);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.UpdateMediaAsync(id, new Croft.Shared.Contracts.UpdateMediaRequest("New alt"));

        capturedBody.Should().NotBeNull();
        capturedBody!.AltText.Should().Be("New alt", "the alt text travels in the PUT body");
        result.AltText.Should().Be("New alt");
    }

    [Fact]
    public async Task DeleteMediaAsync_SendsDeleteAndCompletesOn204()
    {
        var id = Guid.CreateVersion7();
        var capturedMethod = default(HttpMethod?);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedMethod = request.Method;
            capturedPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.DeleteMediaAsync(id);

        capturedMethod.Should().Be(HttpMethod.Delete);
        capturedPath.Should().Be($"/api/media/{id}");
    }

    [Fact]
    public async Task DeleteMediaAsync_WhenTheApiRefusesBecauseTheImageIsABanner_ThrowsWithTheProblemDetail()
    {
        // The API answers 409 ProblemDetails naming the post(s) that use the image as a banner;
        // the UI shows that detail verbatim, so it must survive the client round-trip.
        var id = Guid.CreateVersion7();
        var detail = "Cannot delete: used as a banner image by A Post Title.";
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Title = "Conflict",
                Status = 409,
                Detail = detail
            })
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.DeleteMediaAsync(id);

        // Rule-5-sanctioned assertion update (brief item 13 unifies the two 409-detail exceptions
        // into one type): the behaviour asserted is unchanged — the refusal surfaces verbatim.
        var exception = await act.Should().ThrowAsync<DeleteConflictException>();
        exception.Which.Message.Should().Be(detail, "the refusal message names the post(s) and is shown to the user");
    }

    [Fact]
    public async Task UpdateCategoryAsync_PutsNameAndIcon_AndReturnsUpdatedCategory()
    {
        var id = Guid.CreateVersion7();
        var expected = new Croft.Shared.Contracts.CategoryDto(id, "Hiking", "hiking", "Terrain", 3);
        var capturedBody = default(Croft.Shared.Contracts.UpdateCategoryRequest);
        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.PathAndQuery.Should().Be($"/api/categories/{id}");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBody = System.Text.Json.JsonSerializer.Deserialize<Croft.Shared.Contracts.UpdateCategoryRequest>(body, JsonOptions);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.UpdateCategoryAsync(id, new Croft.Shared.Contracts.UpdateCategoryRequest("Hiking", "Terrain"));

        capturedBody.Should().NotBeNull();
        capturedBody!.Name.Should().Be("Hiking", "the name travels in the PUT body");
        capturedBody.Icon.Should().Be("Terrain", "the icon travels in the PUT body");
        result.Icon.Should().Be("Terrain");
    }

    [Fact]
    public async Task UpdateTagAsync_PutsName_AndReturnsRenamedTag()
    {
        var id = Guid.CreateVersion7();
        var expected = new Croft.Shared.Contracts.TagDto(id, "k8s", "k8s", 2, true);
        var capturedBody = default(Croft.Shared.Contracts.UpdateTagRequest);
        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.PathAndQuery.Should().Be($"/api/tags/{id}");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBody = System.Text.Json.JsonSerializer.Deserialize<Croft.Shared.Contracts.UpdateTagRequest>(body, JsonOptions);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.UpdateTagAsync(id, new Croft.Shared.Contracts.UpdateTagRequest("k8s"));

        capturedBody.Should().NotBeNull();
        capturedBody!.Name.Should().Be("k8s", "the name travels in the PUT body");
        result.Name.Should().Be("k8s");
    }

    [Fact]
    public async Task DeleteCategoryAsync_WhenTheApiRefusesBecausePostsUseIt_ThrowsWithTheProblemDetail()
    {
        var id = Guid.CreateVersion7();
        var detail = "Cannot delete: category is used by one or more posts.";
        var capturedMethod = default(HttpMethod?);
        var handler = new TestHandler(request =>
        {
            capturedMethod = request.Method;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Conflict",
                    Status = 409,
                    Detail = detail
                })
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.DeleteCategoryAsync(id);

        var exception = await act.Should().ThrowAsync<DeleteConflictException>();
        exception.Which.Message.Should().Be(detail, "the refusal message is shown to the user verbatim");
        capturedMethod.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteTagAsync_WhenTheApiRefusesBecausePostsUseIt_ThrowsWithTheProblemDetail()
    {
        var id = Guid.CreateVersion7();
        var detail = "Cannot delete: tag is used by one or more posts.";
        var capturedMethod = default(HttpMethod?);
        var handler = new TestHandler(request =>
        {
            capturedMethod = request.Method;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Conflict",
                    Status = 409,
                    Detail = detail
                })
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.DeleteTagAsync(id);

        var exception = await act.Should().ThrowAsync<DeleteConflictException>();
        exception.Which.Message.Should().Be(detail, "the refusal message is shown to the user verbatim");
        capturedMethod.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task GetPostByIdAsync_GetsThePostById()
    {
        var id = Guid.CreateVersion7();
        var expected = new PostDto(
            id, "by-id", "By Id", null, "content", null, null,
            Croft.Shared.Enums.PostStatus.Draft, null,
            DateTime.UtcNow, DateTime.UtcNow, "id", "Name",
            null, [], null, 1);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            capturedPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.GetPostByIdAsync(id);

        capturedPath.Should().Be($"/api/posts/{id}", "the editor loads the post through the same read endpoint as the public page");
        result!.Id.Should().Be(id);
    }

    [Fact]
    public async Task UnpublishPostAsync_PostsUnpublish_AndReturnsTheDraft()
    {
        var id = Guid.CreateVersion7();
        var expected = new PostDto(
            id, "unpublished", "Unpublished", null, "content", null, null,
            Croft.Shared.Enums.PostStatus.Draft, null,
            DateTime.UtcNow, DateTime.UtcNow, "id", "Name",
            null, [], null, 1);
        var capturedMethod = default(HttpMethod?);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedMethod = request.Method;
            capturedPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.UnpublishPostAsync(id);

        capturedMethod.Should().Be(HttpMethod.Post);
        capturedPath.Should().Be($"/api/posts/{id}/unpublish");
        result.Status.Should().Be(Croft.Shared.Enums.PostStatus.Draft, "the response carries the post back so the editor can show its new state");
    }

    [Fact]
    public async Task DeletePostAsync_SendsDeleteAndCompletesOn204()
    {
        var id = Guid.CreateVersion7();
        var capturedMethod = default(HttpMethod?);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedMethod = request.Method;
            capturedPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.DeletePostAsync(id);

        capturedMethod.Should().Be(HttpMethod.Delete);
        capturedPath.Should().Be($"/api/posts/{id}");
    }

    [Fact]
    public async Task ResolveFeedbackAsync_PostsResolve_AndReturnsTheResolvedFeedback()
    {
        var postId = Guid.CreateVersion7();
        var feedbackId = Guid.CreateVersion7();
        var resolvedAt = new DateTime(2026, 10, 6, 9, 30, 0, DateTimeKind.Utc);
        var expected = new FeedbackDto(feedbackId, postId, "author-id", "Ed", "a quote", "a note",
            resolvedAt.AddMinutes(-5), resolvedAt);
        var capturedMethod = default(HttpMethod?);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedMethod = request.Method;
            capturedPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.ResolveFeedbackAsync(postId, feedbackId);

        capturedMethod.Should().Be(HttpMethod.Post);
        capturedPath.Should().Be($"/api/posts/{postId}/feedback/{feedbackId}/resolve");
        result.ResolvedAt.Should().Be(resolvedAt);
    }

    [Fact]
    public async Task UpdatePostAsync_WhenTheApiRejectsTheInput_ThrowsWithTheProblemDetail()
    {
        // The editor shows the API's problem detail in its error alert, so the text must survive
        // the client round-trip.
        var id = Guid.CreateVersion7();
        var detail = "Subtitle must be 300 characters or fewer.";
        var handler = new TestHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Title = "Validation failed",
                Status = 400,
                Detail = detail
            })
        });

        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        Func<Task> act = () => client.UpdatePostAsync(id, new UpdatePostRequest("Title", "content", Subtitle: new string('x', 301)));

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().Be(detail, "the rejection message is shown to the user verbatim");
    }

    [Fact]
    public async Task GetTagsAsync_SendsThePagingRequest_AndReadsThePagedResult()
    {
        var expected = new PagedResult<TagDto>([new(Guid.CreateVersion7(), "k8s", "k8s", 2, true)], 21, 2, 10);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var result = await client.GetTagsAsync(2, 10, "k8s");

        capturedPath.Should().Be("/api/tags?page=2&pageSize=10&q=k8s",
            "the admin list pages and filters on the server");
        result.TotalCount.Should().Be(21);
        result.Items.Should().ContainSingle().Which.Name.Should().Be("k8s");
    }

    [Fact]
    public async Task GetTagBySlugAsync_GetsTheOneTag_AndAnUnknownSlugIsNullable()
    {
        var expected = new TagDto(Guid.CreateVersion7(), "k8s", "k8s", 2, true);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedPath = request.RequestUri!.PathAndQuery;
            return request.RequestUri!.AbsolutePath == "/api/tags/k8s"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(expected) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var found = await client.GetTagBySlugAsync("k8s");
        var missing = await client.GetTagBySlugAsync("no-such-tag");

        capturedPath.Should().Be("/api/tags/no-such-tag", "the second call resolves the unknown slug");
        found!.Name.Should().Be("k8s");
        missing.Should().BeNull("an unknown tag slug is the public page's 404 path");
    }

    [Fact]
    public async Task GetPageAsync_GetsTheOnePage_AndAnUnknownSlugIsNullable()
    {
        var expected = new PostDto(Guid.CreateVersion7(), "about", "About", null, "content", null, null,
            Croft.Shared.Enums.PostStatus.Published, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            "id", "Name", null, [], null, 1, Croft.Shared.Enums.PostKind.Page);
        var capturedPath = default(string);
        var handler = new TestHandler(request =>
        {
            capturedPath = request.RequestUri!.PathAndQuery;
            return request.RequestUri!.AbsolutePath == "/api/pages/about"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(expected) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var found = await client.GetPageAsync("about");
        var missing = await client.GetPageAsync("no-such-page");

        capturedPath.Should().Be("/api/pages/no-such-page", "the second call resolves the unknown slug");
        found!.Title.Should().Be("About");
        missing.Should().BeNull("an unknown page slug is the public page's 404 path");
    }

    [Fact]
    public async Task GetPageNavigationsAsync_GetsTheSidebarAndFooterEntriesInOneCall()
    {
        var expected = new PageNavigationDto([new("About", "about")], [new("Impressum", "impressum")]);
        var capturedPaths = new List<string>();
        var handler = new TestHandler(request =>
        {
            capturedPaths.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var navigation = await client.GetPageNavigationsAsync();

        capturedPaths.Should().Equal(["/api/pages/navigation/all"], "the shell's page read is one call for both lists");
        navigation.Sidebar.Should().ContainSingle().Which.Slug.Should().Be("about");
        navigation.Footer.Should().ContainSingle().Which.Slug.Should().Be("impressum");
    }

    [Fact]
    public async Task GetCategoriesAsync_SendsThePagingRequest_AndGetTopCategoriesAsync_SendsTop()
    {
        var expected = new PagedResult<CategoryDto>([new(Guid.CreateVersion7(), "Tech", "tech", "Code", 3)], 1, 1, 10);
        var top = new List<CategoryDto> { new(Guid.CreateVersion7(), "Busy", "busy", null, 9) };
        var capturedPaths = new List<string>();
        var handler = new TestHandler(request =>
        {
            capturedPaths.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri!.Query.Contains("top=")
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(top) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var page = await client.GetCategoriesAsync(1, 10);
        var sidebar = await client.GetTopCategoriesAsync(12);

        string[] expectedPaths = ["/api/categories?page=1&pageSize=10", "/api/categories?top=12"];
        capturedPaths.Should().Equal(expectedPaths,
            "the admin list pages on the server while the sidebar takes only its bounded top");
        page.Items.Should().ContainSingle().Which.Name.Should().Be("Tech");
        sidebar.Should().ContainSingle().Which.Name.Should().Be("Busy");
    }

    [Fact]
    public async Task GetCategoryBySlugAsync_GetsTheOneCategory_AndAnUnknownSlugIsNullable()
    {
        var expected = new CategoryDto(Guid.CreateVersion7(), "Tech", "tech", "Code", 3);
        var handler = new TestHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/categories/tech"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(expected) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var found = await client.GetCategoryBySlugAsync("tech");
        var missing = await client.GetCategoryBySlugAsync("no-such-category");

        found!.Name.Should().Be("Tech");
        missing.Should().BeNull("an unknown category slug is the public page's 404 path");
    }

    [Fact]
    public async Task GetGlobalMediaAsync_And_GetPostMediaAsync_SendThePagingRequest()
    {
        var id = Guid.CreateVersion7();
        var asset = new MediaAssetDto(id, "f.png", "photo.png", "image/png", $"/api/media/{id}",
            Croft.Shared.Enums.MediaAssetScope.Global, null, null);
        var expected = new PagedResult<MediaAssetDto>([asset], 600, 1, 24);
        var capturedPaths = new List<string>();
        var handler = new TestHandler(request =>
        {
            capturedPaths.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        var global = await client.GetGlobalMediaAsync(1, 24, "photo");
        var post = await client.GetPostMediaAsync(id, 1, 24);

        string[] expectedPaths =
        [
            $"/api/media?page=1&pageSize=24&q=photo",
            $"/api/posts/{id}/media?page=1&pageSize=24",
        ];
        capturedPaths.Should().Equal(expectedPaths,
            "both media listings page and filter on the server, newest first");
        global.TotalCount.Should().Be(600);
        post.Items.Should().ContainSingle().Which.OriginalName.Should().Be("photo.png");
    }

    [Fact]
    public async Task Calls_CarryTheirTokenPerRequest_AndNeverLeaveItOnTheClientsDefaultHeaders()
    {
        // The factory hands out a new client per call today; were one ever cached and shared,
        // a token left on DefaultRequestHeaders would be sent for the next user's calls.
        var seen = new List<string?>();
        var handler = new TestHandler(request =>
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(new List<FeedbackDto>()) };
        });
        var shared = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var tokens = new Queue<string?>(["token-of-ada", "token-of-bob"]);
        var tokenService = new FakeAccessTokenService { Token = () => tokens.Dequeue() };
        var client = new BlogApiClient(new SharedClientFactory(shared), new FakeAuthenticationStateProvider(User()), tokenService, new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.GetFeedbackAsync(Guid.NewGuid());
        await client.GetFeedbackAsync(Guid.NewGuid());

        seen.Should().Equal(["Bearer token-of-ada", "Bearer token-of-bob"],
            "each request carries the token of its own call");
        shared.DefaultRequestHeaders.Authorization.Should().BeNull("no call mutates the client's default headers");
    }

    [Fact]
    public async Task Responses_AreDisposed_WhetherTheCallSucceedsReturnsNothingOrFails()
    {
        var responses = new List<TrackedResponse>();
        var handler = new TestHandler(request =>
        {
            var status = request.RequestUri!.AbsolutePath.Contains("/missing/", StringComparison.Ordinal)
                ? System.Net.HttpStatusCode.NotFound
                : request.RequestUri.AbsolutePath.Contains("/broken/", StringComparison.Ordinal)
                    ? System.Net.HttpStatusCode.InternalServerError
                    : System.Net.HttpStatusCode.OK;
            var response = new TrackedResponse(status) { Content = JsonContent.Create(new List<FeedbackDto>()) };
            responses.Add(response);
            return response;
        });
        var client = new BlogApiClient(new FakeHttpClientFactory(handler), new FakeAuthenticationStateProvider(User()), new FakeAccessTokenService(), new FakeNavigationManager(), new SessionExpiryHooks(), new OutputCache.RecordingStore(TimeProvider.System));

        await client.GetFeedbackAsync(Guid.NewGuid());
        (await client.GetPostAsync("missing/post")).Should().BeNull();
        var failing = () => client.GetPostAsync("broken/post");
        await failing.Should().ThrowAsync<HttpRequestException>();

        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r.Disposed, "the client owns each response and releases it, an unread error body included");
    }

    private sealed class SharedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TrackedResponse(System.Net.HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim("sub", "admin-id")], "test"));

    private static ClaimsPrincipal AnonymousUser() =>
        new(new ClaimsIdentity());

    private sealed class FakeAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FakeAccessTokenService : IUserAccessTokenService
    {
        public Func<string?> Token = () => "test-token";
        public List<ClaimsPrincipal> SeenUsers = [];

        public Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            SeenUsers.Add(user);
            return Task.FromResult(Token());
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:5000/")
            };
        }
    }

    private sealed class FakeNavigationManager : NavigationManager
    {
        public FakeNavigationManager(
            string baseUri = "http://localhost:5002/",
            string currentUri = "http://localhost:5002/")
        {
            Initialize(baseUri, currentUri);
        }

        public string? NavigatedTo { get; private set; }
        public bool? ForceLoad { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            NavigatedTo = uri;
            ForceLoad = forceLoad;
        }
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
