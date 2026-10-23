using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 12d item 6, the page's part: the announcement text and flags travel with a save (null when unchanged, "" when emptied, the flags as they are),
/// the API's refusal of the text shows under its field, the Discussion tab's Announcements block (one line per network, with "Announce now" / "Retry"),
/// and the status message after Publish now.
/// </summary>
public class PostEditPageAnnouncementTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid PostId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private FakeBlogApiClient _api = null!;

    private void Setup()
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        _api = Services.GetRequiredService<FakeBlogApiClient>();

        // The editor polls and reads the announcements only in the interactive phase (RendererInfo.IsInteractive).
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private void SeedPost(PostStatus status = PostStatus.Draft, string? text = null, bool mastodon = true, bool bluesky = true, string? mastodonUrl = null, string? blueskyUrl = null) =>
        _api.PostsById[PostId] = new PostDto(PostId, "a-post", "A post", null, "Body.", null, null,
            status, status == PostStatus.Draft ? null : Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1,
            MastodonUrl: mastodonUrl, BlueskyUrl: blueskyUrl, AnnouncementText: text, AnnounceMastodon: mastodon, AnnounceBluesky: bluesky);

    private IRenderedComponent<PostEditPage> RenderEditor() => Render<PostEditPage>(p => p.Add(x => x.Id, PostId));

    private IRenderedComponent<PostEditPage> RenderNew()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/posts/new");
        var cut = Render<PostEditPage>();
        var model = cut.FindComponent<PostEditorForm>().Instance.Model;
        model.Title = "A post";
        model.MarkdownContent = "Body.";
        return cut;
    }

    private static void Click(IRenderedComponent<PostEditPage> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == text).Click(new MouseEventArgs());

    private async Task<UpdatePostRequest> SaveEditedAsync(IRenderedComponent<PostEditPage> cut)
    {
        Click(cut, "Save draft");
        await cut.WaitForAssertionAsync(() => _api.UpdatedPosts.Should().NotBeEmpty());
        return _api.UpdatedPosts[^1].Request;
    }

    // ---- what a save sends ----

    [Fact]
    public async Task ANewPost_SendsTheFlagsOn_AndNoTextByDefault()
    {
        Setup();
        var cut = RenderNew();

        Click(cut, "Save draft");

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var request = _api.CreatedPosts.Single();
        (request.AnnouncementText, request.AnnounceMastodon, request.AnnounceBluesky).Should().Be((null, true, true));
    }

    [Fact]
    public async Task ANewPost_SendsTheTypedText_AndTheFlagsAsTheyAre()
    {
        Setup();
        var cut = RenderNew();
        cut.Find("#announcement-text").Input("My own words #homelab");
        cut.Find("#announce-bluesky").Change(false);

        Click(cut, "Save draft");

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var request = _api.CreatedPosts.Single();
        (request.AnnouncementText, request.AnnounceMastodon, request.AnnounceBluesky).Should().Be(("My own words #homelab", true, false));
    }

    [Fact]
    public async Task Update_LoadsTheTextAndFlagsIntoTheFields()
    {
        Setup();
        SeedPost(text: "Saved words", mastodon: false, bluesky: true);

        var cut = RenderEditor();

        cut.Find("#announcement-text").TextContent.Should().Be("Saved words");
        cut.Find("#announce-mastodon").HasAttribute("checked").Should().BeFalse();
        cut.Find("#announce-bluesky").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task Update_AnUnchangedText_IsSentAsNull_AndTheFlagsAsTheyAre()
    {
        Setup();
        SeedPost(text: "Saved words", mastodon: true, bluesky: false);
        var cut = RenderEditor();

        var request = await SaveEditedAsync(cut);

        request.AnnouncementText.Should().BeNull("the text is unchanged, so the API leaves it alone");
        (request.AnnounceMastodon, request.AnnounceBluesky).Should().Be((true, false));
    }

    [Fact]
    public async Task Update_AnEmptiedText_IsSentAsEmpty_ToGoBackToTheDefault()
    {
        Setup();
        SeedPost(text: "Saved words");
        var cut = RenderEditor();
        cut.Find("#announcement-text").Input("");

        var request = await SaveEditedAsync(cut);

        request.AnnouncementText.Should().Be("");
    }

    [Fact]
    public async Task Update_AChangedText_IsSent()
    {
        Setup();
        SeedPost(text: "Saved words");
        var cut = RenderEditor();
        cut.Find("#announcement-text").Input("Better words");

        var request = await SaveEditedAsync(cut);

        request.AnnouncementText.Should().Be("Better words");
    }

    [Fact]
    public async Task Update_NothingTypedAndNothingSaved_SendsNull_NotAnEmptyText()
    {
        Setup();
        SeedPost(text: null);
        var cut = RenderEditor();

        var request = await SaveEditedAsync(cut);

        request.AnnouncementText.Should().BeNull("there is nothing to clear");
    }

    [Fact]
    public async Task TheSecondSave_SendsNull_BecauseTheFirstSavedTheText()
    {
        Setup();
        SeedPost(text: "Saved words");
        var cut = RenderEditor();
        cut.Find("#announcement-text").Input("Newer words");
        await SaveEditedAsync(cut);

        await SaveEditedAsync(cut);

        _api.UpdatedPosts[0].Request.AnnouncementText.Should().Be("Newer words");
        _api.UpdatedPosts[1].Request.AnnouncementText.Should().BeNull("what the API stored is the baseline of the next save");
    }

    [Fact]
    public async Task APage_HasNoFields_AndSendsNothingOfAnnouncing()
    {
        Setup();
        _api.PostsById[PostId] = new PostDto(PostId, "about", "About", null, "Body.", null, null, PostStatus.Draft, null, Now.UtcDateTime, Now.UtcDateTime,
            "author-id", "Author", null, [], null, 1, Kind: PostKind.Page);
        var cut = RenderEditor();

        cut.FindAll("#announcement-text, #announce-mastodon, .announce-preview").Should().BeEmpty();
        var request = await SaveEditedAsync(cut);
        (request.AnnouncementText, request.AnnounceMastodon, request.AnnounceBluesky).Should().Be((null, null, null));
    }

    [Fact]
    public async Task ANewPage_SendsNothingOfAnnouncing()
    {
        Setup();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/pages/new");
        var cut = Render<PostEditPage>();
        var model = cut.FindComponent<PostEditorForm>().Instance.Model;
        model.Title = "About";
        model.MarkdownContent = "Body.";
        model.AnnouncementText = "ignored";

        Click(cut, "Save draft");

        await cut.WaitForAssertionAsync(() => _api.CreatedPosts.Should().ContainSingle());
        var request = _api.CreatedPosts.Single();
        (request.AnnouncementText, request.AnnounceMastodon, request.AnnounceBluesky).Should().Be((null, null, null));
    }

    // ---- the API's refusal of the text ----

    [Fact]
    public async Task ATextTheApiRefuses_ShowsUnderItsField_AndNotInTheGeneralLine()
    {
        Setup();
        var cut = RenderNew();
        cut.Find("#announcement-text").Input(new string('x', 330));
        _api.PostFailure = new HttpRequestException("AnnouncementText is too long for Bluesky (360 of 300 characters).");

        Click(cut, "Save draft");

        await cut.WaitForAssertionAsync(() => cut.Find(".post-form__announcement").TextContent.Should().Contain("AnnouncementText is too long for Bluesky (360 of 300 characters)."));
        cut.FindAll(".post-edit__error").Should().BeEmpty();
        cut.Find("#announcement-text").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("#announcement-text").TextContent.Should().HaveLength(330, "the rest of the form is unchanged");
    }

    // ---- the Announcements block on the Discussion tab ----

    private static readonly DateTime T = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    private void SeedAnnouncements(bool mastodonReady = true, bool blueskyReady = true, params AnnouncementStatusDto[] items)
    {
        _api.Announcements[PostId] = new PostAnnouncementsDto(mastodonReady, blueskyReady, "https://blog.example/post/a-post", items);
        _api.DiscussionAdmins[PostId] = new PostDiscussionAdminDto(new PostDiscussionDto("https://social.example/@owner/1", null, 0, []), []);
    }

    private static AnnouncementStatusDto Row(SocialPlatform platform, AnnouncementState state, int attempts = 0, DateTime? next = null,
        string? error = null, string? url = null, DateTime? posted = null) =>
        new(platform, state, attempts, T, T, next, error, url, posted);

    private static async Task OpenTabAsync(IRenderedComponent<PostEditPage> cut)
    {
        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Discussion").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.FindAll(".discussion-admin__announcements").Should().ContainSingle());
    }

    private static string Line(IRenderedComponent<PostEditPage> cut, string network) =>
        cut.Find($"#announcement-{network} .discussion-admin__announcement-text").TextContent.Trim();

    private static IElement? Button(IRenderedComponent<PostEditPage> cut, string network) =>
        cut.FindAll($"#announcement-{network} button").SingleOrDefault();

    [Fact]
    public async Task TheBlock_IsAboveTheReplies_AndTheReadWaitsForTheTab()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements();
        var cut = RenderEditor();
        _api.AnnouncementRequests.Should().BeEmpty("the editor opens as fast as it did: the read waits for the tab");

        await OpenTabAsync(cut);

        _api.AnnouncementRequests.Should().Equal([PostId]);
        var panel = cut.Find(".discussion-admin").InnerHtml;
        panel.IndexOf("discussion-admin__announcements", StringComparison.Ordinal).Should().BeLessThan(panel.IndexOf("discussion-admin__status", StringComparison.Ordinal));
        cut.Find(".discussion-admin__announcements h3").TextContent.Should().Be("Announcements");
    }

    [Fact]
    public async Task ANetworkThatIsNotSetUp_SaysSo_WithNoButton()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(mastodonReady: false, blueskyReady: false);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Not set up on this site");
        Line(cut, "bluesky").Should().Be("Bluesky: Not set up on this site");
        cut.FindAll(".discussion-admin__announcements button").Should().BeEmpty();
    }

    [Fact]
    public async Task NotAnnounced_OffersAnnounceNow_OnAPublishedPost()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements();
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Not announced");
        Button(cut, "mastodon")!.TextContent.Trim().Should().Be("Announce now");
        Button(cut, "bluesky")!.GetAttribute("aria-label").Should().Be("Announce now on Bluesky");
    }

    [Fact]
    public async Task NotAnnounced_OnADraft_OffersNoButton()
    {
        Setup();
        SeedPost(PostStatus.Draft);
        SeedAnnouncements();
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Not announced");
        cut.FindAll(".discussion-admin__announcements button").Should().BeEmpty("the post is not published");
    }

    [Fact]
    public async Task APostWithAHandEnteredLink_IsNotOfferedAnnounceNow()
    {
        Setup();
        SeedPost(PostStatus.Published, mastodonUrl: "https://mastodon.example/@owner/1");
        SeedAnnouncements();
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Linked by hand");
        Button(cut, "mastodon").Should().BeNull();
        Button(cut, "bluesky").Should().NotBeNull();
    }

    [Fact]
    public async Task Pending_ShowsTheAttemptAndTheNextTry()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(items: [
            Row(SocialPlatform.Mastodon, AnnouncementState.Pending, attempts: 2, next: new DateTime(2026, 10, 23, 8, 7, 0, DateTimeKind.Utc)),
            Row(SocialPlatform.Bluesky, AnnouncementState.Pending, attempts: 0, next: T)]);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Pending, attempt 2, next try 08:07");
        Line(cut, "bluesky").Should().Be("Bluesky: Pending, next try 08:00");
        cut.FindAll(".discussion-admin__announcements button").Should().BeEmpty("a pending announcement needs no click");
    }

    [Fact]
    public async Task Posted_ShowsTheTime_AndLinksToTheRemotePost()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(items: [Row(SocialPlatform.Mastodon, AnnouncementState.Posted, 1, url: "https://mastodon.example/@owner/115", posted: T)]);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Posted 23 Oct 08:00");
        var link = cut.Find("#announcement-mastodon a");
        link.GetAttribute("href").Should().Be("https://mastodon.example/@owner/115");
        link.TextContent.Trim().Should().Be("View on Mastodon");
        link.GetAttribute("rel").Should().Contain("noopener");
        Button(cut, "mastodon").Should().BeNull();
    }

    [Fact]
    public async Task APostedRowWithAnUnsafeUrl_ShowsNoLink()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(items: [Row(SocialPlatform.Mastodon, AnnouncementState.Posted, 1, url: "javascript:alert(1)", posted: T)]);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        cut.FindAll("#announcement-mastodon a").Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_ShowsTheError_AndOffersRetry()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(items: [Row(SocialPlatform.Bluesky, AnnouncementState.Failed, 3, error: "Bluesky refused the sign-in (HTTP 401): AuthenticationRequired")]);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "bluesky").Should().Be("Bluesky: Failed: Bluesky refused the sign-in (HTTP 401): AuthenticationRequired");
        Button(cut, "bluesky")!.TextContent.Trim().Should().Be("Retry");
    }

    [Fact]
    public async Task Cancelled_SaysSo_AndOffersAnnounceNowWhenThePostIsPublishedAgain()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(items: [Row(SocialPlatform.Mastodon, AnnouncementState.Cancelled, 0)]);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        Line(cut, "mastodon").Should().Be("Mastodon: Cancelled when unpublished");
        Button(cut, "mastodon")!.TextContent.Trim().Should().Be("Announce now");

        SeedPost(PostStatus.Draft);
        var draft = RenderEditor();
        await OpenTabAsync(draft);
        Button(draft, "mastodon").Should().BeNull("an unpublished post cannot be announced");
    }

    [Fact]
    public async Task AnnounceNow_CallsTheApiOnce_AndReloadsTheBlock()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Button(cut, "mastodon")!.Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => Line(cut, "mastodon").Should().StartWith("Mastodon: Pending"));
        _api.Announced.Should().Equal([(PostId, SocialPlatform.Mastodon)]);
        _api.AnnouncementRequests.Should().HaveCount(2, "the block is read again after the click");
        Button(cut, "mastodon").Should().BeNull();
        cut.FindAll(".post-edit__error").Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_OnAFailedRow_CallsTheApiForThatNetwork()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements(items: [Row(SocialPlatform.Bluesky, AnnouncementState.Failed, 3, error: "refused")]);
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Button(cut, "bluesky")!.Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.Announced.Should().Equal([(PostId, SocialPlatform.Bluesky)]));
    }

    [Fact]
    public async Task A409_ShowsItsMessageInThePagesErrorArea()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements();
        _api.AnnounceFailure = new HttpRequestException("The post is already announced on Mastodon.");
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Button(cut, "mastodon")!.Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit__error").TextContent.Should().Contain("The post is already announced on Mastodon."));
        _api.Announced.Should().ContainSingle();
    }

    [Fact]
    public async Task AFailedRead_ShowsInTheTabsErrorLine()
    {
        Setup();
        SeedPost(PostStatus.Published);
        SeedAnnouncements();
        _api.AnnouncementsFailure = new HttpRequestException("The announcements could not be read.");
        var cut = RenderEditor();

        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Discussion").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find(".discussion-admin__error").TextContent.Should().Contain("The announcements could not be read."));
    }

    // ---- after Publish now ----

    [Fact]
    public async Task PublishNow_WithPendingRows_SaysWhichNetworksAreBeingAnnounced()
    {
        Setup();
        SeedPost(PostStatus.Draft);
        SeedAnnouncements(items: [Row(SocialPlatform.Mastodon, AnnouncementState.Pending, 0, next: T), Row(SocialPlatform.Bluesky, AnnouncementState.Pending, 0, next: T)]);
        var cut = RenderEditor();

        Click(cut, "Publish now");

        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__saved").TextContent.Trim().Should().Be("Published. Announcing on Mastodon and Bluesky."));
    }

    [Fact]
    public async Task PublishNow_NamesOnlyTheNetworksWithAPendingRow()
    {
        Setup();
        SeedPost(PostStatus.Draft);
        SeedAnnouncements(items: [Row(SocialPlatform.Bluesky, AnnouncementState.Pending, 0, next: T)]);
        var cut = RenderEditor();

        Click(cut, "Publish now");

        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__saved").TextContent.Trim().Should().Be("Published. Announcing on Bluesky."));
    }

    [Fact]
    public async Task PublishNow_WithNoPendingRows_JustSaysPublished()
    {
        Setup();
        SeedPost(PostStatus.Draft);
        SeedAnnouncements();
        var cut = RenderEditor();

        Click(cut, "Publish now");

        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__saved").TextContent.Trim().Should().Be("Published"));
    }

    [Fact]
    public async Task PublishNow_WhenTheAnnouncementsCannotBeRead_StillSaysPublished()
    {
        Setup();
        SeedPost(PostStatus.Draft);
        _api.AnnouncementsFailure = new HttpRequestException("down");
        var cut = RenderEditor();

        Click(cut, "Publish now");

        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__saved").TextContent.Trim().Should().Be("Published"));
        cut.FindAll(".post-edit__error").Should().BeEmpty("the publish worked; only the extra read failed");
    }

    [Fact]
    public async Task Schedule_DoesNotMentionAnnouncing()
    {
        Setup();
        SeedPost(PostStatus.Draft);
        SeedAnnouncements(items: [Row(SocialPlatform.Mastodon, AnnouncementState.Pending, 0, next: T)]);
        _api.PublishStatus = (_, _) => PostStatus.Scheduled;
        var cut = RenderEditor();
        cut.FindComponent<PostEditorForm>().Instance.Model.PublishedAtDate = Now.UtcDateTime.AddDays(1).Date;
        cut.FindComponent<PostEditorForm>().Instance.Model.PublishedAtTime = TimeSpan.FromHours(9);
        cut.Render();

        Click(cut, "Schedule");

        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__saved").TextContent.Trim().Should().StartWith("Scheduled for"));
        cut.Find(".post-actions__saved").TextContent.Should().NotContain("Announcing");
    }
}
