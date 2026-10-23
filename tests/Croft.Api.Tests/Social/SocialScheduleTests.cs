using AwesomeAssertions;
using Croft.Api.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 3: a discussion is fetched every 10 minutes for its first 2 days, hourly until day 30, daily after that.</summary>
public class SocialScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0:00:00:00", 10)]
    [InlineData("0:01:00:00", 10)]
    [InlineData("0:47:59:59", 10)]
    [InlineData("2:00:00:00", 60)]
    [InlineData("3:00:00:00", 60)]
    [InlineData("29:23:59:59", 60)]
    [InlineData("30:00:00:00", 24 * 60)]
    [InlineData("40:00:00:00", 24 * 60)]
    [InlineData("400:00:00:00", 24 * 60)]
    public void TheIntervalFollowsTheDiscussionsAge(string age, int expectedMinutes)
    {
        var parts = age.Split(':').Select(int.Parse).ToArray();
        var span = new TimeSpan(parts[0], parts[1], parts[2], parts[3]);

        SocialSchedule.Interval(Now - span, Now).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void AnAnchorInTheFuture_IsTreatedAsYoung()
    {
        SocialSchedule.Interval(Now.AddHours(5), Now).Should().Be(TimeSpan.FromMinutes(10));
    }
}
