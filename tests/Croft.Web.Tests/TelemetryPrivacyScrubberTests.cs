using System.Diagnostics;
using AwesomeAssertions;
using Croft.Shared.Telemetry;

namespace Croft.Web.Tests;

/// <summary>Brief 13i item 4 (M-14): what the activity scrubber redacts, pinned while its hot path was reworked.</summary>
public sealed class TelemetryPrivacyScrubberTests
{
    private static readonly ActivitySource Source = new("Croft.Web.Tests.TelemetryPrivacy");

    [Fact]
    public void ATagWhoseValueCarriesAQueryString_IsStripped_WhateverItsKey()
    {
        using var activity = Source.StartActivity("work")!;
        activity.SetTag("note", "see /page?a=1");
        activity.SetTag("plain", "no query here? just prose");
        activity.SetTag("count", 3);

        new ActivityPrivacyScrubber().OnEnd(activity);

        activity.GetTagItem("note").Should().Be("see /page");
        activity.GetTagItem("plain").Should().Be("no query here? just prose", "prose with a question mark is not a query string");
        activity.GetTagItem("count").Should().Be(3);
    }

    [Fact]
    public void AQueryBearingKey_LosesItsQueryAndFragment_AndAForbiddenKeyIsDropped()
    {
        using var activity = Source.StartActivity("work")!;
        activity.SetTag("url.full", "https://example.test/search?q=secret#frag");
        activity.SetTag("http.request.header.cookie", "blog_sid=abc");
        activity.SetTag("user.email", "reader@example.com");

        new ActivityPrivacyScrubber().OnEnd(activity);

        activity.GetTagItem("url.full").Should().Be("https://example.test/search");
        activity.GetTagItem("http.request.header.cookie").Should().BeNull();
        activity.GetTagItem("user.email").Should().BeNull();
    }

    [Theory]
    [InlineData("a?x=1", true)]
    [InlineData("?q=term", true)]
    [InlineData("what? really", false)]
    [InlineData("no query", false)]
    public void CarriesQueryString_MatchesAKeyEqualsRunAfterAQuestionMark(string value, bool expected)
    {
        TelemetryPrivacy.CarriesQueryString(value).Should().Be(expected);
    }

    static TelemetryPrivacyScrubberTests()
    {
        // One listener for the process: an activity is only created while somebody listens to its source.
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        });
    }
}
