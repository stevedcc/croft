using System.Net;
using AwesomeAssertions;
using Bunit;
using Croft.Shared.Contracts;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The post analytics page (spec §6.3): the post title, the total, and the last 30 days as one series
/// the page zero-fills (the API returns only the days with views), plus its two error lines. The chart
/// test (ViewsChartTests) asserts only that a chart is drawn; the zero-fill and the error branches had
/// no test (review 2026-10-19, M-24).
/// </summary>
public class PostAnalyticsPageTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TheSeries_IsThirtyDaysEndingToday_ZeroFilledWhereTheApiHadNoViews()
    {
        var id = Guid.CreateVersion7();
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            Analytics = new AnalyticsSummaryDto(id, "Analytics Post", 8,
            [
                new DailyViewCount(new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), 5),
                new DailyViewCount(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), 3),
            ]),
        });

        var cut = Render<PostAnalyticsPage>(p => p.Add(x => x.Id, id));

        cut.Find(".page-header__title").TextContent.Should().Contain("Analytics Post");
        cut.Find(".analytics__total").TextContent.Should().Be("Total views: 8");
        var daily = cut.FindComponent<ViewsChart>().Instance.Daily!;
        daily.Should().HaveCount(30, "the window is the last 30 days");
        daily[0].Date.Should().Be(new DateTime(2026, 9, 21), "29 days before today");
        daily[^1].Date.Should().Be(new DateTime(2026, 10, 20), "today is the last day");
        daily.Select(d => d.Date).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        daily.Single(d => d.Date == new DateTime(2026, 10, 18)).Count.Should().Be(5);
        daily.Single(d => d.Date == new DateTime(2026, 10, 20)).Count.Should().Be(3);
        daily.Where(d => d.Date != new DateTime(2026, 10, 18) && d.Date != new DateTime(2026, 10, 20))
            .Should().OnlyContain(d => d.Count == 0, "a day the API did not return is a day with no views");
    }

    [Fact]
    public async Task APostTheApiDoesNotKnow_ShowsPostNotFound()
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            AnalyticsFailure = new HttpRequestException("not found", null, HttpStatusCode.NotFound),
        });

        var cut = Render<PostAnalyticsPage>(p => p.Add(x => x.Id, Guid.CreateVersion7()));

        await cut.WaitForAssertionAsync(() => cut.Find(".analytics__error").TextContent.Should().Be("Post not found."));
        cut.FindAll(".analytics__total").Should().BeEmpty("no total is shown for a post that was not found");
    }

    [Fact]
    public async Task AnApiFailure_ShowsTheErrorLine_AndLogsAWarningWithTheStatus()
    {
        var logger = new CapturingLogger<PostAnalyticsPage>();
        Services.AddSingleton<ILogger<PostAnalyticsPage>>(logger);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient
        {
            AnalyticsFailure = new HttpRequestException("analytics are down", null, HttpStatusCode.InternalServerError),
        });

        var cut = Render<PostAnalyticsPage>(p => p.Add(x => x.Id, Guid.CreateVersion7()));

        await cut.WaitForAssertionAsync(() => cut.Find(".analytics__error").TextContent.Should().Be("Could not load the analytics."));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("500") && e.Text.Contains("loading the analytics"));
    }
}
