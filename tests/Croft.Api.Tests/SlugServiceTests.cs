using Croft.Api.Services;
using AwesomeAssertions;

namespace Croft.Api.Tests;

public class SlugServiceTests
{
    private readonly SlugService _slugService = new();

    [Theory]
    [InlineData("C#", "c-sharp")]
    [InlineData("F#", "f-sharp")]
    [InlineData("C# Programming", "c-sharp-programming")]
    public void GenerateSlug_ReplacesHashSign(string input, string expected)
    {
        _slugService.GenerateSlug(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("A+", "a-plus")]
    [InlineData("C++", "c-plus-plus")]
    [InlineData("A+ Tips", "a-plus-tips")]
    public void GenerateSlug_ReplacesPlusSign(string input, string expected)
    {
        _slugService.GenerateSlug(input).Should().Be(expected);
    }

    [Fact]
    public void GenerateSlug_DifferentNames_CanProduceSameSlug()
    {
        _slugService.GenerateSlug("C#").Should().Be(_slugService.GenerateSlug("C Sharp"));
    }
}
