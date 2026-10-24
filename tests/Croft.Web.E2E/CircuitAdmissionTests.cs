using System.Diagnostics;
using System.Text.Json;
using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 09g item 2, end to end: a burst of readers can no longer cost every reader on the pod.
/// This fixture owns a Web instance of its own — Croft:Web:Circuits:MaxActive=1 with a 15 s
/// disconnected retention (the brief's "an instance with Croft__Web__Circuits__MaxActive=1") —
/// against its pinned stack instance's API, started the way dev/ha-local.sh starts its Web
/// processes (dotnet &lt;dll&gt;, the project directory as content root). The port plan:
/// instance N's limited Web is 5400 + N, outside the instance range (5000-5199) and the HA
/// block (5300-5319); the parameters below mirror this file's own start logic.
///
/// The story: reader A takes the only circuit; reader B arrives and is REFUSED a circuit
/// (503 + Retry-After) yet still reads the full prerendered page with nothing overlaying it;
/// A leaves and B's reload gets a circuit within the retention window. Since brief 09f the
/// reading pages are static SSR and never ask for a circuit, and since brief 13a neither do the
/// not-found and error pages: circuits are for signed-in staff only, so the story runs with two
/// signed-in admins on /admin/posts (reader A and B stand for two staff browsers); a post page
/// opens no circuit at all, even at the limit (asserted below). The instance signs in against the
/// mock IdP with the settings dev/run-e2e.sh passes on from .env.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class CircuitAdmissionTests : WebTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string PostTitle = $"Circuitadmission {RunId}";

    private static bool _dataReady;
    private static string _postSlug = string.Empty;

    private Process? _limitedWeb;
    private StreamWriter? _limitedLog;
    private string _limitedBase = string.Empty;
    private string _limitedLogPath = string.Empty;

    [OneTimeSetUp]
    public async Task StartLimitedWeb()
    {
        var port = 5400 + InstanceNumberFor(BaseUrl);
        _limitedBase = $"http://localhost:{port}";
        var repoRoot = RepoRoot();
        var dll = Path.Combine(repoRoot, "src", "Croft.Web", "bin", "Debug", "net11.0", "Croft.Web.dll");
        Assert.That(File.Exists(dll), Is.True,
            $"{dll} is missing — the suite runs against a built tree (dev/run-e2e.sh builds first).");

        // One log per instance (like the port above): two suite runs on disjoint instance ranges
        // must not truncate and read each other's file.
        var logPath = Path.Combine(Path.GetTempPath(), $"blog-circuits-limit-web-{InstanceNumberFor(BaseUrl)}.log");
        _limitedLogPath = logPath;
        // A fresh log per run: the assertions below read it and must never match an earlier run.
        _limitedLog = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read));

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(repoRoot, "src", "Croft.Web"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"{_limitedBase}");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ApiBaseUrl"] = $"{ApiUrl}/";
        start.Environment["Blog__BaseUrl"] = $"{_limitedBase}/";
        // The sign-in settings the stack's own instances got (dev/run-e2e.sh passes them on).
        foreach (var key in new[] { "Oidc__Authority", "Oidc__ClientId", "Oidc__ClientSecret" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            Assert.That(value, Is.Not.Null.And.Not.Empty,
                $"{key} is not set: run the suite through dev/run-e2e.sh, which passes the sign-in settings on.");
            start.Environment[key] = value;
        }

        start.Environment["Croft__Web__Circuits__MaxActive"] = "1";
        start.Environment["Croft__Web__Circuits__DisconnectedRetentionSeconds"] = "15";

        _limitedWeb = Process.Start(start)!;
        _limitedWeb.OutputDataReceived += (_, e) => WriteLogLine(e.Data);
        _limitedWeb.ErrorDataReceived += (_, e) => WriteLogLine(e.Data);
        _limitedWeb.BeginOutputReadLine();
        _limitedWeb.BeginErrorReadLine();

        Assert.That(await WaitForUrlAsync(_limitedBase, "the MaxActive=1 Web instance"), Is.True,
            $"the limited Web instance did not answer {_limitedBase}; see {logPath}");
    }

    [OneTimeTearDown]
    public void StopLimitedWeb()
    {
        if (_limitedWeb is { } process)
        {
            // HasExited first: a child that already crashed (port in use, config error) must not
            // have its teardown exception replace the setup diagnostic the log assertion built.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }

            process.Dispose();
        }

        _limitedLog?.Dispose();
    }

    [Test]
    public async Task SecondReader_RefusedACircuit_StillReads_ThenGetsACircuitAfterTheFirstCloses()
    {
        await EnsureFixturesAsync();
        // Interactive page for the circuit story: a staff page (the reading pages take no circuit,
        // brief 09f, and neither do the not-found and error pages, brief 13a).
        var postUrl = $"{_limitedBase}/admin/posts";

        // Reader A (a signed-in admin) takes the single circuit slot — proven by A's app going
        // interactive.
        await using var contextA = await Browser.NewContextAsync();
        var pageA = await contextA.NewPageAsync();
        await SignInAtLimitedWebAsync(pageA);
        await pageA.GotoAsync(postUrl);
        await WaitForInteractiveAsync(pageA);

        // Reader B arrives while A holds the slot. The page is prerendered, so the reader sees
        // the content whatever happens to the circuit.
        await using var contextB = await Browser.NewContextAsync();
        var pageB = await contextB.NewPageAsync();
        await SignInAtLimitedWebAsync(pageB);
        var negotiate = pageB.WaitForResponseAsync(
            r => r.Url.Contains("/_blazor/negotiate", StringComparison.Ordinal));
        await pageB.GotoAsync(postUrl);
        var refused = await negotiate;

        Assert.That(refused.Status, Is.EqualTo(503),
            "at MaxActive=1 the second reader's circuit start must be refused with 503");
        Assert.That(await refused.HeaderValueAsync("retry-after"), Is.EqualTo("5"),
            "the refusal tells the client when to try again");

        // The refusal must reach the operator's log, not only the reader's response: a burst of
        // 503s on /_blazor is otherwise inexplicable beside the gauge. Bounded wait on the
        // concrete condition — the line in this instance's own log.
        Assert.That(await WaitForLogLineAsync(_limitedLogPath, "Circuit start refused"), Is.True,
            "the refusal must be logged for the operator (see the middleware's warning)");

        // Outcomes the reader cares about: the prerendered page is there to read...
        await Expect(pageB.Locator("main#content")).ToBeVisibleAsync();
        await Expect(pageB.GetByText("Posts").First).ToBeVisibleAsync();

        // A post page, opened by a third reader while A still holds the only circuit, never asks
        // for one: no negotiate request, no WebSocket, and the whole post reads.
        await using var contextC = await Browser.NewContextAsync();
        var pageC = await contextC.NewPageAsync();
        var asked = new List<string>();
        pageC.Request += (_, request) =>
        {
            if (request.Url.Contains("/_blazor", StringComparison.Ordinal))
            {
                asked.Add(request.Url);
            }
        };
        await pageC.GotoAsync($"{_limitedBase}/post/{_postSlug}");
        await WaitForInteractiveAsync(pageC);
        await Expect(pageC.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = PostTitle })).ToBeVisibleAsync();
        await Expect(pageC.GetByText($"Body {RunId}", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        Assert.That(asked, Is.Empty, "a static post page must not ask the circuit endpoint for anything");

        // ...and nothing covers or blocks it: the element at the viewport's centre is the
        // post's own content (the reconnect overlay, if the framework showed one, would be the
        // topmost element there).
        await pageB.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var centre = await pageB.EvaluateAsync<JsonElement>(
            """
            () => {
                const hit = document.elementFromPoint(window.innerWidth / 2, window.innerHeight / 2);
                return {
                    tag: hit ? hit.tagName : null,
                    id: hit ? (hit.id || null) : null,
                    inMainContent: !!hit && !!hit.closest('main#content'),
                    inOverlay: !!hit && (!!hit.closest('#components-reconnect-modal') || !!hit.closest('dialog')),
                };
            }
            """);
        var hitDescription = $"{centre.GetProperty("tag").GetString()}#{centre.GetProperty("id").GetString()}";
        Assert.That(centre.GetProperty("inMainContent").GetBoolean(), Is.True,
            $"the viewport's centre must hold the post's content, not an overlay (elementFromPoint hit {hitDescription})");
        Assert.That(centre.GetProperty("inOverlay").GetBoolean(), Is.False,
            $"the reconnect modal must not cover or block the content (elementFromPoint hit {hitDescription})");

        // A leaves the site. A real reader's tab close fires pagehide, whose sendBeacon posts
        // /_blazor/disconnect and closes A's circuit at once (measured: Playwright's context
        // close runs no pagehide handlers — A's retained circuit kept refusing B's reloads for
        // the whole 15 s retention — so the departure is modeled by navigation, which does fire
        // pagehide). B's reload then gets a circuit within the retention window (15 s).
        var stopwatch = Stopwatch.StartNew();
        // WORKAROUND(W-069, Playwright): context close runs no pagehide, so departure is modelled as navigation to about:blank
        await pageA.GotoAsync("about:blank");
        // END WORKAROUND(W-069)

        // GotoAsync returns when A's navigation commits, not when the server has closed A's
        // circuit: the pagehide beacon is delivered afterwards, then the server disposes the
        // circuit and the counter drops. A's context must stay open until that has happened:
        // closing it right away cancels a beacon still in flight, and A's circuit then holds the
        // slot until its retention ends (15 s). Measured with 400 ms of lag on
        // /_blazor/disconnect: closing the context at once left B refused for the whole 10 s
        // probe below, which is how this test failed under full-suite load (503 at 123 ms).
        // So wait on the concrete condition — the slot is free: a circuit start is no longer
        // refused. The probe is a negotiate POST from B's page (it opens no circuit and takes
        // no slot). The bound stays under the retention, so a beacon that never arrives still
        // fails the test.
        // WORKAROUND(W-065, Playwright): waitForFunction does not await async predicates, so the poll uses a synchronous XHR
        await pageB.WaitForFunctionAsync(
            """
            () => {
                const request = new XMLHttpRequest();
                request.open('POST', '/_blazor/negotiate?negotiateVersion=1', false);
                request.send();
                return request.status === 200;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 10000, PollingInterval = 100 });
        // END WORKAROUND(W-065)
        await contextA.CloseAsync();

        var reloaded = pageB.WaitForResponseAsync(
            r => r.Url.Contains("/_blazor/negotiate", StringComparison.Ordinal));
        await pageB.ReloadAsync();
        var postReload = await reloaded;
        Assert.That(postReload.Status, Is.EqualTo(200),
            $"B's reload after A left must be admitted to a circuit ({stopwatch.ElapsedMilliseconds} ms after A's departure)");
        await pageB.WaitForFunctionAsync(
            "() => document.documentElement.dataset.interactive === 'on'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15000 });
    }

    /// <summary>Signs a page in at the limited instance through the mock IdP (LoginAsync signs
    /// in at the stack's own instance).</summary>
    private async Task SignInAtLimitedWebAsync(IPage page)
    {
        await page.GotoAsync($"{_limitedBase}/login");
        await page.WaitForURLAsync(url => url.StartsWith($"{OAuthAuthority}/authorize"), new PageWaitForURLOptions { Timeout = 15000 });
        await page.Locator("input[name='username']").FillAsync("admin@example.com");
        await page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();
        await page.Locator("a[href='/logout']").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
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

            using var client = MockIdpApi.CreateClient();
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts", new CreatePostRequest(
                PostTitle,
                $"Body {RunId} is the paragraph the refused reader must still be able to read."), token);
            var postId = post.RootElement.GetProperty("id").GetString()!;
            var published = await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{postId}/publish", "{}", token);
            _postSlug = published.RootElement.GetProperty("slug").GetString()!;
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }

    private void WriteLogLine(string? line)
    {
        if (line is null || _limitedLog is null)
        {
            return;
        }

        lock (_limitedLog)
        {
            _limitedLog.WriteLine(line);
            _limitedLog.Flush();
        }
    }

    /// <summary>Instance N's number from its Web port (dev/instance-params.sh's plan); anything
    /// not on the plan (an explicit E2E_WEB_URL) stands in as instance 1.</summary>
    private static int InstanceNumberFor(string baseUrl)
    {
        var webPort = new Uri(baseUrl).Port;
        return webPort >= 5002 && (webPort - 5002) % 10 == 0 ? 1 + ((webPort - 5002) / 10) : 1;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null,
            $"Could not find Croft.slnx above {AppContext.BaseDirectory}; run these tests through dev/run-e2e.sh.");
        return dir!.FullName;
    }

    /// <summary>Polls the concrete condition — a line in the instance's own log — with a bound.</summary>
    private static async Task<bool> WaitForLogLineAsync(string logPath, string needle)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (File.Exists(logPath) && File.ReadAllText(logPath).Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    /// <summary>Polls the concrete condition — the instance answers — with a bound.</summary>
    private static async Task<bool> WaitForUrlAsync(string url, string label)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }

            TestContext.Progress.WriteLine($"{label}: not answering {url} yet (attempt {attempt + 1})");
            await Task.Delay(500);
        }

        return false;
    }
}
