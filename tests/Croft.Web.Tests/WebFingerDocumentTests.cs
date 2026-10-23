using AwesomeAssertions;
using System.Text;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 11a item 3: <c>.well-known/webfinger</c> (RFC 7033) is parsed at startup as a JSON
/// Resource Descriptor: the required <c>subject</c> (string) and the optional
/// <c>aliases</c> (array of strings). A malformed file fails startup naming the file. The
/// <c>?resource=&lt;r&gt;</c> endpoint matches case-insensitively on the host part of an
/// <c>acct:</c> handle (<c>ACCT:steve@EXAMPLE.NET</c> ≡ <c>acct:steve@example.net</c>) and
/// exactly otherwise.
/// </summary>
public class WebFingerDocumentTests
{
    [Fact]
    public void Parse_ValidJsonWithSubjectOnly_Succeeds()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"subject":"acct:steve@example.net"}""");

        var doc = WebFingerDocument.Parse(".well-known/webfinger", bytes);

        doc.Subject.Should().Be("acct:steve@example.net");
        doc.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ValidJsonWithSubjectAndAliases_Succeeds()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"subject":"acct:steve@example.net","aliases":["acct:steve@example.com","acct:steve@users.example.net"]}""");

        var doc = WebFingerDocument.Parse(".well-known/webfinger", bytes);

        doc.Subject.Should().Be("acct:steve@example.net");
        doc.Aliases.Should().Equal("acct:steve@example.com", "acct:steve@users.example.net");
    }

    [Fact]
    public void Parse_NotJson_FailsStartupWithTheFileNamed()
    {
        var bytes = Encoding.UTF8.GetBytes("not json at all");

        var act = () => WebFingerDocument.Parse(".well-known/webfinger", bytes);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*.well-known/webfinger*not valid JSON*");
    }

    [Fact]
    public void Parse_JsonArray_FailsStartup()
    {
        var bytes = Encoding.UTF8.GetBytes("[]");

        var act = () => WebFingerDocument.Parse(".well-known/webfinger", bytes);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*must be a JSON object*");
    }

    [Fact]
    public void Parse_JsonObjectMissingSubject_FailsStartupNamingTheField()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"aliases":["acct:other@example.com"]}""");

        var act = () => WebFingerDocument.Parse(".well-known/webfinger", bytes);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*missing the required 'subject'*");
    }

    [Fact]
    public void Parse_SubjectIsNotAString_FailsStartup()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"subject":42}""");

        var act = () => WebFingerDocument.Parse(".well-known/webfinger", bytes);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*'subject' field must be a string*");
    }

    [Fact]
    public void Parse_AliasesIsNotAnArray_FailsStartup()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"subject":"acct:steve@example.com","aliases":"oops"}""");

        var act = () => WebFingerDocument.Parse(".well-known/webfinger", bytes);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*the 'aliases' field must be an array*");
    }

    [Fact]
    public void Parse_AliasIsNotAString_FailsStartup()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"subject":"acct:steve@example.com","aliases":[42]}""");

        var act = () => WebFingerDocument.Parse(".well-known/webfinger", bytes);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*every 'aliases' entry must be a string*");
    }

    [Fact]
    public void Matches_Subject_MatchesExactly()
    {
        var doc = MakeDocument("acct:steve@example.net");

        doc.Matches("acct:steve@example.net").Should().BeTrue();
        doc.Matches("acct:other@example.com").Should().BeFalse();
    }

    [Fact]
    public void Matches_Alias_MatchesExactly()
    {
        var doc = MakeDocument("acct:steve@example.net", aliases: ["acct:steve@example.com"]);

        doc.Matches("acct:steve@example.com").Should().BeTrue();
        doc.Matches("acct:other@example.com").Should().BeFalse();
    }

    [Fact]
    public void Matches_AcctHost_IsCaseInsensitive()
    {
        var doc = MakeDocument("acct:steve@example.net");

        doc.Matches("ACCT:steve@EXAMPLE.NET").Should().BeTrue(
            "RFC 7033 §4.5 says case-sensitive, but the deployed public web treats hosts case-insensitively and the brief specifies this behavior");
        doc.Matches("acct:STEVE@example.net").Should().BeFalse(
            "the user-local part is exact-matched (Ordinal): the brief's 'exactly otherwise' applies to everything except the acct: host part");
        doc.Matches("acct:steve@other.dev").Should().BeFalse();
    }

    [Fact]
    public void Matches_AliasHost_IsAlsoCaseInsensitive()
    {
        var doc = MakeDocument("acct:steve@example.net", aliases: ["acct:steve@example.com"]);

        doc.Matches("acct:steve@EXAMPLE.COM").Should().BeTrue();
        doc.Matches("acct:steve@example.org").Should().BeFalse();
    }

    [Fact]
    public void Matches_NonAcctResource_IsExactOnly()
    {
        var doc = MakeDocument("acct:steve@example.net");

        // The user-local part of acct: handles is exact-matched (case-sensitive): the user
        // knows their own handle and changing case is a different handle. Only the host
        // part is case-insensitive.
        doc.Matches("acct:STEVE@example.net").Should().BeFalse(
            "the local part 'STEVE' does not equal 'steve' (Ordinal) even though the host matches");
        doc.Matches("mailto:steve@example.net").Should().BeFalse();
        doc.Matches("acct:steve@example.net ").Should().BeFalse("a trailing space is not the same");
    }

    private static WebFingerDocument MakeDocument(string subject, params string[] aliases) =>
        new(subject, aliases);
}