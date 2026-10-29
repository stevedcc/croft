using System.Text.Json;
using Microsoft.Playwright.NUnit;

namespace Croft.Web.E2E;

public abstract class WebTest : PageTest
{
    // The port plan mirrors dev/instance-params.sh, the single source of truth: instance N is
    // API 5000 + 10*(N-1), Web 5002 + 10*(N-1). Keep both sides in sync.
    private const int FirstInstanceApiPort = 5000;
    private const int FirstInstanceWebPort = 5002;
    private const int PortsPerInstance = 10;

    private string _baseUrl = null!;
    private string _apiUrl = null!;

    /// <summary>The stack instance this fixture is pinned to (WebTest.StackUrls).</summary>
    protected string BaseUrl => _baseUrl;
    protected string ApiUrl => _apiUrl;

    protected const string OAuthAuthority = "http://localhost:5556/default";

    /// <summary>
    /// Pins this fixture to one stack instance before its first test. dev/run-e2e.sh starts
    /// instances E2E_FIRST_INSTANCE .. +E2E_INSTANCE_COUNT-1 (one per NUnit worker) and passes
    /// the worker count to NUnit; a fixture runs entirely on one worker with sequential tests,
    /// so worker w owns instance FIRST + (w mod COUNT) for the fixture's lifetime and two
    /// fixtures never share an instance at the same time — the premise every assertion about
    /// instance-global state (featured post, "page 1 contains", analytics counts) rests on.
    /// Non-parallelizable fixtures run exclusively and map through the same formula, which the
    /// modulo keeps inside the started range. With the instance variables unset, the previous
    /// behavior wins: E2E_WEB_URL / E2E_API_URL (a single manually-started instance) or the
    /// instance-1 defaults.
    /// </summary>
    [OneTimeSetUp]
    public void StackUrls()
    {
        var worker = TestContext.CurrentContext.WorkerId;
        (_baseUrl, _apiUrl) = ResolveStackUrls(worker);
        TestContext.Progress.WriteLine(
            $"{TestContext.CurrentContext.Test.FullName}: pinned to Web {_baseUrl} / API {_apiUrl} (worker {worker ?? "none"}).");
    }

    private static (string BaseUrl, string ApiUrl) ResolveStackUrls(string? workerId)
    {
        var webUrl = Environment.GetEnvironmentVariable("E2E_WEB_URL");
        var apiUrl = Environment.GetEnvironmentVariable("E2E_API_URL");
        var instanceCount = PositiveEnvironmentVariable("E2E_INSTANCE_COUNT");

        // Explicit single-instance URLs keep winning over the worker mapping, and no mapping is
        // configured outside dev/run-e2e.sh: resolve each variable on its own as before.
        if (instanceCount is null || webUrl is { Length: > 0 } || apiUrl is { Length: > 0 })
        {
            return (UrlOrPort(webUrl, FirstInstanceWebPort), UrlOrPort(apiUrl, FirstInstanceApiPort));
        }

        var instance = (PositiveEnvironmentVariable("E2E_FIRST_INSTANCE") ?? 1) + WorkerNumber(workerId) % instanceCount.Value;
        return (
            $"http://localhost:{FirstInstanceWebPort + PortsPerInstance * (instance - 1)}",
            $"http://localhost:{FirstInstanceApiPort + PortsPerInstance * (instance - 1)}");
    }

    private static string UrlOrPort(string? url, int port) =>
        url is { Length: > 0 } value ? value.TrimEnd('/') : $"http://localhost:{port}";

    private static int? PositiveEnvironmentVariable(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } raw
        && int.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : null;

    // WORKAROUND(W-064, NUnit): instances are mapped from NUnit's worker numbers 1..N, which are distinct modulo the instance count
    /// <summary>"NUnit.Fw.ParallelWorker#3" → 3; anything unparseable (no parallel workers) → 0.
    /// NUnit numbers its parallel workers 1..N, so the residues (w mod N) are pairwise distinct
    /// and every started instance gets exactly one worker. Parsed from NUnit's own worker
    /// identity rather than the inherited Playwright property, so the mapping depends on NUnit's
    /// worker assignment alone.</summary>
    private static int WorkerNumber(string? workerId)
    {
        var separator = workerId?.LastIndexOf('#') ?? -1;
        return separator >= 0 && int.TryParse(workerId![(separator + 1)..], out var index) ? index : 0;
    }
    // END WORKAROUND(W-064)

    /// <summary>This fixture's instance number, from its Web port (the plan above, inverted).</summary>
    protected int InstanceNumber => (new Uri(BaseUrl).Port - FirstInstanceWebPort) / PortsPerInstance + 1;

    /// <summary>
    /// The precedence rule shared with start-local.sh and dev/run-e2e.sh: a variable already set
    /// and non-empty in the environment is never overwritten by .env — .env only fills what is
    /// missing, so its value is read lazily and only when the environment does not supply one.
    /// </summary>
    internal static string PreferEnvironment(string? environmentValue, Func<string> readDotenvValue) =>
        environmentValue is { Length: > 0 } ? environmentValue : readDotenvValue();

    /// <summary>
    /// <c>ConnectionStrings__BlogDb</c>: from the environment when it is set and non-empty,
    /// otherwise from <c>.env</c> at the repository root (which must hold the line), the same
    /// precedence rule as start-local.sh and dev/run-e2e.sh. The instance databases are this
    /// connection string with the database name swapped (dev/instance-params.sh).
    /// </summary>
    private static string BlogDbConnectionString() =>
        PreferEnvironment(
            Environment.GetEnvironmentVariable("ConnectionStrings__BlogDb"),
            () =>
            {
                var line = File.ReadLines(Path.Combine(RepositoryRoot().FullName, ".env"))
                    .FirstOrDefault(l => l.StartsWith("ConnectionStrings__BlogDb=", StringComparison.Ordinal));
                Assert.That(line, Is.Not.Null, ".env must hold ConnectionStrings__BlogDb");
                return line!["ConnectionStrings__BlogDb=".Length..];
            });

    /// <summary>The repository root (the directory holding Croft.slnx).</summary>
    private static DirectoryInfo RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.GetFiles("*.slnx").Length == 0)
        {
            root = root.Parent;
        }

        Assert.That(root, Is.Not.Null, "the tests run from inside the repository");
        return root!;
    }

    /// <summary>
    /// Ends the admin's server-side sessions the way the purge does: deletes the user's rows from
    /// the instance's Web database (blog_e2e_N_web, dev/instance-params.sh; the connection string
    /// is the configured one with that database). Returns how many rows went, so a test can check
    /// it really ended a session.
    /// </summary>
    protected async Task<int> EndAdminSessionsAsync(string sub = "admin-id")
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(BlogDbConnectionString())
        {
            Database = $"blog_e2e_{InstanceNumber}_web"
        };
        await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"UserSessions\" WHERE \"Sub\" = @sub";
        command.Parameters.AddWithValue("sub", sub);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>How many "CSP violation" lines this instance's Web log holds that name
    /// <paramref name="blockedUriPart"/> as the blocked URI: the CSP report endpoint logs one line
    /// per violation the browser reported, so a rise proves a report really arrived.</summary>
    protected int ReportedCspViolationCount(string blockedUriPart)
    {
        var log = $"/tmp/blog-e2e-{InstanceNumber}-web.log";
        return File.Exists(log)
            ? File.ReadLines(log).Count(l => l.Contains("CSP violation:", StringComparison.Ordinal)
                && l.Contains(blockedUriPart, StringComparison.Ordinal))
            : 0;
    }

    /// <summary>The instance's Web log (the stack's own file, dev/instance-params.sh).</summary>
    protected string WebLogText()
    {
        var log = $"/tmp/blog-e2e-{InstanceNumber}-web.log";
        Assert.That(File.Exists(log), Is.True, $"the instance's Web log {log} exists");
        return File.ReadAllText(log);
    }

    /// <summary>The instance's API log (the stack's own file, dev/instance-params.sh).</summary>
    protected string ApiLogText()
    {
        var log = $"/tmp/blog-e2e-{InstanceNumber}-api.log";
        Assert.That(File.Exists(log), Is.True, $"the instance's API log {log} exists");
        return File.ReadAllText(log);
    }

    /// <summary>The instance's database clock, read from the server: the reference for
    /// <see cref="ReadConnectionServedSinceAsync"/>, which compares server timestamps.</summary>
    protected async Task<DateTime> DatabaseClockAsync()
    {
        await using var connection = await OpenInstanceDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT clock_timestamp()";
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Whether a connection of the API's read route (application name <c>croft-api-read</c>, which only
    /// Database:ReadHost routing uses) did work on this instance's database after <paramref name="since"/>
    /// (a server time from <see cref="DatabaseClockAsync"/>): the server's own record that a read taken
    /// since then went over the read host. Not a count of pooled connections: those outlive the read that
    /// opened them and say nothing about the read under test.
    /// </summary>
    protected async Task<bool> ReadConnectionServedSinceAsync(DateTime since)
    {
        await using var connection = await OpenInstanceDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() "
            + "AND application_name = 'croft-api-read' AND state_change >= @since";
        command.Parameters.AddWithValue("since", since);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    protected async Task<Npgsql.NpgsqlConnection> OpenInstanceDatabaseAsync()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(BlogDbConnectionString())
        {
            Database = $"blog_e2e_{InstanceNumber}",
            Pooling = false,
        };
        var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>How many times this instance's Web log says a circuit died of an unhandled exception.</summary>
    protected int UnhandledCircuitExceptionCount()
    {
        var log = $"/tmp/blog-e2e-{InstanceNumber}-web.log";
        return File.Exists(log)
            ? File.ReadLines(log).Count(l => l.Contains("Unhandled exception in circuit", StringComparison.Ordinal))
            : 0;
    }

    /// <summary>
    /// Signs in again through the mock IdP after the app sent the browser to it (an ended
    /// session): fills the sign-in form the same way LoginAsync does, then waits for the browser
    /// to be back on the app.
    /// </summary>
    protected async Task SignInAgainAtTheMockIdpAsync(IPage page, string email = "admin@example.com")
    {
        try
        {
            await page.WaitForURLAsync(url => url.StartsWith($"{OAuthAuthority}/authorize"), new PageWaitForURLOptions { Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The app did not send the browser to sign in. Current URL: {page.Url}. Page content:\n{await page.ContentAsync()}");
        }

        await page.Locator("input[name='username']").FillAsync(email);
        await page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();
        try
        {
            await page.WaitForURLAsync(url => url.StartsWith(BaseUrl), new PageWaitForURLOptions { Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Signing in again did not return to the app. Current URL: {page.Url}. Page content:\n{await page.ContentAsync()}");
        }
    }

    /// <summary>
    /// Signs out and waits until the sign-out has finished. GET /logout resolves when the
    /// interstitial has loaded, but the sign-out continues from there by script (POST
    /// /logout/complete, its continue page, the IdP's end-session redirect back to "/"); a test
    /// that navigates before that last hop has its navigation interrupted by it. The gate is the
    /// signed-out shell: the app's home with the /login control rendered (attached, as its label
    /// is hidden in rail and phone modes).
    /// </summary>
    protected async Task SignOutAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/logout");
        await Page.WaitForURLAsync($"{BaseUrl}/", new PageWaitForURLOptions { Timeout = 10000 });
        await Page.Locator("a[href='/login']").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10000 });
    }

    protected Task LoginAsync(string email, string password) => LoginAsync(Page, email, password);

    /// <summary>Signs in through the mock IdP on a specific page: tests that capture or scan in
    /// fresh browser contexts (their own theme preference and viewport) authenticate the
    /// context's own page, which shares no cookies with the test's default one.</summary>
    protected async Task LoginAsync(IPage page, string email, string password)
    {
        await page.GotoAsync($"{BaseUrl}/login");
        await page.WaitForURLAsync(url => url.StartsWith($"{OAuthAuthority}/authorize"), new PageWaitForURLOptions { Timeout = 10000 });

        await page.Locator("input[name='username']").FillAsync(email);
        await page.Locator("input[type='submit'][value='Sign-in']").ClickAsync();

        try
        {
            // The signed-in shell renders the /logout control at every breakpoint; its label is
            // hidden in rail and phone modes by design, so the gate is on the control being
            // rendered — the authenticated outcome — not on one responsive presentation of it.
            await page.Locator("a[href='/logout']").First
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            var url = page.Url;
            var content = await page.ContentAsync();
            throw new TimeoutException($"Login did not reach authenticated state. Current URL: {url}. Page content:\n{content}");
        }
    }

    /// <summary>Waits for the rendered dark palette: the light first-paint flash (spec §4.3) is not
    /// the state the user ends up seeing — reads and captures must wait for the dark one. The
    /// colour is the dark --bg (#0F172A), which branding.example leaves to the built-in defaults.
    /// On a static reading page that is the whole palette (the theme bootstrap sets it before
    /// first paint); an interactive document also has MudThemeProvider, which MainLayout flips
    /// after first render, so the wait covers that too.</summary>
    protected static async Task WaitForDarkPaletteAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            @"() => {
                const isDark = c => { const m = c.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/); return m !== null && +m[1] === 15 && +m[2] === 23 && +m[3] === 42; };
                // The site's own --bg token paints the page on every document.
                if (!isDark(getComputedStyle(document.body).backgroundColor)) return false;
                // An interactive document also has MudThemeProvider, which flips after first render.
                if (document.documentElement.dataset.render !== 'interactive') return true;
                return isDark(getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background'));
            }");
    }

    /// <summary>The post id in the editor URL (/admin/posts/{id}/edit) after a create or save.</summary>
    protected Guid PostIdFromEditorUrl()
    {
        var match = System.Text.RegularExpressions.Regex.Match(Page.Url, @"[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}");
        Assert.That(match.Success, Is.True, $"No post id in the editor URL {Page.Url}.");
        return Guid.Parse(match.Value);
    }

    /// <summary>The post's public slug, read from the API by id (the editor routes by id).</summary>
    protected async Task<string> SlugForPostAsync(Guid id)
    {
        await using var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts/{id}");
        Assert.That(response.Status, Is.EqualTo(200), $"The public API did not return the post {id}.");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        return doc.RootElement.GetProperty("slug").GetString()!;
    }

    /// <summary>Adds each name through the editor's tag chip input (free text creates the tag).</summary>
    protected async Task AddTagsAsync(params string[] names)
    {
        foreach (var name in names)
        {
            await Page.GetByLabel("Tags").FillAsync(name);
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add tag" }).ClickAsync();
        }
    }

    /// <summary>
    /// The bound for a wait that spans a page's whole data load on a loaded machine (three instances
    /// share one IdP and one Postgres; a full run once saw a 30 s timeout under swap): a bound is a
    /// failure threshold, not a measurement, so it is generous and every use names what it waits for.
    /// </summary>
    protected const float SlowLoadTimeoutMs = 30_000;

    /// <summary>
    /// Runs one wait (an <c>Expect</c>, a <c>WaitFor…</c>) and, when it fails or times out, says in
    /// one line what the test was waiting for. Playwright's own message names the locator, not the
    /// step of the scenario; a red run should be diagnosable from that one line.
    /// </summary>
    protected static async Task WaitedFor(string what, Func<Task> wait)
    {
        try
        {
            await wait();
        }
        catch (PlaywrightException ex)
        {
            throw new NUnit.Framework.AssertionException($"Waiting for {what} failed: {ex.Message}", ex);
        }
    }

    // WORKAROUND(W-011, Blazor): events dispatched before the first interactive render are dropped; tests wait on data-interactive
    /// <summary>Waits for the app to be interactive (its shell JS ran): DOM events dispatched
    /// during the prerender → interactive handoff have no Blazor handlers and are silently
    /// dropped (this suite's documented race).</summary>
    public static async Task WaitForInteractiveAsync(IPage page) =>
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.interactive === 'on'");
    // END WORKAROUND(W-011)

    /// <summary>
    /// Opens the new-post editor and waits until its first interactive render has happened (W-011:
    /// a field filled before it is replaced by that render and the typed text is lost), with the
    /// "New Post" heading as the sign that the right page is up.
    /// </summary>
    protected async Task OpenNewPostEditorAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "New Post" })).ToBeVisibleAsync();
    }

    protected async Task AssertImageLoadsAsync(ILocator img)
    {
        await img.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var isComplete = await img.EvaluateAsync<bool>("el => el.complete && el.naturalWidth > 0");
        Assert.That(isComplete, Is.True, "Image element is visible but did not actually render.");

        var src = await img.GetAttributeAsync("src");
        Assert.That(src, Is.Not.Null.And.Not.Empty, "Image src was not set.");

        var url = src!.StartsWith('/') ? $"{BaseUrl}{src}" : src;
        await using var response = await Page.APIRequest.GetAsync(url);
        Assert.That(response.Status, Is.EqualTo(200), $"Image request returned non-200 status. URL: {url}");

        response.Headers.TryGetValue("content-type", out var contentType);
        Assert.That(contentType, Is.Not.Null, $"Image response missing Content-Type header. URL: {url}");
        Assert.That(contentType, Does.StartWith("image/png"), $"Image response was not image/png. URL: {url}");
    }

    /// <summary>
    /// The first category link the sidebar renders, read from the DOM. Which categories the
    /// sidebar shows is data-dependent — it caps at the top 12 by published-post count — so tests
    /// anchor on an item that is on screen by construction and use its href and name instead of
    /// assuming a specific category made the cut.
    /// </summary>
    protected static async Task<(string Href, string Name)> FirstSidebarCategoryAsync(IPage page)
    {
        var item = page.Locator("#sidebar-nav a.nav-item[href^='/category/']").First;
        await item.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10000 });
        var href = await item.GetAttributeAsync("href");
        var name = (await item.Locator(".label:not(.count)").TextContentAsync())?.Trim();
        return (href!, name!);
    }
}
