using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// PostEditPage (spec §6.2): the editor's orchestration — every action saves the form first,
/// Publish now sends no date, Set date sends the entered past moment, Schedule sends the
/// entered UTC moment, and Unpublish is a save followed by the unpublish call. Errors from the
/// API surface as the alert at the top.
/// </summary>
public class PostEditPageTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private FakeBlogApiClient _api = null!;

    private FakeTimeProvider Setup()
    {
        _api = new FakeBlogApiClient();
        var clock = new FakeTimeProvider(Now);
        Services.AddSingleton<IBlogApiClient>(_api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        Services.AddSingleton<TimeProvider>(clock);
        return clock;
    }

    [Fact]
    public async Task Editor_Load_BoundsTheTagSuggestions_AndNeverLoadsTheCategoryTable()
    {
        Setup();
        _api.Tags.Add(new TagDto(Guid.CreateVersion7(), "k8s", "k8s", 1, true));
        _api.Categories.Add(new CategoryDto(Guid.CreateVersion7(), "Tech", "tech", null, 1));

        var cut = Render<PostEditPage>();

        await cut.WaitForAssertionAsync(() => _api.TagPageRequests.Should()
            .ContainSingle().Which.Should().Be((1, 20, (string?)null),
                "the editor opens with one bounded page of suggestions, not the whole tags table"));
        _api.CategoryPageRequests.Should().BeEmpty(
            "the category table is never loaded whole; the picker searches the server on demand");
    }

    [Fact]
    public async Task TagEntry_Typing_RefreshesTheSuggestionsFromTheServer()
    {
        Setup();
        _api.Tags.Add(new TagDto(Guid.CreateVersion7(), "k8s", "k8s", 1, true));
        var cut = Render<PostEditPage>();
        await cut.WaitForAssertionAsync(() => _api.TagPageRequests.Should().NotBeEmpty());

        cut.Find("#post-tag-entry").Input("k8");

        await cut.WaitForAssertionAsync(() =>
        {
            _api.TagPageRequests.Last().Should().Be((1, 20, "k8"),
                "each edit refreshes the suggestions from the server");
            cut.Find("#post-tag-suggestions").InnerHtml.Should().Contain("value=\"k8s\"");
        });
    }

    [Fact]
    public async Task CategorySearch_QueriesTheServerWithTheTypedText()
    {
        Setup();
        var category = new CategoryDto(Guid.CreateVersion7(), "Technology", "technology", "Code", 2);
        _api.Categories.Add(category);
        var cut = Render<PostEditPage>();

        var autocomplete = cut.FindComponent<MudAutocomplete<CategoryDto>>().Instance;
        Func<string?, CancellationToken, Task<IEnumerable<CategoryDto>>?> search = autocomplete.SearchFunc
            ?? throw new InvalidOperationException("the category picker must search the server");
        var task = search("tech", CancellationToken.None) ?? Task.FromResult<IEnumerable<CategoryDto>>([]);
        var results = (await task).ToList();

        results.Should().ContainSingle().Which.Name.Should().Be("Technology");
        _api.CategoryPageRequests.Last().Should().Be((1, 20, "tech"),
            "the category picker searches the server with the typed text");
    }

    [Fact]
    public async Task PostEditPage_PublishNow_SendsNoDate_AndSavesFirst()
    {
        Setup();
        var cut = Render<PostEditPage>();

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        form.Model.PublishedAtDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        form.Model.PublishedAtTime = new TimeSpan(9, 30, 0);

        cut.FindAll("button").Single(b => b.TextContent == "Publish now").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            _api.Calls.Should().Equal(["create", "publish"], "every action saves the form first");
        });
        _api.PublishedPosts.Should().ContainSingle().Which.Request.PublishedAt.Should()
            .BeNull("Publish now sends no date: the API decides \"now\" for an unpublished post and keeps a published post's date");
    }

    [Fact]
    public async Task PostEditPage_SetDate_SendsTheEnteredPastMoment_AfterSavingFirst()
    {
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Published, Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, Now.UtcDateTime,
            "author-id", "Author", null, [], null, 1);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.PublishedAtDate = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        form.Model.PublishedAtTime = new TimeSpan(8, 0, 0);
        cut.Render();

        cut.FindAll("button").Single(b => b.TextContent == "Set date").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.Calls.Should().Equal("update", "publish"));
        _api.PublishedPosts.Should().ContainSingle().Which.Request.PublishedAt.Should()
            .Be(new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task PostEditPage_Schedule_SendsTheEnteredUtcMoment()
    {
        Setup();
        var cut = Render<PostEditPage>();

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A scheduled post";
        form.Model.MarkdownContent = "Body.";
        form.Model.PublishedAtDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        form.Model.PublishedAtTime = new TimeSpan(9, 30, 0);

        cut.FindAll("button").Single(b => b.TextContent == "Schedule").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => { _api.PublishedPosts.Should().ContainSingle(); });
        _api.PublishedPosts[0].Request.PublishedAt.Should()
            .Be(new DateTime(2026, 12, 1, 9, 30, 0, DateTimeKind.Utc), "the entered date and time combine as UTC");
        // Schedule saves the form first too: the call order is create, then publish.
        _api.Calls.Should().Equal("create", "publish");
    }

    [Fact]
    public async Task PostEditPage_Edit_LoadsThePostIntoTheForm()
    {
        Setup();
        var id = Guid.CreateVersion7();
        var banner = new MediaAssetDto(Guid.CreateVersion7(), "file", "hills.png", "image/png",
            "/api/media/banner", MediaAssetScope.Global, null, null);
        _api.PostsById[id] = new PostDto(id, "a-post", "Loaded title", "Loaded subtitle", "Loaded body",
            "An excerpt", null, PostStatus.Published, new DateTime(2026, 11, 2, 8, 15, 0, DateTimeKind.Utc),
            Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", banner,
            [new TagDto(Guid.CreateVersion7(), "k8s", "k8s", 1, true)], null, 3);

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        var model = cut.FindComponent<PostEditorForm>().Instance.Model;
        model.Title.Should().Be("Loaded title");
        model.Subtitle.Should().Be("Loaded subtitle");
        model.Excerpt.Should().Be("An excerpt");
        model.MarkdownContent.Should().Be("Loaded body");
        model.Tags.Should().Equal("k8s");
        model.BannerImageId.Should().Be(banner.Id);
        model.PublishedAtDate.Should().Be(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        model.PublishedAtTime.Should().Be(new TimeSpan(8, 15, 0));
    }

    [Fact]
    public async Task PostEditPage_Unpublish_SavesFirstThenUnpublishes()
    {
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Published, Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, Now.UtcDateTime,
            "author-id", "Author", null, [], null, 1);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.FindAll("button").Single(b => b.TextContent == "Unpublish").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.Calls.Should().Equal("update", "unpublish"));
        cut.WaitForAssertion(() => { cut.Find(".status-badge").TextContent.Should().Be("Draft"); });
    }

    [Fact]
    public void PostEditPage_Preview_UsesPostHeroAndPostBody()
    {
        Setup();
        var cut = Render<PostEditPage>();

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Some **body**.";

        cut.Render();

        // h2 since brief 13g item 5 (review M-12): the page's own header carries the document's one h1.
        cut.Find(".post-hero h2").TextContent.Trim().Should().Be("A post",
            "the Preview tab renders the real post hero with the title being edited (Find throws when the hero is absent, so presence alone proves nothing)");
        cut.FindAll("h1").Should().ContainSingle("the preview adds no second h1");
        var body = cut.Find(".markdown-body");
        body.InnerHtml.Should().Contain("<strong>body</strong>", "the markdown renders through the real post body");
    }

    [Fact]
    public void PostEditPage_Preview_MarksEveryTopLevelBlockWithItsSourceLine_ForTheScrollSync()
    {
        Setup();
        var cut = Render<PostEditPage>();

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.MarkdownContent = "First.\n\n## Second\n\nThird.";

        cut.Render();

        cut.FindAll(".markdown-body [data-source-line]").Select(e => e.GetAttribute("data-source-line"))
            .Should().Equal("1", "3", "5");
    }

    [Fact]
    public async Task PostEditPage_SaveError_ShowsTheProblemDetailAtTheTop()
    {
        Setup();
        _api.PostFailure = new HttpRequestException("Title is required.");
        var cut = Render<PostEditPage>();

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find(".post-edit__error").TextContent.Should().Contain("Title is required.");
        });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(220, 1)]
    [InlineData(221, 2)]
    [InlineData(660, 3)]
    public void PostEditPage_Preview_ReadingMinutes_FollowsTheSpecCases(int words, int expected)
    {
        // The preview shows the reading time the API will store; both sides share the rule
        // (Croft.Shared.ReadingTime) and these are the spec's documented cases (ruling R5-3-d).
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = string.Join(' ', Enumerable.Repeat("w", words));

        cut.Render();

        cut.Find(".post-meta__when").TextContent.Should().Be($"{expected} min read");
    }

    [Fact]
    public void PostEditPage_Preview_ReadingMinutes_ExcludesFencedCode()
    {
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "```\n" + string.Join(' ', Enumerable.Repeat("w", 500)) + "\n```\n\nhi";

        cut.Render();

        cut.Find(".post-meta__when").TextContent.Should().Be("1 min read",
            "code contributes no words, exactly like the API's stored reading time");
    }

    [Fact]
    public async Task PostEditPage_TabFeedbackQuery_OpensTheFeedbackTab()
    {
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Published, Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, Now.UtcDateTime,
            "author-id", "Author", null, [], null, 1);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/admin/posts/{id}/edit?tab=feedback");

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit"));

        cut.Find(".mud-tab-active").TextContent.Should().Be("Feedback",
            "?tab=feedback opens the editor on the Feedback tab (the row action's target)");
    }

    [Fact]
    public async Task EditRoute_HeadingShowsEditAlone_UntilThePostLoads()
    {
        // On an edit route the kind comes from the loaded post, so before it lands the heading
        // must say "Edit" alone: "Edit Post" would flash on every page being edited. The read is
        // held on a gate so the test observes the waiting state, then released to prove the noun
        // arrives with the post.
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = PagePost(id);
        _api.PostByIdGate = new TaskCompletionSource();

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.Find(".page-header__title").TextContent.Trim().Should().Be("Edit",
            "before the post loads the kind is unknown and the heading says 'Edit' alone");

        _api.PostByIdGate.SetResult();
        await cut.WaitForAssertionAsync(() =>
            cut.Find(".page-header__title").TextContent.Trim().Should().Be("Edit Page",
                "once the page's own Kind has landed, the heading names the kind"));
    }

    /// <summary>A page (PostKind.Page) as the API hands it to the editor, with its navigation flags.</summary>
    private static PostDto PagePost(Guid id, NavigationPlacement placement = NavigationPlacement.Footer, int navigationOrder = 5) => new(
        id, "about", "About", "A subtitle", "Some **body**.", "An excerpt", null,
        PostStatus.Published, Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, Now.UtcDateTime,
        "author-id", "Author", null, [], null, 1,
        Kind: PostKind.Page, NavigationPlacement: placement, NavigationOrder: navigationOrder);

    [Fact]
    public async Task NewPageRoute_CreatesAPage_WithKindAndTheNavigationFlags()
    {
        Setup();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/pages/new");
        var cut = Render<PostEditPage>();

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "About";
        form.Model.MarkdownContent = "Body.";
        form.Model.NavigationPlacement = NavigationPlacement.Footer;
        form.Model.NavigationOrder = 2;

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var request = _api.CreatedPosts.Single();
        request.Kind.Should().Be(PostKind.Page, "the /admin/pages/new route creates a page");
        request.NavigationPlacement.Should().Be(NavigationPlacement.Footer, "the chosen placement travels with the create");
        request.NavigationOrder.Should().Be(2, "the position travels with the create");
    }

    [Fact]
    public async Task NewPageRoute_AfterTheFirstSave_GoesToThePagesEditRoute()
    {
        Setup();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin/pages/new");
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "About";
        form.Model.MarkdownContent = "Body.";

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var created = _api.PostsById.Keys.Single();
        navigation.Uri.Should().EndWith($"/admin/pages/{created}/edit", "a new page stays under /admin/pages");
    }

    [Fact]
    public async Task NewPostRoute_AfterTheFirstSave_StaysUnderPosts()
    {
        Setup();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin/posts/new");
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var created = _api.PostsById.Keys.Single();
        navigation.Uri.Should().EndWith($"/admin/posts/{created}/edit");
    }

    [Fact]
    public async Task AnOldPostsEditUrl_OfAPage_RedirectsToThePagesRoute_KeepingTheQuery()
    {
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = PagePost(id);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/admin/posts/{id}/edit?tab=feedback");

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        // The redirect runs when the page's load completes: wait for its outcome (the fake completes
        // synchronously today, which a bare yield relied on).
        await cut.WaitForAssertionAsync(() =>
            navigation.Uri.Should().EndWith($"/admin/pages/{id}/edit?tab=feedback", "bookmarks to the old URL still work"));
    }

    [Fact]
    public async Task APostOpenedOnThePagesRoute_RedirectsToThePostsRoute()
    {
        Setup();
        var id = Guid.CreateVersion7();
        var post = PagePost(id) with { Kind = PostKind.Post, NavigationPlacement = NavigationPlacement.None };
        _api.PostsById[id] = post;
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/admin/pages/{id}/edit");

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => navigation.Uri.Should().EndWith($"/admin/posts/{id}/edit"));
    }

    [Fact]
    public async Task NewPostRoute_CreatesAPost()
    {
        Setup();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/posts/new");
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        _api.CreatedPosts.Single().Kind.Should().Be(PostKind.Post,
            "the post route keeps creating posts");
    }

    [Fact]
    public async Task EditPage_Update_CarriesTheExistingKind_AndTheNavigationFlags()
    {
        // Every update must carry the post's existing Kind: the API refuses a Kind change with a
        // 400, so an update that drops it would make a page unsaveable.
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = PagePost(id);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.UpdatedPosts.Should().ContainSingle());
        var request = _api.UpdatedPosts.Single().Request;
        request.Kind.Should().Be(PostKind.Page, "the update carries the page's existing Kind");
        request.NavigationPlacement.Should().Be(NavigationPlacement.Footer, "the loaded placement round-trips through the form");
        request.NavigationOrder.Should().Be(5, "the loaded position round-trips through the form");
    }

    private const string LoadedMastodon = "https://mastodon.example/@owner/1";
    private const string LoadedBluesky = "https://bsky.app/profile/owner.example/post/3l6oveex3ii2l";

    private Guid SeedLinkedPost(string? mastodon = LoadedMastodon, string? bluesky = LoadedBluesky)
    {
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Draft, null, Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1,
            MastodonUrl: mastodon, BlueskyUrl: bluesky);
        return id;
    }

    private async Task<UpdatePostRequest> SaveEditedAsync(IRenderedComponent<PostEditPage> cut)
    {
        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.UpdatedPosts.Should().ContainSingle());
        return _api.UpdatedPosts.Single().Request;
    }

    [Fact]
    public async Task EditPage_LoadsTheDiscussionLinksIntoTheFields()
    {
        Setup();
        var id = SeedLinkedPost();

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.Find("#post-mastodon-url").GetAttribute("value").Should().Be(LoadedMastodon);
        cut.Find("#post-bluesky-url").GetAttribute("value").Should().Be(LoadedBluesky);
    }

    [Fact]
    public async Task EditPage_Update_UntouchedLinks_SendNull_SoAnUnrelatedEditNeverRevalidatesThem()
    {
        Setup();
        var id = SeedLinkedPost();
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        var request = await SaveEditedAsync(cut);

        request.MastodonUrl.Should().BeNull("an unchanged link is sent as null (unchanged)");
        request.BlueskyUrl.Should().BeNull();
    }

    [Fact]
    public async Task EditPage_Update_EmptiedLinks_SendEmptyString_ToClearThem()
    {
        Setup();
        var id = SeedLinkedPost();
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        cut.Find("#post-mastodon-url").Input("");
        cut.Find("#post-bluesky-url").Input("   ");

        var request = await SaveEditedAsync(cut);

        request.MastodonUrl.Should().Be("", "an emptied field clears the link");
        request.BlueskyUrl.Should().Be("", "a field of only spaces is empty");
    }

    [Fact]
    public async Task EditPage_Update_ChangedLinks_SendTheTrimmedValue()
    {
        Setup();
        var id = SeedLinkedPost(mastodon: null, bluesky: LoadedBluesky);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        cut.Find("#post-mastodon-url").Input("  https://mastodon.example/@owner/2  ");
        cut.Find("#post-bluesky-url").Input(" https://bsky.app/profile/owner.example/post/other ");

        var request = await SaveEditedAsync(cut);

        request.MastodonUrl.Should().Be("https://mastodon.example/@owner/2");
        request.BlueskyUrl.Should().Be("https://bsky.app/profile/owner.example/post/other");
    }

    [Fact]
    public async Task EditPage_Update_NoLinksAtAll_SendNull()
    {
        Setup();
        var id = SeedLinkedPost(mastodon: null, bluesky: null);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        cut.Find("#post-mastodon-url").Input("   ");

        var request = await SaveEditedAsync(cut);

        request.MastodonUrl.Should().BeNull("nothing was linked and nothing is entered: there is nothing to clear");
        request.BlueskyUrl.Should().BeNull();
    }

    [Fact]
    public async Task EditPage_Update_ALoadedLinkWithSurroundingSpaces_UnchangedInTheField_SendsNull()
    {
        Setup();
        var id = SeedLinkedPost(mastodon: $"  {LoadedMastodon} ", bluesky: null);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        var request = await SaveEditedAsync(cut);

        request.MastodonUrl.Should().BeNull("the field still holds what was loaded; the stray spaces are not a change");
    }

    [Fact]
    public async Task EditPage_AfterASave_TheSavedLinkIsTheNewBaseline()
    {
        // The second save of an unchanged field must not re-send what the first save sent.
        Setup();
        var id = SeedLinkedPost(mastodon: null, bluesky: null);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        cut.Find("#post-mastodon-url").Input(LoadedMastodon);
        await SaveEditedAsync(cut);

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.UpdatedPosts.Should().HaveCount(2));

        _api.UpdatedPosts[0].Request.MastodonUrl.Should().Be(LoadedMastodon);
        _api.UpdatedPosts[1].Request.MastodonUrl.Should().BeNull("the link was saved by the first save, so the second leaves it");
    }

    [Fact]
    public async Task NewPost_Create_SendsTheTrimmedLinks_AndNullWhenEmpty()
    {
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        cut.Find("#post-mastodon-url").Input("  https://mastodon.example/@owner/1 ");
        cut.Find("#post-bluesky-url").Input("   ");

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var request = _api.CreatedPosts.Single();
        request.MastodonUrl.Should().Be("https://mastodon.example/@owner/1");
        request.BlueskyUrl.Should().BeNull("an empty field on create is no link");
    }

    [Fact]
    public async Task NewPost_Create_WithNoLinks_SendsNull()
    {
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        _api.CreatedPosts.Single().MastodonUrl.Should().BeNull();
        _api.CreatedPosts.Single().BlueskyUrl.Should().BeNull();
    }

    [Fact]
    public async Task APage_NeverShowsTheDiscussionFields_AndNeverSendsLinks()
    {
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = PagePost(id) with { MastodonUrl = LoadedMastodon, BlueskyUrl = LoadedBluesky };
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.FindAll("#post-mastodon-url").Should().BeEmpty();
        cut.FindAll("#post-bluesky-url").Should().BeEmpty();
        cut.Markup.Should().NotContain("Discussion");
        var request = await SaveEditedAsync(cut);
        request.MastodonUrl.Should().BeNull();
        request.BlueskyUrl.Should().BeNull();
    }

    [Fact]
    public async Task ANewPage_NeverSendsLinks()
    {
        Setup();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/pages/new");
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "About";
        form.Model.MarkdownContent = "Body.";
        form.Model.MastodonUrl = "https://mastodon.example/@owner/1";

        cut.FindAll("#post-mastodon-url").Should().BeEmpty();
        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        _api.CreatedPosts.Single().MastodonUrl.Should().BeNull("a page has no discussion");
        _api.CreatedPosts.Single().BlueskyUrl.Should().BeNull();
    }

    [Fact]
    public async Task APostShowsTheDiscussionHeadingAndBothFieldsWithTheirHelpers()
    {
        Setup();
        var cut = Render<PostEditPage>();

        cut.Find(".post-form__discussion h2, .post-form__discussion h3").TextContent.Should().Be("Discussion");
        cut.Markup.Should().Contain("Mastodon post URL").And.Contain("Bluesky post URL")
            .And.Contain("The post that announces this one on Mastodon")
            .And.Contain("The post that announces this one on Bluesky");
        foreach (var id in new[] { "#post-mastodon-url", "#post-bluesky-url" })
        {
            cut.Find(id).GetAttribute("inputmode").Should().Be("url");
            cut.Find(id).GetAttribute("spellcheck").Should().Be("false");
        }
    }

    [Theory]
    [InlineData("#post-mastodon-url", "Mastodon post URL")]
    [InlineData("#post-bluesky-url", "Bluesky post URL")]
    public void TheLinkFields_AreLabelled_SoABrowserAndAScreenReaderFindThemByTheirLabel(string selector, string label)
    {
        Setup();
        var cut = Render<PostEditPage>();

        var input = cut.Find(selector);
        var labelFor = cut.FindAll("label").Single(l => l.TextContent.Trim() == label).GetAttribute("for");

        labelFor.Should().Be(input.Id, "the label points at the input (GetByLabel and axe's label rule depend on it)");
    }

    [Theory]
    [InlineData("MastodonUrl must be a post on social.example.", "#post-mastodon-url", "#post-bluesky-url")]
    [InlineData("BlueskyUrl must be a post of the configured Bluesky account.", "#post-bluesky-url", "#post-mastodon-url")]
    public async Task ALinkFailure_ShowsUnderItsField_NotInTheGeneralLine(string detail, string failing, string other)
    {
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        cut.Find("#post-mastodon-url").Input("https://x.example/1");
        cut.Find("#post-bluesky-url").Input("https://y.example/1");
        _api.PostFailure = new HttpRequestException(detail);

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(detail));
        cut.FindAll(".post-edit__error").Should().BeEmpty("a field's failure is not the page's general error");
        var field = cut.FindAll(".post-form__link").Single(f => f.QuerySelector(failing) is not null);
        field.TextContent.Should().Contain(detail, "the message is under the field it names");
        var untouched = cut.FindAll(".post-form__link").Single(f => f.QuerySelector(other) is not null);
        untouched.TextContent.Should().NotContain(detail);
        cut.Find(failing).GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("#post-mastodon-url").GetAttribute("value").Should().Be("https://x.example/1", "the rest of the form is unchanged");
    }

    [Fact]
    public async Task AnotherFailure_StaysInTheGeneralLine_AndNotUnderTheFields()
    {
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        cut.Find("#post-mastodon-url").Input("https://x.example/1");
        _api.PostFailure = new HttpRequestException("Title is required.");

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit__error").TextContent.Should().Contain("Title is required."));
        cut.FindAll(".post-form__link .mud-input-helper-text.mud-input-error").Should().BeEmpty();
        cut.Find("#post-mastodon-url").GetAttribute("aria-invalid").Should().NotBe("true");
    }

    [Fact]
    public async Task TypingInAField_ClearsItsError_AndOnlyItsOwn()
    {
        Setup();
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        cut.Find("#post-mastodon-url").Input("https://x.example/1");
        _api.PostFailure = new HttpRequestException("MastodonUrl must be a post on social.example.");
        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("MastodonUrl must be a post on"));

        cut.Find("#post-bluesky-url").Input("https://y.example/1");
        cut.Markup.Should().Contain("MastodonUrl must be a post on", "typing elsewhere leaves the error");
        cut.Find("#post-mastodon-url").Input("https://x.example/2");

        cut.Markup.Should().NotContain("MastodonUrl must be a post on");
    }

    [Fact]
    public async Task EditPage_StaysInPageModeAfterSaving()
    {
        // Page mode persists across saves: the editor's mode comes from the post's Kind, and a
        // save must not flip the page back into post mode.
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = PagePost(id);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.UpdatedPosts.Should().HaveCount(1));
        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.UpdatedPosts.Should().HaveCount(2));

        _api.UpdatedPosts.Select(u => u.Request.Kind).Should().Equal(PostKind.Page, PostKind.Page);
        cut.FindComponents<MudAutocomplete<CategoryDto>>().Should().BeEmpty("the editor is still in page mode");
        cut.FindAll("#post-nav-placement").Should().ContainSingle("the editor is still in page mode");
    }

    [Fact]
    public void EditPage_Preview_RendersThePageSurface_NotThePostHero()
    {
        // The preview must show the page layout (brief 07g item 3's PageSurface), not the post
        // hero — the preview is how the owner sees what readers will get at /<slug>.
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = PagePost(id);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.Find(".page-surface__title").TextContent.Trim().Should().Be("About",
            "the Preview tab renders the page layout with the page's own title");
        cut.FindAll(".post-hero").Should().BeEmpty("a page preview is not the post hero");
        cut.Find(".page-surface .markdown-body").InnerHtml.Should().Contain("<strong>body</strong>");
    }

    [Fact]
    public async Task PostEditPage_StatusAfterPublish_ComesFromTheApiResponseNotTheClientClock()
    {
        Setup();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Draft, null, Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1);
        // The API is authoritative for the status its clock computes at the moment of the
        // request; the client's clock may differ (skew), so the response decides what shows.
        _api.PublishStatus = (_, _) => PostStatus.Published;
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        form.Model.PublishedAtDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        form.Model.PublishedAtTime = new TimeSpan(9, 30, 0);

        cut.FindAll("button").Single(b => b.TextContent == "Schedule").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".status-badge").TextContent.Should().Be("Published",
                "the status after publish comes from the API's response, not the client's clock"));
    }

    [Fact]
    public async Task StatusRegion_AnnouncesSaved_Published_Scheduled_AndUnpublished()
    {
        // Brief 13g item 6 (review I-4): each outcome is written into the one status region.
        var clock = Setup();
        _api.PublishStatus = (_, request) => request.PublishedAt > clock.GetUtcNow().UtcDateTime ? PostStatus.Scheduled : PostStatus.Published;
        var cut = Render<PostEditPage>();
        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.Model.Title = "A post";
        form.Model.MarkdownContent = "Body.";
        string Region() => cut.Find("[role=status].post-actions__saved").TextContent.Trim();

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => Region().Should().Be("Saved 12:00"));

        cut.FindAll("button").Single(b => b.TextContent == "Publish now").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => Region().Should().Be("Published"));

        cut.FindAll("button").Single(b => b.TextContent == "Unpublish").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => Region().Should().Be("Unpublished"));

        form.Model.PublishedAtDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        form.Model.PublishedAtTime = new TimeSpan(9, 30, 0);
        cut.FindAll("button").Single(b => b.TextContent == "Schedule").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => Region().Should().Be("Scheduled for 2026-12-01 09:30 UTC"));
    }
}
