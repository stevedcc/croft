using System.Text.Json;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10c item 3: scripts are pinned by content and place. Every <c>&lt;script src&gt;</c> a
/// reading page renders carries an <c>integrity</c> attribute, and a script the page never listed
/// is refused even from the page's own origin: a same-origin file outside the allowed
/// directories (the MudBlazor script, which only interactive documents may load, and an uploaded
/// file served from <c>/api/media/…</c>) injected into a reading page does not run, raises a
/// <c>script-src-elem</c> violation and makes the browser post a report to <c>/csp-report</c>.
/// Category=Csp, any engine (<c>BROWSER=…</c>), API fixtures only.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Csp")]
public class CspScriptTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static string _categorySlug = string.Empty;
    private static string _tagSlug = string.Empty;
    private static string _pageSlug = string.Empty;
    private static Guid _mediaId;

    [Test]
    public async Task EveryReadingPage_RendersEachScriptWithAnIntegrityDigest()
    {
        await EnsureFixturesAsync();
        string[] paths =
        [
            "/", $"/post/{_postSlug}", $"/category/{_categorySlug}", $"/tag/{_tagSlug}", "/categories",
            $"/{_pageSlug}", $"/search?q={RunId}", $"/post/missing-{RunId}"
        ];
        foreach (var path in paths)
        {
            await Page.GotoAsync($"{BaseUrl}{path}");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            // The page's own module ran: its digest and its directory were both accepted.
            var scripts = await Page.EvaluateAsync<string[][]>(
                "() => [...document.querySelectorAll('script[src]')].map(s => [s.getAttribute('src'), s.getAttribute('integrity') ?? ''])");
            Assert.That(scripts, Is.Not.Empty, $"{path} loads scripts");
            foreach (var script in scripts)
            {
                Assert.That(script[1], Does.Match("^sha256-[A-Za-z0-9+/]{43}=$"),
                    $"{path}: <script src=\"{script[0]}\"> must carry its SHA-256 integrity digest");
            }

            var preloads = await Page.EvaluateAsync<string[][]>(
                "() => [...document.querySelectorAll('link[rel=modulepreload]')].map(l => [l.getAttribute('href'), l.getAttribute('integrity') ?? ''])");
            Assert.That(preloads.Where(p => p[1].Length == 0), Is.Empty, $"{path}: every module preload carries an integrity digest");
            await WaitForInteractiveAsync(Page);
        }
    }

    [TestCase("/_content/MudBlazor/MudBlazor.min.js", "a real same-origin script that only interactive documents may load")]
    [TestCase("MEDIA", "an uploaded file served from /api/media")]
    public async Task ASameOriginScriptOutsideTheAllowedSet_IsRefused_AndReported(string target, string what)
    {
        await EnsureFixturesAsync();
        var url = target == "MEDIA" ? $"/api/media/{_mediaId}" : target;
        var probe = await CspViolationProbe.AttachAsync(Page);
        await probe.StepAsync("reading page, before the injection");
        var document = await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForInteractiveAsync(Page);
        await probe.DrainAsync();
        Assert.That(probe.Violations, Is.Empty, "the page itself is clean before the injection");

        await probe.StepAsync($"injected {what}");
        var reportedBefore = ReportedCspViolationCount(url);
        var queuedReports = new List<(string Destination, string Type, JsonElement Body, string InitiatorUrl)>();
        if (Browser.BrowserType.Name == "chromium")
        {
            // Chromium sends reports for a document with a report-to group through the Reporting
            // API, which batches them about a minute; DevTools shows each report the moment the
            // browser queues it, with the group it is destined for.
            var session = await Context.NewCDPSessionAsync(Page);
            session.Event("Network.reportingApiReportAdded").OnEvent += (_, e) =>
            {
                var report = e!.Value.GetProperty("report");
                lock (queuedReports)
                {
                    queuedReports.Add((
                        report.GetProperty("destination").GetString()!,
                        report.GetProperty("type").GetString()!,
                        report.GetProperty("body").Clone(),
                        report.GetProperty("initiatorUrl").GetString()!));
                }
            };
            await session.SendAsync("Network.enable");
            await session.SendAsync("Network.enableReportingApi", new Dictionary<string, object> { ["enable"] = true });
        }
        await Page.EvaluateAsync(
            "(src) => { const script = document.createElement('script'); script.src = src; document.head.appendChild(script); }", url);
        await Page.WaitForFunctionAsync("() => (window.__cspProbeEvents ?? []).length > 0");
        var violations = await probe.DrainAsync();

        Assert.That(violations, Has.Some.Matches<CspViolation>(v =>
                v.Directive == "script-src-elem" && v.BlockedUri.Contains(url, StringComparison.Ordinal)),
            $"the injected {what} must raise a script-src-elem violation; got: {string.Join("; ", violations)}");
        Assert.That(await Page.EvaluateAsync<bool>("() => typeof window.MudBlazor !== 'undefined'"), Is.False,
            "the refused script did not run");
        if (Browser.BrowserType.Name == "chromium")
        {
            // The refusal itself (script-src-elem for this URL), not the report-only Trusted Types report that
            // the same injected script also raises (W-091): that one names the URL in its sample only.
            bool IsTheRefusal((string Destination, string Type, JsonElement Body, string InitiatorUrl) r) =>
                r.Destination == "csp" && r.Type == "csp-violation"
                && r.Body.TryGetProperty("effectiveDirective", out var directive) && directive.GetString() == "script-src-elem"
                && r.Body.TryGetProperty("blockedURL", out var blocked) && (blocked.GetString() ?? string.Empty).Contains(url, StringComparison.Ordinal);
            Assert.That(() => { lock (queuedReports) { return queuedReports.Any(IsTheRefusal); } },
                Is.True.After(15000, 250), "Chromium queued the script-src-elem csp-violation report for the 'csp' group (/csp-report)");

            // That is only the browser's side: it queues the report and posts it up to a minute later.
            // The server's side is proven here by sending what the browser queued, as the browser
            // would (the Reporting API's wire format), to the endpoint the page's own headers name
            // for that group — so a route, content type or auth change on /csp-report, or a group
            // renamed in one header only, fails this test instead of silently losing every report.
            var headers = await document!.AllHeadersAsync();
            var endpoints = headers.GetValueOrDefault("reporting-endpoints") ?? string.Empty;
            var endpoint = System.Text.RegularExpressions.Regex.Match(endpoints, "(?:^|,)\\s*csp=\"(?<url>[^\"]+)\"");
            Assert.That(endpoint.Success, Is.True, $"the page's Reporting-Endpoints header names the 'csp' group; it says '{endpoints}'");
            var policy = headers.GetValueOrDefault("content-security-policy") ?? headers.GetValueOrDefault("content-security-policy-report-only") ?? string.Empty;
            Assert.That(policy, Does.Contain("report-to csp"), "the policy sends its reports to the 'csp' group");

            (string Destination, string Type, JsonElement Body, string InitiatorUrl) queued;
            lock (queuedReports)
            {
                queued = queuedReports.First(IsTheRefusal);
            }

            var wire = JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = queued.Type, ["age"] = 0, ["url"] = queued.InitiatorUrl,
                    ["user_agent"] = await Page.EvaluateAsync<string>("() => navigator.userAgent"), ["body"] = queued.Body,
                },
            });
            await using var response = await Page.APIRequest.PostAsync(
                new Uri(new Uri(BaseUrl), endpoint.Groups["url"].Value).ToString(),
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string> { ["Content-Type"] = "application/reports+json" },
                    DataString = wire,
                });
            Assert.That(response.Status, Is.EqualTo(204), $"/csp-report must accept the browser's own report; it answered {response.Status}");
            // The instance's log file is written asynchronously (the process's redirected output): wait for the line.
            Assert.That(() => ReportedCspViolationCount(url), Is.GreaterThan(reportedBefore).After(15000, 250),
                "the endpoint logged the violation it was sent");
            return;
        }

        // Firefox and WebKit post the report to /csp-report straight away, and the endpoint logs
        // one line per violation: wait for that line.
        Assert.That(() => ReportedCspViolationCount(url), Is.GreaterThan(reportedBefore).After(15000, 250),
            "the browser reported the violation to /csp-report and the server logged it");
    }

    private async Task EnsureFixturesAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            var category = await CreateCategoryAsync($"Csp Script Cat {RunId}");
            _categorySlug = category.Slug;
            _tagSlug = $"cspscript{RunId}";
            _mediaId = await UploadBannerAsync($"csp-script-{RunId}.png");
            var post = await PublishAsync($"Csp Script Post {RunId}", BodyWithSections(RunId, 3), [_tagSlug], category.Id, bannerImageId: _mediaId);
            _postSlug = post.Slug;
            var page = await PublishAsync($"Csp Script About {RunId}", $"Pagemarker {RunId} waits.", kind: PostKind.Page);
            _pageSlug = page.Slug;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
