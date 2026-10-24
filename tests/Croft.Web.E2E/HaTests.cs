using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Croft.Shared.Enums;
using Microsoft.Playwright.NUnit;

namespace Croft.Web.E2E;

/// <summary>
/// The HA tests for the local HA stack (dev/ha-local.sh): two API and two Web instances
/// against one database, behind the local Traefik container. Ports, paths and the sticky
/// cookie name mirror dev/ha-local.sh's header comment (the single source of truth); keep
/// both sides in sync. dev/run-ha-e2e.sh starts and stops the stack around this category.
///
/// The order is fixed with [Order]: the tests that need the whole stack run first, then the
/// tests that kill instances. Every killing test restores what it killed (dev/ha-local.sh
/// start api|web N — the harness stand-in for k8s rescheduling a pod) so the next one finds
/// two Web and two API instances again; the readers test runs LAST and restores nothing.
/// Every test creates the data it needs (the stack starts from a freshly migrated,
/// seeded-empty database).
/// </summary>
[Category("HaTests")]
[NonParallelizable] // the tests kill stack instances; they own the whole stack serially
public class HaTests : PageTest
{
    // The stack parameters, mirroring dev/ha-local.sh (single source of truth).
    private const string TraefikWebUrl = "http://localhost:5300"; // "web" entrypoint: readers
    private const string TraefikApiUrl = "http://localhost:5301"; // "api" entrypoint: Web -> API
    private const string Api1Url = "http://localhost:5302"; // direct ports, bypassing Traefik
    private const string Web1Url = "http://localhost:5303";
    private const string Api2Url = "http://localhost:5312";
    private const string Web2Url = "http://localhost:5313";
    private const string StickyCookieName = "blog_ha_web";
    private const string Api1Log = "/tmp/blog-ha-api1.log";
    private const string Api2Log = "/tmp/blog-ha-api2.log";

    // dev/ha-oidc-proxy.cs, started by dev/ha-local.sh: the Web instances' OIDC authority whose
    // discovery document points the token endpoint at the proxy, so this log is the request
    // counter at the token endpoint (ruling 3) — one JSON line per token request.
    private const string TokenLog = "/tmp/blog-ha-oidc-tokens.jsonl";

    // The two Web servers exactly as dev/ha-local.sh writes them into Traefik's config:
    // Traefik's sticky cookie is the SHA-256 of the server URL truncated to 16 hex chars
    // (Traefik's pkg/server/service/loadbalancer/sticky.go), so a reader's cookie says
    // exactly which Web instance it is pinned to.
    private const string Web1Server = "http://host.containers.internal:5303";
    private const string Web2Server = "http://host.containers.internal:5313";

    // This stack's own mock IdP (dev/ha-local.sh's blog-ha-mock container on :5315): the HA
    // stack never touches the shared dev mock on :5556, and this mock's Web-client tokens
    // expire after 70 s (dev/ha-mock-oauth2-config.json) — the refresh scenario's premise.
    private const string HaMockAuthority = "http://localhost:5315/default";
    // Never listened on: only the redirect target of the mock's authorization-code flow.
    private const string CallbackUrl = "http://localhost:9876/callback";

    private sealed record Failure(string Url, string Detail);

    [Test, Order(1)]
    public async Task ScheduledPublishing_AcrossApiInstances_PublishesEachPostExactlyOnce()
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false });
        var token = await GetAdminAccessTokenAsync(http);

        // 10 posts due 30 s from now, created and scheduled through the API. Both API
        // instances run the publisher; the advisory lock and the idempotent update must
        // publish each post exactly once.
        var dueAt = DateTime.UtcNow.AddSeconds(30);
        List<(Guid Id, string Slug)> posts = [];
        for (var i = 0; i < 10; i++)
        {
            var title = $"HA scheduled publishing {i} {Guid.NewGuid():N}";
            var (id, slug) = await CreatePostAsync(http, token, TraefikApiUrl, title);
            var status = await PublishAsync(http, token, TraefikApiUrl, id, dueAt);
            Assert.That(status, Is.EqualTo(nameof(PostStatus.Scheduled)),
                $"'{title}' was not accepted as scheduled for {dueAt:O}.");
            posts.Add((id, slug));
        }

        // Wait for the concrete outcome — every post published — with a bounded poll. The
        // publisher ticks once a minute per API instance, so up to ~4 minutes is expected.
        await PollUntilAsync(
            TimeSpan.FromMinutes(4),
            () => AllPublishedAsync(http, token, posts),
            "all 10 scheduled posts published");

        foreach (var (id, slug) in posts)
        {
            var status = await PostStatusAsync(http, token, TraefikApiUrl, slug);
            Assert.That(status, Is.EqualTo(nameof(PostStatus.Published)),
                $"Post {id} ({slug}) is '{status}', not published.");

            // "Published scheduled post {PostId} ({Slug})" — exactly once across the two API
            // logs together: the instance that published it logs it, the other must not. The
            // line reaches the log file asynchronously after the HTTP response, so first poll
            // (bounded) for the expected line to appear — no blind sleep — and only then count.
            await PollUntilAsync(
                TimeSpan.FromSeconds(30),
                () => Task.FromResult(PublishedLogLines(id) >= 1),
                $"the expected 'Published scheduled post {id}' line to appear across {Api1Log} and {Api2Log}");
            Assert.That(PublishedLogLines(id), Is.EqualTo(1),
                $"Post {id} ({slug}): expected exactly 1 'Published scheduled post' line across " +
                $"{Api1Log} and {Api2Log}, found {PublishedLogLines(id)}.");
        }
    }

    [Test, Order(2)]
    public async Task Uploads_VisibleFromBothApiInstances_SameBytes()
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false });
        var token = await GetAdminAccessTokenAsync(http);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "test-image.png"));

        // Upload through API 1, fetch through API 2 — both by direct port, bypassing Traefik.
        // The media folder is shared (the RWX stand-in), so the other instance serves the
        // very same bytes. With separate folders per instance this test fails.
        var id = await UploadImageAsync(http, token, Api1Url, bytes);

        using var response = await http.GetAsync($"{Api2Url}/api/media/{id}");
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            $"API 2 did not serve the image API 1 accepted (HTTP {(int)response.StatusCode}).");
        Assert.That(body, Is.EqualTo(bytes),
            "API 2 served different bytes than were uploaded through API 1.");
    }

    [Test, Order(3)]
    public async Task RefreshAcrossInstances_SessionSurvives_ExactlyOneRefreshPerExpiry()
    {
        // A signed-in session whose access tokens live just over the app's 60 s refresh window,
        // requested from BOTH Web instances at once for three token lifetimes (210 s). Each tick
        // fires one request per instance simultaneously — the moment the session's token is
        // stale, both instances want to refresh it — so the shared store's row lock is what
        // keeps the token endpoint at exactly one refresh per expiry (ruling 3), while the
        // alternation by port proves each instance serves the session the other created and
        // refreshed. The short lifetime is the HA mock IdP's own (dev/ha-mock-oauth2-config.json:
        // tokenExpiry 70 for the Web client) — the brief's design — so the JWT's own exp is
        // short and the API's token validation is part of the test.
        var jar = new CookieJar();

        var windowStart = DateTimeOffset.UtcNow;
        await SignInScenarioUserAsync(jar, Web1Url);
        var windowEnd = DateTimeOffset.UtcNow.AddSeconds(210);

        // Premise (the brief's "short access-token lifetime in the mock OAuth2 server"): the
        // login exchange must mint an access token that lives just over the app's 60 s refresh
        // window. Without it there is no refresh to observe and the scenario is meaningless.
        var login = ReadTokenGrants(windowStart, DateTimeOffset.UtcNow)
            .Single(g => g.Grant == "authorization_code");
        Assert.That(login.ExpiresIn, Is.LessThanOrEqualTo(80).And.GreaterThan(60),
            $"the HA mock IdP minted a {login.ExpiresIn}s access token; the refresh scenario needs one just " +
            "over the 60 s refresh window (tokenExpiry: 70 in dev/ha-mock-oauth2-config.json)");

        var urls = new[] { $"{Web1Url}/admin/posts", $"{Web2Url}/admin/posts" };
        List<Failure> failures = [];
        using var pace = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < windowEnd)
        {
            // One request per Web instance at the same instant (successive requests alternate
            // between the instances by port, and every expiry is met by both at once).
            var responses = await Task.WhenAll(urls.Select(u => jar.GetAsync(u)));
            for (var i = 0; i < urls.Length; i++)
            {
                using var response = responses[i];
                var status = (int)response.StatusCode;
                if (status is < 200 or >= 300)
                {
                    failures.Add(new Failure(urls[i],
                        $"HTTP {status}" + (status is >= 300 and < 400 ? " — a redirect away: the session did not survive" : "")));
                }
            }

            if (!await pace.WaitForNextTickAsync())
            {
                break;
            }
        }

        AssertNoFailures("the signed-in session across Web 1 and Web 2 over three token lifetimes", failures);

        // The token endpoint's own log (dev/ha-oidc-proxy.cs) for exactly this window.
        var grants = ReadTokenGrants(windowStart, DateTimeOffset.UtcNow);
        var refreshes = grants.Where(g => g.Grant == "refresh_token").ToList();
        TestContext.Progress.WriteLine(
            $"Token endpoint saw {grants.Count} grants in the window, {refreshes.Count} refreshes: " +
            string.Join(", ", refreshes.Select(r => $"+{(r.At - windowStart).TotalSeconds:F0}s(exp {r.ExpiresIn}s)")));

        Assert.That(refreshes, Has.Count.GreaterThanOrEqualTo(3),
            "the 70-second access tokens never drove a refresh in 210 s: the short-lifetime premise is not met");
        Assert.That(refreshes, Has.Count.LessThanOrEqualTo(27),
            $"the token endpoint saw {refreshes.Count} refreshes in 210 s — far more than the ~10 s expiry " +
            "rhythm (one refresh per expiry); the refresh path is over-firing");
        for (var i = 1; i < refreshes.Count; i++)
        {
            Assert.That(refreshes[i].At - refreshes[i - 1].At, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)),
                $"two refreshes hit the token endpoint {(refreshes[i].At - refreshes[i - 1].At).TotalSeconds:F1}s apart " +
                $"(at {refreshes[i - 1].At:O} and {refreshes[i].At:O}): one expiry was refreshed twice — " +
                "exactly one refresh per expiry is required");
        }

        Assert.That(grants.Count(g => g.Grant == "authorization_code" && g.Status == 200), Is.EqualTo(1),
            "the window must contain exactly the one login exchange plus its refreshes");
    }

    [Test, Order(4)]
    public async Task SignedInAdminProbe_AcrossWebAndApiKills_SeesOnly2xxAndNoSignInRedirect()
    {
        // A signed-in browser requests an admin page every second (the probe cadence) while an
        // instance is killed. The probe pins itself to Web 1 through Traefik's sticky cookie —
        // the client-side cookie a browser carries — so "kill web 1" deterministically strands
        // the signed-in session to Web 2: with the Web database the survivor serves it (green);
        // with the in-memory store (HA_NO_WEB_DB=1, the break-it-once run) the stranded probe
        // is sent to sign-in instead.
        await PinBrowserToAsync(Page, Web1Server);
        await SignInAsync(Page, TraefikWebUrl);

        // --- Kill Web 1 while the probe loops ----------------------------------------------
        var loopA = new AdminProbeLoop(Page, $"{TraefikWebUrl}/admin/posts");
        var taskA = loopA.RunAsync();
        await RunHaKillAsync("web", "1");
        var roundsTarget = loopA.Rounds + 20;
        await PollUntilAsync(TimeSpan.FromSeconds(30),
            () => Task.FromResult(loopA.Rounds >= roundsTarget), "20 probe rounds after the Web 1 kill", pollMs: 200);
        loopA.Stop();
        await taskA;
        AssertNoFailures("the signed-in admin probe while Web 1 was killed", loopA.Failures);
        await AssertInstanceGoneAsync($"{Web1Url}/", "Web 1");

        // --- Kill API 1 while the probe loops ----------------------------------------------
        var loopB = new AdminProbeLoop(Page, $"{TraefikWebUrl}/admin/posts");
        var taskB = loopB.RunAsync();
        await RunHaKillAsync("api", "1");
        roundsTarget = loopB.Rounds + 20;
        await PollUntilAsync(TimeSpan.FromSeconds(30),
            () => Task.FromResult(loopB.Rounds >= roundsTarget), "20 probe rounds after the API 1 kill", pollMs: 200);
        loopB.Stop();
        await taskB;
        AssertNoFailures("the signed-in admin probe while API 1 was killed", loopB.Failures);
        await AssertInstanceGoneAsync($"{Api1Url}/health", "API 1");

        // Restore what this test killed so the next one finds the full stack again.
        await RunHaLocalAsync("start", "web", "1");
        await RunHaLocalAsync("start", "api", "1");
        await WaitUntilTraefikRoutesToBothWebsAsync();
    }

    [Test, Order(5)]
    public async Task EditorWords_SurviveTheServingInstanceDying_RestoredFromTheLocalBackup()
    {
        // Type 2,000 characters into the editor through Traefik, kill the Web instance serving
        // the session (the sticky cookie names it), then reload and take the Restore the
        // prompt offers (08a item 6's spike settled it: the circuit state dies with the
        // process, so reload + Restore is the expected path). The outcome is the editor's
        // actual value: all 2,000 characters, exactly.
        await SignInAsync(Page, TraefikWebUrl);
        await Page.GotoAsync($"{TraefikWebUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);

        var body = Words(2000);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"HA words {Guid.NewGuid():N}");
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(body);

        // The local draft backup must hold the typed text before anything is killed — the
        // concrete condition this scenario's guarantee rests on (08a item 5, D3).
        await Page.WaitForFunctionAsync(
            """
            body => Object.keys(localStorage)
                .filter(k => k.startsWith('croft-draft:'))
                .some(k => JSON.parse(localStorage.getItem(k)).body === body)
            """,
            body,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        var pin = await StickyCookieAsync(Page);
        Assert.That(pin, Is.AnyOf(StickyServerHash(Web1Server), StickyServerHash(Web2Server)),
            $"the sticky cookie {pin} names no known Web instance; the kill target is unknown");
        var victim = pin == StickyServerHash(Web1Server) ? "1" : "2";
        await RunHaKillAsync("web", victim);

        // Reload (the expected path) and take the prompt's Restore.
        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        var prompt = Page.Locator(".draft-restore");
        await Expect(prompt).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await prompt.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Restore", Exact = true }).ClickAsync();

        var textarea = Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true });
        await Page.WaitForFunctionAsync(
            "(expected) => { const el = document.querySelector('textarea.md-editor__textarea'); return el && el.value.length === expected; }",
            2000,
            new PageWaitForFunctionOptions { Timeout = 30000 });
        var value = await textarea.InputValueAsync();
        Assert.That(value.Length, Is.EqualTo(2000), "the restored editor does not hold 2,000 characters");
        Assert.That(value, Is.EqualTo(body),
            "not every one of the 2,000 typed characters came back from the local backup");

        // Restore the killed instance so the next test finds the full stack again.
        await RunHaLocalAsync("start", "web", victim);
        await WaitUntilTraefikRoutesToBothWebsAsync();
    }

    [Test, Order(6)]
    public async Task Readers_ThroughApiAndWebFailures_SeeOnly2xx()
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false });
        var token = await GetAdminAccessTokenAsync(http);

        // Reader content: one published post and one image (this test creates its own data).
        var (postId, slug) = await CreatePostAsync(http, token, TraefikApiUrl, $"HA readers {Guid.NewGuid():N}");
        var publishStatus = await PublishAsync(http, token, TraefikApiUrl, postId, DateTime.UtcNow.AddMinutes(-1));
        Assert.That(publishStatus, Is.EqualTo(nameof(PostStatus.Published)), "The reader post did not publish.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "test-image.png"));
        var mediaId = await UploadImageAsync(http, token, TraefikApiUrl, bytes);

        var urls = new[]
        {
            "/",
            $"/post/{slug}",
            "/feed.xml",
            $"/api/media/{mediaId}",
        };

        // Prove the Web -> API traffic is really load-balanced: these sequential GETs through
        // the Traefik api entrypoint alternate strictly between the two API instances, so both
        // logs must show the media request. (Web proxies /api/media/{id} to the same
        // entrypoint, per AGENTS.md; the reader loop below goes through that path.)
        for (var i = 0; i < 4; i++)
        {
            using var probe = await http.GetAsync($"{TraefikApiUrl}/api/media/{mediaId}");
            Assert.That((int)probe.StatusCode, Is.EqualTo(200), "The Traefik api entrypoint did not serve the image.");
        }
        Assert.That(LogContains(Api1Log, mediaId.ToString()), Is.True,
            $"API 1's log shows no request for media {mediaId}: the api entrypoint did not load-balance to it.");
        Assert.That(LogContains(Api2Log, mediaId.ToString()), Is.True,
            $"API 2's log shows no request for media {mediaId}: the api entrypoint did not load-balance to it.");

        // Two readers, each with its own cookie jar: with the sticky cookie they must pin to
        // different Web instances. Their first requests are made one after the other, so
        // Traefik's round-robin hands out the two different servers deterministically.
        using var reader1 = CreateReader(out var jar1);
        using var reader2 = CreateReader(out var jar2);
        await reader1.GetAsync($"{TraefikWebUrl}/");
        await reader2.GetAsync($"{TraefikWebUrl}/");
        var hash1 = StickyServerHash(Web1Server);
        var hash2 = StickyServerHash(Web2Server);
        var cookie1 = StickyCookie(jar1);
        var cookie2 = StickyCookie(jar2);
        Assert.That(cookie1, Is.AnyOf(hash1, hash2), $"Reader 1's sticky cookie {cookie1} names no known Web instance.");
        Assert.That(cookie2, Is.AnyOf(hash1, hash2), $"Reader 2's sticky cookie {cookie2} names no known Web instance.");
        Assert.That(cookie1, Is.Not.EqualTo(cookie2),
            "The two readers are not pinned to different Web instances; the Web failover check would be meaningless.");

        // --- Kill API 1 while both readers loop: zero non-2xx -----------------------------
        var loopA1 = new ReaderLoop(reader1, TraefikWebUrl, urls);
        var loopA2 = new ReaderLoop(reader2, TraefikWebUrl, urls);
        var taskA1 = loopA1.RunAsync();
        var taskA2 = loopA2.RunAsync();

        await RunHaKillAsync("api", "1");
        // Keep reading for 20 more rounds after the kill completed (the recovery window).
        var roundsTarget = loopA1.Rounds + 20;
        await PollUntilAsync(TimeSpan.FromSeconds(30),
            () => Task.FromResult(loopA1.Rounds >= roundsTarget && loopA2.Rounds >= roundsTarget),
            "20 reader rounds after the API 1 kill", pollMs: 200);
        loopA1.Stop();
        loopA2.Stop();
        await Task.WhenAll(taskA1, taskA2);

        AssertNoFailures("readers while API 1 was killed", loopA1.Failures, loopA2.Failures);
        await AssertInstanceGoneAsync($"{Api1Url}/health", "API 1");

        // --- Kill Web 1 while both readers loop: zero non-2xx, the stranded reader lands
        // --- on Web 2 (its sticky cookie is rewritten to Web 2's) -------------------------
        var cookieBefore1 = StickyCookie(jar1);
        var cookieBefore2 = StickyCookie(jar2);
        Assert.That(cookieBefore1, Is.Not.EqualTo(cookieBefore2),
            "The readers converged before the Web kill; the failover check would be meaningless.");

        var loopB1 = new ReaderLoop(reader1, TraefikWebUrl, urls);
        var loopB2 = new ReaderLoop(reader2, TraefikWebUrl, urls);
        var taskB1 = loopB1.RunAsync();
        var taskB2 = loopB2.RunAsync();

        await RunHaKillAsync("web", "1");
        // Keep reading for 20 more rounds after the kill completed (the recovery window).
        roundsTarget = loopB1.Rounds + 20;
        await PollUntilAsync(TimeSpan.FromSeconds(30),
            () => Task.FromResult(loopB1.Rounds >= roundsTarget && loopB2.Rounds >= roundsTarget),
            "20 reader rounds after the Web 1 kill", pollMs: 200);
        loopB1.Stop();
        loopB2.Stop();
        await Task.WhenAll(taskB1, taskB2);

        AssertNoFailures("readers while Web 1 was killed", loopB1.Failures, loopB2.Failures);
        await AssertInstanceGoneAsync($"{Web1Url}/", "Web 1");

        // A reader's next request lands on Web 2: exactly one reader was pinned to Web 1, and
        // after the kill both readers are pinned to Web 2 (the survivor). The 20 post-kill
        // rounds all ran after ha-kill.sh confirmed Web 1's process was gone, and every one of
        // their responses was served by Web 2, which rewrites a stranded reader's cookie to
        // Web 2's hash (Traefik wrr: the cookie is re-written when the pinned server is DOWN).
        var cookieAfter1 = StickyCookie(jar1);
        var cookieAfter2 = StickyCookie(jar2);
        Assert.That(cookieAfter1, Is.EqualTo(cookieAfter2),
            $"After the Web 1 kill the readers are pinned to {cookieAfter1} and {cookieAfter2}, not both to Web 2.");
        Assert.That(cookieAfter1, Is.EqualTo(hash2),
            $"After the Web 1 kill the surviving pin {cookieAfter1} is not Web 2's ({hash2}).");
    }

    // --- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// The reader half of the HA story: one client's GETs through Traefik's web entrypoint,
    /// a round of the URLs every 200 ms. Non-2xx responses and transport errors are recorded
    /// — never swallowed: the tests assert the list is empty, which is the zero-non-2xx rule.
    /// </summary>
    private sealed class ReaderLoop(HttpClient reader, string baseUrl, IReadOnlyList<string> urls)
    {
        private readonly List<Failure> _failures = [];
        private int _rounds;
        private volatile bool _running = true;

        public IReadOnlyList<Failure> Failures => _failures;
        public int Rounds => Volatile.Read(ref _rounds);

        public async Task RunAsync()
        {
            using var pace = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await pace.WaitForNextTickAsync())
            {
                foreach (var url in urls)
                {
                    try
                    {
                        using var response = await reader.GetAsync(baseUrl + url);
                        if ((int)response.StatusCode is < 200 or >= 300)
                        {
                            _failures.Add(new Failure(url, $"HTTP {(int)response.StatusCode}"));
                        }
                    }
                    catch (Exception ex)
                    {
                        // A failed request is the harshest non-2xx: it must be on the record.
                        _failures.Add(new Failure(url, $"{ex.GetType().Name}: {ex.Message}"));
                    }
                }

                Interlocked.Increment(ref _rounds);
                if (!_running)
                {
                    break;
                }
            }
        }

        public void Stop() => _running = false;
    }

    /// <summary>Polls a concrete condition until it holds or the bounded timeout passes. The
    /// delay is the poll interval — the tests wait on real outcomes, never on a blind sleep.</summary>
    private static async Task PollUntilAsync(
        TimeSpan timeout, Func<Task<bool>> condition, string what, int pollMs = 2000)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline),
                $"Timed out after {timeout} waiting for {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(pollMs));
        }
    }

    private static HttpClient CreateReader(out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = cookies })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static string? StickyCookie(CookieContainer jar) =>
        jar.GetCookies(new Uri(TraefikWebUrl)).Cast<System.Net.Cookie>()
            .SingleOrDefault(c => c.Name == StickyCookieName)?.Value;

    private static string StickyServerHash(string serverUrl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl))).ToLowerInvariant()[..16];

    private static void AssertNoFailures(string phase, params IReadOnlyList<Failure>[] loops)
    {
        var failures = loops.SelectMany(l => l).ToList();
        Assert.That(failures, Is.Empty,
            $"{phase}: expected zero non-2xx responses and no failed requests, got {failures.Count}:\n" +
            string.Join("\n", failures.Select(f => $"  {f.Url}: {f.Detail}")));
    }

    /// <summary>After ha-kill.sh exits the instance's direct port must stop serving — the kill
    /// was real, not a process left draining. Polls the concrete condition with a bound.</summary>
    private static async Task AssertInstanceGoneAsync(string url, string label)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url);
            }
            catch (HttpRequestException)
            {
                return; // connection refused: the instance is gone
            }

            Assert.That(attempt, Is.LessThan(100), $"{label} is still serving {url} after it was killed.");
            await Task.Delay(100);
        }
    }

    private static async Task RunHaKillAsync(string kind, string instance)
    {
        var script = Path.Combine(RepoRoot(), "dev", "ha-kill.sh");
        var start = new ProcessStartInfo("bash", $"\"{script}\" {kind} {instance}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        var streams = await output;
        Assert.That(process.ExitCode, Is.Zero,
            $"dev/ha-kill.sh {kind} {instance} did not kill gracefully:\n{streams[0]}{streams[1]}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dev", "ha-kill.sh")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null,
            $"Could not find dev/ha-kill.sh above {AppContext.BaseDirectory}; run these tests through dev/run-ha-e2e.sh.");
        return dir!.FullName;
    }

    private static bool LogContains(string logPath, string needle) =>
        File.Exists(logPath) && File.ReadAllText(logPath).Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>The "Published scheduled post {PostId}" lines for one post across both API logs.</summary>
    private static int PublishedLogLines(Guid postId)
    {
        var needle = Regex.Escape($"Published scheduled post {postId}");
        return CountMatches(Api1Log, needle) + CountMatches(Api2Log, needle);
    }

    private static int CountMatches(string logPath, string pattern) =>
        File.Exists(logPath)
            ? Regex.Count(File.ReadAllText(logPath), pattern, RegexOptions.IgnoreCase)
            : 0;

    // --- the signed-in probes --------------------------------------------------------------

    /// <summary>
    /// The admin probe half of the HA story: a signed-in browser navigating to the admin page
    /// every second. Non-2xx responses, navigation failures and landing anywhere that is not
    /// the admin page (a redirect to sign-in) are recorded — never swallowed: the tests assert
    /// the list is empty. The 1-second cadence is the test's design (a periodic probe during a
    /// kill event); every other wait in these tests is on a concrete condition.
    /// </summary>
    private sealed class AdminProbeLoop(IPage page, string url)
    {
        private readonly List<Failure> _failures = [];
        private int _rounds;
        private volatile bool _running = true;

        public IReadOnlyList<Failure> Failures => _failures;
        public int Rounds => Volatile.Read(ref _rounds);

        public async Task RunAsync()
        {
            using var pace = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await pace.WaitForNextTickAsync())
            {
                try
                {
                    var response = await page.GotoAsync(url, new PageGotoOptions { Timeout = 15000 });
                    var status = response?.Status ?? 0;
                    if (status is < 200 or >= 300)
                    {
                        _failures.Add(new Failure(url, $"HTTP {status}"));
                    }

                    if (!page.Url.StartsWith(url, StringComparison.Ordinal))
                    {
                        // Playwright follows redirects: ending up off the admin page means the
                        // session was rejected and the browser was sent to sign-in.
                        _failures.Add(new Failure(url, $"redirected to sign-in: the browser landed on {page.Url}"));
                    }
                }
                catch (Exception ex)
                {
                    // A failed navigation is the harshest non-2xx: it must be on the record.
                    _failures.Add(new Failure(url, $"{ex.GetType().Name}: {ex.Message}"));
                }

                Interlocked.Increment(ref _rounds);
                if (!_running)
                {
                    break;
                }
            }
        }

        public void Stop() => _running = false;
    }

    /// <summary>Signs the browser in as the mock admin through the mock IdP's login page
    /// (dev/login.html's dev-only identities), on the given base URL (Traefik or a direct
    /// Web port). The authenticated outcome is the /logout control of the signed-in shell.</summary>
    private static async Task SignInAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync($"{baseUrl}/login");
        await page.WaitForURLAsync(
            url => url.StartsWith(HaMockAuthority, StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 10000 });
        await page.Locator("input[name='username']").FillAsync("admin@example.com");
        await page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();
        await page.Locator("a[href='/logout']").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10000 });
    }

    /// <summary>Pins the browser to one Web instance through Traefik's sticky cookie — the
    /// client-side cookie a browser carries, whose value names the server (see
    /// <see cref="StickyServerHash"/>). The kill that follows then strands the session
    /// deterministically.</summary>
    private static async Task PinBrowserToAsync(IPage page, string serverUrl) =>
        await page.Context.AddCookiesAsync(
        [
            new Microsoft.Playwright.Cookie
            {
                Name = StickyCookieName,
                Value = StickyServerHash(serverUrl),
                Domain = "localhost",
                Path = "/",
            },
        ]);

    private static async Task<string?> StickyCookieAsync(IPage page) =>
        (await page.Context.CookiesAsync([$"{TraefikWebUrl}/"]))
            .FirstOrDefault(c => c.Name == StickyCookieName)?.Value;

    private static async Task WaitForInteractiveAsync(IPage page) =>
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.interactive === 'on'");

    /// <summary>
    /// The refresh scenario's Web sign-in: the same OIDC handshake a browser runs, over HTTP, as
    /// the scenario's own dev identity (ha-refresh@example.com, Admin). The short access-token
    /// lifetime is the HA mock IdP's job (dev/ha-mock-oauth2-config.json: tokenExpiry 70 for the
    /// Web client) — the brief's design: "a short access-token lifetime in the mock OAuth2 server".
    /// </summary>
    private static async Task SignInScenarioUserAsync(CookieJar jar, string webBase)
    {
        using var challenge = await jar.GetAsync($"{webBase}/admin/posts");
        var authorizeUrl = challenge.Headers.Location?.ToString();
        Assert.That(authorizeUrl, Is.Not.Null.And.Contains("/authorize"),
            $"the admin challenge did not redirect to the IdP: HTTP {(int)challenge.StatusCode} -> {authorizeUrl}");

        await jar.GetAsync(authorizeUrl);
        using var login = await jar.SendAsync(new HttpRequestMessage(HttpMethod.Post, authorizeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "ha-refresh@example.com",
                ["claims"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["sub"] = "ha-refresh-id",
                    ["name"] = "HA Refresh",
                    ["email"] = "ha-refresh@example.com",
                    ["email_verified"] = true,
                    ["groups"] = new[] { "Admin" },
                }),
            }),
        });
        Assert.That((int)login.StatusCode, Is.EqualTo(200),
            $"the mock IdP login did not answer: HTTP {(int)login.StatusCode}");

        // The Web's challenge asks for response_mode=form_post: the mock's login response is an
        // auto-submitting form POSTing the code to the Web's callback — a browser submits it as
        // it stands, so this client does the same.
        var formHtml = await login.Content.ReadAsStringAsync();
        var action = WebUtility.HtmlDecode(Regex.Match(formHtml, "<form[^>]*action=\"([^\"]+)\"").Groups[1].Value);
        var fields = Regex.Matches(formHtml, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .Select(m => new KeyValuePair<string, string>(
                m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value)))
            .ToList();
        Assert.That(action, Does.StartWith(webBase), $"the login form does not post back to the Web: {action}");
        Assert.That(fields.Select(f => f.Key), Does.Contain("code"), $"the login form carries no code: {formHtml}");

        using var callback = await jar.SendAsync(new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new FormUrlEncodedContent(fields),
        });
        Assert.That((int)callback.StatusCode, Is.InRange(300, 399),
            $"the Web sign-in callback did not complete the login: HTTP {(int)callback.StatusCode}");

        using var admin = await jar.GetAsync($"{webBase}/admin/posts");
        Assert.That((int)admin.StatusCode, Is.EqualTo(200),
            $"the session did not sign in on {webBase}: HTTP {(int)admin.StatusCode}");
    }

    // WORKAROUND(W-061, .NET): CookieContainer refuses secure cookies over http, which the OIDC correlation cookies need
    /// <summary>
    /// A client that carries cookies BY HAND. .NET's <see cref="CookieContainer"/> refuses to
    /// send the OIDC handler's <c>secure</c> correlation cookies over plain http (browsers
    /// except localhost from that rule, CookieContainer does not), so an idiomatic HttpClient
    /// jar dies with "Correlation failed". Every request here attaches the jar explicitly and
    /// every response folds Set-Cookie back into it.
    /// </summary>
    private sealed class CookieJar : IDisposable
    {
        private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        private readonly Dictionary<string, string> _cookies = [with(StringComparer.OrdinalIgnoreCase)];
        private readonly Lock _gate = new();

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            string cookieHeader;
            lock (_gate)
            {
                cookieHeader = string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}"));
            }

            if (cookieHeader.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            var response = await _http.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                lock (_gate)
                {
                    foreach (var setCookie in setCookies)
                    {
                        var pair = setCookie.Split(';')[0];
                        var separator = pair.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        var name = pair[..separator].Trim();
                        var value = pair[(separator + 1)..].Trim();
                        if (value.Length == 0)
                        {
                            _cookies.Remove(name);
                        }
                        else
                        {
                            _cookies[name] = value;
                        }
                    }
                }
            }

            return response;
        }

        public void Dispose() => _http.Dispose();
    }
    // END WORKAROUND(W-061)

    /// <summary>2,000 characters of run-keyed, newline-free text — the exact scenario size.</summary>
    private static string Words(int length)
    {
        var pattern = "HA words survive the loss of the instance that typed them. ";
        var text = new StringBuilder(length + pattern.Length);
        while (text.Length < length)
        {
            text.Append(pattern);
        }

        return text.ToString()[..length];
    }

    /// <summary>dev/ha-local.sh start api|web N: the killed instance is back — the harness
    /// stand-in for k8s rescheduling a pod — with its health endpoint answering.</summary>
    private static async Task RunHaLocalAsync(params string[] arguments)
    {
        var script = Path.Combine(RepoRoot(), "dev", "ha-local.sh");
        var start = new ProcessStartInfo("bash", $"\"{script}\" {string.Join(' ', arguments)}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        var streams = await output;
        Assert.That(process.ExitCode, Is.Zero,
            $"dev/ha-local.sh {string.Join(' ', arguments)} failed:\n{streams[0]}{streams[1]}");
    }

    /// <summary>
    /// After an instance is restarted, Traefik must see both Web servers healthy again before a
    /// later test relies on round-robin handing its readers different instances. Two first
    /// requests on fresh cookie jars get different sticky pins exactly when both servers are
    /// in rotation — the concrete condition, polled with a bound.
    /// </summary>
    private static async Task WaitUntilTraefikRoutesToBothWebsAsync()
    {
        var hash1 = StickyServerHash(Web1Server);
        var hash2 = StickyServerHash(Web2Server);
        await PollUntilAsync(TimeSpan.FromSeconds(60), async () =>
        {
            using var reader1 = CreateReader(out var jar1);
            using var reader2 = CreateReader(out var jar2);
            await reader1.GetAsync($"{TraefikWebUrl}/");
            await reader2.GetAsync($"{TraefikWebUrl}/");
            var pin1 = StickyCookie(jar1);
            var pin2 = StickyCookie(jar2);
            return pin1 == hash1 && pin2 == hash2 || pin1 == hash2 && pin2 == hash1;
        }, "Traefik to route to both Web instances again", pollMs: 500);
    }

    // --- the token endpoint's counter log ---------------------------------------------------

    private sealed record TokenGrant(DateTimeOffset At, string Grant, int Status, int? ExpiresIn);

    /// <summary>dev/ha-oidc-proxy.cs's JSONL log, filtered to the given window: one record per
    /// token request the Web instances made (the login exchange and every refresh).</summary>
    private static List<TokenGrant> ReadTokenGrants(DateTimeOffset from, DateTimeOffset to)
    {
        List<TokenGrant> grants = [];
        if (!File.Exists(TokenLog))
        {
            Assert.Fail($"The token counter log {TokenLog} does not exist; the HA stack must run dev/ha-oidc-proxy.cs.");
        }

        foreach (var line in File.ReadAllLines(TokenLog))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var at = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("t").GetInt64());
            if (at < from || at > to)
            {
                continue;
            }

            grants.Add(new TokenGrant(
                at,
                root.GetProperty("grant").GetString() ?? string.Empty,
                root.GetProperty("status").GetInt32(),
                root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                    ? ei.GetInt32()
                    : null));
        }

        return grants;
    }

    // --- API calls as the mock admin -------------------------------------------------------

    /// <summary>
    /// The mock IdP's interactive login over HTTP — the identity dev/login.html maps to the
    /// Admin role (dev-only identity, exactly what dev/seed-dev-data.sh does) — and the
    /// authorization-code exchange for the API's bearer token. Redirects are never followed:
    /// the code arrives in the 302's Location header pointing at the never-listened callback
    /// URL (dev/seed-dev-data.sh does the same with curl without -L).
    /// </summary>
    private static async Task<string> GetAdminAccessTokenAsync(HttpClient http)
    {
        var authorizeUrl = $"{HaMockAuthority}/authorize" +
            $"?client_id=blog-web&redirect_uri={CallbackUrl}&response_type=code&scope=openid&state=ha";

        await http.GetAsync(authorizeUrl);
        using var login = await http.PostAsync(authorizeUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = "admin@example.com",
                ["claims"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["sub"] = "admin-id",
                    ["name"] = "Admin User",
                    ["email"] = "admin@example.com",
                    ["email_verified"] = true,
                    ["groups"] = new[] { "Admin" },
                }),
            }));
        var location = login.Headers.Location?.ToString() ?? login.Headers.ToString();
        var code = Regex.Match(location, @"[?&]code=([^&]+)").Groups[1].Value;
        Assert.That(code, Is.Not.Empty, $"The mock IdP returned no authorization code: {location}");

        using var tokenResponse = await http.PostAsync($"{HaMockAuthority}/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = CallbackUrl,
                ["client_id"] = "blog-web",
                ["client_secret"] = "secret",
            }));
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        Assert.That(tokenResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"The mock IdP did not exchange the authorization code: {tokenBody}");
        using var doc = JsonDocument.Parse(tokenBody);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static HttpRequestMessage Authorized(
        HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<(Guid Id, string Slug)> CreatePostAsync(
        HttpClient http, string token, string apiBase, string title)
    {
        using var request = Authorized(HttpMethod.Post, $"{apiBase}/api/posts", token,
            JsonContent.Create(new { title, markdownContent = $"Body of {title}." }));
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created),
            $"The API did not create '{title}': {body}");
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("id").GetGuid(), doc.RootElement.GetProperty("slug").GetString()!);
    }

    private static async Task<string> PublishAsync(
        HttpClient http, string token, string apiBase, Guid id, DateTime publishedAtUtc)
    {
        using var request = Authorized(HttpMethod.Post, $"{apiBase}/api/posts/{id}/publish", token,
            JsonContent.Create(new { publishedAt = publishedAtUtc }));
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"The API did not accept the schedule for {id}: {body}");
        using var doc = JsonDocument.Parse(body);
        return StatusOf(doc.RootElement);
    }

    private static async Task<string> PostStatusAsync(
        HttpClient http, string token, string apiBase, string slug)
    {
        using var request = Authorized(HttpMethod.Get, $"{apiBase}/api/posts/{slug}", token);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"The API did not return the post '{slug}': {body}");
        using var doc = JsonDocument.Parse(body);
        return StatusOf(doc.RootElement);
    }

    private static async Task<bool> AllPublishedAsync(
        HttpClient http, string token, List<(Guid Id, string Slug)> posts)
    {
        var statuses = await Task.WhenAll(
            posts.Select(p => PostStatusAsync(http, token, TraefikApiUrl, p.Slug)));
        return statuses.All(s => s == nameof(PostStatus.Published));
    }

    private static string StatusOf(JsonElement post)
    {
        var status = post.GetProperty("status");
        return status.ValueKind switch
        {
            JsonValueKind.Number => ((PostStatus)status.GetInt32()).ToString(),
            JsonValueKind.String => status.GetString()!,
            _ => throw new InvalidOperationException($"Unexpected status value: {status}"),
        };
    }

    private static async Task<Guid> UploadImageAsync(
        HttpClient http, string token, string apiBase, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", $"ha-{Guid.NewGuid():N}.png");
        form.Add(new StringContent(nameof(MediaAssetScope.Global)), "scope");

        using var request = Authorized(HttpMethod.Post, $"{apiBase}/api/media", token, form);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created),
            $"The API did not accept the upload: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}
