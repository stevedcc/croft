using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Services;
using AngleSharp.Dom;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// MediaGrid (spec §6.4/§7.4): the manage grid lists, uploads, saves alt text on blur, copies
/// markdown/URL and deletes (with a confirm); the pick grid raises OnPicked. Colour/markup aside,
/// the behaviours pinned here are the ones the admin page and the editor dialog depend on.
/// </summary>
public class MediaGridTests : BlogBunitContext
{
    private static MediaAssetDto Asset(string name = "photo.png", string? alt = null)
    {
        var id = Guid.CreateVersion7();
        return new MediaAssetDto(id, $"{id:N}", name, "image/png", $"/api/media/{id}",
            MediaAssetScope.Global, null, alt);
    }

    private static FakeBlogApiClient FakeWith(params MediaAssetDto[] assets) => new() { GlobalMedia = [.. assets] };

    private static void Upload(IRenderedComponent<MediaGrid> cut, params (string Name, string Body)[] files)
    {
        var content = files
            .Select(f => InputFileContent.CreateFromBinary(System.Text.Encoding.UTF8.GetBytes(f.Body), f.Name, null, "image/png"))
            .ToArray();
        cut.FindComponent<InputFile>().UploadFiles(content);
    }

    private static IElement DeleteButton(IRenderedComponent<MediaGrid> cut) =>
        cut.FindAll(".media-tile__actions button").Single(b => b.TextContent == "Delete");

    private static IElement ConfirmDeleteButton(IRenderedComponent<MediaGrid> cut) =>
        cut.FindAll(".media-tile__confirm button").Single(b => b.TextContent == "Delete image");

    [Fact]
    public async Task FilterBox_SendsQToTheServer_AndShowsOnlyTheMatchingFiles()
    {
        var one = Asset("one.png");
        var fake = FakeWith(one, Asset("two.png"));
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();
        cut.Find(".media-tile__name").TextContent.Should().Be("one.png");

        var search = cut.FindComponents<MudBlazor.MudTextField<string>>().Single(f => f.Instance.Label == "Search filenames");
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("one"));
        search.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await cut.WaitForAssertionAsync(() =>
        {
            fake.GlobalMediaRequests.Last().Q.Should().Be("one", "the filename filter runs on the server");
            cut.FindAll(".media-tile").Should().HaveCount(1);
            cut.Find(".media-tile__name").TextContent.Should().Be("one.png");
        });
    }

    [Fact]
    public async Task Tiles_RenderOnePageAtATime_AndPageOnTheServer()
    {
        var fake = FakeWith([.. Enumerable.Range(1, 25).Select(n => Asset($"image-{n:00}.png"))]);
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        cut.FindAll(".media-tile").Should().HaveCount(24, "one page of thumbnails renders, never the whole library");

        var pager = cut.FindComponent<MudBlazor.MudPagination>();
        await cut.InvokeAsync(() => pager.Instance.SelectedChanged.InvokeAsync(2));

        await cut.WaitForAssertionAsync(() =>
        {
            fake.GlobalMediaRequests.Last().Page.Should().Be(2);
            cut.FindAll(".media-tile").Should().HaveCount(1, "page 2 shows the remaining tile");
        });
    }

    [Fact]
    public void ManageMode_RendersATilePerGlobalAsset()
    {
        var one = Asset("one.png");
        var two = Asset("two.png", "Second image");
        var fake = FakeWith(one, two);
        Services.AddSingleton<IBlogApiClient>(fake);

        var cut = Render<MediaGrid>();

        var tiles = cut.FindAll(".media-tile");
        tiles.Should().HaveCount(2);
        tiles[0].QuerySelector("img")!.GetAttribute("src").Should().Be(one.Url);
        tiles[0].QuerySelector(".media-tile__name")!.TextContent.Should().Be("one.png");
        tiles[1].QuerySelector("img")!.GetAttribute("alt").Should().Be("Second image", "the alt text is the image's alt, falling back to the file name");
    }

    [Fact]
    public void EmptyLibrary_ShowsNoImagesYetWithoutErrorsOrImages()
    {
        Services.AddSingleton<IBlogApiClient>(FakeWith());

        var cut = Render<MediaGrid>();

        cut.Find(".media-grid__empty").TextContent.Should().Be("No images yet.");
        cut.FindAll("img").Should().BeEmpty("an empty library must not render broken images");
        cut.FindAll(".media-grid__error").Should().BeEmpty("an empty library is not an error");
    }

    [Fact]
    public void MultiUpload_AddsATilePerFile_AndStoresThemAsGlobal()
    {
        var fake = FakeWith();
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        Upload(cut, ("one.png", "first"), ("two.png", "second"));

        cut.FindAll(".media-tile").Should().HaveCount(2, "one tile per uploaded file");
        cut.Markup.Should().Contain("one.png").And.Contain("two.png");
        fake.Uploads.Should().HaveCount(2);
        fake.Uploads.Should().OnlyContain(u => u.PostId == null && u.Scope == "Global");
        fake.Uploads.Select(u => u.FileName).Should().BeEquivalentTo("one.png", "two.png");
    }

    [Fact]
    public async Task Upload_ShowsTheProgressIndicator_UntilTheUploadsFinish()
    {
        var fake = FakeWith();
        fake.UploadGate = new TaskCompletionSource();
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        // WORKAROUND(W-076, bUnit): UploadFiles blocks until the handler finishes, so the dispatch runs on a worker thread
        // bUnit's UploadFiles blocks until the handler finishes, so the dispatch runs on a worker
        // thread while the test observes the in-progress state and releases the gate.
        var dispatch = Task.Run(() => Upload(cut, ("one.png", "body")));
        // END WORKAROUND(W-076)

        cut.WaitForAssertion(() => cut.Find(".media-upload__progress").Should().NotBeNull(),
            TimeSpan.FromSeconds(5));

        fake.UploadGate.SetResult();
        await dispatch;

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".media-upload__progress").Should().BeEmpty();
            cut.FindAll(".media-tile").Should().HaveCount(1);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AltText_SavesOnBlur()
    {
        var asset = Asset("photo.png");
        var fake = FakeWith(asset);
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        var input = cut.Find(".media-tile__alt input");
        input.Change(new ChangeEventArgs { Value = "Sunset over the hills" });
        input.Blur(new FocusEventArgs());

        fake.AltTextUpdates.Should().ContainSingle().Which.Should().Be((asset.Id, "Sunset over the hills"));
    }

    [Fact]
    public void AltText_SaveFailure_ShowsTheInlineError()
    {
        var asset = Asset("photo.png");
        var fake = FakeWith(asset);
        fake.MediaUpdateFailure = new HttpRequestException("The API said no.");
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        var input = cut.Find(".media-tile__alt input");
        input.Change(new ChangeEventArgs { Value = "Sunset over the hills" });
        input.Blur(new FocusEventArgs());

        cut.Find(".media-grid__error").TextContent.Should().Be("The API said no.",
            "a failed alt-text save surfaces inline, like the upload and delete actions");
    }

    [Fact]
    public void InitialLoad_Failure_ShowsTheInlineError_WithoutAnEmptyStateClaim()
    {
        var fake = FakeWith();
        fake.MediaLoadFailure = new HttpRequestException("The API is down.");
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        cut.Find(".media-grid__error").TextContent.Should().Be("The API is down.");
        cut.FindAll(".media-grid__empty").Should().BeEmpty("a failed load does not know the library is empty");
    }

    [Fact]
    public void CopyMarkdown_WritesTheExactMarkdownToTheClipboard()
    {
        var asset = Asset("photo.png", "Sunset over the hills");
        Services.AddSingleton<IBlogApiClient>(FakeWith(asset));
        var cut = Render<MediaGrid>();
        // The matcher overload records the call whatever text is written — the assertion below is
        // what pins the markdown, not the setup.
        var clipboard = JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);

        cut.FindAll(".media-tile__actions button").Single(b => b.TextContent == "Copy Markdown").Click(new MouseEventArgs());

        clipboard.Invocations.Should().ContainSingle();
        clipboard.Invocations.Single().Arguments[0].Should().Be($"![Sunset over the hills]({asset.Url})");
    }

    [Fact]
    public void CopyUrl_WritesTheAssetUrlToTheClipboard()
    {
        var asset = Asset("photo.png");
        Services.AddSingleton<IBlogApiClient>(FakeWith(asset));
        var cut = Render<MediaGrid>();
        var clipboard = JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);

        cut.FindAll(".media-tile__actions button").Single(b => b.TextContent == "Copy URL").Click(new MouseEventArgs());

        clipboard.Invocations.Should().ContainSingle();
        clipboard.Invocations.Single().Arguments[0].Should().Be(asset.Url);
    }

    [Fact]
    public void Upload_Failure_ShowsTheErrorAndClearsTheProgressIndicator()
    {
        var fake = FakeWith();
        fake.UploadFailure = new HttpRequestException("413 Payload Too Large");
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        Upload(cut, ("huge.png", "body"));

        cut.Find(".media-grid__error").TextContent.Should().Contain("10 MB",
            "the error tells the user what may be uploaded");
        cut.FindAll(".media-upload__progress").Should().BeEmpty("the progress indicator must not stick after a failure");
        cut.FindAll(".media-tile").Should().BeEmpty();
    }

    [Fact]
    public void Delete_AsksForConfirmation_AndWarnsAboutBrokenImages()
    {
        var asset = Asset("photo.png");
        var fake = FakeWith(asset);
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        DeleteButton(cut).Click(new MouseEventArgs());

        cut.Find(".media-tile__confirm").TextContent.Should().Contain(
            "Posts embedding the image in their content will show a broken image.");
        fake.DeletedIds.Should().BeEmpty("nothing is deleted before the confirmation");
        cut.FindAll(".media-tile").Should().HaveCount(1);
    }

    [Fact]
    public void Delete_AfterConfirmation_RemovesTheTileAndDeletesTheAsset()
    {
        var asset = Asset("photo.png");
        var fake = FakeWith(asset);
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        DeleteButton(cut).Click(new MouseEventArgs());
        ConfirmDeleteButton(cut).Click(new MouseEventArgs());

        fake.DeletedIds.Should().Equal(asset.Id);
        cut.FindAll(".media-tile").Should().BeEmpty("the deleted image is gone from the grid");
    }

    [Fact]
    public void Delete_RefusedByTheApi_ShowsTheMessageWithThePostTitle_AndKeepsTheTile()
    {
        var asset = Asset("photo.png");
        var fake = FakeWith(asset);
        fake.DeleteFailure = new DeleteConflictException("Cannot delete: used as a banner image by Banner Fixture Post.");
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        DeleteButton(cut).Click(new MouseEventArgs());
        ConfirmDeleteButton(cut).Click(new MouseEventArgs());

        cut.Find(".media-grid__error").TextContent.Should().Contain("Banner Fixture Post",
            "the refusal message must name the post(s) that use the image as a banner");
        cut.FindAll(".media-tile").Should().HaveCount(1, "the refused image stays in the grid");
        cut.FindAll(".media-tile__confirm").Should().BeEmpty();
    }

    [Fact]
    public void DeleteConfirmations_ArePerTile()
    {
        var fake = FakeWith(Asset("one.png"), Asset("two.png"));
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>();

        // Re-queried per click: the first click re-renders and detaches the old element handles.
        for (var i = 0; i < 2; i++)
        {
            var tile = cut.FindAll(".media-tile")[i];
            tile.QuerySelectorAll("button").Single(b => b.TextContent == "Delete").Click(new MouseEventArgs());
        }

        cut.FindAll(".media-tile__confirm").Should().HaveCount(2,
            "opening the delete confirmation on a second tile must not cancel the first");
    }

    [Fact]
    public void PickMode_WithPostId_DefaultsToAllImages_AndTheToggleSwitchesTheListingSource()
    {
        // Spec §6.4: opened from the editor, the grid shows the global images first and can be
        // toggled to "this post's images".
        var global = Asset("global.png");
        var postId = Guid.CreateVersion7();
        var post = new MediaAssetDto(Guid.CreateVersion7(), "p.png", "post.png", "image/png",
            "/api/media/p", MediaAssetScope.PostSpecific, postId, null);
        var fake = new FakeBlogApiClient { GlobalMedia = [global], PostMedia = [post] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>(p => p
            .Add(g => g.Mode, MediaGridMode.Pick)
            .Add(g => g.PostId, postId));

        fake.PostMediaRequestedFor.Should().BeNull("the global images are the default scope");
        cut.Find(".media-tile__name").TextContent.Should().Be("global.png");

        cut.FindAll("button").Single(b => b.TextContent == "This post's images").Click(new MouseEventArgs());
        cut.WaitForAssertion(() =>
        {
            fake.PostMediaRequestedFor.Should().Be(postId);
            cut.Find(".media-tile__name").TextContent.Should().Be("post.png");
        });

        cut.FindAll("button").Single(b => b.TextContent == "All images").Click(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".media-tile__name").TextContent.Should().Be("global.png"));
    }

    [Fact]
    public void PickMode_WithPostId_PostScopeWithNoImages_ShowsNoImagesYet()
    {
        var postId = Guid.CreateVersion7();
        var fake = new FakeBlogApiClient { GlobalMedia = [Asset("global.png")] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>(p => p
            .Add(g => g.Mode, MediaGridMode.Pick)
            .Add(g => g.PostId, postId));

        cut.FindAll("button").Single(b => b.TextContent == "This post's images").Click(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".media-grid__empty").TextContent.Should().Be("No images yet.");
            cut.FindAll(".media-tile").Should().BeEmpty();
        });
    }

    [Fact]
    public void PickMode_WithoutPostId_ShowsNoScopeToggle()
    {
        Services.AddSingleton<IBlogApiClient>(FakeWith(Asset("global.png")));
        var cut = Render<MediaGrid>(p => p.Add(g => g.Mode, MediaGridMode.Pick));

        cut.FindAll(".media-grid__scopes").Should().BeEmpty("a fresh post has no images of its own to switch to");
        cut.Find(".media-tile__name").TextContent.Should().Be("global.png");
    }

    [Fact]
    public void ManageMode_WithPostId_ShowsNoScopeToggle()
    {
        // The manage grid keeps R4.3's behaviour: with a PostId it is "this post's images".
        var postId = Guid.CreateVersion7();
        var fake = new FakeBlogApiClient();
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>(p => p.Add(g => g.PostId, postId));

        cut.FindAll(".media-grid__scopes").Should().BeEmpty();
        fake.PostMediaRequestedFor.Should().Be(postId);
    }

    [Fact]
    public void PickMode_ClickingATile_RaisesOnPicked()
    {
        var asset = Asset("photo.png");
        Services.AddSingleton<IBlogApiClient>(FakeWith(asset));
        var picked = new List<MediaAssetDto>();
        var cut = Render<MediaGrid>(p => p
            .Add(g => g.Mode, MediaGridMode.Pick)
            .Add(g => g.OnPicked, EventCallback.Factory.Create<MediaAssetDto>(this, picked.Add)));

        cut.Find(".media-tile__pick").Click(new MouseEventArgs());

        picked.Should().ContainSingle().Which.Id.Should().Be(asset.Id);
    }

    [Fact]
    public void PostIdMode_ListsThePostsMedia_AndUploadsPostSpecific()
    {
        var postId = Guid.CreateVersion7();
        var asset = new MediaAssetDto(Guid.CreateVersion7(), "f.png", "draft.png", "image/png",
            "/api/media/f", MediaAssetScope.PostSpecific, postId, null);
        var fake = new FakeBlogApiClient { PostMedia = [asset] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<MediaGrid>(p => p.Add(g => g.PostId, postId));

        fake.PostMediaRequestedFor.Should().Be(postId);
        cut.Find(".media-tile__name").TextContent.Should().Be("draft.png");

        Upload(cut, ("new.png", "x"));

        fake.Uploads.Should().ContainSingle().Which.Should().Be((postId, "PostSpecific", "new.png"));
    }

    [Fact]
    public void DragEnter_HighlightsTheDropZone_DragLeave_ClearsIt()
    {
        Services.AddSingleton<IBlogApiClient>(FakeWith());
        var cut = Render<MediaGrid>();

        cut.Find(".media-upload").DragEnter(new DragEventArgs());
        cut.Find(".media-upload").ClassList.Should().Contain("media-upload--active");

        cut.Find(".media-upload").DragLeave(new DragEventArgs());
        cut.Find(".media-upload").ClassList.Should().NotContain("media-upload--active");
    }
}
