using Microsoft.AspNetCore.Components;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 12c item 3: the Discussion tab on the post edit page (status, refresh, the replies, hide reply and hide account).</summary>
public class PostEditPageDiscussionTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PostId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private FakeBlogApiClient _api = null!;

    private void Setup()
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        _api = Services.GetRequiredService<FakeBlogApiClient>();
        _api.PostsById[PostId] = new PostDto(PostId, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Published, Now.UtcDateTime.AddDays(-2), Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1);

        // The editor polls and reads the announcements only in the interactive phase (RendererInfo.IsInteractive).
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private static DiscussionReplyDto Reply(string id, string account, string html = "<p>Example reply text.</p>",
        SocialPlatform platform = SocialPlatform.Mastodon, string? name = null, string? handle = null,
        string? avatar = null, string? warning = null, params DiscussionReplyDto[] answers) => new(
        platform, id, handle ?? $"{account}@social.example", name ?? $"Person {account}", $"https://social.example/@{account}", account,
        avatar, new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero), $"https://social.example/@{account}/{id}", html, warning, false, answers);

    private static DiscussionThreadStatusDto Thread(SocialPlatform platform = SocialPlatform.Mastodon,
        DateTimeOffset? ok = null, DateTimeOffset? attempt = null, string? error = null, DateTimeOffset? next = null,
        int stored = 0, bool stale = false) => new(platform, ok, attempt, error, next, stored, stale);

    private void Seed(IReadOnlyList<DiscussionReplyDto> replies, params DiscussionThreadStatusDto[] threads) =>
        _api.DiscussionAdmins[PostId] = new PostDiscussionAdminDto(
            new PostDiscussionDto("https://social.example/@owner/1", null, replies.Count, replies), threads);

    private IRenderedComponent<PostEditPage> RenderEditor() => Render<PostEditPage>(p => p.Add(x => x.Id, PostId));

    private static async Task OpenTabAsync(IRenderedComponent<PostEditPage> cut)
    {
        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Discussion").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.FindAll(".discussion-admin").Should().ContainSingle());
    }

    private static IElement[] Buttons(IRenderedComponent<PostEditPage> cut, string text) =>
        [.. cut.FindAll(".discussion-admin button").Where(b => b.TextContent.Trim() == text)];

    private IRenderedComponent<MudDialogProvider> Dialogs() => Render<MudDialogProvider>();

    private static async Task<IElement> DialogButtonAsync(IRenderedComponent<MudDialogProvider> dialogs, string text)
    {
        await dialogs.WaitForAssertionAsync(() => dialogs.FindAll("button").Should().Contain(b => b.TextContent.Trim() == text));
        return dialogs.FindAll("button").Single(b => b.TextContent.Trim() == text);
    }

    // ---- the tab itself ----

    [Fact]
    public async Task TheTab_IsThereForAnExistingPost_AndTheAdminReadWaitsForItsFirstOpening()
    {
        Setup();
        Seed([], Thread());

        var cut = RenderEditor();

        await cut.WaitForAssertionAsync(() => cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim())
            .Should().Equal("Preview", "Feedback", "Discussion"));
        _api.DiscussionAdminRequests.Should().BeEmpty("the editor must not get slower: the admin read waits for the tab");
        await OpenTabAsync(cut);
        _api.DiscussionAdminRequests.Should().Equal([PostId]);
    }

    [Fact]
    public async Task ReopeningTheTab_DoesNotReadAgain()
    {
        Setup();
        Seed([], Thread());
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Preview").Click(new MouseEventArgs());
        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Discussion").Click(new MouseEventArgs());

        _api.DiscussionAdminRequests.Should().HaveCount(1, "the tab loads when it is first opened");
    }

    [Fact]
    public void TheTab_IsAbsentOnANewPost()
    {
        Setup();

        var cut = Render<PostEditPage>();

        cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).Should().Equal("Preview", "Feedback");
    }

    [Fact]
    public async Task TheTab_IsAbsentOnAPage()
    {
        Setup();
        _api.PostsById[PostId] = _api.PostsById[PostId] with { Kind = PostKind.Page };

        var cut = RenderEditor();

        await cut.WaitForAssertionAsync(() => cut.Find("#post-nav-placement"));
        cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).Should().Equal("Preview", "Feedback");
    }

    // ---- the status lines ----

    private async Task<string[]> StatusLinesAsync()
    {
        var cut = RenderEditor();
        await OpenTabAsync(cut);
        return [.. cut.FindAll(".discussion-admin__status p").Select(p => p.TextContent.Trim())];
    }

    [Fact]
    public async Task Status_AFetchThatWorked_ShowsWhenItRanAndHowManyRepliesAreStored()
    {
        Setup();
        Seed([], Thread(ok: Now.AddMinutes(-5), attempt: Now.AddMinutes(-5), next: Now.AddMinutes(10), stored: 3));

        var lines = await StatusLinesAsync();

        lines.Should().Equal("Mastodon: last fetched 5 minutes ago (3 replies stored)", "Next fetch in 10 minutes");
    }

    [Fact]
    public async Task Status_OneStoredReply_IsSingular()
    {
        Setup();
        Seed([], Thread(ok: Now.AddMinutes(-5), stored: 1));

        (await StatusLinesAsync()).Should().Equal("Mastodon: last fetched 5 minutes ago (1 reply stored)");
    }

    [Fact]
    public async Task Status_AFailedAttempt_ShowsTheErrorAndWhen_AndBothPlatformsAppear()
    {
        Setup();
        Seed([],
            Thread(ok: Now.AddHours(-3), attempt: Now.AddMinutes(-20), error: "The server answered 503.", next: Now.AddMinutes(-1), stored: 2),
            Thread(SocialPlatform.Bluesky, ok: Now.AddMinutes(-1), attempt: Now.AddMinutes(-1), stored: 0));

        var lines = await StatusLinesAsync();

        lines.Should().Equal(
            "Mastodon: last fetched 3 hours ago (2 replies stored)",
            "Last attempt failed 20 minutes ago: The server answered 503.",
            "Next fetch due now",
            "Bluesky: last fetched just now (0 replies stored)");
    }

    [Fact]
    public async Task Status_AStaleThread_SaysItsRepliesAreNotShownToReaders()
    {
        Setup();
        Seed([], Thread(ok: Now.AddDays(-3), attempt: Now.AddMinutes(-2), error: "Timed out.", stored: 4, stale: true));

        var lines = await StatusLinesAsync();

        lines.Should().Contain("Not shown to readers: no successful fetch for 48 hours.");
        lines.Should().Contain("Mastodon: last fetched 3 days ago (4 replies stored)");
    }

    [Fact]
    public async Task Status_NoFetchYet_SaysSo()
    {
        Setup();
        Seed([], Thread(next: Now));

        (await StatusLinesAsync()).Should().Equal("Mastodon: not fetched yet (0 replies stored)", "Next fetch due now");
    }

    [Fact]
    public async Task Status_NothingLinked_SaysSoAndShowsNothingElseAboutFetching()
    {
        Setup();
        Seed([]);
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        cut.Find(".discussion-admin__status").TextContent.Trim().Should()
            .Be("No Mastodon or Bluesky post is linked. Add one under Discussion in the form.");
        cut.FindAll(".discussion-admin__status p").Should().ContainSingle();
    }

    // ---- refresh ----

    [Fact]
    public async Task RefreshNow_CallsTheApiOnce_SaysItIsQueued_AndReloadsTheStatus()
    {
        Setup();
        Seed([], Thread(ok: Now.AddMinutes(-5), stored: 0));
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Refresh now").Single().Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find(".discussion-admin [role=status]").TextContent.Trim().Should()
                .Be("Refresh queued. New replies appear within a few minutes.");
            _api.DiscussionAdminRequests.Should().HaveCount(2, "the status is read again after the call, once, without polling");
        });
        _api.RefreshedDiscussions.Should().Equal([PostId]);
    }

    [Fact]
    public async Task RefreshNow_IsDisabledWhileItsCallRuns()
    {
        Setup();
        Seed([], Thread(ok: Now.AddMinutes(-5)));
        var gate = new TaskCompletionSource();
        _api.RefreshGate = gate.Task;
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Refresh now").Single().Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => Buttons(cut, "Refresh now").Single().HasAttribute("disabled").Should().BeTrue());
        gate.SetResult();
        await cut.WaitForAssertionAsync(() => Buttons(cut, "Refresh now").Single().HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public async Task RefreshNow_Failure_ShowsInTheTabsAlertLine()
    {
        Setup();
        Seed([], Thread(ok: Now.AddMinutes(-5)));
        _api.RefreshFailure = new HttpRequestException("The API said no.");
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Refresh now").Single().Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find(".discussion-admin [role=alert]").TextContent.Trim().Should().Be("The API said no."));
        cut.FindAll(".discussion-admin [role=status]").Should().BeEmpty("a failed refresh is not 'queued'");
    }

    // ---- the replies ----

    [Fact]
    public async Task NoReplies_SaysSo()
    {
        Setup();
        Seed([], Thread(ok: Now.AddMinutes(-5)));
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        cut.Find(".discussion-admin").TextContent.Should().Contain("No replies yet.");
        cut.FindAll(".discussion-admin__reply").Should().BeEmpty();
    }

    [Fact]
    public async Task Replies_AreListedFlatInTheApisOrder_WithAnswersIndentedByDepth_AndEveryField()
    {
        Setup();
        var answer = Reply("a2", "bob", "<p>An answer.</p>", SocialPlatform.Bluesky, name: "Bob", handle: "bob.example", avatar: new string('b', 64));
        var first = Reply("r1", "ann", "<p>First <a href=\"https://example.org/x\" rel=\"nofollow ugc noopener\">link</a></p>",
            name: "Ann", handle: "ann@social.example", warning: "Politics", answers: answer);
        var second = Reply("r3", "cy", name: "Cy");
        Seed([first, second], Thread(ok: Now.AddMinutes(-5), stored: 3));
        var cut = RenderEditor();

        await OpenTabAsync(cut);

        var rows = cut.FindAll(".discussion-admin__reply");
        rows.Select(r => r.QuerySelector(".discussion-admin__name")!.TextContent.Trim()).Should().Equal("Ann", "Bob", "Cy");
        rows.Select(r => r.GetAttribute("data-depth")).Should().Equal(["0", "1", "0"], "an answer is one step deeper");
        rows[1].ClassList.Should().Contain("discussion-admin__reply--depth-1");
        var ann = rows[0];
        ann.QuerySelector(".discussion-admin__handle")!.TextContent.Should().Be("@ann@social.example");
        ann.QuerySelector(".discussion-admin__platform")!.TextContent.Should().Be("Mastodon");
        ann.QuerySelector(".discussion-admin__time")!.TextContent.Should().Be("2026-10-21 09:30 UTC");
        ann.QuerySelector(".discussion-admin__warning")!.TextContent.Trim().Should().Contain("Politics");
        ann.QuerySelector(".discussion-admin__content")!.InnerHtml.Should().Contain("<a href=\"https://example.org/x\"", "the API's sanitised HTML is rendered as it is");
        ann.QuerySelector(".discussion-admin__initial")!.TextContent.Should().Be("A", "no avatar: the initial");
        ann.QuerySelector("img").Should().BeNull();
        var bob = rows[1];
        bob.QuerySelector("img.discussion-admin__avatar")!.GetAttribute("src").Should().Be($"/social/avatar/{new string('b', 64)}.webp");
        bob.QuerySelector("img")!.GetAttribute("alt").Should().Be("");
        bob.QuerySelector(".discussion-admin__platform")!.TextContent.Should().Be("Bluesky");
        var view = ann.QuerySelector("a.discussion-admin__view")!;
        view.TextContent.Trim().Should().Be("View on Mastodon");
        view.GetAttribute("href").Should().Be("https://social.example/@ann/r1");
        view.GetAttribute("rel").Should().Be("nofollow ugc noopener");
        view.GetAttribute("target").Should().Be("_blank");
        rows.Should().OnlyContain(r => r.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).SequenceEqual(new[] { "Hide reply", "Hide account" }));
    }

    // ---- hide reply ----

    [Fact]
    public async Task HideReply_AfterConfirming_SendsAReplyBlockWithTheLabelAndNote_AndTheReplyAndItsAnswerLeave()
    {
        Setup();
        var answer = Reply("a2", "bob", "<p>An answer.</p>", name: "Bob", handle: "bob@social.example");
        var target = Reply("r1", "ann", "<p>Short   <b>bold</b>\n text &amp; more</p>", name: "Ann", handle: "ann@social.example", answers: answer);
        var other = Reply("r3", "cy", name: "Cy");
        Seed([target, other], Thread(ok: Now.AddMinutes(-5), stored: 3));
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide reply")[0].Click(new MouseEventArgs());
        var note = await dialogs.WaitForElementAsync("input[type=text], textarea");
        _api.CreatedBlocks.Should().BeEmpty("hiding is behind a confirm dialog");
        note.Change("spam");
        (await DialogButtonAsync(dialogs, "Hide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.FindAll(".discussion-admin__name").Select(n => n.TextContent.Trim()).Should().Equal("Cy"));
        var request = _api.CreatedBlocks.Should().ContainSingle().Subject;
        request.Should().Be(new CreateSocialBlockRequest(SocialPlatform.Mastodon, SocialBlockKind.Reply, "r1",
            "@ann@social.example: Short bold text & more", "spam"));
        _api.DiscussionAdminRequests.Should().HaveCount(2, "the panel reads the discussion again after the block");
    }

    [Fact]
    public async Task HideReply_WithNoNote_SendsNullNote()
    {
        Setup();
        Seed([Reply("r1", "ann")], Thread(ok: Now.AddMinutes(-5), stored: 1));
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide reply").Single().Click(new MouseEventArgs());
        (await DialogButtonAsync(dialogs, "Hide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedBlocks.Should().ContainSingle());
        _api.CreatedBlocks.Single().Note.Should().BeNull();
    }

    [Fact]
    public async Task HideReply_TheLabelCutsAt80Characters_WithAnEllipsis_AndLeavesAnExactly80CharacterTextAlone()
    {
        Setup();
        var exactly80 = new string('x', 80);
        var longer = new string('y', 80) + "ZZZ";
        Seed([Reply("r1", "ann", $"<p>{exactly80}</p>", handle: "ann@social.example"), Reply("r2", "bob", $"<p>{longer}</p>", handle: "bob@social.example")],
            Thread(ok: Now.AddMinutes(-5), stored: 2));
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide reply")[0].Click(new MouseEventArgs());
        (await DialogButtonAsync(dialogs, "Hide")).Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.CreatedBlocks.Should().HaveCount(1));
        await cut.WaitForAssertionAsync(() => Buttons(cut, "Hide reply").Should().ContainSingle());
        Buttons(cut, "Hide reply")[0].Click(new MouseEventArgs());
        (await DialogButtonAsync(dialogs, "Hide")).Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.CreatedBlocks.Should().HaveCount(2));

        _api.CreatedBlocks[0].Label.Should().Be($"@ann@social.example: {exactly80}", "exactly 80 characters is not cut");
        _api.CreatedBlocks[1].Label.Should().Be($"@bob@social.example: {new string('y', 80)}…", "longer text is cut at 80 characters and gets an ellipsis");
    }

    [Fact]
    public async Task HideReply_Cancelling_SendsNothing_AndKeepsTheReply()
    {
        Setup();
        Seed([Reply("r1", "ann")], Thread(ok: Now.AddMinutes(-5), stored: 1));
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide reply").Single().Click(new MouseEventArgs());
        (await DialogButtonAsync(dialogs, "Cancel")).Click(new MouseEventArgs());

        await dialogs.WaitForAssertionAsync(() => dialogs.FindAll("button").Should().BeEmpty());
        _api.CreatedBlocks.Should().BeEmpty();
        _api.DiscussionAdminRequests.Should().HaveCount(1);
        cut.FindAll(".discussion-admin__reply").Should().ContainSingle();
    }

    [Fact]
    public async Task HideReply_Failure_ShowsInTheTabsAlertLine_AndKeepsTheReply()
    {
        Setup();
        Seed([Reply("r1", "ann")], Thread(ok: Now.AddMinutes(-5), stored: 1));
        _api.CreateBlockFailure = new HttpRequestException("The API said no.");
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide reply").Single().Click(new MouseEventArgs());
        (await DialogButtonAsync(dialogs, "Hide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find(".discussion-admin [role=alert]").TextContent.Trim().Should().Be("The API said no."));
        cut.FindAll(".discussion-admin__reply").Should().ContainSingle();
    }

    // ---- hide account ----

    [Fact]
    public async Task HideAccount_SendsAnAccountBlockWithTheAccountId_AndEveryReplyOfThatAccountLeaves()
    {
        Setup();
        var one = Reply("r1", "ann", name: "Ann", handle: "ann@social.example");
        var two = Reply("r2", "ann", name: "Ann", handle: "ann@social.example");
        var other = Reply("r3", "cy", name: "Cy");
        Seed([one, two, other], Thread(ok: Now.AddMinutes(-5), stored: 3));
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide account")[0].Click(new MouseEventArgs());
        await dialogs.WaitForAssertionAsync(() => dialogs.Markup.Should().Contain("every reply of @ann@social.example on every post"));
        _api.CreatedBlocks.Should().BeEmpty("hiding is behind a confirm dialog");
        (await DialogButtonAsync(dialogs, "Hide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.FindAll(".discussion-admin__name").Select(n => n.TextContent.Trim()).Should().Equal("Cy"));
        _api.CreatedBlocks.Should().ContainSingle().Which.Should().Be(new CreateSocialBlockRequest(
            SocialPlatform.Mastodon, SocialBlockKind.Account, "ann", "Ann (@ann@social.example)", null));
    }

    [Fact]
    public async Task HideAccount_Cancelling_SendsNothing()
    {
        Setup();
        Seed([Reply("r1", "ann")], Thread(ok: Now.AddMinutes(-5), stored: 1));
        var dialogs = Dialogs();
        var cut = RenderEditor();
        await OpenTabAsync(cut);

        Buttons(cut, "Hide account").Single().Click(new MouseEventArgs());
        (await DialogButtonAsync(dialogs, "Cancel")).Click(new MouseEventArgs());

        await dialogs.WaitForAssertionAsync(() => dialogs.FindAll("button").Should().BeEmpty());
        _api.CreatedBlocks.Should().BeEmpty();
    }
}
