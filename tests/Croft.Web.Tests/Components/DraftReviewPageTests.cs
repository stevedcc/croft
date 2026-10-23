using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages.Editor;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The draft review (spec §6.6): the draft rendered with the real post layout — PostHero with the
/// Draft ribbon, PostBody, PostRail carrying the feedback notes — and the selection hand-off: text
/// selected in the body shows an "Add feedback" button whose quote becomes the note's.
/// </summary>
public class DraftReviewPageTests : BlogBunitContext
{
    private static readonly DateTime Created = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    private FakeBlogApiClient _api = null!;

    private FakeBlogApiClient Setup(PostDto post, params FeedbackDto[] feedback)
    {
        _api = new FakeBlogApiClient();
        _api.PostsById[post.Id] = post;
        _api.Feedback.AddRange(feedback);
        Services.AddSingleton<IBlogApiClient>(_api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        return _api;
    }

    /// <summary>A draft with none of the optional data (Review Focus 1).</summary>
    private static PostDto Draft(string markdown = "The **hillsideword** waits for the rain.") => new(
        Guid.CreateVersion7(), "the-draft", "The Draft", null, markdown, null, null,
        PostStatus.Draft, null,
        new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc),
        "author-id", "Author", null, [], null, 1);

    private static FeedbackDto Note(Guid postId, string quote, string comment) =>
        new(Guid.CreateVersion7(), postId, "editor-id", "Ed", quote, comment, Created, null);

    [Fact]
    public async Task DraftReviewPage_RendersTheDraftWithRibbon_AndNotesInTheRail()
    {
        var post = Draft();
        Setup(post, Note(post.Id, "A quoted bit", "Needs a source."));
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        cut.Find(".post-hero__ribbon").TextContent.Should().Be("Draft", "the review marks the draft (spec §6.6)");
        cut.Find(".markdown-body").TextContent.Should().Contain("hillsideword");
        var rail = cut.Find(".post-rail");
        rail.QuerySelector(".feedback-panel")!.TextContent.Should().Contain("Needs a source.")
            .And.Contain("A quoted bit", "the notes appear in the right-hand column");
        cut.Markup.Should().NotContain("null", "Review Focus 1: the draft may lack category, banner, subtitle and tags");
    }

    [Fact]
    public async Task DraftReviewPage_RailTocLinks_ArePathQualified()
    {
        Setup(Draft("## First section\n\n## Second section\n\nBody."));
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        // A bare "#id" would resolve against <base href="/"> and leave the review page.
        cut.Find("a.toc__link").GetAttribute("href").Should().Be("/editor/posts/the-draft#first-section");
    }

    [Fact]
    public async Task DraftReviewPage_Selection_ShowsTheAddFeedbackButton_AndTheSelectionBecomesTheNoteQuote()
    {
        var post = Draft();
        Setup(post);
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        cut.FindAll("button").Should().NotContain(b => b.TextContent == "Add feedback",
            "no selection, no button");

        await cut.Instance.OnSelectionChanged("some words", 120, 40);

        var button = cut.FindAll("button").Single(b => b.TextContent == "Add feedback");
        button.HasAttribute("style").Should().BeFalse(
            "the button is positioned from JS through the CSSOM; a style attribute would need style-src-attr 'unsafe-inline'");
        cut.Markup.Should().NotContain("style=\"", "no element of the review page may write a style attribute");
        await cut.WaitForAssertionAsync(() =>
            JSInterop.Invocations["blogEditor.placeSelectionButton"].Should().HaveCount(1));
        JSInterop.Invocations["blogEditor.placeSelectionButton"].Single().Arguments
            .Should().Equal([120, 40], "the button appears at the selection");
        button.Click(new MouseEventArgs());
        cut.FindAll("button").Should().NotContain(b => b.TextContent == "Add feedback",
            "the hand-off is done once the button is used");

        cut.Find("textarea[name='comment']").Change("Check the second paragraph.");
        cut.FindAll("button").Single(b => b.TextContent == "Add").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => _api.CreatedFeedback.Should().ContainSingle());
        _api.CreatedFeedback[0].Should().Be((post.Id, new CreateFeedbackRequest("some words", "Check the second paragraph.")),
            "the selection travels as the note's quote");
        cut.Find(".feedback-panel__open").TextContent.Should().Contain("Check the second paragraph.",
            "the new note appears among the open ones");
    }

    [Fact]
    public async Task DraftReviewPage_EmptySelectionReport_HidesTheButton()
    {
        Setup(Draft());
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        await cut.Instance.OnSelectionChanged("some words", 120, 40);
        cut.FindAll("button").Should().Contain(b => b.TextContent == "Add feedback");

        await cut.Instance.OnSelectionChanged("", 0, 0);
        cut.FindAll("button").Should().NotContain(b => b.TextContent == "Add feedback",
            "a cleared selection takes the button with it");
    }

    [Fact]
    public async Task DraftReviewPage_HandOff_ClearsTheDomSelection()
    {
        Setup(Draft());
        JSInterop.SetupVoid("blogEditor.clearSelection");
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body"));

        await cut.Instance.OnSelectionChanged("some words", 120, 40);
        cut.FindAll("button").Single(b => b.TextContent == "Add feedback").Click(new MouseEventArgs());

        // The DOM selection goes with the hand-off: left alive it re-shows the button on the
        // next keyup/mouseup even though its quote was already consumed.
        JSInterop.VerifyInvoke("blogEditor.clearSelection", calledTimes: 1);
    }

    [Fact]
    public async Task DraftReviewPage_SlugToSlugReload_DisposesTheReplacedSelectionWatch()
    {
        var first = Draft();
        var second = first with { Id = Guid.CreateVersion7(), Slug = "the-second", Title = "The Second" };
        Setup(first);
        _api.PostsById[second.Id] = second;
        JSInterop.SetupVoid("blogEditor.watchSelection");
        var cut = Render<DraftReviewPage>(p => p.Add(x => x.Slug, "the-draft"));
        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogEditor.watchSelection"].Should().HaveCount(1));

        cut.Render(p => p.Add(x => x.Slug, "the-second"));
        await cut.WaitForAssertionAsync(() =>
            JSInterop.Invocations["blogEditor.watchSelection"].Should().HaveCount(2, "the new slug starts its own watch"));

        var watches = JSInterop.Invocations["blogEditor.watchSelection"]
            .Select(i => (DotNetObjectReference<DraftReviewPage>)i.Arguments[1]!).ToList();
        var readOld = () => watches[0].Value;
        readOld.Should().Throw<ObjectDisposedException>("the replaced watch's reference must be disposed");
        watches[1].Value.Should().BeSameAs(cut.Instance, "the current watch's reference stays live");
    }
}
