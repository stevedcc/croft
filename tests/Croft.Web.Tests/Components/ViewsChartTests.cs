using Croft.Shared.Contracts;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// ViewsChart (spec §6.3/§7.4): the daily-views line chart, and the empty state when the series
/// carries no views (Review Focus 5).
/// </summary>
public class ViewsChartTests : BlogBunitContext
{
    private static DailyViewCount Day(int day, long count) =>
        new(new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc), count);

    [Fact]
    public void ViewsChart_NoViews_ShowsTheEmptyStateInsteadOfAChart()
    {
        var cut = Render<ViewsChart>(p => p.Add(x => x.Daily, (IReadOnlyList<DailyViewCount>)[Day(1, 0), Day(2, 0)]));

        cut.Markup.Should().Contain("No views yet.");
        cut.Markup.Should().NotContain("mud-chart", "a chart of nothing tells the user less than the empty state");
    }

    [Fact]
    public void ViewsChart_WithViews_RendersTheLineChart()
    {
        var cut = Render<ViewsChart>(p => p.Add(x => x.Daily, (IReadOnlyList<DailyViewCount>)[Day(1, 3), Day(2, 5), Day(3, 0)]));

        cut.Markup.Should().NotContain("No views yet.");
        cut.Find(".mud-chart").Should().NotBeNull();
        cut.Markup.Should().Contain("mud-chart-line", "the daily series must be drawn as a line");
    }

    [Fact]
    public void ViewsChart_DayLabels_AreDayFirst()
    {
        var cut = Render<ViewsChart>(p => p.Add(x => x.Daily, (IReadOnlyList<DailyViewCount>)[Day(8, 3), Day(9, 5)]));

        var labels = cut.FindAll(".mud-charts-xaxis text").Select(t => t.TextContent);

        labels.Should().Equal(["8 Oct", "9 Oct"],
            "the chart's day labels are day-first ('8 Oct'), never month-day order");
    }

    [Fact]
    public void ViewsChart_WithViews_HasAHiddenTable_WithOneRowPerDay_AndTheSameNumbers()
    {
        // Brief 13g item 6 (review M-11): the chart is a picture; the same numbers are in a table
        // that is hidden visually but not from assistive technology.
        var cut = Render<ViewsChart>(p => p.Add(x => x.Daily, (IReadOnlyList<DailyViewCount>)[Day(1, 3), Day(2, 5), Day(3, 0)]));

        var rows = cut.FindAll("table.visually-hidden tbody tr")
            .Select(r => r.QuerySelectorAll("td, th").Select(c => c.TextContent.Trim()).ToArray()).ToList();
        rows.Should().HaveCount(3);
        rows[0].Should().Equal("1 Oct", "3");
        rows[1].Should().Equal("2 Oct", "5");
        rows[2].Should().Equal("3 Oct", "0");
        cut.Find("table.visually-hidden caption").TextContent.Should().Contain("Views per day");
    }

    [Fact]
    public void ViewsChart_NoViews_HasNoTable()
    {
        var cut = Render<ViewsChart>(p => p.Add(x => x.Daily, (IReadOnlyList<DailyViewCount>)[Day(1, 0)]));

        cut.FindAll("table").Should().BeEmpty();
    }
}
