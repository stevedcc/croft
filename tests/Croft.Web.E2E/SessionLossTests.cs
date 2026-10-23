using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 10a item 4: losing the session in the editor costs nothing. The owner was sent to the
/// identity provider several times while writing a long post, and the page they came back to had
/// lost fields (or, loading an existing post, was a dead shell). Each test ends the admin's
/// server-side session the way the purge did (the row is deleted from the instance's Web
/// database) while the editor is open in a live circuit, then checks what the user gets back.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class SessionLossTests : WebTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task SessionEndingOnSaveDraft_BacksUpFirst_SignsInAgain_AndRestoresEveryFieldWithoutAsking()
    {
        var scenario = new EveryFieldScenario(Page, BaseUrl, ApiUrl, RunId);
        await LogDraftWritesAsync();
        await LoginAsync("admin@example.com", "password");
        await scenario.SeedAsync();
        await scenario.FillNewPostAsync();
        await scenario.WaitForBackupOfEveryFieldAsync();
        var visited = new List<string>();
        Page.FrameNavigated += (_, frame) =>
        {
            if (frame == Page.MainFrame)
            {
                visited.Add(frame.Url);
            }
        };

        Assert.That(await EndAdminSessionsAsync(), Is.GreaterThan(0), "the admin's session row must exist, and now be gone");

        // The last keystroke is typed an instant before Save, inside the backup's 500 ms debounce:
        // only a backup written at the moment the session ends can hold it. The page's pagehide
        // handler would also write one as the browser leaves, so every backup write is logged
        // (in sessionStorage, which outlives the navigation) with whether it happened while the
        // page was already leaving: the immediate write is the one that did not.
        var typedLast = scenario.Body + "Z";
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).PressAsync("End");
        await Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true }).PressAsync("Z");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();

        await SignInAgainAtTheMockIdpAsync(Page);
        var writes = JsonDocument.Parse(await Page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('e2e-draft-writes') || '[]'"));
        Assert.That(
            writes.RootElement.EnumerateArray().Any(w =>
                !w.GetProperty("leaving").GetBoolean() && w.GetProperty("body").GetString() == typedLast),
            Is.True,
            "a backup holding the last keystroke was written before the page started to leave");

        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/new") && !url.Contains("signedBackIn"),
            new PageWaitForURLOptions { Timeout = 30000 });
        await WaitForInteractiveAsync(Page);

        Assert.That(visited, Has.Some.StartWith($"{BaseUrl}/login").Or.Some.StartsWith(OAuthAuthority),
            "the browser passed through the sign-in");
        await Expect(Page.Locator(".post-edit__notice")).ToContainTextAsync(
            "Your session ended, so you signed in again. Your unsaved changes were restored.");
        await Expect(Page.Locator(".draft-restore")).ToHaveCountAsync(0);
        await scenario.AssertEveryFieldShownAsync(typedLast);
        Assert.That(Page.Url, Does.Not.Contain("signedBackIn"), "the one-shot parameter is gone from the address bar");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
        await scenario.AssertSavedAsync(PostIdFromEditorUrl(), typedLast);
    }

    [Test]
    public async Task SessionEndingWhileOpeningAnExistingPost_DoesNotKillTheCircuit_AndShowsTheSavedPostAfterSigningIn()
    {
        var title = $"Existing post {RunId}";
        var subtitle = $"Existing subtitle {RunId}";
        var description = $"Existing description {RunId}.";
        var tags = new[] { $"existing-one-{RunId}", $"existing-two-{RunId}" };
        Guid id;
        using (var client = MockIdpApi.CreateClient())
        {
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
                new Croft.Shared.Contracts.CreatePostRequest(title, $"Existing body {RunId}.", Subtitle: subtitle, Excerpt: description, Tags: tags), token);
            id = Guid.Parse(post.RootElement.GetProperty("id").GetString()!);
        }

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts");
        await WaitForInteractiveAsync(Page);
        var editLink = Page.Locator($"a[href='/admin/posts/{id}/edit']").First;
        await Expect(editLink).ToBeVisibleAsync();
        var circuitDeathsBefore = UnhandledCircuitExceptionCount();
        Assert.That(await EndAdminSessionsAsync(), Is.GreaterThan(0), "the admin's session row must exist, and now be gone");

        // Opening the post is a navigation inside the live circuit: no HTTP request runs the
        // cookie check, so the editor's own load meets the ended session.
        await editLink.ClickAsync();
        await SignInAgainAtTheMockIdpAsync(Page);
        await Page.WaitForURLAsync(url => url == $"{BaseUrl}/admin/posts/{id}/edit", new PageWaitForURLOptions { Timeout = 30000 });
        await WaitForInteractiveAsync(Page);

        await Expect(Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(title);
        await Expect(Page.GetByLabel("Subtitle", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(subtitle);
        await Expect(Page.GetByLabel("Search & link-preview description")).ToHaveValueAsync(description);
        await Expect(Page.Locator(".post-form__tag")).ToHaveCountAsync(2);
        await Expect(Page.Locator(".post-form__tag").Nth(0)).ToContainTextAsync(tags[0]);
        await Expect(Page.Locator(".post-form__tag").Nth(1)).ToContainTextAsync(tags[1]);
        Assert.That(UnhandledCircuitExceptionCount(), Is.EqualTo(circuitDeathsBefore),
            "no circuit may die of an unhandled exception while the session ends");
        // Add tag works: the circuit that loaded the page is alive.
        await AddTagsAsync($"alive-{RunId}");
        await Expect(Page.Locator(".post-form__tag", new PageLocatorOptions { HasText = $"alive-{RunId}" })).ToBeVisibleAsync();
    }

    /// <summary>
    /// Logs every draft-backup write in sessionStorage (it outlives the navigation to the identity
    /// provider), with whether the write happened while the page was already leaving.
    /// </summary>
    private Task LogDraftWritesAsync() =>
        Page.AddInitScriptAsync(
            """
            (() => {
                let leaving = false;
                addEventListener('pagehide', () => { leaving = true; }, true);
                const original = Storage.prototype.setItem;
                Storage.prototype.setItem = function (key, value) {
                    if (this === window.localStorage && key.startsWith('croft-draft:')) {
                        const log = JSON.parse(sessionStorage.getItem('e2e-draft-writes') || '[]');
                        log.push({ leaving, body: JSON.parse(value).body });
                        sessionStorage.setItem('e2e-draft-writes', JSON.stringify(log));
                    }
                    return original.apply(this, arguments);
                };
            })();
            """);
}
