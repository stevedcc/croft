using Bunit;
using AwesomeAssertions;
using Croft.Web.Components.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 11a item 4: the owner's profile links in the head — one <c>rel="me"</c> link per
/// profile in the owner's order with its exact href, and the <c>fediverse:creator</c> meta when
/// the branding sets a handle. The component is rendered from the document shell (App.razor),
/// so every page (static and interactive) carries the links; with no profiles in the branding
/// it renders nothing at all.
/// </summary>
public class ProfileLinksTests : BlogBunitContext
{
    [Fact]
    public void OneRelMeLinkPerProfile_InOwnerOrder_WithExactHrefs()
    {
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            Profiles =
            [
                new("mastodon", "https://example.social/@someone"),
                new("bluesky", "https://bsky.app/profile/example.com"),
                new("github", "https://github.com/someone"),
                new("linkedin", "https://www.linkedin.com/in/someone"),
            ],
        });

        var cut = Render<ProfileLinks>();

        string[] expected =
        [
            "https://example.social/@someone",
            "https://bsky.app/profile/example.com",
            "https://github.com/someone",
            "https://www.linkedin.com/in/someone",
        ];
        cut.FindAll("link[rel='me']").Select(link => link.GetAttribute("href")).Should().Equal(
            expected,
            "the owner's order is kept and every href is exact");
    }

    [Fact]
    public void FediverseCreatorMeta_PresentWithTheExactHandle()
    {
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            FediverseCreator = "@someone@example.social",
        });

        var cut = Render<ProfileLinks>();

        cut.Find("meta[name='fediverse:creator']").GetAttribute("content")
            .Should().Be("@someone@example.social");
    }

    [Fact]
    public void NoProfilesAndNoHandle_RendersNothing()
    {
        // BlogBunitContext's branding has neither: nothing below renders.
        var cut = Render<ProfileLinks>();

        cut.MarkupMatches(string.Empty);
    }

    [Fact]
    public void MetaAbsent_WhenTheBrandingHasNoHandle()
    {
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            Profiles = [new("github", "https://github.com/someone")],
        });

        var cut = Render<ProfileLinks>();

        cut.FindAll("meta[name='fediverse:creator']").Should().BeEmpty(
            "no fediverseCreator in the branding means no meta tag");
    }
}
