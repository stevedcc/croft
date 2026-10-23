namespace Croft.Web.E2E;

/// <summary>
/// Task R3.5: missing posts and unknown category/tag slugs must answer with a real HTTP 404 that
/// renders the not-found copy, and unmatched routes likewise (spec §5.5). The assertions are on the
/// raw GET response: status + body text — a redirect to /not-found that answers 200 is a failure.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class NotFoundTests : WebTest
{
    [Test]
    public async Task MissingPost_Returns404WithNotFoundCopy()
    {
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/post/does-not-exist");

        Assert.That(response.Status, Is.EqualTo(404),
            "A missing post must answer with HTTP 404 (NavigationManager.NotFound), not a redirect to /not-found.");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task UnknownRoute_Returns404WithNotFoundCopy()
    {
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/no-such-route");

        Assert.That(response.Status, Is.EqualTo(404),
            "An unmatched route must answer with HTTP 404.");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task UnknownCategorySlug_Returns404WithNotFoundCopy()
    {
        // Unique per run: the shared dev DB accumulates categories from other tests.
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/category/missing-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "An unknown category slug must answer with HTTP 404 (NavigationManager.NotFound).");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task UnknownRoute_Returns404MarkedNoIndex()
    {
        // Unique per run, like the slug tests above: no shared state with the other route test.
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/no-such-route-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "A not-found response must keep its HTTP 404, not a 200 page.");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("<meta name=\"robots\" content=\"noindex\""),
            "The not-found page must tell search engines not to index it.");
    }

    [Test]
    public async Task UnknownTagSlug_Returns404WithNotFoundCopy()
    {
        // Unique per run: the shared dev DB accumulates tags from other tests.
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/tag/missing-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "An unknown tag slug must answer with HTTP 404 (NavigationManager.NotFound).");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }
}
