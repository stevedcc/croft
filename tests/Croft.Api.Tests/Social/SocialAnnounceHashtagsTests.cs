using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12e item 3: the sender puts the category's and tags' hashtags into the default text, and leaves a custom text alone.</summary>
public class SocialAnnounceHashtagsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Url = "https://blog.example/post/hashtagged";

    private static async Task GiveThePostHashtagsAsync(AnnounceHarness h, Guid postId) =>
        await h.ChangePostAsync(postId, post =>
        {
            post.Category = new Category
            {
                Name = "Homelab category", Slug = "homelab-category",
                Hashtags = [new("Homelab", HashtagNetworks.Both), new("SelfHosted", HashtagNetworks.BlueskyOnly)],
            };
            post.Tags =
            [
                new Tag { Name = "Kubernetes", Slug = "kubernetes", Hashtags = [new("homelab", HashtagNetworks.Both), new("Kubernetes", HashtagNetworks.MastodonOnly)] },
                new Tag { Name = "dotnet", Slug = "dotnet", Hashtags = [new("DotNet", HashtagNetworks.Both)] },
            ];
        });

    /// <summary>A real Bluesky announcer on a stub PDS that keeps the createRecord body.</summary>
    private static (BlueskyAnnouncer Announcer, List<JsonDocument> Records) BlueskyOnStubPds(AnnounceHarness h)
    {
        List<JsonDocument> records = [];
        var pds = new StubHandler(request =>
        {
            var method = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (method == "com.atproto.server.createSession")
                return Json($$"""{"accessJwt":"test-jwt-9d2e.eyJ0ZXN0IjoxfQ.sig","did":"{{SocialTestSettings.Did}}"}""");

            records.Add(JsonDocument.Parse(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult()));
            return Json(SocialFixtures.Read("bluesky-create-record.json"));
        });
        return (new BlueskyAnnouncer(new StubClientFactory(pds), Options.Create(h.Settings), h.Time), records);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static List<(string Type, int Start, int End, string? Tag)> Facets(JsonDocument record) =>
    [
        .. record.RootElement.GetProperty("record").GetProperty("facets").EnumerateArray().Select(facet =>
        {
            var feature = facet.GetProperty("features").EnumerateArray().Single();
            var index = facet.GetProperty("index");
            return (feature.GetProperty("$type").GetString()!, index.GetProperty("byteStart").GetInt32(), index.GetProperty("byteEnd").GetInt32(),
                feature.TryGetProperty("tag", out var tag) ? tag.GetString() : null);
        })
    ];

    [Fact]
    public async Task TheDefaultText_GetsTheHashtags_PerNetwork_AndBlueskyMakesThemTagFacets()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync("Title", "Subtitle", slug: "hashtagged");
        await GiveThePostHashtagsAsync(h, postId);
        var (bluesky, records) = BlueskyOnStubPds(h);

        await h.NewReplica(null, h.Mastodon, bluesky).TickAsync();

        h.Mastodon.Sends.Should().ContainSingle().Which.Text.Should().Be($"Title\n\nSubtitle\n\n#Homelab #DotNet #Kubernetes\n\n{Url}",
            "category first, then the tags by name (dotnet before Kubernetes), Bluesky-only left out, the homelab duplicate dropped");

        var record = records.Should().ContainSingle().Subject;
        record.RootElement.GetProperty("record").GetProperty("text").GetString().Should().Be($"Title\n\nSubtitle\n\n#Homelab #SelfHosted #DotNet\n\n{Url}");
        // "Title\n\nSubtitle\n\n" is 17 bytes; then #Homelab 8, a space, #SelfHosted 11, a space, #DotNet 7.
        Facets(record).Where(f => f.Type.EndsWith("#tag", StringComparison.Ordinal)).Should().Equal(
            ("app.bsky.richtext.facet#tag", 17, 25, "Homelab"),
            ("app.bsky.richtext.facet#tag", 26, 37, "SelfHosted"),
            ("app.bsky.richtext.facet#tag", 38, 45, "DotNet"));
    }

    [Fact]
    public async Task ACustomText_IsSentAsTyped_WithNoHashtagsAdded_AndNoExtraTagFacets()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync("Title", "Subtitle", slug: "hashtagged", announcementText: "My words #homelab");
        await GiveThePostHashtagsAsync(h, postId);
        var (bluesky, records) = BlueskyOnStubPds(h);

        await h.NewReplica(null, h.Mastodon, bluesky).TickAsync();

        h.Mastodon.Sends.Should().ContainSingle().Which.Text.Should().Be($"My words #homelab\n\n{Url}");
        var facets = Facets(records.Should().ContainSingle().Subject);
        facets.Where(f => f.Type.EndsWith("#tag", StringComparison.Ordinal)).Should().ContainSingle().Which.Tag.Should().Be("homelab",
            "only the hashtag the owner typed");
    }

    [Fact]
    public async Task ThePostRead_IsOneStatement_WhateverTheNumberOfTags()
    {
        await using var h = await AnnounceHarness.CreateAsync(fixture);
        var postId = await h.SeedPostAsync("Title", "Subtitle", slug: "hashtagged");
        await GiveThePostHashtagsAsync(h, postId);
        var statements = new HashtagStatementRecorder();
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(h.Database.ConnectionString).AddInterceptors(statements).Options);

        var result = await h.NewReplica().TickAsync(db);

        result.Claimed.Should().Be(2);
        statements.Selecting.Should().HaveCount(2, "one statement per claimed row (the post read), not one more per tag or category");
        statements.Selecting.Should().OnlyContain(sql => sql.Contains("\"Categories\"") && sql.Contains("\"Tags\""), "category and tags come with the post in the same statement");
    }

    /// <summary>The SQL commands that read the hashtag columns.</summary>
    private sealed class HashtagStatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _selecting = [];

        public IReadOnlyList<string> Selecting
        {
            get { lock (_selecting) return [.. _selecting]; }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Hashtags\"", StringComparison.Ordinal))
            {
                lock (_selecting)
                    _selecting.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
