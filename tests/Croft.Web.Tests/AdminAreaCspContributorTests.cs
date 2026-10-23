using Croft.Web.Security.Csp;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;

namespace Croft.Web.Tests;

/// <summary>
/// The staff half of the site (/admin and /editor) is the only place inline style attributes are
/// allowed: MudBlazor's grids, dialogs and tabs position their cells, rows and overlays with
/// them. The public pages keep <c>style-src-attr 'none'</c>. The policy is per document, so the
/// contribution is decided by the request path of the document request — a page under /admin or
/// /editor gets <c>'unsafe-inline'</c>, every other path keeps the base policy alone (R2's Add
/// drops the standalone <c>'none'</c> the moment the real source joins).
/// </summary>
public class AdminAreaCspContributorTests
{
    private static string StyleSrcAttr(string path)
    {
        var policy = new CspPolicy().Add("style-src-attr", "'none'");
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        new AdminAreaCspContributor().Contribute(context, policy);
        return policy.ToString();
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/posts")]
    [InlineData("/admin/posts/new")]
    [InlineData("/admin/posts/01234567-89ab-cdef-0123-456789abcdef/edit")]
    [InlineData("/admin/posts/01234567-89ab-cdef-0123-456789abcdef/analytics")]
    [InlineData("/admin/tags")]
    [InlineData("/admin/discussion/blocks")]
    [InlineData("/admin/media")]
    [InlineData("/editor")]
    [InlineData("/editor/posts/some-draft")]
    public void StaffPagePaths_AllowInlineStyleAttributes(string path)
    {
        var policy = StyleSrcAttr(path);

        policy.Should().Contain("style-src-attr 'unsafe-inline'",
            "MudBlazor's grids, dialogs and tabs write style attributes on the staff pages");
        policy.Should().NotContain("style-src-attr 'none'", "CSP 'none' cannot stand beside a real source");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/search")]
    [InlineData("/post/some-post")]
    [InlineData("/category/travel")]
    [InlineData("/tag/hiking")]
    [InlineData("/categories")]
    [InlineData("/branding/theme.css")]
    [InlineData("/administer")]
    [InlineData("/editorial")]
    public void EveryOtherPath_KeepsInlineStyleAttributesBanned(string path)
    {
        var policy = StyleSrcAttr(path);

        policy.Should().Contain("style-src-attr 'none'",
            "the public pages render no inline style attributes, so nothing may loosen the ban");
        policy.Should().NotContain("'unsafe-inline'");
    }
}
