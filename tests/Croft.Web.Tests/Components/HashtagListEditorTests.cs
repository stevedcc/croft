using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts.Social;
using Croft.Web.Components.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Croft.Web.Tests.Components;

/// <summary>Brief 12e item 4: the hashtag list editor of Tags &amp; categories: one row per hashtag, the shared rules' message under a bad row, and no change reported for it.</summary>
public class HashtagListEditorTests : BlogBunitContext
{
    private readonly List<IReadOnlyList<HashtagDto>> _changes = [];
    private readonly List<bool> _validity = [];

    private IRenderedComponent<HashtagListEditor> RenderEditor(params HashtagDto[] value) =>
        Render<HashtagListEditor>(p => p
            .Add(x => x.Value, value)
            .Add(x => x.Label, "Hashtags")
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<IReadOnlyList<HashtagDto>>(this, list => _changes.Add(list)))
            .Add(x => x.ValidChanged, EventCallback.Factory.Create<bool>(this, _validity.Add)));

    private static void AddRow(IRenderedComponent<HashtagListEditor> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Add hashtag")).Click(new MouseEventArgs());

    [Fact]
    public void Adding_AndTyping_ReportsTheNewList_WithBothNetworksByDefault()
    {
        var cut = RenderEditor();

        AddRow(cut);
        cut.Find("input[aria-label='Hashtag']").Input("#Homelab");

        _changes.Should().ContainSingle().Which.Should().Equal([new HashtagDto("Homelab", HashtagNetworks.Both)],
            "the # is stripped and the new row is for both networks");
    }

    [Fact]
    public void ChangingTheNetwork_ReportsTheListWithThatNetwork_AndOffersTheThreeChoices()
    {
        var cut = RenderEditor(new HashtagDto("Homelab", HashtagNetworks.Both));

        var select = cut.Find("select[aria-label='Networks']");
        select.QuerySelectorAll("option").Select(o => o.TextContent).Should().Equal("Both", "Mastodon only", "Bluesky only");
        select.Change(nameof(HashtagNetworks.BlueskyOnly));

        _changes.Should().ContainSingle().Which.Should().Equal([new HashtagDto("Homelab", HashtagNetworks.BlueskyOnly)]);
    }

    [Fact]
    public void Removing_ReportsTheListWithoutIt_AndTheButtonNamesTheHashtag()
    {
        var cut = RenderEditor(new HashtagDto("Homelab", HashtagNetworks.Both), new HashtagDto("SelfHosted", HashtagNetworks.BlueskyOnly));

        cut.Find("button[aria-label='Remove #Homelab']").Click(new MouseEventArgs());

        _changes.Should().ContainSingle().Which.Should().Equal([new HashtagDto("SelfHosted", HashtagNetworks.BlueskyOnly)]);
        cut.FindAll("input[aria-label='Hashtag']").Should().ContainSingle();
    }

    [Fact]
    public void ABadHashtag_ShowsTheRulesMessageUnderItsRow_ReportsNoChange_AndIsNotValid()
    {
        var cut = RenderEditor();
        AddRow(cut);

        cut.Find("input[aria-label='Hashtag']").Input("two words");

        _changes.Should().BeEmpty("a hashtag that breaks the rules is never reported");
        cut.Find(".hashtag-editor__problem").TextContent.Should().Contain("two words").And.Contain("letters, digits and underscores");
        cut.Find("input[aria-label='Hashtag']").GetAttribute("aria-invalid").Should().Be("true");
        _validity.Should().Equal(false);

        cut.Find("input[aria-label='Hashtag']").Input("TwoWords");

        _changes.Should().ContainSingle().Which.Should().Equal([new HashtagDto("TwoWords", HashtagNetworks.Both)]);
        cut.FindAll(".hashtag-editor__problem").Should().BeEmpty();
        _validity.Should().Equal(false, true);
    }

    [Fact]
    public void ACaseInsensitiveDuplicate_IsFlaggedOnTheLaterRow()
    {
        var cut = RenderEditor(new HashtagDto("Homelab", HashtagNetworks.Both));
        AddRow(cut);

        cut.FindAll("input[aria-label='Hashtag']")[1].Input("homelab");

        cut.Find(".hashtag-editor__problem").TextContent.Should().Contain("homelab").And.Contain("Homelab");
        _changes.Should().BeEmpty();
    }

    [Fact]
    public void AddIsOffered_UpToTenRows_ThenDisabled()
    {
        var cut = RenderEditor([.. Enumerable.Range(0, 10).Select(i => new HashtagDto("Tag" + (char)('a' + i), HashtagNetworks.Both))]);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Add hashtag")).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void TheHint_SaysUseEstablishedHashtagsInCamelCase_AndTheCap()
    {
        var cut = RenderEditor();

        cut.Find(".hashtag-editor__hint").TextContent.Should().Be(
            "Use established hashtags in CamelCase, e.g. #SelfHosted. Up to 3 per announcement: category first, then tags.");
    }
}
