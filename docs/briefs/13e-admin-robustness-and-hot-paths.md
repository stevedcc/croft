# Brief 13e: admin robustness and hot-path costs (.NET review)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-dotnet-code-review.md`: I-2, I-3, I-4, I-5 (the editor part),
I-8, I-9, M-10, M-11, M-13, M-16, M-19, M-21. Read each before its item; it has file:line and the
reasoning.

**Owner-visible outcome:** an API hiccup or a too-long name never crashes the admin; admin pages
load their data once; image requests and every response cost less work; the editor's preview
doesn't re-render Markdown for each keystroke; client disconnects stop showing up as errors.

**CSP impact:** none in policy. Item 3 changes how the header is built, not what it says:
`CspSnapshots.txt` must be byte-identical before and after.

## Item 1: the admin survives API failures and bad input (I-2, I-3, M-10, M-21)
- `TagsPage` (and every admin page with the same pattern — search for it): any API failure, not
  only `SessionExpiredException`, is caught, logged at Warning, and shown in the page's existing
  error UI; the circuit keeps running.
- `CategoryService.CreateAsync` and `TagService.CreateAsync` (and their update paths) validate like
  the post paths: name required, max length from the EF configuration, slug rules; violations are
  400 with a problem-details message the admin shows, never a database error and a 500.
- `FeedbackService`: `QuotedText` and `Comment` get length limits (decide from the UI; validate in
  the API, 400 above; columns: pending contract, not this release).
- `MainLayout` logs the API failures it swallows (Warning, once per failure kind), no behaviour
  change otherwise.
Tests: a failing API on the tags page shows the error UI and the circuit stays connected (bUnit or
E2E); a 129-char category or tag name → 400 with a message; over-long feedback → 400.
Break-it-once: narrow the catch back to SessionExpiredException → the tags-page test fails.

## Item 2: admin pages load once (I-8)
- Every admin page that loads data in `OnInitializedAsync` uses `[PersistentState]` with the `??=`
  pattern (AGENTS.md "Interactivity", skill `blazor-support-prerendering`), so a full page load
  fetches each piece of data once, not twice.
Test: an E2E or integration test counts API calls for a full load of each admin page (posts,
pages, tags & categories, media, a post's edit page): one per resource. Break-it-once: remove the
attribute from one page → its count doubles and the test fails.

## Item 3: the CSP header is built once per kind (I-4)
- Precompute the final header strings per document kind (and per the few per-request variants that
  exist) at startup or on first use; a response picks its string instead of running the
  contributors. Static assets get their (static) header string with no per-request work.
- Keep the contributors as the single source the precomputed strings come from.
Tests: `CspSnapshots.txt` unchanged (byte-identical); a benchmark-style test or a counter shows the
contributors run once per kind, not per response. Break-it-once: bypass the cache → the counter
test fails.

## Item 4: cheaper image requests (I-9)
- `MediaController.Get` reads only what it needs to authorise the file (the owning post's status,
  via a projection), never the whole `Post` row with its Markdown and search vector.
Test: the SQL for a media GET selects no Markdown/search-vector column (EF's logging in the test,
or a query-shape assertion); access rules unchanged (draft media still needs staff).

## Item 5: the editor renders Markdown when it changes (I-5, editor part)
- The editor's preview renders Markdown when the text changes (debounced, e.g. 150 ms), not in a
  property getter that runs on every render. The anonymous post page's rendering cost belongs to
  the output-cache brief, not here.
Test: typing N characters causes at most N/2 renders within the debounce window (bUnit with a fake
clock), and the preview shows the final text.

## Item 6: smaller correctness items (M-11, M-13, M-16, M-19)
- M-11: `LocalFileStorage`'s path check uses a full-path containment check (`Path.GetFullPath` +
  root with trailing separator), with traversal tests.
- M-13: a client disconnect (`OperationCanceledException` with the request aborted) is not logged
  as an unhandled 500 (Debug or nothing).
- M-16: `BlogApiClient` stops mutating `DefaultRequestHeaders` (per-request headers instead) and
  disposes its responses.
- M-19: the back-channel logout validator checks the issuer against the discovered metadata issuer.
Tests for each; break-it-once for M-11 (`..` traversal must fail the test when the check is loosened).

## Out of scope
- Output caching and the anonymous Markdown render (later brief).
- The other minors of the .NET review (a collective brief later).
- Anything in 13a/13b/13c/13d.
