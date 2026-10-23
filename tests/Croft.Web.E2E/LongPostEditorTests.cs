using System.Text;

namespace Croft.Web.E2E;

/// <summary>
/// Long posts through the Markdown editor: one input event carries the WHOLE textarea value to
/// the server, so every fill, paste and keystroke must survive up to the 200,000-character
/// content limit (Croft.Shared.PostContent) — and no text may ever be lost: over the limit the
/// editor warns (role="alert"), the count line shows used / limit, and the save actions are
/// disabled, while the textarea keeps every character.
///
/// The fills are newline-free on purpose: Playwright's FillAsync fires exactly one input event
/// only for newline-free text (newline values burst into one event per line and hang above
/// ~22K characters — measured against this code before the fix), so FillAsync(N) is one input
/// event of N characters. The 20-keystroke test uses PressSequentiallyAsync: one input event per
/// key, each carrying the whole grown text.
///
/// "No reconnect" is asserted on the circuit's WebSocket through a tracker installed before any
/// navigation (hence before every fill): the `_blazor` connection is never closed and never
/// replaced. The `#components-reconnect-modal` MutationObserver is installed before the fill too
/// and asserted: App.razor renders ReconnectModal.razor, so the dialog exists and opens on a
/// circuit drop. The connection check stays the primary outcome; the modal is a second sight of
/// the same failure.
///
/// Fixtures (the suite's rule): each test creates its own run-keyed post through the admin UI.
/// The save of an existing post has no browser response to await (AdminTests'
/// Editor_EditSubtitle_Save_ShowsItOnThePublicPost rule), so it is awaited on the outcome: the
/// server-served editor page starts carrying the typed keys (synchronous XHR inside
/// WaitForFunction).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class LongPostEditorTests : WebTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Exactly 20 characters: the one-key-at-a-time suffix of the keystroke test.</summary>
    private static string TypedKeys => $"zzzz{RunId}qqqq";

    [SetUp]
    public async Task TrackTheCircuitConnection()
    {
        // Before any navigation, so before every fill: wrap WebSocket and count the circuit's
        // connections and closes. A killed circuit (the pre-fix behaviour on one big input
        // event) closes this socket; a reconnect opens a second one.
        await Page.Context.AddInitScriptAsync(
            """
            (() => {
                window.__circuit = { opened: 0, closed: 0 };
                // The reconnect-modal observer, installed in every document this test opens (the
                // editor is reloaded several times per test): App.razor renders the dialog, so
                // this fires if the circuit drops and the modal opens. __reconnectWatching proves
                // the observer is really on the dialog in the current document.
                window.__reconnectModalOpened = false;
                window.__reconnectWatching = false;
                const watchModal = () => {
                    const modal = document.getElementById('components-reconnect-modal');
                    if (!modal) return;
                    window.__reconnectWatching = true;
                    if (modal.open) window.__reconnectModalOpened = true;
                    new MutationObserver(() => { if (modal.open) window.__reconnectModalOpened = true; })
                        .observe(modal, { attributes: true, attributeFilter: ['open'] });
                };
                if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', watchModal);
                else watchModal();
                const Original = window.WebSocket;
                window.WebSocket = class extends Original {
                    constructor(url, protocols) {
                        super(url, protocols);
                        if (String(url).includes('_blazor')) {
                            window.__circuit.opened += 1;
                            this.addEventListener('close', () => { window.__circuit.closed += 1; });
                        }
                    }
                };
            })();
            """);
    }

    [Test]
    public async Task Editor_20KFillPlusOneKey_SavesAndReloads_TheExactText_WithoutReconnect()
    {
        var (textarea, fill) = await OpenNewPostWithBodyAsync("07e 20K", FillText(20_000));

        await textarea.PressAsync("z");
        await AssertExactTextareaAsync(fill + "z", "after the 20K fill and the extra keystroke");
        await Expect(Page.Locator(".md-editor__count")).ToHaveCountAsync(0);
        await AssertNoReconnectAsync("after the 20K fill and the extra keystroke");

        await SaveDraftAndWaitForEditorAsync();
        var postId = PostIdFromEditorUrl();
        await AssertNoReconnectAsync("after saving the 20K draft");

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(fill + "z", "after reload: length and content of the stored text");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    [Test]
    public async Task Editor_60KFillPlusOneKey_SavesAndReloads_TheExactText_WithoutReconnect()
    {
        var (textarea, fill) = await OpenNewPostWithBodyAsync("07e 60K", FillText(60_000));

        await textarea.PressAsync("z");
        await AssertExactTextareaAsync(fill + "z", "after the 60K fill and the extra keystroke");
        await AssertNoReconnectAsync("after the 60K fill and the extra keystroke");

        await SaveDraftAndWaitForEditorAsync();
        var postId = PostIdFromEditorUrl();
        await AssertNoReconnectAsync("after saving the 60K draft");

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(fill + "z", "after reload: length and content of the stored text");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    [Test]
    public async Task Editor_WorstCaseEuro60KFillPlusOneKey_SavesAndReloads_TheExactText()
    {
        // The worst-case text (measured): all-`€` at 6 net hub-message bytes per character
        // (3 UTF-8 bytes × the value's two occurrences in one input event) — 60,001 × 6 ≈ 360KB
        // in one message.
        var (textarea, fill) = await OpenNewPostWithBodyAsync("07e Euro 60K", new string('€', 60_000));

        await textarea.PressAsync("z");
        await AssertExactTextareaAsync(fill + "z", "after the € fill and the extra keystroke");
        await AssertNoReconnectAsync("after the € fill and the extra keystroke");

        await SaveDraftAndWaitForEditorAsync();
        var postId = PostIdFromEditorUrl();
        await AssertNoReconnectAsync("after saving the € draft");

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(fill + "z", "after reload: length and content of the stored text");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    [Test]
    public async Task Editor_TwentyKeysTypedIntoASaved60KPost_EachSurvives_AndTheStoredTextEndsWithThem()
    {
        var (textarea, fill) = await OpenNewPostWithBodyAsync("07e 60K Keys", FillText(60_000));

        await SaveDraftAndWaitForEditorAsync();
        var postId = PostIdFromEditorUrl();

        // One key at a time into the grown post: every keystroke is its own input event carrying
        // the whole ~60K text to the server (the pre-fix bug killed the circuit on each).
        await textarea.PressSequentiallyAsync(TypedKeys);
        await AssertExactTextareaAsync(fill + TypedKeys, "after the twenty keystrokes");
        await AssertNoReconnectAsync("after twenty keystrokes in the 60K post");

        // The save of an existing post travels over the circuit with no browser response to
        // await, so wait on the outcome: the server-served editor page carries the typed keys.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForFunctionAsync(
            $@"keys => {{
                const request = new XMLHttpRequest();
                request.open('GET', location.pathname, false);
                request.send();
                return request.responseText.includes(keys);
            }}",
            TypedKeys,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(fill + TypedKeys, "after reload: the stored text ends with the twenty typed keys");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    [Test]
    public async Task Editor_200KEuroFill_SavesAndReloads_TheExactText_WithoutReconnect()
    {
        // The hub limit AT the content limit with the worst-case text: 200,000 all-`€`
        // characters in ONE input event — 164 + 2 × 3 × 200,000 ≈ 1,200,164 hub-message bytes,
        // which 2,097,152 carries and 2^20 (1,048,576) cannot.
        var fill = new string('€', 200_000);
        var (textarea, _) = await OpenNewPostWithBodyAsync("07e Euro 200K", fill);

        await AssertExactTextareaAsync(fill, "after the 200K € fill");
        await AssertNoReconnectAsync("after the 200K € fill");

        await SaveDraftAndWaitForEditorAsync();
        var postId = PostIdFromEditorUrl();
        await AssertNoReconnectAsync("after saving the 200K € draft");

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(fill, "after reload: exactly the 200,000 € characters");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    [Test]
    public async Task Editor_200KFillPlusOneKey_AlertsAndDisablesSave_DeleteOneSavesThe200K()
    {
        // The 200K flow: one input event of 200,000 characters, then one typed character over
        // the limit (200,001: alert, Save disabled, every character kept, no reconnect), then
        // one Backspace back to the limit (alert gone, Save enabled) and the save persists
        // exactly the 200,000 fill characters.
        var (textarea, fill) = await OpenNewPostWithBodyAsync("07e 200K", FillText(200_000));

        await textarea.PressAsync("z");
        await AssertExactTextareaAsync(fill + "z", "at 200,001 characters the textarea still holds everything");
        await Expect(Page.Locator(".md-editor__count")).ToHaveTextAsync("200,001 / 200,000 characters");
        var alert = Page.Locator(".md-editor__over");
        await Expect(alert).ToBeVisibleAsync();
        await Expect(alert).ToHaveAttributeAsync("role", "alert");
        var save = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" });
        await Expect(save).ToBeDisabledAsync();
        await AssertNoReconnectAsync("after the 200K fill and the extra keystroke");

        await textarea.PressAsync("Backspace");
        await AssertExactTextareaAsync(fill, "after deleting one character");
        await Expect(alert).ToHaveCountAsync(0);
        await Expect(Page.Locator(".md-editor__count")).ToHaveTextAsync("200,000 / 200,000 characters");
        await Expect(save).ToBeEnabledAsync();
        await AssertNoReconnectAsync("after deleting the over-limit character");

        await save.ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
        var postId = PostIdFromEditorUrl();

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(fill, "after reload: exactly the 200,000 fill characters");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    [Test]
    public async Task Editor_200001Characters_AlertShows_SaveDisabled_TextKept_DeleteOneSaveWorks()
    {
        // The dedicated over-limit test: 200,001 characters in one input event — the alert
        // shows, Save is disabled and the textarea still holds all 200,001 characters; delete
        // one: the alert goes and Save works (and stores the remaining 200,000).
        var text = FillText(200_001);
        var (textarea, _) = await OpenNewPostWithBodyAsync("07e 200,001", text);

        await AssertExactTextareaAsync(text, "at 200,001 characters");
        var alert = Page.Locator(".md-editor__over");
        await Expect(alert).ToBeVisibleAsync();
        await Expect(alert).ToHaveAttributeAsync("role", "alert");
        var save = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" });
        await Expect(save).ToBeDisabledAsync();
        await AssertNoReconnectAsync("after the 200,001-character fill");

        await textarea.PressAsync("Backspace");
        await Expect(alert).ToHaveCountAsync(0);
        await Expect(save).ToBeEnabledAsync();

        await save.ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
        var postId = PostIdFromEditorUrl();

        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);
        await AssertExactTextareaAsync(text[..^1], "after reload: the 200,000 kept characters were saved");
        await AssertNoReconnectAsync("after reloading the editor");
    }

    // WORKAROUND(W-065, Playwright): FillAsync fires one input event only for newline-free text, so the fills have no newlines
    /// <summary>
    /// Newline-free fill text of exactly <paramref name="chars"/> characters, run-keyed unique.
    /// Newline-free on purpose: FillAsync is exactly one input event only without newlines.
    /// </summary>
    private static string FillText(int chars)
    {
        var pattern = $"Long post fixture {RunId} the quick brown fox jumps over the lazy dog. ";
        var text = new StringBuilder(chars + pattern.Length);
        while (text.Length < chars)
        {
            text.Append(pattern);
        }

        return text.ToString(0, chars);
    }
    // END WORKAROUND(W-065)

    /// <summary>Logs in, opens the new-post editor and fills title and body (the body in one
    /// FillAsync = one input event). </summary>
    private async Task<(ILocator Textarea, string Fill)> OpenNewPostWithBodyAsync(string titlePrefix, string body)
    {
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();

        var title = $"{titlePrefix} {RunId}";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();

        var textarea = Page.GetByLabel("Markdown", new PageGetByLabelOptions { Exact = true });
        await textarea.FillAsync(body);
        return (textarea, body);
    }

    /// <summary>Saves the new draft and waits for the save's outcome: the editor navigates to
    /// /admin/posts/{id}/edit only after the create has completed.</summary>
    private async Task SaveDraftAndWaitForEditorAsync()
    {
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 30000 });
        await WaitForInteractiveAsync(Page);
    }

    /// <summary>The circuit must never have dropped or reconnected: its one WebSocket is still
    /// open, and the reconnect modal never opened.</summary>
    private async Task AssertNoReconnectAsync(string when)
    {
        // Without this the next check is vacuous: "not true" holds for an observer that was never installed.
        var watching = await Page.EvaluateAsync<bool>("() => window.__reconnectWatching === true");
        Assert.That(watching, Is.True, $"{when}: the reconnect-modal observer must be installed on this document's dialog.");
        var modalOpened = await Page.EvaluateAsync<bool>("() => window.__reconnectModalOpened === true");
        Assert.That(modalOpened, Is.False, $"{when}: the reconnect modal must never open.");

        var opened = await Page.EvaluateAsync<int>("() => window.__circuit ? window.__circuit.opened : -1");
        var closed = await Page.EvaluateAsync<int>("() => window.__circuit ? window.__circuit.closed : -1");
        Assert.That(opened, Is.EqualTo(1), $"{when}: the circuit must still be the one connection it started with.");
        Assert.That(closed, Is.EqualTo(0), $"{when}: the circuit connection must never close (a reconnect loses the text).");
    }

    /// <summary>Rule 7: the textarea holds exactly the expected text — length and content —
    /// waited on the length first so a slow server round-trip is not a false failure.</summary>
    private async Task AssertExactTextareaAsync(string expected, string when)
    {
        await Page.WaitForFunctionAsync(
            @"expected => {
                const t = document.querySelector('textarea.md-editor__textarea');
                return t !== null && t.value.length === expected.length;
            }",
            expected,
            new PageWaitForFunctionOptions { Timeout = 30000 });

        var actual = await Page.Locator("textarea.md-editor__textarea").InputValueAsync();
        Assert.That(actual.Length, Is.EqualTo(expected.Length),
            $"{when}: the textarea must hold exactly {expected.Length:N0} characters.");
        var mismatch = 0;
        while (mismatch < actual.Length && actual[mismatch] == expected[mismatch])
        {
            mismatch++;
        }

        Assert.That(actual, Is.EqualTo(expected),
            $"{when}: the text must be exactly the input text (length and content); first difference at index {mismatch}.");
    }
}
