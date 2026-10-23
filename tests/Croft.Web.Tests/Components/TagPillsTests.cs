using Croft.Shared.Contracts;
using Croft.Web.Components;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Task R3.2: green tag pills linking to their tag pages (spec §7.3). Review Focus 1/2: no tags
/// renders nothing; 20 tags all render.
/// </summary>
public class TagPillsTests : BlogBunitContext
{
    [Fact]
    public void TagPills_LinksToTagPages()
    {
        var tags = new List<TagDto>
        {
            new(Guid.NewGuid(), "blazor", "blazor", 0, true),
            new(Guid.NewGuid(), "dotnet", "dotnet", 0, true),
        };

        var cut = Render<TagPills>(p => p.Add(x => x.Tags, tags));

        var links = cut.FindAll("a.tag-pill");
        links.Select(l => l.GetAttribute("href")).Should().Equal("/tag/blazor", "/tag/dotnet");
        links.Select(l => l.TextContent).Should().Equal("#blazor", "#dotnet");
    }

    [Fact]
    public void TagPills_NoTags_RendersNothing()
    {
        // Review Focus 1: no empty pill row.
        var cut = Render<TagPills>(p => p.Add(x => x.Tags, []));

        cut.Markup.Should().BeEmpty();
    }

    [Fact]
    public void TagPills_TwentyTags_AllRender()
    {
        // Review Focus 2: 20 tags must all be reachable; the wrapping/clamping is CSS (pinned in
        // CardCssContractTests) and the 390px overflow check is Task R3.6's E2E.
        var tags = Enumerable.Range(1, 20)
            .Select(i => new TagDto(Guid.NewGuid(), $"tag-{i}", $"tag-{i}", 0, true))
            .ToList();

        var cut = Render<TagPills>(p => p.Add(x => x.Tags, tags));

        cut.FindAll("a.tag-pill").Should().HaveCount(20);
    }
}
