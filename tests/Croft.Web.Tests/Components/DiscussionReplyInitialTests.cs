using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts.Social;
using Croft.Web.Components;
using Croft.Web.Components.Admin;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>A reply's initial comes from the first character of its author's name; a lone surrogate (broken text from a remote server) must not take the page down.</summary>
public class DiscussionReplyInitialTests : BlogBunitContext
{
    private static DiscussionReplyDto Reply(string name, string handle = "nora@social.example") => new(SocialPlatform.Mastodon, "1", handle, name, "https://social.example/@nora", "nora",
        null, new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero), "https://social.example/@nora/1", "<p>Text.</p>", null, false, []);

    // The names are looked up by case: a lone surrogate does not survive xunit's theory-data serialisation (it turns into U+FFFD).
    private static readonly Dictionary<string, (string Name, string Expected)> Cases = new()
    {
        ["lone high surrogate"] = ("\uD83D", "?"),
        ["high surrogate then letters"] = ("\uD83Dabc", "?"),
        ["lone low surrogate"] = ("\uDE00x", "?"),
        ["a surrogate pair"] = ("\uD83D\uDE00 smile", "\uD83D\uDE00"),
        ["blank name and handle"] = ("", "?"),
        ["whitespace name and handle"] = ("   ", "?"),
        ["leading space and an accent"] = ("  \u00e9lan", "\u00c9"),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ThePublicReply_ShowsTheInitial_AndNeverThrows(string caseName)
    {
        var (name, expected) = Cases[caseName];
        var handle = caseName.EndsWith("name and handle", StringComparison.Ordinal) ? name : "nora@social.example";
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)));

        var cut = Render<DiscussionReply>(p => p.Add(x => x.Reply, Reply(name, handle)));

        cut.Find(".discussion-reply__initial").TextContent.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TheAdminReply_ShowsTheInitial_AndNeverThrows(string caseName)
    {
        var (name, expected) = Cases[caseName];
        var handle = caseName.EndsWith("name and handle", StringComparison.Ordinal) ? name : "nora@social.example";
        var cut = Render<DiscussionAdminReply>(p => p.Add(x => x.Reply, Reply(name, handle)));

        cut.Find(".discussion-admin__initial").TextContent.Should().Be(expected);
    }
}
