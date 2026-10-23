using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The editor form's draft-backup hooks (D3's guaranteed layer): the DOM contract the
/// same-origin draft-backup.js module reads — the scope with the post key and the server's
/// saved-version stamp, and the two backed-up fields (title, body). Data attributes, no inline
/// script or style attribute: the module must work under the enforced CSP.
/// </summary>
public class PostEditorFormDraftHooksTests : BlogBunitContext
{
    private static PostEditModel Model() => new()
    {
        Title = "A title",
        MarkdownContent = "Some body.",
    };

    [Fact]
    public void Form_CarriesTheDraftScope_WithThePostKeyAndTheSavedVersionStamp()
    {
        Services.AddSingleton<TimeProvider>(FakeClockAtNoon());
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.DraftKey, "9f0e2f6a-1111-2222-3333-444455556666")
            .Add(x => x.SavedStamp, "2026-10-12T09:00:00.0000000Z"));

        var scope = cut.Find("[data-draft-scope]");
        scope.GetAttribute("data-draft-key").Should().Be("9f0e2f6a-1111-2222-3333-444455556666",
            "the backup is keyed to the post the editor loaded");
        scope.GetAttribute("data-draft-stamp").Should().Be("2026-10-12T09:00:00.0000000Z",
            "the backup stores the server's saved version stamp the editor loaded");
    }

    [Fact]
    public void ANewPostsForm_SaysNew_AsItsDraftKey()
    {
        Services.AddSingleton<TimeProvider>(FakeClockAtNoon());
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.DraftKey, "new")
            .Add(x => x.SavedStamp, string.Empty));

        cut.Find("[data-draft-scope]").GetAttribute("data-draft-key").Should().Be("new");
    }

    [Fact]
    public void TitleAndBody_CarryTheirDraftFieldHooks()
    {
        Services.AddSingleton<TimeProvider>(FakeClockAtNoon());
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.DraftKey, "new")
            .Add(x => x.SavedStamp, string.Empty));

        cut.Find("[data-draft-field=\"title\"] input").Should().NotBeNull(
            "the module finds the title input through its hook");
        cut.Find("textarea[data-draft-field=\"body\"]").Should().NotBeNull(
            "the module finds the markdown textarea through its hook");

        // The module must work under the enforced CSP: my markup writes no inline style attribute.
        cut.Find("[data-draft-scope]").GetAttribute("style").Should().BeNull();
        cut.Find("[data-draft-field=\"title\"]").GetAttribute("style").Should().BeNull();
        cut.Find("textarea[data-draft-field=\"body\"]").GetAttribute("style").Should().BeNull();
    }

    private static FakeTimeProvider FakeClockAtNoon() =>
        new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
}
