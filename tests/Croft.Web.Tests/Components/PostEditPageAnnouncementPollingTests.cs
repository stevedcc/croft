using Microsoft.AspNetCore.Components;
using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>Issue #3: while the Discussion tab holds a Pending announcement the editor reads the announcements every 5 s, until none is Pending.</summary>
public class PostEditPageAnnouncementPollingTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PostId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private const string RemoteUrl = "https://social.example/@owner/4711";

    private FakeBlogApiClient _api = null!;
    private ManualTimeProvider _clock = null!;

    private void Setup()
    {
        _clock = new ManualTimeProvider(Now);
        Services.AddSingleton<TimeProvider>(_clock);
        _api = Services.GetRequiredService<FakeBlogApiClient>();
        _api.PostsById[PostId] = new PostDto(PostId, "a-post", "A post", null, "Body.", null, null,
            PostStatus.Published, Now.UtcDateTime.AddDays(-2), Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1);
        _api.DiscussionAdmins[PostId] = new PostDiscussionAdminDto(
            new PostDiscussionDto("https://social.example/@owner/1", null, 0, []), []);
        _api.Announcements[PostId] = Announcements(AnnouncementState.Pending);

        // The editor polls and reads the announcements only in the interactive phase (RendererInfo.IsInteractive).
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private static PostAnnouncementsDto Announcements(AnnouncementState state) => new(true, false, "https://blog.example/post/a-post",
        [new AnnouncementStatusDto(SocialPlatform.Mastodon, state, 1, Now.UtcDateTime, RemoteUrl: state == AnnouncementState.Posted ? RemoteUrl : null)]);

    private async Task<IRenderedComponent<PostEditPage>> OpenDiscussionAsync()
    {
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, PostId));
        cut.FindAll(".mud-tab").Single(t => t.TextContent.Trim() == "Discussion").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => _api.AnnouncementRequests.Should().HaveCount(1));
        return cut;
    }

    [Fact]
    public async Task APendingRow_IsReadAgainEveryFiveSeconds_UntilItIsPosted_ThenPollingStops()
    {
        Setup();
        var cut = await OpenDiscussionAsync();
        _api.Announcements[PostId] = Announcements(AnnouncementState.Posted);

        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);

        await cut.WaitForAssertionAsync(() => _api.AnnouncementRequests.Should().HaveCount(2, "the timer fired once"));
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RemoteUrl, "the second answer has the row posted, with its link"));
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);
        _api.AnnouncementRequests.Should().HaveCount(2, "no row is Pending any more, so nothing is read again");
    }

    [Fact]
    public async Task APendingRow_KeepsPolling_AfterAFailedRead()
    {
        Setup();
        var cut = await OpenDiscussionAsync();
        _api.AnnouncementsFailure = new HttpRequestException("unavailable");

        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);
        await cut.WaitForAssertionAsync(() => _api.AnnouncementRequests.Should().HaveCount(2));
        _api.AnnouncementsFailure = null;
        _api.Announcements[PostId] = Announcements(AnnouncementState.Posted);
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RemoteUrl, "the poll went on after the failure"));
    }

    [Fact]
    public async Task DisposingThePage_StopsThePolling()
    {
        Setup();
        var cut = await OpenDiscussionAsync();

        cut.Instance.Dispose();
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);

        _api.AnnouncementRequests.Should().HaveCount(1, "a disposed page reads nothing");
    }

    [Fact]
    public async Task AnnouncementsThatAreAllPosted_StartNoPolling()
    {
        Setup();
        _api.Announcements[PostId] = Announcements(AnnouncementState.Posted);
        await OpenDiscussionAsync();

        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(5), expectTimers: 0);

        _api.AnnouncementRequests.Should().HaveCount(1);
    }
}
