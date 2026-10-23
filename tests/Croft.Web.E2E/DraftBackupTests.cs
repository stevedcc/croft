using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Croft.Shared;
using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 08a item 5 — D3's guaranteed layer (owner's decision 2): the editor keeps a local draft
/// backup in localStorage (`croft-draft:&lt;post id&gt;`, `croft-draft:new` before the first save)
/// and loading an editor with a backup offers a PROMPT — "You have unsaved changes from &lt;local
/// date and time&gt;. Restore them?" with Restore and Discard — never a silent replace. Restore
/// puts the backup's title and body in the editor unsaved; Discard deletes the backup; a backup
/// identical to the saved text is deleted silently; a successful save clears the key (the first
/// save of a new post moves future backups to the id); /logout clears every `croft-draft:` key.
/// Every assertion is an outcome: the prompt's exact text with the time, the textarea's actual
/// value, and localStorage's key state — a visibly broken prompt is still "visible". When
/// storage access itself throws, the layer degrades to no backup at all: the editor and the
/// sign-out must keep working (the two StorageAccessThrowing tests).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class DraftBackupTests : WebTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task ReloadWithoutSaving_PromptsWithTheLocalTime_AndRestore_PutsTheTypedTextBackUnsaved()
    {
        var (id, serverTitle, serverBody) = await CreateAPostAsync("Draft prompt");
        await LoginAsync("admin@example.com", "password");
        await OpenEditorAsync(id);

        var typedTitle = $"Typed title {RunId}";
        var typedBody = $"Typed body {RunId} — words that must not be lost.";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(typedTitle);
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(typedBody);
        var key = await WaitForBackupAsync(typedBody);

        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);

        // No silent replace: before any choice, the editor holds the server's saved text.
        var textarea = Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true });
        await Expect(textarea).ToHaveValueAsync(serverBody);
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(serverTitle);

        var prompt = Page.Locator(".draft-restore");
        await Expect(prompt).ToBeVisibleAsync();
        var expectedTime = await BackupLocalTimeAsync(key);
        var text = (await prompt.Locator(".draft-restore__text").TextContentAsync())?.Trim();
        Assert.That(text, Is.EqualTo($"You have unsaved changes from {expectedTime}. Restore them?"),
            "the prompt must say when the backup was made, in the browser's local date and time");

        // The backup's time is when the typing happened, and its saved stamp is the server's
        // saved version the editor loaded (contentEditedAt, else updatedAt).
        Assert.That(Math.Abs(await BackupTimeMillisAsync(key) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Is.LessThan(120_000), "the backup's time is the moment of the typing, not the load");
        Assert.That(await BackupSavedStampMillisAsync(key), Is.EqualTo(await ServerStampAsync(id)).Within(1),
            "the backup stores the server's saved version stamp the editor loaded");

        await prompt.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Restore", Exact = true }).ClickAsync();
        await Expect(prompt).ToHaveCountAsync(0);
        await Expect(textarea).ToHaveValueAsync(typedBody);
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(typedTitle);

        // Restored is unsaved: the server still has the old text, and the backup stays until a save.
        var (serverTitleAfter, serverBodyAfter) = await ServerPostAsync(id);
        Assert.That(serverTitleAfter, Is.EqualTo(serverTitle), "Restore must not save the post");
        Assert.That(serverBodyAfter, Is.EqualTo(serverBody), "Restore must not save the post");
        Assert.That(await Page.EvaluateAsync<string?>("(key) => localStorage.getItem(key)", key), Is.Not.Null,
            "the backup keeps protecting the restored text until the user saves");
    }

    /// <summary>
    /// The owner's scenario (writing "Load Testing the Blog", 2026-10-16 to 18): every field of a
    /// new post filled, the page reloaded without saving (what returning from the identity
    /// provider is), the restore accepted. Subtitle, description, category, tags and banner were
    /// lost before; each must come back, the banner's thumbnail included, and Save draft must
    /// then store every value. The publish time is not set here (see <see cref="EveryFieldScenario"/>).
    /// </summary>
    [Test]
    public async Task EveryFieldFilledOnANewPost_SurvivesAReloadAndRestore_AndSaves()
    {
        var scenario = new EveryFieldScenario(Page, BaseUrl, ApiUrl, RunId);
        await LoginAsync("admin@example.com", "password");
        await scenario.SeedAsync();
        await scenario.FillNewPostAsync();
        await scenario.WaitForBackupOfEveryFieldAsync();

        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        var prompt = Page.Locator(".draft-restore");
        await Expect(prompt).ToBeVisibleAsync();
        await prompt.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Restore", Exact = true }).ClickAsync();
        await Expect(prompt).ToHaveCountAsync(0);

        await scenario.AssertEveryFieldShownAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
        await scenario.AssertSavedAsync(PostIdFromEditorUrl());
    }

    /// <summary>
    /// The "user acted" flag belongs to one editor, not to the document: in one document the
    /// owner changes something in editor A, moves within the app to editor B (which has a waiting
    /// backup and shows the restore prompt) and picks a category before answering the prompt. The
    /// waiting backup must survive: nothing may replace it with B's saved text plus the pick.
    /// </summary>
    [Test]
    public async Task APickInASecondEditor_BeforeAnsweringThePrompt_LeavesTheWaitingBackupAlone()
    {
        var (idA, _, _) = await CreateAPostAsync("Draft nav A");
        var (idB, _, bodyB) = await CreateAPostAsync("Draft nav B");
        var categoryName = $"Nav Cat {RunId}";
        using (var client = MockIdpApi.CreateClient())
        {
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            (await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/categories", JsonSerializer.Serialize(categoryName), token)).Dispose();
        }

        // Counts the editor's debounced backup writes (draft-backup.js schedules `writeNow` on a 500 ms
        // timer): how many were scheduled and how many have fired. The test asserts on these instead
        // of sleeping past the debounce and finding nothing, which a broken writer also shows.
        await Page.Context.AddInitScriptAsync(
            """
            (() => {
                window.__writeTimers = { scheduled: 0, fired: 0 };
                const original = window.setTimeout;
                window.setTimeout = function (callback, delay, ...rest) {
                    if (typeof callback === 'function' && callback.name === 'writeNow') {
                        window.__writeTimers.scheduled += 1;
                        const wrapped = function () { window.__writeTimers.fired += 1; return callback.apply(this, arguments); };
                        return original.call(this, wrapped, delay, ...rest);
                    }
                    return original.apply(this, arguments);
                };
            })();
            """);
        await LoginAsync("admin@example.com", "password");
        var keyB = $"croft-draft:{idB}";
        var waiting = JsonSerializer.Serialize(new
        {
            version = 2, title = "Waiting title", subtitle = "", excerpt = "", body = "Waiting body.",
            tags = Array.Empty<string>(), savedStamp = "", time = DateTimeOffset.UtcNow.ToString("o"),
        });
        await Page.EvaluateAsync("args => localStorage.setItem(args.key, args.value)", new { key = keyB, value = waiting });

        await OpenEditorAsync(idA);
        await Page.EvaluateAsync("() => { window.__sameDocument = true; }");
        await Page.GetByLabel("Subtitle", new PageGetByLabelOptions { Exact = true }).FillAsync("Changed in A");
        // The control: A's typing scheduled a backup write, so the counters see this editor's writer.
        await Page.WaitForFunctionAsync("() => window.__writeTimers.scheduled >= 1");
        var scheduledByA = await Page.EvaluateAsync<int>("() => window.__writeTimers.scheduled");

        await Page.Locator("#sidebar-nav a[href='/admin/posts']").First.ClickAsync();
        await Page.Locator($"a[href='/admin/posts/{idB}/edit']").First.ClickAsync();
        await Page.WaitForURLAsync($"{BaseUrl}/admin/posts/{idB}/edit");
        Assert.That(await Page.EvaluateAsync<bool>("() => window.__sameDocument === true"), Is.True,
            "the move between the editors must stay inside one document");
        await Expect(Page.Locator(".draft-restore")).ToBeVisibleAsync();

        var category = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Category" });
        await category.ClickAsync();
        await category.FillAsync(categoryName);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = categoryName }).ClickAsync();
        await Expect(category).ToHaveValueAsync(categoryName);

        // The pick has reached B's form model (the attribute the backup script watches carries it).
        // If the "user acted" flag had leaked over from editor A, that change would have scheduled
        // a write right now: the writer's own timer is the signal, not a pause that a broken writer
        // would also survive.
        await Page.WaitForFunctionAsync(
            "name => (document.querySelector('[data-draft-scope]')?.getAttribute('data-draft-extra') ?? '').includes(name)",
            categoryName);
        Assert.That(await Page.EvaluateAsync<int>("() => window.__writeTimers.scheduled"), Is.EqualTo(scheduledByA),
            "picking a category in B, with its restore prompt waiting, must not schedule a backup write");

        // A's own debounce timer (set before the move) must also have run its course without
        // touching B: every scheduled write has fired, and the waiting backup is still as it was.
        await Page.WaitForFunctionAsync("() => window.__writeTimers.fired >= window.__writeTimers.scheduled");
        Assert.That(await Page.EvaluateAsync<string?>("key => localStorage.getItem(key)", keyB), Is.EqualTo(waiting),
            $"the waiting backup must be untouched (the saved text is '{bodyB}')");
        await Expect(Page.Locator(".draft-restore")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Discard_KeepsTheServersText_AndDeletesTheBackup()
    {
        var (id, serverTitle, serverBody) = await CreateAPostAsync("Draft discard");
        await LoginAsync("admin@example.com", "password");
        await OpenEditorAsync(id);

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"Typed title {RunId}");
        var body = $"Typed body {RunId} that gets thrown away.";
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(body);
        var key = await WaitForBackupAsync(body);

        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);
        var prompt = Page.Locator(".draft-restore");
        await Expect(prompt).ToBeVisibleAsync();

        await prompt.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Discard", Exact = true }).ClickAsync();

        await Expect(prompt).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(serverBody);
        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(serverTitle);
        Assert.That(await Page.EvaluateAsync<string?>("(key) => localStorage.getItem(key)", key), Is.Null,
            "Discard deletes the backup key");
    }

    [Test]
    public async Task Saving_ClearsTheBackupKeys()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"New draft {RunId}");
        var body = $"New draft body {RunId} saved right away.";
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(body);
        await WaitForBackupAsync(body, "new");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });

        Assert.That(await DraftKeysAsync(), Is.Empty,
            "a successful save clears the backup — under 'new' and under the id alike");
    }

    [Test]
    public async Task AfterTheFirstSave_NewEditsBackUpUnderThePostId_NotNew()
    {
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);

        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync($"Moved key {RunId}");
        var body = $"Moved key body {RunId} saved first.";
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(body);
        await WaitForBackupAsync(body, "new");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
        var id = PostIdFromEditorUrl();

        var textarea = Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true });
        await textarea.PressAsync("z");
        await WaitForBackupAsync(body + "z", id.ToString());

        Assert.That(await Page.EvaluateAsync<string?>("() => localStorage.getItem('croft-draft:new')"), Is.Null,
            "the 'new' key is gone after the first save; the backups moved to the post id");
        Assert.That(await Page.EvaluateAsync<string?>(
            "(key) => localStorage.getItem(key)", $"croft-draft:{id}"), Is.Not.Null,
            "new edits back up under the created post's id");
    }

    [Test]
    public async Task ABackupIdenticalToTheSavedText_IsDeletedSilently_WithNoPrompt()
    {
        var (id, serverTitle, serverBody) = await CreateAPostAsync("Draft unchanged");
        await LoginAsync("admin@example.com", "password");

        // An unchanged backup: exactly the saved text, typed back after an edit (its time is
        // newer than the save). It is the brief's only silent delete — no prompt either way.
        // Seeded before the editor is opened, so the one load check under test is the one that
        // meets it (a check that ran late on a previous page could steal it first).
        var key = $"croft-draft:{id}";
        await Page.EvaluateAsync(
            """
            args => localStorage.setItem(args.key, JSON.stringify({
                title: args.title, body: args.body, savedStamp: '', time: new Date().toISOString()
            }))
            """,
            new { key, title = serverTitle, body = serverBody });

        await OpenEditorAsync(id);

        // The load check's outcome: the identical backup is gone (the brief's only silent
        // delete) — or, wrongly, a prompt asks about text that never changed.
        await Page.WaitForFunctionAsync(
            """
            key => localStorage.getItem(key) === null || document.querySelector('.draft-restore') !== null
            """,
            key,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        var promptText = await Page.EvaluateAsync<string>(
            "() => { const p = document.querySelector('.draft-restore'); return p ? p.textContent : 'NO PROMPT'; }");
        Assert.That(await Page.EvaluateAsync<string?>("(key) => localStorage.getItem(key)", key), Is.Null,
            $"the identical backup is deleted silently; prompt=[{promptText}]");
        await Expect(Page.Locator(".draft-restore")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(serverBody);
    }

    [Test]
    public async Task BodyAtTheContentLimit_RoundTripsThroughTheBackup()
    {
        var (id, _, serverBody) = await CreateAPostAsync("Draft 200k");
        await LoginAsync("admin@example.com", "password");
        await OpenEditorAsync(id);

        var fill = LimitText();
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(fill);
        var key = await WaitForBackupAsync(fill);

        await Page.ReloadAsync();
        await WaitForInteractiveAsync(Page);

        var textarea = Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true });
        await Expect(textarea).ToHaveValueAsync(serverBody);
        var prompt = Page.Locator(".draft-restore");
        await Expect(prompt).ToBeVisibleAsync();
        await prompt.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Restore", Exact = true }).ClickAsync();

        // Rule 7: exactly the typed text — length first, then the content.
        await Page.WaitForFunctionAsync(
            "(expected) => { const el = document.querySelector('textarea.md-editor__textarea'); return el && el.value.length === expected; }",
            PostContent.MaxLength,
            new PageWaitForFunctionOptions { Timeout = 30000 });
        var value = await textarea.InputValueAsync();
        Assert.That(value.Length, Is.EqualTo(PostContent.MaxLength), "the 200,000-character body survives the backup");
        Assert.That(value, Is.EqualTo(fill), "the backup round-trips exactly the typed text");
    }

    [Test]
    public async Task Logout_ClearsEveryDraftBackupKey()
    {
        var (id, _, _) = await CreateAPostAsync("Draft logout");
        await LoginAsync("admin@example.com", "password");
        await OpenEditorAsync(id);

        var body = $"Logout body {RunId} must not survive sign-out.";
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(body);
        await WaitForBackupAsync(body);

        await Page.Locator("#sidebar-nav a[href='/logout']").ClickAsync();

        // The sign-out still ends at the blog's home page, logged out — and a shared computer
        // keeps no drafts: not the edited post's, not any other.
        await Expect(Page.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync();
        Assert.That(Page.Url, Is.EqualTo($"{BaseUrl}/"), $"Logout did not end at the blog's home page: {Page.Url}");
        Assert.That(await DraftKeysAsync(), Is.Empty,
            "no croft-draft: key may remain after sign-out");
    }

    /// <summary>
    /// A browser that denies storage access (cookies/storage disabled, privacy extensions) makes
    /// the `localStorage` property getter itself throw — and that must degrade the backup layer,
    /// not kill the editor: with every storage access throwing, the editor still loads and saves
    /// the typed text over a living circuit (a later Add tag round-trip through it renders the
    /// chip), and sign-out still completes.
    /// </summary>
    [Test]
    public async Task StorageAccessThrowing_EditorStillSavesTheTypedText_OverALivingCircuit()
    {
        await BlockStorageAccessAsync();
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/new");
        await WaitForInteractiveAsync(Page);

        var title = $"Storage blocked {RunId}";
        var body = $"Storage blocked body {RunId} reaches the server.";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).FillAsync(body);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });

        var id = PostIdFromEditorUrl();
        var (serverTitle, serverBody) = await ServerPostAsync(id);
        Assert.That(serverTitle, Is.EqualTo(title), "the typed title must reach the server although storage access throws");
        Assert.That(serverBody, Is.EqualTo(body), "the typed body must reach the server although storage access throws");

        // The circuit is still alive after the save's backup-clearing interop: an Add tag click
        // must round-trip through it and render the chip (a dead circuit drops the click).
        await AddTagsAsync($"storage-{RunId}");
        await Expect(Page.Locator(".post-form__tag", new PageLocatorOptions { HasText = $"storage-{RunId}" }))
            .ToBeVisibleAsync();
    }

    /// <summary>With storage access throwing, the sign-out interstitial must still complete the
    /// sign-out instead of dying on its draft-clearing loop.</summary>
    [Test]
    public async Task StorageAccessThrowing_LogoutStillCompletesSignOut()
    {
        await BlockStorageAccessAsync();
        await LoginAsync("admin@example.com", "password");
        await Page.Locator("#sidebar-nav a[href='/logout']").ClickAsync();

        await Page.WaitForURLAsync(url => url == $"{BaseUrl}/", new PageWaitForURLOptions { Timeout = 30000 });
        await Expect(Page.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync();
    }

    // WORKAROUND(W-067, Chromium): relies on Chromium letting an own throwing getter shadow the configurable localStorage accessor
    /// <summary>
    /// Makes every later `localStorage` access throw before any page script runs, in every
    /// navigation of the page — the shape a storage-denying browser has: the property getter
    /// itself throws a SecurityError. Chromium allows the redefinition (`window.localStorage`
    /// is a configurable accessor, so an own throwing getter shadows it), which these tests'
    /// RED runs on unguarded code also prove: the throw is what kills the flow there.
    /// </summary>
    private Task BlockStorageAccessAsync() =>
        Page.AddInitScriptAsync(
            """
            Object.defineProperty(window, 'localStorage', {
                configurable: true,
                get() { throw new DOMException('The storage is disabled.', 'SecurityError'); }
            });
            """);
    // END WORKAROUND(W-067)

    /// <summary>The post id in the editor URL and the server's saved text, from the mock admin's
    /// own draft (each test creates its own — the suite's fixture rule).</summary>
    private async Task<(Guid Id, string Title, string Body)> CreateAPostAsync(string prefix)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var title = $"{prefix} {RunId}";
        var body = $"Server body for {prefix} {RunId}.";
        var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest(title, body), token);
        return (Guid.Parse(post.RootElement.GetProperty("id").GetString()!), title, body);
    }

    private async Task OpenEditorAsync(Guid id)
    {
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
    }

    /// <summary>Waits on the concrete outcome: the debounced backup holds exactly the expected
    /// body text (under the given key suffix when given, else under whichever key carries it).
    /// Returns its localStorage key.</summary>
    private async Task<string> WaitForBackupAsync(string expectedBody, string? keySuffix = null)
    {
        await Page.WaitForFunctionAsync(
            """
            args => Object.keys(localStorage)
                .filter(k => k.startsWith('croft-draft:'))
                .some(k => (args.key === null || k === 'croft-draft:' + args.key)
                    && JSON.parse(localStorage.getItem(k)).body === args.body)
            """,
            new { key = keySuffix, body = expectedBody },
            new PageWaitForFunctionOptions { Timeout = 30000 });

        return keySuffix is null
            ? await FindKeyByBodyAsync(expectedBody)
            : $"croft-draft:{keySuffix}";
    }

    private async Task<string> FindKeyByBodyAsync(string expectedBody) =>
        await Page.EvaluateAsync<string>(
            """
            body => Object.keys(localStorage).find(
                k => k.startsWith('croft-draft:') && JSON.parse(localStorage.getItem(k)).body === body)
            """,
            expectedBody);

    private async Task<string> BackupLocalTimeAsync(string key) =>
        await Page.EvaluateAsync<string>(
            "(key) => new Date(JSON.parse(localStorage.getItem(key)).time).toLocaleString()", key);

    private async Task<double> BackupTimeMillisAsync(string key) =>
        await Page.EvaluateAsync<double>(
            "(key) => new Date(JSON.parse(localStorage.getItem(key)).time).getTime()", key);

    private async Task<double> BackupSavedStampMillisAsync(string key) =>
        await Page.EvaluateAsync<double>(
            "(key) => new Date(JSON.parse(localStorage.getItem(key)).savedStamp).getTime()", key);

    private async Task<List<string>> DraftKeysAsync() =>
        await Page.EvaluateAsync<List<string>>("() => Object.keys(localStorage).filter(k => k.startsWith('croft-draft:'))");

    /// <summary>The server's saved version stamp (contentEditedAt, falling back to updatedAt) as
    /// a millisecond instant, so the stored stamp's string format is not pinned.</summary>
    private async Task<double> ServerStampAsync(Guid id)
    {
        using var doc = await ServerGetAsync(id);
        var stamp = doc.RootElement.TryGetProperty("contentEditedAt", out var edited) && edited.ValueKind == JsonValueKind.String
            ? edited.GetString()
            : doc.RootElement.GetProperty("updatedAt").GetString();
        return new DateTimeOffset(DateTime.Parse(stamp!, null, System.Globalization.DateTimeStyles.RoundtripKind)).ToUnixTimeMilliseconds();
    }

    private async Task<(string Title, string Body)> ServerPostAsync(Guid id)
    {
        using var doc = await ServerGetAsync(id);
        return (doc.RootElement.GetProperty("title").GetString()!, doc.RootElement.GetProperty("markdownContent").GetString()!);
    }

    private async Task<JsonDocument> ServerGetAsync(Guid id)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/api/posts/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"the admin API must return the post {id}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Newline-free text of exactly the content limit's length (one FillAsync input
    /// event), run-keyed so no other test's text can match it.</summary>
    private static string LimitText()
    {
        var pattern = $"200k draft {RunId} the quick brown fox jumps over the lazy dog. ";
        var text = new StringBuilder(PostContent.MaxLength + pattern.Length);
        while (text.Length < PostContent.MaxLength)
        {
            text.Append(pattern);
        }

        return text.ToString(0, PostContent.MaxLength);
    }
}
