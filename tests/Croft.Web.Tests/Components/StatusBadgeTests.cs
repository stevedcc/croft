using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// StatusBadge (spec §3.1/§7.4): Published uses the green container, Scheduled the amber
/// container and Draft neutral grey. bUnit has no colours, so the render tests pin the per-status
/// class and the CSS contract pins the verbatim container values underneath each class.
/// </summary>
public class StatusBadgeTests : BlogBunitContext
{
    private static string Css() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Croft.Web", "Components", "Admin", "StatusBadge.razor.css"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent!;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    [Theory]
    [InlineData(PostStatus.Published, "status-badge--published", "Published")]
    [InlineData(PostStatus.Scheduled, "status-badge--scheduled", "Scheduled")]
    [InlineData(PostStatus.Draft, "status-badge--draft", "Draft")]
    public void StatusBadge_ColoursPerStatus(PostStatus status, string cssClass, string text)
    {
        var cut = Render<StatusBadge>(p => p.Add(x => x.Status, status));

        var badge = cut.Find(".status-badge");
        badge.ClassList.Should().Contain(cssClass);
        badge.TextContent.Should().Be(text);
    }

    [Fact]
    public void StatusBadgeCss_Published_UsesTheGreenContainer()
    {
        var css = Css();

        css.Should().Contain(".status-badge--published");
        css.Should().Contain("var(--secondary-container)");
        css.Should().Contain("var(--on-secondary-container)");
    }

    [Fact]
    public void StatusBadgeCss_Scheduled_UsesTheAmberContainer()
    {
        var css = Css();

        css.Should().Contain(".status-badge--scheduled");
        css.Should().MatchRegex(@"background:\s*#FFF1D6;");
        css.Should().MatchRegex(@"color:\s*#7A4B00;");
        css.Should().MatchRegex(@"background:\s*#3A2E14;");
        css.Should().MatchRegex(@"color:\s*#F2C877;");
    }

    [Fact]
    public void StatusBadgeCss_Draft_UsesNeutralGrey()
    {
        var css = Css();

        css.Should().Contain(".status-badge--draft");
        css.Should().MatchRegex(@"background:\s*rgba\(128,\s*128,\s*128");
    }
}
