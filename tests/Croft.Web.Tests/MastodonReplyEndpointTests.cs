using System.Net;
using AwesomeAssertions;
using Croft.Shared.Contracts.Social;
using Croft.Web.Tests.OutputCache;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 12b item 3: <c>GET /discussion/mastodon/reply?post=&lt;slug&gt;&amp;server=&lt;host&gt;</c>. A valid server gets a page with one
/// link to that server's <c>authorize_interaction</c> (never a redirect); an invalid one gets the form again with a 400;
/// an unknown post, or one without a Mastodon link, is a 404. Names are invented.
/// </summary>
public class MastodonReplyEndpointTests
{
    private const string PostUrl = "https://social.example/@owner/115000000000000001?x=1&y=a b";
    private const string Slug = CacheHost.PostSlug;

    private static CacheHost HostWithDiscussion(string? mastodonUrl = PostUrl)
    {
        var host = CacheHost.Create(staffHeader: false);
        host.Api.Discussions[Slug] = new PostDiscussionDto(mastodonUrl, "https://bsky.example/profile/owner/post/3k", 0, []);
        return host;
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(CacheHost host, string server, string post = Slug)
    {
        using var client = host.Client();
        var response = await client.GetAsync($"/discussion/mastodon/reply?post={Uri.EscapeDataString(post)}&server={Uri.EscapeDataString(server)}");
        return (response, await response.Content.ReadAsStringAsync());
    }

    private static string ContinueLink(string body)
    {
        var anchors = System.Text.RegularExpressions.Regex.Matches(body, "<a [^>]*href=\"([^\"]*authorize_interaction[^\"]*)\"");
        anchors.Should().ContainSingle("the page has exactly one link to the server's authorize_interaction");
        return System.Net.WebUtility.HtmlDecode(anchors[0].Groups[1].Value);
    }

    [Theory]
    [InlineData("mastodon.example")]
    [InlineData("https://mastodon.example/")]
    [InlineData("http://mastodon.example")]
    [InlineData("@reader@mastodon.example")]
    [InlineData("  Mastodon.Example ")]
    public async Task AValidServer_GetsAPageWithOneLink_ToAuthorizeInteraction(string typed)
    {
        using var host = HostWithDiscussion();

        var (response, body) = await GetAsync(host, typed);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ContinueLink(body).Should().Be($"https://mastodon.example/authorize_interaction?uri={Uri.EscapeDataString(PostUrl)}");
        body.Should().Contain("Continue to mastodon.example").And.Contain("sign in");
    }

    [Theory]
    [InlineData("mastodon")]
    [InlineData("-bad.example")]
    [InlineData("a..b")]
    [InlineData("host:8080")]
    [InlineData("host/path")]
    [InlineData("user:pass@host")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task AnInvalidServer_Is400_WithTheFormAgain_AndNoLink(string typed)
    {
        using var host = HostWithDiscussion();

        var (response, body) = await GetAsync(host, typed);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain("authorize_interaction");
        body.Should().Contain("That doesn't look like a server name", "the page says what is wrong")
            .And.Contain("name=\"server\"").And.Contain($"name=\"post\"").And.Contain(Slug);
    }

    [Fact]
    public async Task ATooLongServer_Is400()
    {
        using var host = HostWithDiscussion();
        var name = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 62));

        var (response, body) = await GetAsync(host, name);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain("authorize_interaction");
    }

    [Fact]
    public async Task TheTypedValue_IsPutBackInTheForm_HtmlEncoded()
    {
        using var host = HostWithDiscussion();

        var (response, body) = await GetAsync(host, "\"><script>alert(1)</script>");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain("<script>alert(1)</script>");
        body.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Fact]
    public async Task AnUnknownPost_Is404()
    {
        using var host = HostWithDiscussion();

        var (response, body) = await GetAsync(host, "mastodon.example", post: "no-such-post");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("authorize_interaction");
    }

    [Fact]
    public async Task APostWithoutAMastodonLink_Is404()
    {
        using var host = HostWithDiscussion(mastodonUrl: null);

        var (response, body) = await GetAsync(host, "mastodon.example");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("authorize_interaction");
    }

    [Fact]
    public async Task AMissingPostParameter_Is404()
    {
        using var host = HostWithDiscussion();
        using var client = host.Client();

        using var response = await client.GetAsync("/discussion/mastodon/reply?server=mastodon.example");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("mastodon.example")]
    [InlineData("mastodon")]
    public async Task NoResponse_HasALocation_OrSetsACookie_AndEveryOneCarriesTheRobotsMeta(string typed)
    {
        using var host = HostWithDiscussion();

        var (response, body) = await GetAsync(host, typed);

        response.Headers.Location.Should().BeNull("a page with a link, never a redirect: the endpoint must not be an open redirect");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("no cookie");
        body.Should().MatchRegex("<meta name=\"robots\" content=\"noindex\"\\s*/?>");
    }

    [Fact]
    public async Task TheEndpoint_IsNotOutputCached()
    {
        using var host = HostWithDiscussion();

        await GetAsync(host, "mastodon.example");
        await GetAsync(host, "mastodon.example");

        host.Api.Requests.Count(r => r.PathAndQuery.EndsWith("/discussion", StringComparison.Ordinal))
            .Should().Be(2, "not a reading route: every request asks the API");
        host.Store.Writes.Should().BeEmpty();
    }
}
