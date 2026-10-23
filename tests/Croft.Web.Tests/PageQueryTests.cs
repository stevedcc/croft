using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>Task R3.3: the ?page=N list-page number (spec §5.2) is parsed consistently by the pages.</summary>
public class PageQueryTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("?q=hello", 1)]
    [InlineData("?page=2", 2)]
    [InlineData("?q=hello&page=7", 7)]
    [InlineData("?page=abc", 1)]
    [InlineData("?page=0", 1)]
    [InlineData("?page=-3", 1)]
    public void ReadPageNumber_ReturnsPageOrOne(string? query, int expected)
    {
        PageQuery.ReadPageNumber(query).Should().Be(expected);
    }
}
