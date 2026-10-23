using System.Net;
using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Layout;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// An API that fails (a restart, a 500, a 400 for bad input) is shown as the page's own error
/// line and logged at Warning; it never ends the circuit. Rendering the page and reaching the
/// assertion without an unhandled exception is the "circuit survives" evidence: an exception
/// thrown from a lifecycle method or event handler fails the render in bUnit as it ends the
/// circuit in production.
/// </summary>
public class AdminPagesApiFailureTests : BlogBunitContext
{
    private static HttpRequestException Failure(HttpStatusCode status, string message = "The API failed.") =>
        new(message, null, status);

    private CapturingLogger<T> LoggerFor<T>()
    {
        var logger = new CapturingLogger<T>();
        Services.AddSingleton<ILogger<T>>(logger);
        return logger;
    }

    [Fact]
    public async Task TagsPage_ApiFailingOnLoad_ShowsTheErrorLine_AndLogsAWarning()
    {
        var logger = LoggerFor<TagsPage>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            TagsFailure = Failure(HttpStatusCode.InternalServerError, "tags are down"),
        });

        var cut = Render<TagsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Contain("tags are down"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("500"),
            "an API failure on an admin page is logged, with its status");
    }

    [Fact]
    public async Task TagsPage_CategoriesFailingOnLoadWithAnHttpError_ShowsTheErrorLine()
    {
        // The existing load tests fail the tags list with an HTTP error and the categories list with
        // the unavailable type; the categories list failing like an HTTP error is the third case.
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            CategoriesFailure = Failure(HttpStatusCode.ServiceUnavailable, "categories are down"),
        });

        var cut = Render<TagsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Contain("categories are down"));
    }

    [Fact]
    public async Task TagsPage_ApiUnavailableOnLoad_ShowsTheErrorLine()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            CategoriesFailure = new ApiUnavailableException("The API did not answer in time.", new TimeoutException()),
        });

        var cut = Render<TagsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Contain("did not answer in time"));
    }

    [Fact]
    public async Task TagsPage_AddingACategoryTheApiRefuses_ShowsTheApisMessage()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            CreateFailure = Failure(HttpStatusCode.BadRequest, "Name must be 128 characters or fewer."),
        });
        var cut = Render<TagsPage>();

        var input = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "New category");
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync(new string('x', 129)));
        input.Find(".mud-input-adornment button").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Contain("128 characters or fewer"));
    }

    [Fact]
    public async Task TagsPage_AddingATagWhileTheApiFails_ShowsTheErrorLine()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            CreateFailure = Failure(HttpStatusCode.InternalServerError, "create failed"),
        });
        var cut = Render<TagsPage>();

        var input = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "New tag");
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync("k8s"));
        input.Find(".mud-input-adornment button").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Contain("create failed"));
    }

    [Fact]
    public async Task TagsPage_DeletingATagWhileTheApiFails_ShowsTheErrorLine_AndKeepsTheRow()
    {
        var tag = new TagDto(Guid.CreateVersion7(), "k8s", "k8s", 0, true);
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            Tags = [tag],
            TagDeleteFailure = Failure(HttpStatusCode.InternalServerError, "delete failed"),
        });
        var cut = Render<TagsPage>();

        cut.FindAll("button").Single(b => b.TextContent == "Delete").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Contain("delete failed"));
        cut.FindAll("tr.admin-table__row").Should().HaveCount(1);
    }

    // ---- brief 13i item 5: the load failures that showed an error line but logged nothing ------------

    [Fact]
    public async Task MediaGrid_ApiFailingOnLoad_ShowsTheErrorLine_AndLogsOneWarningNamingTheLoad()
    {
        var logger = LoggerFor<MediaGrid>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            MediaLoadFailure = Failure(HttpStatusCode.InternalServerError, "media are down"),
        });

        var cut = Render<MediaGrid>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".media-grid__error").TextContent.Should().Contain("media are down"));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Text.Should().Contain("loading the media library").And.Contain("500");
    }

    [Fact]
    public async Task PostsList_ApiFailingOnLoad_ShowsTheErrorLine_AndLogsOneWarningNamingTheLoad()
    {
        var logger = LoggerFor<PostsList>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            PostsLoadFault = _ => Failure(HttpStatusCode.BadGateway, "posts are down"),
        });

        var cut = Render<PostsList>(p => p.Add(x => x.Kind, PostKind.Post));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("posts are down"));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Text.Should().Contain("loading the posts list").And.Contain("502");
    }

    [Fact]
    public async Task DraftsPage_ApiFailingOnLoad_ShowsTheErrorLine_AndLogsOneWarningNamingTheLoad()
    {
        var logger = LoggerFor<DraftsPage>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            PostsLoadFault = request => request.Status == PostStatus.Draft ? Failure(HttpStatusCode.ServiceUnavailable, "drafts are down") : null,
        });

        var cut = Render<DraftsPage>();

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".drafts__error").TextContent.Should().Contain("drafts are down"));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Text.Should().Contain("loading the drafts").And.Contain("503");
    }

    [Fact]
    public async Task MediaGrid_SessionExpiringOnLoad_LogsNothing()
    {
        var logger = LoggerFor<MediaGrid>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { MediaLoadFailure = new SessionExpiredException() });

        var cut = Render<MediaGrid>();

        await cut.WaitForAssertionAsync(() => cut.Find(".media-grid__error"));
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "a session that ends is not an API fault");
    }

    [Fact]
    public async Task PostsList_SessionExpiringOnLoad_LogsNothing()
    {
        var logger = LoggerFor<PostsList>();
        var api = new FakeBlogApiClient { PostsLoadFault = _ => new SessionExpiredException() };
        Services.AddSingleton<IBlogApiClient>(api);

        var cut = Render<PostsList>(p => p.Add(x => x.Kind, PostKind.Post));

        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().NotBeEmpty("the load ran and failed"));
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "a session that ends is not an API fault");
    }

    [Fact]
    public async Task DraftsPage_SessionExpiringOnLoad_LogsNothing()
    {
        var logger = LoggerFor<DraftsPage>();
        var api = new FakeBlogApiClient { PostsLoadFault = _ => new SessionExpiredException() };
        Services.AddSingleton<IBlogApiClient>(api);

        var cut = Render<DraftsPage>();

        await cut.WaitForAssertionAsync(() => api.PostRequests.Should().NotBeEmpty("the load ran and failed"));
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "a session that ends is not an API fault");
    }

    [Fact]
    public async Task DraftReviewPage_ApiFailingOnLoad_ShowsTheErrorLine()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            PostReadFailure = Failure(HttpStatusCode.InternalServerError, "post read failed"),
        });

        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".draft-review__error").TextContent.Should().Contain("post read failed"));
    }

    [Fact]
    public void MediaGrid_DeleteFailingWithAnApiError_ShowsTheErrorLine_AndKeepsTheTile()
    {
        var asset = new MediaAssetDto(Guid.CreateVersion7(), "f.png", "photo.png", "image/png", "/api/media/x",
            MediaAssetScope.Global, null, null);
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            DeleteFailure = Failure(HttpStatusCode.InternalServerError, "delete failed"),
            GlobalMedia = [asset],
        });
        var cut = Render<MediaGrid>();

        cut.FindAll(".media-tile__actions button").Single(b => b.TextContent == "Delete").Click(new MouseEventArgs());
        cut.FindAll(".media-tile__confirm button").Single(b => b.TextContent == "Delete image").Click(new MouseEventArgs());

        cut.Find(".media-grid__error").TextContent.Should().Contain("delete failed");
        cut.FindAll(".media-tile").Should().HaveCount(1);
    }

    [Fact]
    public void MainLayout_ApiFailure_RendersTheShell_AndLogsAWarningWithTheStatus()
    {
        var logger = LoggerFor<MainLayout>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            TopCategoriesFailure = Failure(HttpStatusCode.BadGateway),
        });
        Services.AddSingleton(new MudTheme());

        var cut = Render<MainLayout>();

        cut.Markup.Should().Contain("sidebar-nav");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Text.Contains("502"),
            "the swallowed failure is in the Web logs, once");
    }

    [Fact]
    public void MainLayout_SessionEnding_IsNotLoggedAsAnApiFailure()
    {
        var logger = LoggerFor<MainLayout>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { TopCategoriesFailure = new SessionExpiredException() });
        Services.AddSingleton(new MudTheme());

        Render<MainLayout>();

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning,
            "a session that ends is the sign-in redirect's business, not an API fault");
    }

    private void PostEditorServices()
    {
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task PostEditPage_TagSuggestionsFailing_LeavesTheEditorUsable_AndLogsAWarning()
    {
        var logger = LoggerFor<PostEditPage>();
        var api = new FakeBlogApiClient { TagsFailure = Failure(HttpStatusCode.BadGateway, "tags are down") };
        Services.AddSingleton<IBlogApiClient>(api);
        PostEditorServices();

        var cut = Render<PostEditPage>();
        await cut.WaitForAssertionAsync(() => api.TagPageRequests.Should().NotBeEmpty("the editor asked for suggestions on load"));

        // Typing in the tag entry asks again: that failure must not end the circuit either.
        cut.Find("#post-tag-entry").Input("k8");
        await cut.WaitForAssertionAsync(() =>
            api.TagPageRequests.Last().Should().Be((1, 20, "k8"), "the typeahead asked the server and failed"));

        cut.FindComponent<PostEditorForm>().Should().NotBeNull("the editor is still on the page");
        cut.Find("#post-tag-entry").Should().NotBeNull();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("502") && e.Text.Contains("searching tags"),
            "the swallowed failure is in the Web logs with its status");
    }

    [Fact]
    public async Task PostEditPage_CategorySearchFailing_FindsNothing_LeavesTheEditorUsable_AndLogsAWarning()
    {
        var logger = LoggerFor<PostEditPage>();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            CategoriesFailure = Failure(HttpStatusCode.InternalServerError, "categories are down"),
        });
        PostEditorServices();
        var cut = Render<PostEditPage>();

        var search = cut.FindComponent<MudAutocomplete<CategoryDto>>().Instance.SearchFunc
            ?? throw new InvalidOperationException("the category picker must search the server");
        var results = await (search("tech", CancellationToken.None) ?? Task.FromResult<IEnumerable<CategoryDto>>([]));

        results.Should().BeEmpty("a failed search finds nothing instead of ending the circuit");
        cut.FindComponent<PostEditorForm>().Should().NotBeNull("the editor is still on the page");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("500") && e.Text.Contains("searching categories"),
            "the swallowed failure is in the Web logs with its status");
    }
}
