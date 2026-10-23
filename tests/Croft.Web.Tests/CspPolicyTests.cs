using Croft.Web.Security.Csp;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The CSP policy model: sources union per directive without duplicates and keep their order,
/// a source added to a directive holding only 'none' drops the 'none' (CSP requires 'none' to
/// stand alone), and the header value comes out in a fixed directive order so it is stable for
/// tests and caches, with directives outside that order appended sorted.
/// </summary>
public class CspPolicyTests
{
    [Fact]
    public void Merge_UnionsSourcesPerDirective_WithoutDuplicates_KeepingOrder()
    {
        var policy = new CspPolicy()
            .Add("default-src", "'self'")
            .Add("img-src", "'self'", "https://images.example.com");
        var other = new CspPolicy()
            .Add("img-src", "https://cdn.example.com", "'self'")
            .Add("font-src", "'self'");

        policy.Merge(other);

        policy.ToString().Should().Be(
            "default-src 'self'; " +
            "img-src 'self' https://images.example.com https://cdn.example.com; " +
            "font-src 'self'",
            "a merge unions the sources of both policies per directive, in order and without duplicates");
    }

    [Fact]
    public void Add_ToADirectiveHoldingOnlyNone_DropsTheNone()
    {
        var policy = new CspPolicy()
            .Add("style-src-attr", "'none'")
            .Add("style-src-attr", "'unsafe-inline'");

        policy.ToString().Should().Be(
            "style-src-attr 'unsafe-inline'",
            "'none' must stand alone in a directive, so the first real source replaces it");
    }

    [Fact]
    public void Merge_IntoADirectiveHoldingOnlyNone_DropsTheNone()
    {
        var policy = new CspPolicy().Add("style-src-attr", "'none'");

        policy.Merge(new CspPolicy().Add("style-src-attr", "'unsafe-inline'"));

        policy.ToString().Should().Be(
            "style-src-attr 'unsafe-inline'",
            "merging a source into a 'none'-only directive is the same addition as Add");
    }

    [Fact]
    public void ToString_WritesTheFixedDirectiveOrder_ThenUnknownDirectivesSorted()
    {
        var policy = new CspPolicy()
            .Add("form-action", "'self'")
            .Add("zebra-src", "x")
            .Add("default-src", "'self'")
            .Add("report-to", "csp")
            .Add("apple-src", "y")
            .Add("img-src", "'self'");

        policy.ToString().Should().Be(
            "default-src 'self'; img-src 'self'; form-action 'self'; report-to csp; " +
            "apple-src y; zebra-src x",
            "the header value must be stable for tests and caches whatever order the sources were added in");
    }
}
