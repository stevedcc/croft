using System.Net;
using AwesomeAssertions;
using Croft.Shared.Contracts;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14b item 2: the feed and the sitemap cost one cached build per <c>FeedDuration</c>, the
/// sitemap is one API read, and both tell browsers and proxies they may keep the document for the
/// same five minutes.
/// </summary>
public class FeedAndSitemapCacheTests
{
    public static TheoryData<string, string> Documents => new()
    {
        { "/feed.xml", "application/atom+xml; charset=utf-8" },
        { "/sitemap.xml", "application/xml; charset=utf-8" },
    };

    [Fact]
    public async Task TheSitemap_WithManyPublishedPosts_IsOneApiRequest()
    {
        using var host = CacheHost.Create(posts: 250);
        using var client = host.Client();

        using var response = await client.GetAsync("/sitemap.xml");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Api.Requests.Select(r => r.PathAndQuery).Should().Equal(["/api/sitemap"],
            "250 posts used to be three pages of the posts list plus the pages, categories and tags lists");
        xml.Split("<url>").Length.Should().Be(1 + 1 + 250 + 1 + 1 + 1,
            "the document still lists the home page, every post, the page, the category and the tag");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task TheSecondRequestWithinFeedDuration_MakesNoApiRequest_AndIsTheSameDocument(string path, string contentType)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        using var first = await client.GetAsync(path);
        var firstBody = await first.Content.ReadAsStringAsync();
        var afterFirst = host.Calls;
        host.Clock.UtcNow += TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59);
        using var second = await client.GetAsync(path);

        afterFirst.Should().BeGreaterThan(0);
        host.Calls.Should().Be(afterFirst, "the document is built once per FeedDuration");
        (await second.Content.ReadAsStringAsync()).Should().Be(firstBody);
        second.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        host.Store.Writes.Should().ContainSingle().Which.Should().Match<(string Key, string[] Tags, TimeSpan ValidFor)>(
            w => w.ValidFor == TimeSpan.FromMinutes(5) && w.Tags.SequenceEqual(new[] { "content" }),
            "five minutes by default, and tagged so a staff write on this pod drops it");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task AfterFeedDuration_TheDocumentIsBuiltAgain(string path, string contentType)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        using var first = await client.GetAsync(path);
        var afterFirst = host.Calls;
        host.Clock.UtcNow += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        using var second = await client.GetAsync(path);

        second.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        host.Calls.Should().BeGreaterThan(afterFirst);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task BothResponses_SendCacheControlPublicMaxAge300(string path, string contentType)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        using var built = await client.GetAsync(path);
        using var cached = await client.GetAsync(path);

        built.Headers.CacheControl.Should().NotBeNull("the document says how long it may be kept");
        built.Headers.CacheControl!.ToString().Should().Be("public, max-age=300");
        cached.Headers.CacheControl.Should().NotBeNull();
        cached.Headers.CacheControl!.ToString().Should().Be("public, max-age=300",
            "the cached copy carries the same header");
        cached.Content.Headers.ContentType!.ToString().Should().Be(contentType);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task AStaffWrite_DropsTheDocument_OnThePodThatHandledIt(string path, string contentType)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();
        using var first = await client.GetAsync(path);
        var afterFirst = host.Calls;

        var staff = StaffApiClient.Create(host.Api, host.Services.GetRequiredService<IOutputCacheStore>());
        var post = host.Api.PostDetails.Single(p => p.Slug == CacheHost.PostSlug);
        await staff.UpdatePostAsync(post.Id, new UpdatePostRequest(post.Title, post.MarkdownContent));
        var afterWrite = host.Calls;
        using var second = await client.GetAsync(path);

        second.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        host.Calls.Should().BeGreaterThan(afterWrite, "the next request builds the document again");
        afterWrite.Should().BeGreaterThan(afterFirst, "(the write itself is one API call)");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task AFeedDurationOfZero_TurnsItOff(string path, string contentType)
    {
        using var host = CacheHost.Create(new() { ["Croft:Web:OutputCache:FeedDuration"] = "00:00:00" });
        using var client = host.Client();

        using var first = await client.GetAsync(path);
        var afterFirst = host.Calls;
        using var second = await client.GetAsync(path);

        second.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        host.Calls.Should().Be(2 * afterFirst);
        host.Store.Writes.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task TheKey_IsThePath_WhateverTheHostOrTheQuery(string path, string contentType)
    {
        using var host = CacheHost.Create();
        using var client = host.Client();

        using var first = await client.GetAsync(path);
        var afterFirst = host.Calls;
        using var loopback = CacheHost.Get(path + "?utm_source=x", host: "127.0.0.1:8080");
        using var second = await client.SendAsync(loopback);

        second.Content.Headers.ContentType!.ToString().Should().Be(contentType);
        host.Calls.Should().Be(afterFirst, "the warm-up asks 127.0.0.1; readers ask the public host, with whatever query a crawler adds");
    }
}
