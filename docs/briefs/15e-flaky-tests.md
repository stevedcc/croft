# Brief 15e: the three tests that flaked in CI on 2026-10-24 (issues #43, #49, #51)

Read AGENTS.md first and follow it. One commit per item. Use the `systematic-debugging` skill for
each: find the cause, then fix the test (or the product, if the test is right and the product has a
race). **Never** fix a flake with `Task.Delay`, a sleep, a longer timeout, a retry loop or a looser
assertion (AGENTS.md rule 6 and the `systematic-debugging` skill); bUnit's `WaitForAssertionAsync`
/ `WaitForState` and Playwright's auto-waiting `Expect` are the proper primitives, not waits.
No pushes except your branch `brief/15e` to `origin` and ONE draft PR; E2E instances 9..10 are
yours (`dev/run-e2e.sh --instances 2 --first-instance 9`).

Each failed exactly once on the GitHub runner today, on a tree that had passed minutes earlier, under
six parallel CI runs (slow runners). So each is timing-dependent; your job is to say precisely which
timing, reproduce it deterministically where possible, and remove the dependency.

## Item 1 (#51): `PostEditPageTests.TypingInAField_ClearsItsError_AndOnlyItsOwn` (bUnit)
`tests/Croft.Web.Tests/Components/PostEditPageTests.cs:716`. Failed with "Did not expect cut.Markup
… to contain 'MastodonUrl must be a post on'" at line 732, after 291 ms. The final assertion follows
`cut.Find("#post-mastodon-url").Input(...)` synchronously; if the handler that clears the error is
async (or re-renders through `InvokeAsync`), the assertion can run before the render. Confirm by
reading the handler in `PostEditPage.razor` / `PostEditorForm.razor` (find where the field error is
cleared) and, if you can, by making the handler artificially slow in a probe (not committed) so the
test fails every time. Fix the test with bUnit's `WaitForAssertionAsync` on the negative assertion
(the same primitive the test already uses at line 726), or make the handler synchronous if nothing
in it needs to be async; say which and why. Check the sibling tests in the class for the same
pattern (a synchronous `Markup.Should()` right after an `Input`/`Click` whose handler is async) and
fix those too, in the same commit, listing them.

## Item 2 (#49): `CspReportTests.Report_LogsAtMost25ViolationsPerReport_AndSummarizesTheRest`
`tests/Croft.Web.Tests/CspReportTests.cs:150`. Failed with 27 warnings instead of 26: the extra one
was Data Protection's "No XML encryptor configured. Key {…} may be persisted to storage in
unencrypted form." landing during the test. The assertion counts every Warning the app logs. Fix:
count only the CSP-report logger's warnings (filter `logs.Warnings()` by category or event id; read
`CapturingLogger` and the CSP report endpoint's logger category), and keep the exact 26 / 25 + 1
assertions. Check the other tests in the class and in the suite that assert on `Warnings()` counts
without a category filter (grep), fix them the same way, list them.

## Item 3 (#43): `AdminTests.PostsList_FilterByStatus_AndSearchByTitle` (E2E)
`tests/Croft.Web.E2E/AdminTests.cs:540-542`. After clicking the "Draft" filter chip, `Expect(RowFor(publishedTitle)).ToHaveCountAsync(0)` failed: the published row stayed for the whole 5 s
(14 polls). The draft row's `ToBeVisibleAsync` just before it passed, so the page showed both rows,
i.e. the unfiltered list: the click did not filter. Most likely cause: the chip was clicked before
the Blazor circuit was interactive (the admin pages prerender; a click on the prerendered markup is
lost), so the page stayed unfiltered, or the chip's handler ran but the grid's re-render raced the
assertion. Find how the suite waits for interactivity elsewhere (grep the E2E helpers for the
pattern used after `GotoAsync` on admin pages, e.g. a `data-interactive`/circuit-ready wait in
`WebTest.cs` or `AdminTests`) and whether this test skips it; reproduce with a throttled CPU
(`Page.Context.NewCDPSessionAsync` + `Emulation.setCPUThrottlingRate` 6-8x) until the failure is
deterministic, then fix (the interactivity wait, or asserting the chip's `aria-pressed` state before
the row count). Run the test 10 times (`--filter` + a shell loop, instances 9..10) after the fix: 10/10.

## Report
Per item: the cause in one or two sentences, the fix, the red/deterministic reproduction evidence,
the 10× run for item 3, the suites run (unit classes touched; E2E AdminTests class), the PR's `ci`
run URL (`gh pr checks --watch`). What is left undone.

## Out of scope
Anything else that flakes while you work: file it as a GitHub issue with the evidence and move on.
