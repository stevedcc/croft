namespace Croft.Web.E2E;

/// <summary>
/// Editor-role pages (spec §6.6): /editor lists drafts and scheduled posts with Review actions,
/// and /editor/posts/{slug} renders the draft with the real post layout so the editor can select
/// text, add a note and see it in the rail — which the admin then resolves in the post editor's
/// Feedback tab. Fixtures are created through the admin UI (never unauthenticated API calls).
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class EditorTests : WebTest
{
    /// <summary>The word the feedback flow selects; rendered bold so the selection has one exact box.</summary>
    private const string QuotedWord = "hillsideword";

    private const string Comment = "Needs a source for this claim.";

    [Test]
    public async Task DraftReview_SelectionToFeedback_NoteAppearsInTheRail_AndAdminResolvesIt()
    {
        // Fixture: a draft of its own, created through the admin editor. (The slug comes from the
        // Review link further down: the public API cannot see drafts anonymously.)
        await LoginAsync("admin@example.com", "password");
        var title = $"R51 Feedback {Guid.NewGuid():N}";
        await CreateDraftAsync(title, $"The **{QuotedWord}** waits for the rain.");
        var postId = PostIdFromEditorUrl();

        // The editor's list shows the draft and its Review action leads to the review page.
        await LogoutAsync();
        await LoginAsync("editor@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/editor");
        await WaitForInteractiveAsync(Page);
        var row = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/editor/posts/"),
            new PageWaitForURLOptions { Timeout = 15000 });

        // The draft renders with the real post layout and a Draft ribbon.
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
        var word = Page.Locator(".markdown-body strong");
        await Expect(word).ToHaveTextAsync(QuotedWord);
        // Rule-5-safe input addition (assertions untouched): wait for the selection watch to be
        // live before selecting — it registers with the interactive render, and a selection
        // gesture before that is never reported (the R3.6 prerender race class).
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.selectionWatch === 'on'");

        // Select the word: the "Add feedback" button appears at the selection.
        await word.DblClickAsync();
        var addFeedback = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add feedback" });
        await Expect(addFeedback).ToBeVisibleAsync();
        await addFeedback.ClickAsync();

        // The selection travels into the note; once added the note is in the rail (rule 7: the
        // outcome is the note carrying the quote, not just that a form exists).
        await Page.GetByLabel("Comment").FillAsync(Comment);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add", Exact = true }).ClickAsync();

        var note = Page.Locator(".feedback-panel__open .feedback-note", new PageLocatorOptions { HasText = Comment });
        await Expect(note).ToBeVisibleAsync();
        await Expect(note.Locator(".feedback-note__quote")).ToHaveTextAsync(QuotedWord);

        // The admin resolves it in the post editor's Feedback tab; it moves under "Resolved".
        await LogoutAsync();
        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit?tab=feedback");
        await WaitForInteractiveAsync(Page);
        var adminNote = Page.Locator(".feedback-note", new PageLocatorOptions { HasText = Comment });
        await Expect(adminNote).ToBeVisibleAsync();
        await adminNote.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resolve" }).ClickAsync();

        await Expect(Page.Locator(".feedback-panel__resolved summary")).ToHaveTextAsync("Resolved (1)");
        await Page.Locator(".feedback-panel__resolved summary").ClickAsync();
        await Expect(Page.Locator(".feedback-panel__resolved .feedback-note", new PageLocatorOptions { HasText = Comment }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task DraftReview_UnknownSlug_Returns404WithNotFoundCopy()
    {
        // The review page needs the same real 404 as the public pages (spec §5.5) — asserted on
        // the raw GET response while logged in (the route is Editor/Admin only).
        await LoginAsync("editor@example.com", "password");
        await using var response = await Page.APIRequest.GetAsync(
            $"{BaseUrl}/editor/posts/does-not-exist-{Guid.NewGuid():N}");

        Assert.That(response.Status, Is.EqualTo(404),
            "An unknown draft slug must answer with HTTP 404 (NavigationManager.NotFound), not a redirect.");
        var body = await response.TextAsync();
        Assert.That(body, Does.Contain("This page has wandered off"),
            "The 404 response must render the not-found copy (spec §5.5).");
    }

    [Test]
    public async Task DraftReview_HandOff_DoesNotResurrectTheButtonOnTheNextKeystroke()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R53 HandOff {Guid.NewGuid():N}";
        await CreateDraftAsync(title, $"The **{QuotedWord}** waits for the rain.");
        await LogoutAsync();
        await LoginAsync("editor@example.com", "password");
        await OpenReviewAsync(title);

        var word = Page.Locator(".markdown-body strong");
        var addFeedback = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add feedback" });
        await word.DblClickAsync();
        await Expect(addFeedback).ToBeVisibleAsync();
        await addFeedback.ClickAsync();

        // Wait for the hand-off's outcome (button gone — its click handler also clears the DOM
        // selection) before the keystroke: a keyup in the same round-trip would race the hand-off
        // itself, which is not the symptom under test.
        await Expect(addFeedback).ToHaveCountAsync(0);

        // The hand-off consumed the selection: the next keyup must not resurrect the button.
        await Page.Keyboard.PressAsync("Shift");
        await Expect(addFeedback).ToHaveCountAsync(0);

        // Positive control: the selection reporting itself still works (a fresh selection shows
        // the button again) — so the negative above is the hand-off's doing, not a dead watch.
        await word.DblClickAsync();
        await Expect(addFeedback).ToBeVisibleAsync();
    }

    [Test]
    public async Task DraftReview_SelectionButton_StaysInsideTheViewportAtPhoneWidth()
    {
        await LoginAsync("admin@example.com", "password");
        var title = $"R53 Clamp {Guid.NewGuid():N}";
        await CreateDraftAsync(title, $"The **{QuotedWord}** waits for the rain.");
        await LogoutAsync();
        await LoginAsync("editor@example.com", "password");
        await Page.SetViewportSizeAsync(390, 844);
        await OpenReviewAsync(title);

        // A real selection at the bottom-right corner is layout-dependent; force the reported
        // selection geometry there instead and let the real flow run: report → button → clamp.
        await Page.EvaluateAsync(@"() => {
            const original = Range.prototype.getBoundingClientRect;
            Range.prototype.getBoundingClientRect = function () {
                return { x: window.innerWidth - 20, y: window.innerHeight - 20, top: window.innerHeight - 20,
                         left: window.innerWidth - 20, right: window.innerWidth, bottom: window.innerHeight,
                         width: 20, height: 20, toJSON() { return this; } };
            };
        }");
        await Page.Locator(".markdown-body strong").DblClickAsync();
        var addFeedback = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add feedback" });
        await Expect(addFeedback).ToBeVisibleAsync();

        // Rule 7 outcome: the button's real box sits fully inside the 390x844 viewport.
        var box = await addFeedback.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "the selection button must be rendered");
        Assert.Multiple(() =>
        {
            Assert.That(box!.X, Is.GreaterThanOrEqualTo(0), "the button must not overflow the viewport's left edge");
            Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(390), "the button must not overflow the right edge");
            Assert.That(box.Y, Is.GreaterThanOrEqualTo(0), "the button must not overflow the top edge");
            Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(844), "the button must not overflow the bottom edge");
        });
    }

    /// <summary>Opens the draft's review page through the Editor list — its Review link carries
    /// the slug the public API cannot reveal (drafts are invisible anonymously). Waits for the
    /// selection watch to be live: it registers with the interactive render, and a selection
    /// gesture before that is simply never reported.</summary>
    private async Task OpenReviewAsync(string title)
    {
        await Page.GotoAsync($"{BaseUrl}/editor");
        await WaitForInteractiveAsync(Page);
        var row = Page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/editor/posts/"),
            new PageWaitForURLOptions { Timeout = 15000 });
        await Expect(Page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.selectionWatch === 'on'");
    }

    /// <summary>Saves a post as a draft through the admin editor (ruling R4-auth: fixtures go
    /// through the admin UI, never unauthenticated API calls).</summary>
    private async Task CreateDraftAsync(string title, string body)
    {
        await OpenNewPostEditorAsync();
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync(body);
        await Page.GetByLabel("Markdown").BlurAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Page.WaitForURLAsync(
            url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
            new PageWaitForURLOptions { Timeout = 15000 });
    }

    private async Task LogoutAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/logout");
        await Expect(Page.GetByText("Log in")).ToBeVisibleAsync();
    }
}
