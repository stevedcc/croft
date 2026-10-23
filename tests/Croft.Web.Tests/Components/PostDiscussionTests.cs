using System.Net;
using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 12b item 4: the post page loads the discussion alongside the related posts and shows the section after
/// the body and before the related posts. The discussion never breaks the post: a failed read means no section,
/// one Warning, and the post still renders.
/// </summary>
public class PostDiscussionTests : BlogBunitContext
{
    private const string Slug = "a-post";
    private static readonly DateTimeOffset At = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeBlogApiClient _fake;
    private readonly CapturingLogger<Post> _logger = new();

    public PostDiscussionTests()
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(At.AddHours(2)));
        Services.AddSingleton<ILogger<Post>>(_logger);
        _fake = Services.GetRequiredService<FakeBlogApiClient>();
        var post = new PostDto(
            Guid.NewGuid(), Slug, "A Post About Gardens", null, "Some body text here.", null, null, PostStatus.Published,
            At.UtcDateTime, At.UtcDateTime, At.UtcDateTime, "author-id", "Test Author", null, [], null, 5);
        _fake.PostsById[post.Id] = post;
        _fake.Related.Add(new PostSummaryDto(
            Guid.NewGuid(), "related-one", "Related One", null, null, "Summary.", 5, PostStatus.Published,
            At.UtcDateTime, At.UtcDateTime, "An Author", null, [], null, null, null));
    }

    private static PostDiscussionDto DiscussionWith(string text) => new(
        "https://social.example/@owner/1", "https://bsky.example/profile/owner/post/3k", 1,
        [new DiscussionReplyDto(SocialPlatform.Mastodon, "r1", "nora@social.example", "Nora Example", "https://social.example/@nora",
            "acct-1", null, At, "https://social.example/@nora/r1", $"<p>{text}</p>", null, false, [])]);

    private IRenderedComponent<Post> RenderPost() => Render<Post>(p => p.Add(x => x.Slug, Slug));

    [Fact]
    public void ADiscussionWithReplies_ShowsTheSection_AfterTheBody_AndBeforeTheRelatedPosts()
    {
        _fake.Discussions[Slug] = DiscussionWith("Lovely garden, Nora here");

        var cut = RenderPost();

        cut.Find("section#discussion").TextContent.Should().Contain("Lovely garden, Nora here");
        _fake.DiscussionRequests.Should().ContainSingle().Which.Should().Be(Slug);
        var markup = cut.Markup;
        var body = markup.IndexOf("Some body text here.", StringComparison.Ordinal);
        var discussion = markup.IndexOf("id=\"discussion\"", StringComparison.Ordinal);
        var related = markup.IndexOf("class=\"related-posts", StringComparison.Ordinal);
        body.Should().BePositive();
        discussion.Should().BeGreaterThan(body, "directly after the post body");
        related.Should().BeGreaterThan(discussion, "before the related posts");
    }

    [Fact]
    public void ThePostWithoutADiscussion_HasNoSection_AndNoWarning()
    {
        var cut = RenderPost();

        cut.FindAll("#discussion").Should().BeEmpty();
        cut.Markup.Should().Contain("A Post About Gardens");
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void ADiscussionWithoutLinks_HasNoSection()
    {
        _fake.Discussions[Slug] = new PostDiscussionDto(null, null, 0, []);

        RenderPost().FindAll("#discussion").Should().BeEmpty();
    }

    public static IEnumerable<object[]> Failures() =>
    [
        [new ApiUnavailableException("The API is unavailable.", new HttpRequestException("refused"))],
        [new HttpRequestException("boom", null, HttpStatusCode.InternalServerError)],
        [new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable)],
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public void AFailingDiscussionRead_RendersThePostWithoutTheSection_AndLogsOneWarning(Exception failure)
    {
        _fake.Discussions[Slug] = DiscussionWith("never shown");
        _fake.DiscussionFailure = failure;

        var cut = RenderPost();

        cut.Markup.Should().Contain("A Post About Gardens").And.Contain("Some body text here.").And.Contain("Related One");
        cut.FindAll("#discussion").Should().BeEmpty();
        var warning = _logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Text.Should().Contain(Slug).And.Contain(failure.GetType().Name);
    }

    [Fact]
    public void AnUnexpectedFailure_IsNotSwallowed()
    {
        _fake.DiscussionFailure = new InvalidOperationException("a bug, not an outage");

        var render = () => RenderPost();

        render.Should().Throw<InvalidOperationException>("only an unreachable or failing API is tolerated, not a bug");
    }

    [Fact]
    public async Task TheDiscussionAndTheRelatedReads_AreBothInFlight_BeforeEitherCompletes()
    {
        var gate = new TaskCompletionSource();
        _fake.DiscussionGate = gate.Task;
        _fake.RelatedGate = gate.Task;
        _fake.Discussions[Slug] = DiscussionWith("held");

        var cut = RenderPost();

        // Nothing is released: if the page awaited the discussion before it asked for the related posts,
        // the related read would never start.
        await cut.WaitForAssertionAsync(() =>
        {
            _fake.DiscussionRequests.Should().HaveCount(1, "the discussion read has started");
            _fake.RelatedPostsRequests.Should().HaveCount(1, "the related read has started while the discussion is still pending");
        });
        cut.FindAll("#discussion").Should().BeEmpty("nothing has completed yet");

        gate.SetResult();
        await cut.WaitForAssertionAsync(() => cut.Find("section#discussion"));
    }
}
