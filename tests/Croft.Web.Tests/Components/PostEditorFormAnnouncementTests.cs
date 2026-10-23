using AwesomeAssertions;
using Bunit;
using Croft.Web.Components.Admin;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 12d item 6: the announcement fields in the editor's Discussion section: a flag per network (on for a new post), the announcement text with the
/// default as its placeholder, and per network a preview of the composed text with its count against the network's limit. Pages have none of it.
/// </summary>
public class PostEditorFormAnnouncementTests : BlogBunitContext
{
    // bUnit's navigation manager is at http://localhost/, so the preview's address is http://localhost/post/a-post (28 characters).
    private const string Url = "http://localhost/post/a-post";

    private IRenderedComponent<PostEditorForm> Render(PostEditModel? model = null, bool isPage = false, string? slug = "a-post")
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)));
        model ??= new PostEditModel { Title = "A title", Subtitle = "A subtitle", MarkdownContent = "Body." };
        return Render<PostEditorForm>(p => p.Add(x => x.Model, model).Add(x => x.IsPage, isPage).Add(x => x.Slug, slug));
    }

    private static string Count(IRenderedComponent<PostEditorForm> cut, string network) =>
        cut.Find($"#announce-preview-{network} .announce-preview__count").TextContent.Trim();

    [Fact]
    public void ANewModel_HasBothFlagsOn_AndTheCheckboxesAreChecked()
    {
        new PostEditModel().AnnounceMastodon.Should().BeTrue();
        new PostEditModel().AnnounceBluesky.Should().BeTrue();

        var cut = Render();

        cut.Find("#announce-mastodon").HasAttribute("checked").Should().BeTrue();
        cut.Find("#announce-bluesky").HasAttribute("checked").Should().BeTrue();
        cut.Markup.Should().Contain("Announce on Mastodon when published").And.Contain("Announce on Bluesky when published");
    }

    [Fact]
    public void APage_HasNoAnnouncementFields()
    {
        var cut = Render(isPage: true);

        cut.FindAll("#announce-mastodon, #announce-bluesky, #announcement-text, .announce-preview").Should().BeEmpty();
    }

    [Fact]
    public void TheTextField_HasTheDefaultAsItsPlaceholder_AndTheHelper()
    {
        var cut = Render();

        var field = cut.Find("#announcement-text");
        field.GetAttribute("placeholder").Should().Be("A title\n\nA subtitle");
        var helper = cut.Find(".post-form__announcement .mud-input-helper-text").TextContent;
        helper.Should().Contain("The post's address is added at the end.").And.Contain("Hashtags (#homelab) are linked on both networks.");
    }

    [Fact]
    public void WithNoText_ThePreviewsShowTheDefault_AndTheAddress()
    {
        var cut = Render();

        cut.Find("#announce-preview-mastodon .announce-preview__text").TextContent.Should().Be($"A title\n\nA subtitle\n\n{Url}");
        cut.Find("#announce-preview-bluesky .announce-preview__text").TextContent.Should().Be($"A title\n\nA subtitle\n\n{Url}");
    }

    [Fact]
    public void TheCounts_FollowEachNetworksRule_ForAKnownTextAndAddress()
    {
        var model = new PostEditModel { Title = "T", AnnouncementText = "Hello" };
        var cut = Render(model);

        // "Hello" + 2 line breaks + the address: Bluesky counts the address as written (28), Mastodon as 23.
        Count(cut, "bluesky").Should().Be("35 / 300");
        Count(cut, "mastodon").Should().Be("30 / 500");
    }

    [Fact]
    public void Typing_UpdatesBothCountsAtOnce_AndTheModel()
    {
        var model = new PostEditModel { Title = "T" };
        var cut = Render(model);

        cut.Find("#announcement-text").Input("Hello there");

        model.AnnouncementText.Should().Be("Hello there");
        Count(cut, "bluesky").Should().Be("41 / 300");
        Count(cut, "mastodon").Should().Be("36 / 500");
        cut.Find("#announce-preview-bluesky .announce-preview__text").TextContent.Should().Be($"Hello there\n\n{Url}");
    }

    [Fact]
    public void ATextOverALimit_MarksThatCount_AndOnlyThat()
    {
        var model = new PostEditModel { Title = "T", AnnouncementText = new string('x', 320) };
        var cut = Render(model);

        cut.Find("#announce-preview-bluesky .announce-preview__count").ClassList.Should().Contain("announce-preview__count--over");
        cut.Find("#announce-preview-mastodon .announce-preview__count").ClassList.Should().NotContain("announce-preview__count--over");
        Count(cut, "bluesky").Should().Be("350 / 300");
    }

    [Fact]
    public void ACountIsGraphemes_NotUtf16Units()
    {
        var model = new PostEditModel { Title = "T", AnnouncementText = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466 é" };
        var cut = Render(model);

        // family emoji (1) + space (1) + e with accent (1) + 2 + the address (28)
        Count(cut, "bluesky").Should().Be("33 / 300");
    }

    [Fact]
    public void ALongDefault_IsShownShortened_NotOverTheLimit()
    {
        var model = new PostEditModel { Title = "T", Subtitle = new string('s', 320) };
        var cut = Render(model);

        Count(cut, "bluesky").Should().Be("300 / 300");
        cut.Find("#announce-preview-bluesky .announce-preview__count").ClassList.Should().NotContain("announce-preview__count--over");
        cut.Find("#announce-preview-bluesky .announce-preview__text").TextContent.Should().Contain("…");
    }

    [Fact]
    public void ANetworkWhoseFlagIsOff_HasNoPreview()
    {
        var model = new PostEditModel { Title = "T", AnnounceBluesky = false };
        var cut = Render(model);

        cut.FindAll("#announce-preview-bluesky").Should().BeEmpty();
        cut.FindAll("#announce-preview-mastodon").Should().ContainSingle();
    }

    [Fact]
    public void SwitchingAFlag_ShowsOrHidesItsPreview_AndSetsTheModel()
    {
        var model = new PostEditModel { Title = "T" };
        var cut = Render(model);

        cut.Find("#announce-bluesky").Change(false);

        model.AnnounceBluesky.Should().BeFalse();
        cut.FindAll("#announce-preview-bluesky").Should().BeEmpty();
    }

    [Fact]
    public void ThePreview_IsPlainText_WithItsLineBreaksFromAClass_AndNoInlineStyle()
    {
        var model = new PostEditModel { Title = "<b>bold</b>", AnnouncementText = "<script>alert(1)</script> line\nbreak" };
        var cut = Render(model);

        var preview = cut.Find("#announce-preview-mastodon .announce-preview__text");
        preview.InnerHtml.Should().NotContain("<script>").And.NotContain("<b>");
        preview.TextContent.Should().StartWith("<script>alert(1)</script> line\nbreak");
        cut.FindAll("#announce-preview-mastodon [style], #announce-preview-bluesky [style]").Should().BeEmpty("the CSP allows no inline style of ours");
    }

    [Fact]
    public void WithoutASavedPost_TheAddressUsesAGuessFromTheTitle()
    {
        var model = new PostEditModel { Title = "Load testing the homelab!", AnnouncementText = "Hi" };
        var cut = Render(model, slug: null);

        cut.Find("#announce-preview-bluesky .announce-preview__text").TextContent.Should().EndWith("http://localhost/post/load-testing-the-homelab");
    }

    [Fact]
    public void TheTextsError_ShowsUnderTheField()
    {
        var model = new PostEditModel { Title = "T", AnnouncementTextError = "AnnouncementText is too long for Bluesky (312 of 300 characters)." };
        var cut = Render(model);

        cut.Find("#announcement-text").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find(".post-form__announcement").TextContent.Should().Contain("AnnouncementText is too long for Bluesky (312 of 300 characters).");
    }

    [Fact]
    public void TypingInTheTextField_WithdrawsItsError()
    {
        var model = new PostEditModel { Title = "T", AnnouncementTextError = "AnnouncementText is too long for Bluesky (312 of 300 characters)." };
        var cut = Render(model);

        cut.Find("#announcement-text").Input("shorter");

        model.AnnouncementTextError.Should().BeNull();
    }
}
