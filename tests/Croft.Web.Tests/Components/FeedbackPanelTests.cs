using Croft.Shared.Contracts;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Croft.Web.Tests.Components;

/// <summary>
/// FeedbackPanel (spec §6.2/§7.4): open notes show quote, comment and author with Resolve;
/// resolved notes collapse under "Resolved (N)". The panel reports actions; the page owns the
/// data and updates the items after the API answers.
/// </summary>
public class FeedbackPanelTests : BlogBunitContext
{
    private static readonly DateTime Created = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

    private static FeedbackDto Note(string comment, DateTime? resolvedAt = null, string quote = "A quoted bit")
    {
        var id = Guid.CreateVersion7();
        return new FeedbackDto(id, Guid.CreateVersion7(), "editor-id", "Ed", quote, comment,
            Created, resolvedAt);
    }

    [Fact]
    public void FeedbackPanel_Inputs_CarryTheApisLimits()
    {
        var cut = Render<FeedbackPanel>(p => p.Add(x => x.CanAdd, true).Add(x => x.Items, []));

        cut.Find("#feedback-quote").GetAttribute("maxlength").Should().Be("2000",
            "the browser stops a quote at the API's limit instead of the API refusing it");
        cut.Find("#feedback-comment").GetAttribute("maxlength").Should().Be("10000");
    }

    [Fact]
    public async Task FeedbackPanel_Resolve_RaisesCallback_AndMovesToResolved()
    {
        var open = Note("Needs a source.");
        List<FeedbackDto> items = [open];
        List<FeedbackDto> raised = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanResolve, true)
            .Add(x => x.OnResolve, EventCallback.Factory.Create<FeedbackDto>(this, f =>
            {
                raised.Add(f);
                // The page's half: the API answer replaces the note with its resolved version.
                items[0] = f with { ResolvedAt = Created.AddMinutes(5) };
            })));

        cut.FindAll("button").Single(b => b.TextContent == "Resolve").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            raised.Should().ContainSingle("Resolve reports the note").Which.Should().Be(open));

        cut.Find(".feedback-panel__resolved summary").TextContent.Should().Be("Resolved (1)",
            "the note moves under the resolved group");
        cut.Find(".feedback-panel__resolved").TextContent.Should().Contain("Needs a source.");
        cut.FindAll(".feedback-panel__open .feedback-note").Should().BeEmpty(
            "the note is no longer among the open ones");
    }

    [Fact]
    public void FeedbackPanel_ResolvedNotes_CollapseUnderTheCount()
    {
        IReadOnlyList<FeedbackDto> items =
        [
            Note("Open one."),
            Note("Done one.", resolvedAt: Created.AddMinutes(5)),
            Note("Done two.", resolvedAt: Created.AddMinutes(9)),
        ];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanResolve, true));

        var resolved = cut.Find(".feedback-panel__resolved");
        resolved.GetAttribute("open").Should().BeNull("resolved notes are collapsed by default");
        resolved.TextContent.Should().Contain("Resolved (2)");
        resolved.TextContent.Should().Contain("Done one.").And.Contain("Done two.");
        cut.Find(".feedback-panel__open").TextContent.Should().Contain("Open one.")
            .And.NotContain("Done one.");
    }

    [Fact]
    public void FeedbackPanel_NoNotes_ShowsTheEmptyState()
    {
        IReadOnlyList<FeedbackDto> items = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanResolve, true));

        cut.Find(".feedback-panel").TextContent.Should().Contain("No feedback yet.");
    }

    [Fact]
    public async Task FeedbackPanel_AddForm_RaisesOnAddWithQuoteAndComment()
    {
        List<CreateFeedbackRequest> added = [];
        IReadOnlyList<FeedbackDto> items = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanAdd, true)
            .Add(x => x.OnAdd, EventCallback.Factory.Create<CreateFeedbackRequest>(this, added.Add)));

        cut.Find("textarea[name='quote']").Change("Some selected words");
        cut.Find("textarea[name='comment']").Change("Check the second paragraph.");
        cut.FindAll("button").Single(b => b.TextContent == "Add").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => added.Should().ContainSingle());
        added[0].QuotedText.Should().Be("Some selected words");
        added[0].Comment.Should().Be("Check the second paragraph.");
    }

    [Fact]
    public void FeedbackPanel_WithoutCanAdd_ShowsNoForm()
    {
        IReadOnlyList<FeedbackDto> items = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanAdd, false));

        cut.FindAll("textarea").Should().BeEmpty();
    }

    [Fact]
    public async Task FeedbackPanel_QuoteParameter_SeedsTheQuoteInTheAddRequest()
    {
        List<CreateFeedbackRequest> added = [];
        IReadOnlyList<FeedbackDto> items = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanAdd, true)
            .Add(x => x.OnAdd, EventCallback.Factory.Create<CreateFeedbackRequest>(this, added.Add))
            .Add(x => x.Quote, "some words"));

        cut.Find("textarea[name='comment']").Change("Check the second paragraph.");
        cut.FindAll("button").Single(b => b.TextContent == "Add").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => added.Should().ContainSingle());
        added[0].QuotedText.Should().Be("some words",
            "the draft review's selection hands its text to the quote field through Quote");
        added[0].Comment.Should().Be("Check the second paragraph.");
    }

    [Fact]
    public async Task FeedbackPanel_QuoteParameter_DoesNotClobberManualQuoteEditsOnRerender()
    {
        List<CreateFeedbackRequest> added = [];
        IReadOnlyList<FeedbackDto> items = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanAdd, true)
            .Add(x => x.OnAdd, EventCallback.Factory.Create<CreateFeedbackRequest>(this, added.Add))
            .Add(x => x.Quote, "some words"));

        cut.Find("textarea[name='quote']").Change("my own words");
        cut.Render(p => p.Add(x => x.Quote, "some words"));
        cut.Find("textarea[name='comment']").Change("Check.");
        cut.FindAll("button").Single(b => b.TextContent == "Add").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => added.Should().ContainSingle());
        added[0].QuotedText.Should().Be("my own words", "an unchanged Quote must not refill the field over the user's edit");
    }

    [Fact]
    public async Task FeedbackPanel_QuoteParameter_RefillsWhenTheValueComesBack()
    {
        List<CreateFeedbackRequest> added = [];
        IReadOnlyList<FeedbackDto> items = [];
        var cut = Render<FeedbackPanel>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.CanAdd, true)
            .Add(x => x.OnAdd, EventCallback.Factory.Create<CreateFeedbackRequest>(this, added.Add))
            .Add(x => x.Quote, "some words"));

        // The hand-off value clears after an add (the field with it); the same selection re-offered
        // must fill the field again.
        cut.Render(p => p.Add(x => x.Quote, (string?)null));
        cut.Find("textarea[name='quote']").Change("");
        cut.Render(p => p.Add(x => x.Quote, "some words"));
        cut.Find("textarea[name='comment']").Change("Again.");
        cut.FindAll("button").Single(b => b.TextContent == "Add").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => added.Should().ContainSingle());
        added[0].QuotedText.Should().Be("some words");
    }
}
