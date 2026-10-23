using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 14a item 4: an instance the suite runs against is warm. Both apps answer /health/ready
/// with 200 (dev/start-local.sh waits for exactly that before it returns), and each app's log holds
/// the warm-up's one Information line saying every request it sent succeeded, with no Warning
/// beside it.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public partial class WarmupTests : WebTest
{
    [Test]
    public async Task TheInstanceIsReady_AndBothAppsLoggedASuccessfulWarmup()
    {
        foreach (var (name, url) in new[] { ("Web", BaseUrl), ("API", ApiUrl) })
        {
            await using var ready = await Page.APIRequest.GetAsync($"{url}/health/ready");
            Assert.That(ready.Status, Is.EqualTo(200), $"{name} /health/ready: a started instance is ready");
        }

        foreach (var (name, log) in new[] { ("Web", WebLogText()), ("API", ApiLogText()) })
        {
            var finished = FinishedLine().Match(log);
            Assert.That(finished.Success, Is.True, $"the {name} log holds the warm-up's finish line");
            var succeeded = int.Parse(finished.Groups["succeeded"].Value);
            var total = int.Parse(finished.Groups["total"].Value);
            Assert.That(total, Is.GreaterThan(0), $"the {name} warm-up sent requests");
            Assert.That(succeeded, Is.EqualTo(total), $"every {name} warm-up request succeeded ({finished.Value})");
            Assert.That(log, Does.Not.Contain("Warm-up incomplete"), $"the {name} warm-up logged no Warning");
        }
    }

    [GeneratedRegex(@"Warm-up finished in \d+ ms: (?<succeeded>\d+) of (?<total>\d+) requests succeeded")]
    private static partial Regex FinishedLine();
}
