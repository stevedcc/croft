using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace Croft.Api.Tests;

public class AnalyticsControllerTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task RecordPageView_IncrementsPostViewCount()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Analytics Test", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        var anonymous = factory.CreateClient().AsAnonymous();
        await anonymous.PostAsJsonAsync("/api/analytics/pageview", new PageViewRequest(post.Id));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var views = await db.PostViewCounts.AsNoTracking().SingleAsync(v => v.PostId == post.Id);
        views.ViewCount.Should().Be(1);
    }

    [Fact]
    public async Task Summary_GroupsByDay()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Summary Test", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        await admin.PostAsJsonAsync("/api/analytics/pageview", new PageViewRequest(post.Id));

        var summaryResponse = await admin.GetAsync($"/api/analytics?postId={post.Id}");
        summaryResponse.EnsureSuccessStatusCode();
        var summary = await summaryResponse.Content.ReadFromJsonAsync<AnalyticsSummaryDto>();

        summary!.TotalViews.Should().Be(1);
        summary.DailyViews.Should().ContainSingle(d => d.Count == 1);
    }

    [Fact]
    public async Task Summary_AcceptsLocalDateTime()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Local Date Test", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest());

        await admin.PostAsJsonAsync("/api/analytics/pageview", new PageViewRequest(post.Id));

        var localFrom = Uri.EscapeDataString(DateTime.Now.AddDays(-1).ToString("O"));
        var summaryResponse = await admin.GetAsync($"/api/analytics?postId={post.Id}&from={localFrom}");
        summaryResponse.EnsureSuccessStatusCode();
        var summary = await summaryResponse.Content.ReadFromJsonAsync<AnalyticsSummaryDto>();
        summary!.TotalViews.Should().Be(1,
            "the view just recorded is inside the range a local-time `from` describes (success alone would also be an empty summary)");
    }

    [Fact]
    public async Task RecordPageView_XVisitorIp_IsHonouredWithValidKey()
    {
        await using var factory = CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["Analytics:IngestKey"] = "test-ingest-key"
        });
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Honoured Key Test");

        var anonymous = factory.CreateClient().AsAnonymous();
        await PostPageViewAsync(anonymous, post.Id, visitorIp: "203.0.113.5", ingestKey: "test-ingest-key");
        await PostPageViewAsync(anonymous, post.Id, visitorIp: "203.0.113.6", ingestKey: "test-ingest-key");

        (await GetViewCountAsync(factory, post.Id)).Should().Be(2);
    }

    [Fact]
    public async Task RecordPageView_XVisitorIp_IsIgnoredWithoutValidKey()
    {
        await using var factory = CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["Analytics:IngestKey"] = "test-ingest-key"
        });
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Ignored Key Test");

        var anonymous = factory.CreateClient().AsAnonymous();
        await PostPageViewAsync(anonymous, post.Id, visitorIp: "203.0.113.5", ingestKey: null);
        await PostPageViewAsync(anonymous, post.Id, visitorIp: "203.0.113.6", ingestKey: "wrong-key");

        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RecordPageView_XVisitorIp_IsIgnored_WhenNoIngestKeyIsConfigured()
    {
        // An unconfigured key never matches (AnalyticsIngestKey.Matches): the header is ignored whatever
        // key the caller sends, so two "different" visitors are both the socket peer: one view.
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "No Configured Key Test");

        var anonymous = factory.CreateClient().AsAnonymous();
        await PostPageViewAsync(anonymous, post.Id, visitorIp: "203.0.113.5", ingestKey: "any-key");
        await PostPageViewAsync(anonymous, post.Id, visitorIp: "203.0.113.6", ingestKey: "");

        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RecordPageView_SameIpSameDay_CountsOnce_NextDayTwice()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Same Day Test", publishedAt: new DateTime(2026, 10, 1));

        var anonymous = factory.CreateClient().AsAnonymous();
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(1);

        clock.Advance(TimeSpan.FromDays(1));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        (await GetViewCountAsync(factory, post.Id)).Should().Be(2);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(2);
    }

    // A day is a UTC calendar day and the IP hash is salted with it, so the stored rows of a reader can't be
    // linked across days. A view in the first 30 minutes of a day is a duplicate when the same reader viewed
    // the same post the day before (issue #81).
    private static DateTimeOffset Utc(int day, int hour, int minute, int second = 0) =>
        new(2026, 10, day, hour, minute, second, TimeSpan.Zero);

    // The fake clock only advances; moving to an absolute time keeps the times in the tests readable.
    private static void MoveTo(FakeTimeProvider clock, DateTimeOffset target) =>
        clock.Advance(target - clock.GetUtcNow());

    [Fact]
    public async Task AcrossMidnight_AReloadCountsOnce()
    {
        var clock = new FakeTimeProvider(Utc(5, 23, 59, 50));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Midnight Reload Test", publishedAt: new DateTime(2026, 10, 1));
        var anonymous = factory.CreateClient().AsAnonymous();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        clock.Advance(TimeSpan.FromSeconds(20)); // 2026-10-06 00:00:10
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);

        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(1);
    }

    [Fact]
    public async Task JustInsideTheGrace_IsADuplicate_AtItsEdgeItCounts()
    {
        var clock = new FakeTimeProvider(Utc(5, 23, 50, 0));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Grace Edge Test", publishedAt: new DateTime(2026, 10, 1));
        var anonymous = factory.CreateClient().AsAnonymous();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-05 23:50:00
        MoveTo(clock, Utc(6, 0, 29, 59));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-06 00:29:59, inside
        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);

        MoveTo(clock, Utc(6, 0, 30, 0));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-06 00:30:00, the edge counts
        (await GetViewCountAsync(factory, post.Id)).Should().Be(2);
    }

    [Fact]
    public async Task AnotherReaderAfterMidnight_Counts()
    {
        var clock = new FakeTimeProvider(Utc(5, 23, 59, 50));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Other Reader Test", publishedAt: new DateTime(2026, 10, 1));
        var anonymous = factory.CreateClient().AsAnonymous();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-05 23:59:50
        MoveTo(clock, Utc(6, 0, 0, 10));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.6", TestIngestKey); // 10-06 00:00:10, another reader

        (await GetViewCountAsync(factory, post.Id)).Should().Be(2);
    }

    [Fact]
    public async Task YesterdaysReader_LaterToday_Counts()
    {
        var clock = new FakeTimeProvider(Utc(5, 10, 0));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Later Today Test", publishedAt: new DateTime(2026, 10, 1));
        var anonymous = factory.CreateClient().AsAnonymous();

        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-05 10:00
        MoveTo(clock, Utc(6, 0, 10));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-06 00:10, a duplicate
        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);

        MoveTo(clock, Utc(6, 14, 0));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey); // 10-06 14:00, counts
        (await GetViewCountAsync(factory, post.Id)).Should().Be(2);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(2);
    }

    [Fact]
    public async Task RecordPageView_DraftPost_IsNotCounted()
    {
        await using var factory = CreateFactoryAsync(IngestConfig);
        var admin = factory.CreateClient().WithRole("Admin");
        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Draft Test", "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();

        var anonymous = factory.CreateClient().AsAnonymous();
        await PostPageViewAsync(anonymous, post!.Id, "203.0.113.5", TestIngestKey);

        (await GetViewCountAsync(factory, post.Id)).Should().Be(0);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(0);
    }

    [Fact]
    public async Task RecordPageView_Concurrent20SameIp_RecordsExactlyOneView()
    {
        // Twenty at once from one address is the point of this test (the dedupe under a race), so
        // the per-client concurrency ceiling (default 16, brief 13a) is raised above it.
        await using var factory = CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["Analytics:IngestKey"] = TestIngestKey,
            ["Croft:Api:MaxConcurrentRequestsPerClient"] = "64"
        });
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Concurrency Test");

        var anonymous = factory.CreateClient().AsAnonymous();
        var requests = Enumerable.Range(0, 20)
            .Select(_ => PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey));
        await Task.WhenAll(requests);

        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RecordPageView_ALongReferrer_IsStoredAsExactlyItsFirst2048Characters_AndStillCounts()
    {
        await using var factory = CreateFactoryAsync(IngestConfig);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Long Referrer");
        var referrer = "https://example.org/" + new string('r', 10 * 1024);

        using var response = await SendPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.7", TestIngestKey,
            referrer: referrer);

        response.EnsureSuccessStatusCode();
        var row = await GetSinglePageViewAsync(factory, post.Id);
        row.Referrer.Should().Be(referrer[..2048], "a long legitimate referrer is truncated, not rejected: the view still counts");
        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RecordPageView_ALongUserAgent_IsStoredAsExactlyItsFirst512Characters()
    {
        await using var factory = CreateFactoryAsync(IngestConfig);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Long User Agent");
        var userAgent = "Mozilla/5.0 " + new string('u', 2 * 1024);

        using var response = await SendPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.8", TestIngestKey,
            userAgent: userAgent);

        response.EnsureSuccessStatusCode();
        var row = await GetSinglePageViewAsync(factory, post.Id);
        row.UserAgent.Should().Be(userAgent[..512]);
    }

    [Fact]
    public async Task RecordPageView_AReferrerAndUserAgentWithinTheLimits_AreStoredUnchanged()
    {
        await using var factory = CreateFactoryAsync(IngestConfig);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Short Referrer");
        var referrer = "https://example.org/" + new string('r', 2048 - "https://example.org/".Length);

        using var response = await SendPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.9", TestIngestKey,
            referrer: referrer, userAgent: "Mozilla/5.0");

        response.EnsureSuccessStatusCode();
        var row = await GetSinglePageViewAsync(factory, post.Id);
        row.Referrer.Should().Be(referrer, "a referrer of exactly 2048 characters is within the limit");
        row.UserAgent.Should().Be("Mozilla/5.0");
    }

    [Fact]
    public async Task RecordPageView_TruncationNeverSplitsASurrogatePair()
    {
        await using var factory = CreateFactoryAsync(IngestConfig);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Surrogates");
        // Each emoji is two UTF-16 code units; 2047 'a' puts the first emoji across the 2048 cut.
        var referrer = new string('a', 2047) + "\U0001F600\U0001F600";

        using var response = await SendPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.10", TestIngestKey,
            referrer: referrer);

        response.EnsureSuccessStatusCode();
        var row = await GetSinglePageViewAsync(factory, post.Id);
        row.Referrer.Should().Be(new string('a', 2047), "half a surrogate pair is dropped rather than stored");
    }

    [Fact]
    public async Task RecordPageView_SameVisitorSameDayTwice_CountsOnceAndLogsNothingAboveInformation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var logs = new LogCollector();
        await using var factory = CreateFactoryAsync(IngestConfig, s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddLogging(builder => builder.AddProvider(logs));
        });
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Quiet Duplicate Test", publishedAt: new DateTime(2026, 10, 1));

        var anonymous = factory.CreateClient().AsAnonymous();
        logs.Clear();
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);

        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(1);
        logs.Entries.Should().NotContain(e => e.Level > LogLevel.Information,
            "a repeat view is an expected no-op and must not be reported as a problem");
        logs.Entries.Should().NotContain(e => e.Level == LogLevel.Error || e.Level == LogLevel.Critical,
            "a duplicate view must never surface as an error or failure");
    }

    [Fact]
    public async Task RecordPageView_Concurrent10SameVisitor_AllSucceedAndCountOnce()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Concurrent Ten Test", publishedAt: new DateTime(2026, 10, 1));

        var anonymous = factory.CreateClient().AsAnonymous();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => SendPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey)));

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode,
            "a repeat view is an expected no-op and every request must succeed");
        (await GetViewCountAsync(factory, post.Id)).Should().Be(1);
        (await GetPageViewRowCountAsync(factory, post.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RecordPageView_DifferentHashKeys_ProduceDifferentHashesForSameIpAndDay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        await using var factoryA = CreateFactoryAsync(HashConfig("hash-key-one"), s => s.AddSingleton<TimeProvider>(clock));
        await using var factoryB = CreateFactoryAsync(HashConfig("hash-key-two"), s => s.AddSingleton<TimeProvider>(clock));

        var postA = await CreatePublishedPostAsync(factoryA.CreateClient().WithRole("Admin"), "Hash Key A", publishedAt: new DateTime(2026, 10, 1));
        var postB = await CreatePublishedPostAsync(factoryB.CreateClient().WithRole("Admin"), "Hash Key B", publishedAt: new DateTime(2026, 10, 1));

        await PostPageViewAsync(factoryA.CreateClient(), postA.Id, "203.0.113.5", TestIngestKey);
        await PostPageViewAsync(factoryB.CreateClient(), postB.Id, "203.0.113.5", TestIngestKey);

        var hashA = await GetIpHashAsync(factoryA, postA.Id);
        var hashB = await GetIpHashAsync(factoryB, postB.Id);
        hashA.Should().NotBe(hashB);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.5, 198.51.100.1")]
    [InlineData("203.0.113.5:8080")]
    public async Task RecordPageView_VisitorIpThatIsNotAnAddress_FallsBackToTheSocketPeer(string visitorIp)
    {
        var (factory, post) = await CreateFactoryWithPeerAsync("192.0.2.9");
        await using var _ = factory;

        await PostPageViewAsync(factory.CreateClient(), post.Id, visitorIp, TestIngestKey);

        (await GetIpHashAsync(factory, post.Id)).Should().Be(HashOf("192.0.2.9"),
            "an X-Visitor-IP that does not parse as an address is ignored, never stored or hashed as text");
    }

    [Fact]
    public async Task RecordPageView_ValidVisitorIpWithTheKey_IsUsed()
    {
        var (factory, post) = await CreateFactoryWithPeerAsync("192.0.2.9");
        await using var _ = factory;

        await PostPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.5", TestIngestKey);

        (await GetIpHashAsync(factory, post.Id)).Should().Be(HashOf("203.0.113.5"));
    }

    [Fact]
    public async Task RecordPageView_XForwardedFor_NeverChangesTheVisitor()
    {
        // The API handles no forwarded headers: its only visitor override is X-Visitor-IP with the key.
        var (factory, post) = await CreateFactoryWithPeerAsync("192.0.2.9");
        await using var _ = factory;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
        {
            Content = JsonContent.Create(new PageViewRequest(post.Id))
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        (await factory.CreateClient().SendAsync(request)).EnsureSuccessStatusCode();

        (await GetIpHashAsync(factory, post.Id)).Should().Be(HashOf("192.0.2.9"));
    }

    private static string HashOf(string ip)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("hash-key-one"));
        return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"2026-10-05|{ip}")));
    }

    private async Task<(BlogApiFactory Factory, PostDto Post)> CreateFactoryWithPeerAsync(string peer)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var factory = CreateFactoryAsync(HashConfig("hash-key-one"), s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(System.Net.IPAddress.Parse(peer)));
        });
        var post = await CreatePublishedPostAsync(factory.CreateClient().WithRole("Admin"), "Visitor Address", publishedAt: new DateTime(2026, 10, 1));
        return (factory, post);
    }

    [Fact]
    public async Task RecordPageView_HashesIpAsDateSaltedHmac()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactoryAsync(HashConfig("hash-key-one"), s => s.AddSingleton<TimeProvider>(clock));
        var post = await CreatePublishedPostAsync(factory.CreateClient().WithRole("Admin"), "Hash Formula Test", publishedAt: new DateTime(2026, 10, 1));

        await PostPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.5", TestIngestKey);

        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("hash-key-one"));
        var expected = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("2026-10-05|203.0.113.5")));
        (await GetIpHashAsync(factory, post.Id)).Should().Be(expected);
    }

    [Fact]
    public async Task Summary_GroupsByDay_InSql()
    {
        var recorder = new SqlCommandRecorder();
        await using var factory = CreateFactoryAsync(IngestConfig, commandInterceptor: recorder);
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Sql Grouping Test");

        await PostPageViewAsync(factory.CreateClient(), post.Id, "203.0.113.5", TestIngestKey);

        recorder.Commands.Clear();
        var summaryResponse = await admin.GetAsync($"/api/analytics?postId={post.Id}");
        summaryResponse.EnsureSuccessStatusCode();

        recorder.Commands.Should().Contain(command =>
            command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            && command.Contains("count(", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Summary_BucketsViewsByUtcDay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        await using var factory = CreateFactoryAsync(IngestConfig, s => s.AddSingleton<TimeProvider>(clock));
        var admin = factory.CreateClient().WithRole("Admin");
        var post = await CreatePublishedPostAsync(admin, "Day Buckets Test", publishedAt: new DateTime(2026, 10, 1));

        var anonymous = factory.CreateClient().AsAnonymous();
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.6", TestIngestKey);

        clock.Advance(TimeSpan.FromDays(1));
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.5", TestIngestKey);
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.6", TestIngestKey);
        await PostPageViewAsync(anonymous, post.Id, "203.0.113.7", TestIngestKey);

        var summaryResponse = await admin.GetAsync(
            $"/api/analytics?postId={post.Id}&from={Uri.EscapeDataString(new DateTime(2026, 10, 1).ToString("O"))}&to={Uri.EscapeDataString(new DateTime(2026, 10, 10).ToString("O"))}");
        summaryResponse.EnsureSuccessStatusCode();
        var summary = await summaryResponse.Content.ReadFromJsonAsync<AnalyticsSummaryDto>();

        summary!.TotalViews.Should().Be(5);
        summary.DailyViews.Should().BeEquivalentTo(
        [
            new DailyViewCount(new DateTime(2026, 10, 5), 2),
            new DailyViewCount(new DateTime(2026, 10, 6), 3)
        ], options => options.WithStrictOrdering());
    }

    private sealed class SqlCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private const string TestIngestKey = "test-ingest-key";

    private static readonly IReadOnlyDictionary<string, string?> IngestConfig = new Dictionary<string, string?>
    {
        ["Analytics:IngestKey"] = TestIngestKey
    };

    private static IReadOnlyDictionary<string, string?> HashConfig(string hashKey) => new Dictionary<string, string?>
    {
        ["Analytics:IngestKey"] = TestIngestKey,
        ["Analytics:HashKey"] = hashKey
    };

    // Tests with a fake clock publish at a fixed date before that clock, so the
    // "PublishedAt <= now" counting boundary does not sit exactly on the pin.
    private static async Task<PostDto> CreatePublishedPostAsync(HttpClient admin, string title, DateTime? publishedAt = null)
    {
        var postResponse = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "content"));
        postResponse.EnsureSuccessStatusCode();
        var post = await postResponse.Content.ReadFromJsonAsync<PostDto>();
        await admin.PostAsJsonAsync($"/api/posts/{post!.Id}/publish", new PublishRequest(publishedAt));
        return post;
    }

    private static async Task PostPageViewAsync(HttpClient client, Guid postId, string? visitorIp, string? ingestKey)
    {
        var response = await SendPageViewAsync(client, postId, visitorIp, ingestKey);
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> SendPageViewAsync(
        HttpClient client, Guid postId, string? visitorIp, string? ingestKey,
        string? referrer = null, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
        {
            Content = JsonContent.Create(new PageViewRequest(postId, referrer))
        };
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        if (visitorIp is not null)
        {
            request.Headers.Add("X-Visitor-IP", visitorIp);
        }
        if (ingestKey is not null)
        {
            request.Headers.Add("X-Analytics-Key", ingestKey);
        }

        return client.SendAsync(request);
    }

    private static async Task<long> GetViewCountAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return await db.PostViewCounts.AsNoTracking().Where(v => v.PostId == postId).Select(v => v.ViewCount).SingleOrDefaultAsync();
    }

    private static async Task<long> GetPageViewRowCountAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return await db.PageViews.AsNoTracking().LongCountAsync(p => p.PostId == postId);
    }

    private static async Task<Croft.Api.Models.PageView> GetSinglePageViewAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        return await db.PageViews.AsNoTracking().SingleAsync(p => p.PostId == postId);
    }

    private static async Task<string> GetIpHashAsync(BlogApiFactory factory, Guid postId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var row = await db.PageViews.AsNoTracking().SingleAsync(p => p.PostId == postId);
        return row.IpHash;
    }

    [Fact]
    public async Task Startup_WithoutHashKey_OutsideDevelopment_Fails()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        await using var factory = new BlogApiFactory(fixture.ConnectionString, storagePath)
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var act = () => factory.CreateClient();

        var exception = act.Should().Throw<Exception>().Which;
        var messages = new List<string>();
        for (var error = exception; error is not null; error = error.InnerException)
        {
            messages.Add(error.Message);
        }

        string.Join("\n", messages).Should().Contain("Analytics:HashKey");
    }

    [Fact]
    public async Task Startup_WithoutIngestKey_OutsideDevelopment_Fails()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        await using var factory = new BlogApiFactory(
                fixture.ConnectionString,
                storagePath,
                new Dictionary<string, string?> { ["Analytics:HashKey"] = "hash-key" })
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var act = () => factory.CreateClient();

        var exception = act.Should().Throw<Exception>().Which;
        var messages = new List<string>();
        for (var error = exception; error is not null; error = error.InnerException)
        {
            messages.Add(error.Message);
        }

        string.Join("\n", messages).Should().Contain("Analytics:IngestKey");
    }

    private BlogApiFactory CreateFactoryAsync(
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null,
        DbCommandInterceptor? commandInterceptor = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath, configuration, configureServices, commandInterceptor);
    }
}
