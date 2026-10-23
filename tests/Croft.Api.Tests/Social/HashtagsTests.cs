using Croft.Api.Social;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Social;
using AwesomeAssertions;

namespace Croft.Api.Tests.Social;

/// <summary>The hashtag rules shared by the API and the Web form; they live in Shared, the facet detection they must agree with in the API.</summary>
public class HashtagsTests
{
    [Theory]
    [InlineData("#Homelab", "Homelab")]
    [InlineData("＃Homelab", "Homelab")]
    [InlineData("  #SelfHosted  ", "SelfHosted")]
    [InlineData("DotNet", "DotNet")]
    public void Normalize_StripsALeadingHashAndWhiteSpace(string input, string expected) =>
        Hashtags.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Homelab")]
    [InlineData("self_hosted")]
    [InlineData("C3PO")]
    [InlineData("Übung")]
    [InlineData("日本語")]
    [InlineData("_a")]
    public void ValidNames_HaveNoProblem_AndBlueskyFindsThemAsTheSameTag(string name)
    {
        Hashtags.NameProblem(name).Should().BeNull();

        var facets = BlueskyFacets.Hashtags($"#{name}");
        facets.Should().ContainSingle().Which.Tag.Should().Be(name, "the rules accept only what Bluesky turns into exactly this tag");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("9", "digits only")]
    [InlineData("_", "no letter")]
    [InlineData("two words", "a space")]
    [InlineData("Home-lab", "a hyphen")]
    [InlineData("Home_", "a trailing underscore, which Bluesky drops from the tag")]
    [InlineData("a😀", "an emoji")]
    public void InvalidNames_HaveAProblem(string name, string because) =>
        Hashtags.NameProblem(name).Should().NotBeNullOrEmpty(because);

    [Fact]
    public void Name_OfSixtyFourCharactersIsValid_AndOfSixtyFiveIsNot()
    {
        Hashtags.NameProblem(new string('a', 64)).Should().BeNull();
        Hashtags.NameProblem(new string('a', 65)).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RowProblems_FlagTheBadRow_TheCaseInsensitiveDuplicate_AndEveryRowBeyondTen()
    {
        List<HashtagDto> list = [new("Homelab", HashtagNetworks.Both), new("two words", HashtagNetworks.Both), new("homelab", HashtagNetworks.BlueskyOnly)];

        var problems = Hashtags.RowProblems(list);

        problems[0].Should().BeNull();
        problems[1].Should().Contain("two words");
        problems[2].Should().Contain("homelab").And.Contain("Homelab");

        var eleven = Enumerable.Range(0, 11).Select(i => new HashtagDto("Tag" + (char)('a' + i), HashtagNetworks.Both)).ToList();
        var elevenProblems = Hashtags.RowProblems(eleven);
        elevenProblems.Take(10).Should().OnlyContain(p => p == null);
        elevenProblems[10].Should().Contain("Tagk").And.Contain("10");
    }

    [Fact]
    public void CategoryAndTagDtos_WithTheSameHashtagsInDifferentLists_AreEqual()
    {
        var id = Guid.CreateVersion7();
        var category = new Croft.Shared.Contracts.CategoryDto(id, "C", "c", null, 1, [new("Homelab", HashtagNetworks.Both)]);
        var tag = new Croft.Shared.Contracts.TagDto(id, "T", "t", 1, true, [new("Homelab", HashtagNetworks.Both)]);

        List<HashtagDto> sameInAnotherList = [new("Homelab", HashtagNetworks.Both)];
        category.Should().Be(new Croft.Shared.Contracts.CategoryDto(id, "C", "c", null, 1, sameInAnotherList));
        tag.Should().Be(new Croft.Shared.Contracts.TagDto(id, "T", "t", 1, true, sameInAnotherList));
        category.Should().NotBe(category with { Hashtags = [new("Other", HashtagNetworks.Both)] });
    }
}
