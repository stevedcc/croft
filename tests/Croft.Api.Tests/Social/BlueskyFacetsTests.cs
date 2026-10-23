using System.Text;
using AwesomeAssertions;
using Croft.Api.Social;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12d item 4 (and the owner's amendment): the facets of a Bluesky post. Offsets are UTF-8 byte offsets, not characters; hashtags follow the rule of
/// the official @atproto/api <c>detectFacets</c>; the post address is a link facet.
/// </summary>
public class BlueskyFacetsTests
{
    private static string Slice(string text, BlueskyFacet facet) =>
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)[facet.ByteStart..facet.ByteEnd]);

    [Fact]
    public void Hashtags_AreFound_WithTheTrailingPunctuationLeftOut_AndByteOffsetsAfterAnEmojiAndAnUmlaut()
    {
        // ü is 2 bytes, 😀 is 4: the offsets in bytes differ from the offsets in characters.
        const string text = "Grüße 😀 Load testing #homelab and #dotnet.";

        var facets = BlueskyFacets.Hashtags(text);

        facets.Select(f => (f.Tag, f.ByteStart, f.ByteEnd)).Should().Equal(
            ("homelab", 26, 34),
            ("dotnet", 39, 46));
        facets.Select(f => Slice(text, f)).Should().Equal("#homelab", "#dotnet");
        text.IndexOf("#homelab", StringComparison.Ordinal).Should().BeLessThan(26, "the character offset is smaller than the byte offset");
    }

    [Fact]
    public void Hashtags_ForTheFullWidthNumberSign_AndAtTheStartOfTheText()
    {
        var facets = BlueskyFacets.Hashtags("＃start of it");

        facets.Should().ContainSingle().Which.Tag.Should().Be("start");
        Slice("＃start of it", facets[0]).Should().Be("＃start");
    }

    [Theory]
    [InlineData("#2026")]
    [InlineData("the year #2026 and #123!")]
    [InlineData("a#b")]
    [InlineData("#")]
    [InlineData("# alone")]
    [InlineData("see https://blog.example/post/a-post#section here")]
    [InlineData("https://blog.example/post/a-post#frag")]
    [InlineData("#!!!")]
    public void Hashtags_NotATag_FindsNothing(string text)
    {
        BlueskyFacets.Hashtags(text).Should().BeEmpty();
    }

    [Fact]
    public void Hashtags_ATagWithDigitsAndLetters_IsATag_AndPunctuationInsideIsKept()
    {
        var tags = BlueskyFacets.Hashtags("#a1 #2026ready #pi-hole, #end?!").Select(f => f.Tag);

        tags.Should().Equal("a1", "2026ready", "pi-hole", "end");
    }

    [Fact]
    public void Hashtags_LongerThan64Characters_AreSkipped()
    {
        var text = $"#{new string('t', 65)} and #{new string('u', 64)}";

        BlueskyFacets.Hashtags(text).Select(f => f.Tag).Should().Equal(new string('u', 64));
    }

    [Fact]
    public void Hashtags_OnSeveralLines_AreFoundAfterANewline()
    {
        BlueskyFacets.Hashtags("first line\n#second\n\n#third").Select(f => f.Tag).Should().Equal("second", "third");
    }

    [Fact]
    public void All_HasTheLinkFacetOfTheAddress_AndTheTags_OrderedByStart()
    {
        const string url = "https://blog.example/post/a-post#frag";
        var text = $"Ünïcode 😀 #homelab\n\n{url}";

        var facets = BlueskyFacets.All(text, url);

        facets.Select(f => f.ByteStart).Should().BeInAscendingOrder();
        facets.Should().HaveCount(2, "the '#' inside the address is no tag");
        facets[0].Tag.Should().Be("homelab");
        facets[1].Uri.Should().Be(url);
        Slice(text, facets[1]).Should().Be(url);
        Slice(text, facets[0]).Should().Be("#homelab");
    }

    [Fact]
    public void All_WhenTheAddressIsNotInTheText_HasNoLinkFacet()
    {
        BlueskyFacets.All("no address here", "https://blog.example/post/a-post").Should().BeEmpty();
    }
}
