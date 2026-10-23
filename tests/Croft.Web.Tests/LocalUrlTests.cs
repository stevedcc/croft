using AwesomeAssertions;

namespace Croft.Web.Tests;

public class LocalUrlTests
{
    [Theory]
    [InlineData("/admin")]
    [InlineData("/post/x?y=1")]
    [InlineData("/")]
    [InlineData("~/admin")]
    [InlineData("/admin/posts?page=2&q=a%20b")]
    [InlineData("/post/x#fragment")]
    [InlineData("/a//b")]
    [InlineData("~/")]
    public void IsLocal_LocalPaths_ReturnsTrue(string url)
    {
        LocalUrl.IsLocal(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    // Browsers drop ASCII tab and newline anywhere in a URL (WHATWG), so these are "//evil.example".
    [InlineData("/\t/evil.example")]
    [InlineData("/\n//evil.example")]
    [InlineData("/\r/evil.example")]
    [InlineData("/ /evil.example")]
    [InlineData("/\u000B/evil.example")]
    [InlineData("/\u007F/evil.example")]
    [InlineData("/ok/\tmore")]
    [InlineData("/ok path")]
    [InlineData("/\u00A0/evil.example")]
    [InlineData("~//evil.example")]
    [InlineData("~/\t/evil.example")]
    public void IsLocal_ExternalOrMaliciousUrls_ReturnsFalse(string url)
    {
        LocalUrl.IsLocal(url).Should().BeFalse();
    }

    [Fact]
    public void IsLocal_Null_ReturnsFalse()
    {
        LocalUrl.IsLocal(null).Should().BeFalse();
    }

    // Culture-sensitive prefix matching ignores ignorable characters (soft hyphens, zero-width
    // spaces), so a prefix of ignorables before "~/" would pass the "~/" check and be rewritten
    // into a local path. The check must be ordinal: only a literal "~/" prefix counts.
    [Theory]
    [InlineData("\u00AD\u00AD~/admin")]
    [InlineData("\u200B\u200B~/admin")]
    public void IsLocal_TildePrefixSmuggledWithIgnorableCharacters_ReturnsFalse(string url)
    {
        LocalUrl.IsLocal(url).Should().BeFalse();
    }
}
