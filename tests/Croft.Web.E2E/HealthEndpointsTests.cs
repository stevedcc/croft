namespace Croft.Web.E2E;

/// <summary>
/// The k8s probes' endpoints on the running stack: an anonymous GET answers 200 with a small
/// plain answer that is never cached and never indexed — the contract kubelet and any monitor
/// read. The unit suite covers the failure branches (unreachable database, failing API); this
/// stack is healthy, so the healthy answers are what E2E can observe here.
/// </summary>
public class HealthEndpointsTests : WebTest
{
    [Test]
    public async Task HealthLive_Answers200_NoStore_NoIndex()
    {
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/health/live");

        Assert.That(response.Status, Is.EqualTo(200),
            "Liveness must answer 200 for a running Web instance (the process is the signal).");
        AssertProbeResponse(response, "/health/live");
    }

    [Test]
    public async Task HealthReady_Answers200_NoStore_NoIndex()
    {
        await using var response = await Page.APIRequest.GetAsync($"{BaseUrl}/health/ready");

        Assert.That(response.Status, Is.EqualTo(200),
            "A healthy stack (reachable database, answering API) must be ready for traffic.");
        AssertProbeResponse(response, "/health/ready");
    }

    private static void AssertProbeResponse(IAPIResponse response, string path)
    {
        var headers = response.Headers;
        Assert.Multiple(() =>
        {
            Assert.That(Header(headers, "cache-control"), Is.EqualTo("no-store"),
                $"{path} is a point-in-time signal and must never be served from a cache.");
            Assert.That(Header(headers, "x-robots-tag"), Is.EqualTo("noindex"),
                $"{path} is an operational surface and must never be indexed.");
            Assert.That(Header(headers, "content-type"), Does.StartWith("text/plain"),
                $"{path} answers a small plain body, nothing more.");
        });
    }

    private static string Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : "<missing>";
}
