using Croft.Shared;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The shared reading-time rule (ruling R5-3-d): ceil(words / 220), minimum 1 minute, used by the
/// API's stored reading time and by the editor's live preview — pinned here with the spec's
/// documented cases so the two sides cannot drift apart.
/// </summary>
public class ReadingTimeTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(220, 1)]
    [InlineData(221, 2)]
    [InlineData(660, 3)]
    public void FromWords_UsesTheSpecCases(int words, int expected) =>
        ReadingTime.FromWords(words).Should().Be(expected);
}
