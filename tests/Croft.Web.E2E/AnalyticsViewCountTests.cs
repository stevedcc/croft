using System.Threading.Channels;

namespace Croft.Web.E2E;

[Parallelizable(ParallelScope.Fixtures)]
public class AnalyticsViewCountTests : WebTest
{
    [Test]
    public async Task AnonymousVisit_IncrementsViewCountByOne()
    {
        var (slug, postId) = await CreatePublishedPostAsync();

        await using (var anonContext = await Browser.NewContextAsync())
        {
            var anonPage = await anonContext.NewPageAsync();
            await VisitPostCountingBeaconsAsync(anonPage, $"{BaseUrl}/post/{slug}", visits: 1);
        }

        await AssertTotalViewsAsync(postId, expected: 1);
    }

    [Test]
    public async Task ReloadingPostPage_DoesNotIncrementViewCountAgain()
    {
        var (slug, postId) = await CreatePublishedPostAsync();

        await using (var anonContext = await Browser.NewContextAsync())
        {
            var anonPage = await anonContext.NewPageAsync();
            await VisitPostCountingBeaconsAsync(anonPage, $"{BaseUrl}/post/{slug}", visits: 2);
        }

        await AssertTotalViewsAsync(postId, expected: 1);
    }

    [Test]
    public async Task AdminVisit_DoesNotIncrementViewCount()
    {
        var (slug, postId) = await CreatePublishedPostAsync();

        // Still logged in as admin from creating the post: the visit must not be counted.
        await VisitPostCountingBeaconsAsync(Page, $"{BaseUrl}/post/{slug}", visits: 1);

        await AssertTotalViewsAsync(postId, expected: 0);
    }

    private async Task<(string Slug, Guid PostId)> CreatePublishedPostAsync()
    {
        await LoginAsync("admin@example.com", "password");

        // Route/selector migration (rule 5-sanctioned): /admin → /admin/posts/new and
        // "Publish / Schedule" → "Publish now"; the slug comes from the public API by id.
        await OpenNewPostEditorAsync();

        var title = $"Analytics E2E {Guid.NewGuid():N}";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Analytics counting body.");
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });

        var postId = PostIdFromEditorUrl();
        var slug = await SlugForPostAsync(postId);
        return (slug, postId);
    }

    /// <summary>Opens the post and reloads it until the page has sent <paramref name="visits"/> view
    /// beacons, asserting that each was answered 204. The beacon is observed by routing it: Playwright
    /// performs the request itself and hands the page the response, so what the test awaits is the
    /// route handler's own result, not a <c>Response</c> event for a keepalive request.</summary>
    private static async Task VisitPostCountingBeaconsAsync(IPage page, string url, int visits)
    {
        var answers = Channel.CreateUnbounded<int>();
        var beaconsSent = 0;
        await page.RouteAsync("**/api/analytics/pageview", async route =>
        {
            Interlocked.Increment(ref beaconsSent);
            var response = await route.FetchAsync();
            answers.Writer.TryWrite(response.Status);
            await route.FulfillAsync(new RouteFulfillOptions { Response = response });
        });

        try
        {
            for (var visit = 0; visit < visits; visit++)
            {
                if (visit == 0)
                    await page.GotoAsync(url);
                else
                    await page.ReloadAsync();

                using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                int status;
                try
                {
                    status = await answers.Reader.ReadAsync(giveUp.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        $"Visit {visit + 1} of {visits}: no view beacon was answered; the page sent {Volatile.Read(ref beaconsSent)} beacon(s) in total"
                        + (Volatile.Read(ref beaconsSent) > visit ? " (this visit's beacon was sent, the Web app did not answer it)." : " (this visit sent none)."));
                }

                Assert.That(status, Is.EqualTo(204), "View beacon was not accepted by the Web app.");
            }
        }
        finally
        {
            await page.UnrouteAsync("**/api/analytics/pageview");
        }
    }

    private async Task AssertTotalViewsAsync(Guid postId, int expected)
    {
        // Route migration (rule 5-sanctioned, disclosed in the commit message): the old
        // /admin/analytics page is gone; the same step now drives the post's analytics page.
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/analytics");

        await Expect(Page.GetByText($"Total views: {expected}", new PageGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync();
    }
}
