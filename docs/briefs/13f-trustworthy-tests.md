# Brief 13f: trustworthy tests (test-suite review)

Read AGENTS.md first and follow it. One commit per item, break-it-once for every new or changed
key test (that is the whole point of this brief), E2E twice at the end, the final whole-branch
review.

**Source:** `docs/reviews/2026-10-19-test-suite-review.md`: C-1, I-3 to I-16, and the Minor items
listed in item 5. I-1 and I-2 are already fixed (brief 13a, commits 53b4ff8, b24ad5d). Read each
finding completely before its item; this brief decides the approach where the review offers
several.

**This brief changes tests and test infrastructure only.** Production code changes only where a
test proves a real bug — then stop, report the bug with the failing test, and do not fix it here
(it goes into its own brief). Never weaken an assertion (rule 5); where an existing test is
vacuous, make it measure the outcome and say so in the commit.

**Owner-visible outcome:** removing an `[Authorize]` attribute, a role check or an author check
anywhere makes a test fail; the known flaky tests are gone; every test this brief touches fails
when its feature breaks.

**CSP impact: none.**

## Item 1: authorisation is tested (C-1)
- Test identities: the API test auth handler supports distinct users (different `sub`, name,
  roles) per client, so author-only rules can be tested (Editor A vs Editor B vs Admin vs anonymous).
- **A denial test for every role-gated API endpoint**: for each, anonymous → 401, wrong role → 403,
  and where an author rule exists, the other Editor → 403/404 (whatever the API does — pin it), the
  author and the Admin → success. Cover post update/publish/delete, feedback create/delete
  (feedback DELETE has no test at all today), media, categories, tags, pages, analytics.
- **A guard test that cannot be forgotten:** enumerate every API endpoint (EndpointDataSource) and
  fail when a non-anonymous endpoint has no entry in the denial-test table (a list the tests read,
  with the expected rules). A new endpoint without denial tests turns the suite red.
- Audience validation is **on** in the API tests (Production-like validation of `aud`; switch it off
  only in the one test that exercises the Development relaxation, and assert there that
  Development is what relaxes it). `RoleGroupAuthTests` must not switch it off again.
- Break-it-once (several, all recorded): remove `[Authorize(Roles = …)]` from one controller → its
  denial tests fail and the guard stays green; remove the author check in post update → the
  other-Editor test fails; add a new authorised endpoint without a table entry → the guard fails.

## Item 2: the remaining flaky and order-dependent tests (I-3, I-5, I-14, M-7, M-8, M-14)
- I-3 `AdminPagesListTests` (failed the 13d verify again on 2026-10-19 with `Timeout 30000ms exceeded` while swap was full): apply the review's two candidate fixes (explicit bounds on the reload
  and editor waits; read rows once the superseded load has settled) and make every assertion name
  what it waited for, so a future failure is diagnosable from one line.
- I-5 `ReadingPageApiCountTests`: count against a baseline taken in the test (or a dedicated
  instance log window), so test order doesn't matter; run it in reverse order once to prove it.
- `PublicReadCacheTests.UpdatingPageNavigationThroughOneInstance_UpdatesTheOtherInstancesCachedNavigation`
  still fails in the sandboxed verify after I-1's fix, now at its setup: "B's cache listener connects
  and finishes its setup" not met within 30 s, `LastError` = `NpgsqlException: The connection was
  previously broken because of the following exception` (13d verify, 2026-10-19; the inner exception
  was cut off). First make the failure message carry the whole exception chain (inner exceptions,
  one line each), reproduce under load (`stress-ng` or the full API suite in parallel with E2E), name
  the cause, then fix it. Never just widen the bound.
- I-14 `PostsControllerTests`, M-7, M-8, M-14: each test creates and scopes its own data, writes to
  its own paths, and doesn't depend on what other fixtures filled.

## Item 3: E2E waits and signals (I-4, M-1, M-2, M-9, M-10, M-13, M-19, M-23)
- The 15 editor fixtures wait for `data-interactive` (W-011), not `NetworkIdle`.
- Remove the disguised sleeps (M-1, M-2); bounded polling with a condition (M-9); fix the vacuous
  reconnect watch (M-10) and the beacon/scroll-spy reads (M-13, M-19).
- `AdminTests.MediaLibrary_AltText_*` (13e, 3104982): replace the sync-over-async poll with an
  awaited bounded wait, and make `SavedAltTextAsync` fail with a clear message if the search
  matches more than one row (today `.Single()` throws inside the retry).
- M-23: the slow-by-construction tests get faster without losing strength where the review shows
  how; report the suite wall time before and after.

## Item 4: tests that measure what they claim (I-6 to I-13, I-15, I-16)
- I-6: `ReadYourWritesTests` runs against an instance that really has `Database:ReadHost`
  configured (a replica-like second connection is enough) — or is renamed to what it tests; prefer
  the former.
- I-7, I-8: the CSP measurements get two-sided controls (a loosened policy must make a test fail,
  not pass), and the Chromium report test proves `/csp-report` accepted the report.
- I-9: pin `SaveTokens = false` and that the auth cookie carries no token.
- I-10: `RemoveBySubAsync` tested on the production (database) token store.
- I-11: the fakes honour `TagSlug`/`CategorySlug`, and the listing filter is asserted.
- I-12: brief 13e (a26750b) already added `AdminPagesApiFailureTests` for the tags page and media
  grid; check they cover the review's cases and add only what is missing.
- I-13: the `[PersistentState]` handoff is exercised (prerender then interactive, one API call).
- I-15: the Web analytics proxy forwards `X-Visitor-IP` and `X-Analytics-Key` (unit/integration).
- I-16: the test gaps the .NET review's Important findings need (list in the report).

## Item 5: vacuous or mislabelled tests (M-3 to M-6, M-11, M-12, M-15 to M-22, M-24, M-25)
- Each one measures its outcome (the review gives the specific gap per test); M-25's leaks and
  clean-up fixed.

## Out of scope
- Production-code fixes (they get their own briefs; report any real bug a new test exposes).
- Changing the E2E architecture (instances, shared IdP).
