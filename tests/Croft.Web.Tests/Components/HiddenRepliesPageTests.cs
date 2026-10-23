using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts.Social;
using Croft.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 12c item 4: the "Hidden replies" page, /admin/discussion/blocks.</summary>
public class HiddenRepliesPageTests : BlogBunitContext
{
    private FakeBlogApiClient _api = null!;

    private static SocialBlockDto Block(string id, SocialBlockKind kind = SocialBlockKind.Reply, SocialPlatform platform = SocialPlatform.Mastodon,
        string? label = "a label", string? note = null, string by = "Admin", int day = 20) =>
        new(Guid.CreateVersion7(), platform, kind, id, label, note, by, new DateTimeOffset(2026, 10, day, 9, 5, 0, TimeSpan.Zero));

    private void Setup(params SocialBlockDto[] blocks)
    {
        _api = Services.GetRequiredService<FakeBlogApiClient>();
        _api.Blocks.AddRange(blocks);
    }

    private static string[] Cells(AngleSharp.Dom.IElement row) =>
        [.. row.QuerySelectorAll("td").Select(td => td.TextContent.Trim())];

    private static async Task<AngleSharp.Dom.IElement> ButtonAsync(IRenderedComponent<MudDialogProvider> dialogs, string text)
    {
        await dialogs.WaitForAssertionAsync(() => dialogs.FindAll("button").Should().Contain(b => b.TextContent.Trim() == text));
        return dialogs.FindAll("button").Single(b => b.TextContent.Trim() == text);
    }

    [Fact]
    public async Task TheBlocks_ListNewestFirst_WithEveryColumn()
    {
        Setup(
            Block("acct-1", SocialBlockKind.Account, SocialPlatform.Bluesky, "Ann (@ann.example)", "spam", "Admin", day: 21),
            Block("r1", SocialBlockKind.Reply, SocialPlatform.Mastodon, "@bob@social.example: Hello", null, "Editor", day: 20));

        var cut = Render<HiddenRepliesPage>();

        await cut.WaitForAssertionAsync(() => cut.FindAll("tr.admin-table__row").Should().HaveCount(2));
        cut.FindAll("thead th").Select(th => th.TextContent.Trim()).Should().Equal("Platform", "What", "Label", "Note", "Hidden by", "When", "Actions");
        var rows = cut.FindAll("tr.admin-table__row");
        Cells(rows[0]).Should().Equal("Bluesky", "Account", "Ann (@ann.example)", "spam", "Admin", "2026-10-21 09:05 UTC", "Unhide");
        Cells(rows[1]).Should().Equal("Mastodon", "Reply", "@bob@social.example: Hello", "", "Editor", "2026-10-20 09:05 UTC", "Unhide");
        _api.Calls.Should().NotContain("update", "the page only reads");
    }

    [Fact]
    public async Task NothingHidden_SaysSo()
    {
        Setup();

        var cut = Render<HiddenRepliesPage>();

        await cut.WaitForAssertionAsync(() => cut.Find(".admin-table__empty").TextContent.Trim().Should().Be("Nothing is hidden."));
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public async Task Unhide_AsksFirst_ThenDeletesOnce_AndTheRowLeaves()
    {
        var keep = Block("r2", label: "keep me", day: 21);
        var target = Block("r1", label: "unhide me", day: 20);
        Setup(keep, target);
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<HiddenRepliesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tr.admin-table__row").Should().HaveCount(2));

        cut.FindAll("tr.admin-table__row")[1].QuerySelector("button")!.Click(new MouseEventArgs());
        await dialogs.WaitForAssertionAsync(() => dialogs.Markup.Should().Contain("The reply comes back at the next fetch, if it still exists."));
        _api.DeletedBlockIds.Should().BeEmpty("Unhide is behind a confirm dialog");
        (await ButtonAsync(dialogs, "Unhide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.FindAll("tr.admin-table__row").Select(r => Cells(r)[2]).Should().Equal("keep me"));
        _api.DeletedBlockIds.Should().Equal([target.Id], "one delete, for the row that was asked");
    }

    [Fact]
    public async Task Unhide_Cancelling_DeletesNothing_AndKeepsTheRow()
    {
        Setup(Block("r1"));
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<HiddenRepliesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tr.admin-table__row").Should().ContainSingle());

        cut.Find("tr.admin-table__row button").Click(new MouseEventArgs());
        (await ButtonAsync(dialogs, "Cancel")).Click(new MouseEventArgs());

        await dialogs.WaitForAssertionAsync(() => dialogs.FindAll("button").Should().BeEmpty());
        _api.DeletedBlockIds.Should().BeEmpty();
        cut.FindAll("tr.admin-table__row").Should().ContainSingle();
    }

    [Fact]
    public async Task Unhide_WhenSomeoneElseAlreadyDidIt_ReloadsAndSaysSo()
    {
        Setup(Block("r1"));
        _api.DeleteBlockIsAlreadyGone = true;
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<HiddenRepliesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tr.admin-table__row").Should().ContainSingle());

        cut.Find("tr.admin-table__row button").Click(new MouseEventArgs());
        (await ButtonAsync(dialogs, "Unhide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("[role=status]").TextContent.Trim().Should().Be("Already unhidden.");
            cut.FindAll("tr.admin-table__row").Should().BeEmpty("the list was read again");
        });
    }

    [Fact]
    public async Task Unhide_Failure_ShowsTheError_AndKeepsTheRow()
    {
        Setup(Block("r1"));
        _api.DeleteBlockFailure = new HttpRequestException("The API said no.");
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<HiddenRepliesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tr.admin-table__row").Should().ContainSingle());

        cut.Find("tr.admin-table__row button").Click(new MouseEventArgs());
        (await ButtonAsync(dialogs, "Unhide")).Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find("[role=alert]").TextContent.Trim().Should().Be("The API said no."));
        cut.FindAll("tr.admin-table__row").Should().ContainSingle();
    }

    [Fact]
    public async Task ALoadFailure_ShowsTheError()
    {
        Setup();
        _api.BlocksFailure = new HttpRequestException("blocks are down");

        var cut = Render<HiddenRepliesPage>();

        await cut.WaitForAssertionAsync(() => cut.Find("[role=alert]").TextContent.Trim().Should().Be("blocks are down"));
    }

    /// <summary>The key the framework derives for <c>HiddenRepliesPage.Initial</c> (component type, property name and parent); the two
    /// tests below fail by naming it when the property or component is renamed.</summary>
    private const string StateKey = "/1SFCApGpl7Pw36vvasI2RWRZs37evvibnMXDA+HB5U=";

    [Fact]
    public async Task ARenderThatReadTheBlocks_PersistsExactlyWhatItRead()
    {
        // Interactive pages run OnInitializedAsync twice on a full load; the data travels with [PersistentState].
        var block = Block("r1", label: "persisted");
        Setup(block);
        var cut = Render<HiddenRepliesPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("tr.admin-table__row").Should().ContainSingle());

        PersistentState.TriggerOnPersisting();

        PersistentState.TryTake<List<SocialBlockDto>>(StateKey, out var state).Should().BeTrue(
            "the page persists its list for the circuit; if this fails the framework's key changed (update StateKey) or nothing was persisted");
        state.Should().Equal([block]);
    }

    [Fact]
    public void ARenderWithRestoredState_ReadsNothing_AndShowsTheRestoredRows()
    {
        Setup();
        PersistentState.Persist(StateKey, new List<SocialBlockDto> { Block("r1", label: "restored") });

        var cut = Render<HiddenRepliesPage>();

        _api.BlockReads.Should().Be(0, "the prerender already read the list: the circuit must not read it again");
        Cells(cut.Find("tr.admin-table__row"))[2].Should().Be("restored");
    }
}
