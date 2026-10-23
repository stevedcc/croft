# Brief 13h: the product bugs brief 13f's tests found

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Source:** brief 13f (test-suite review) committed three tests for real bugs as `Skip`ped, each
with its reason, and reported one more observation. This brief fixes them. Un-skip each test as
its item lands: it is the red state (it was proven to fail when un-skipped).

**Owner-visible outcome:** error responses carry the same security headers (and the same CSP) as
every other response; a media row whose file is gone answers 404 and doesn't log an Error; the
cache listener reconnects on a fresh connection of its own; a feedback item can only be deleted
through the post it belongs to.

**CSP impact: stricter or equal.** Item 1 makes error responses carry the policy they should
already carry. `CspSnapshots.txt` must be byte-identical. No directive changes.

## Item 1: security headers survive the exception handler (API and Web)
Now: `SecurityHeadersMiddleware` (API `src/Croft.Api/Security/SecurityHeadersMiddleware.cs`, Web
`src/Croft.Web/Security/SecurityHeadersMiddleware.cs`) assigns the headers before `next()`.
`UseExceptionHandler` clears the response (headers included) before it writes the 400/500/503, so
those responses leave without HSTS, nosniff, Referrer-Policy, Permissions-Policy, COOP and the CSP.
The Web's comment at `Program.cs:444-446` claims the opposite.
- Set the headers in `context.Response.OnStarting(...)` (assign, never append, as now), so they
  are applied to whatever response is finally sent, the handler's included. Check the Web
  `CspMiddleware` the same way: the `/Error` page and a 503 must carry the document CSP the
  snapshots pin for that kind; if it has the same defect, fix it the same way.
- Correct the `Program.cs` comment in both apps.
Tests: un-skip `SecurityHeadersTests.AValidationErrorAnsweredByTheExceptionHandler_StillCarriesEveryHeader`
and `...AnUnhandledException_AnsweredByTheExceptionHandler_StillCarriesEveryHeader`; add the Web
equivalents (a page that throws in Production → 500 from `/Error`, and an API-down → 503, both
with every header and the CSP, each header exactly once). Break-it-once: move the assignment back
before `next()` → both API tests fail.

## Item 2: a media file missing from disk is a 404 (I-9 rest)
Now: `MediaController.Get` (`src/Croft.Api/Controllers/MediaController.cs:85`) calls
`fileStorage.OpenRead`, which throws `FileNotFoundException`/`DirectoryNotFoundException` → 500
and an Error log line.
- Missing file → 404 (problem details like other 404s) and one Warning naming the asset id and
  relative path (an operator should notice a row without its file); never an Error.
- The access rules stay first: an anonymous request for a draft's asset is still 403 before the
  file is touched (don't reveal whether the file exists).
Tests: un-skip `MediaControllerTests.Download_WhenTheStoredFileIsMissingFromDisk_IsNotFound_AndLogsNothingAboveInformation`
— and adjust its name and log assertion to "logs one Warning, nothing above" (that is a decision
of this brief, say so in the commit); add: anonymous + draft asset + missing file → 403.
Break-it-once: remove the catch → the test fails with 500.

## Item 3: the cache listener's connection is never pooled
Now: `CacheListenerService.ListenerConnectionString()` (`src/Croft.Api/Services/CacheListenerService.cs:139`)
keeps Npgsql pooling on. 13f's evidence (its item 2 commit message and the 2026-10-20 retro): a connector returned to the pool
(after `DISCARD ALL`) and taken again by a new listener got no reply to its first command for the
full 30 s command timeout under load; with `Pooling=false` 16 stress loops passed. A dedicated
long-lived LISTEN connection gains nothing from a pool, and in production a reconnect after a
failure must not pick up a pooled connector of the dead one's pool.
- Set `Pooling = false` in `ListenerConnectionString()` (with a comment saying why, citing the
  13f evidence). Remove the test-only `CacheListenerDiagnostics.Unpooled` override from the test
  factories, so the tests run the production setting.
Tests: a unit test pins `Pooling == false` in the built connection string; the listener tests
(PublicReadCacheTests, CountCacheTests, CacheListenerConnect*) pass under the same load 13f used
(stress-ng `--cpu 24` plus one extra suite looping, never with E2E) for at least 10 loops; report
the numbers. Break-it-once: `Pooling = true` → the unit test fails (the load loop is evidence,
not the break test).

## Item 4: feedback is deleted only through its own post
Now: `DELETE /api/posts/{postId}/feedback/{feedbackId}` (`FeedbackController.cs:33-39`) passes
only `feedbackId` to `FeedbackService.DeleteAsync` (`FeedbackService.cs:56`); the `postId` in the
URL is ignored, so any post's URL deletes any feedback id (the author/Admin check still applies).
- `DeleteAsync(postId, feedbackId, ...)`: a feedback row whose `PostId` differs → 404, nothing
  deleted (same as a missing id; don't reveal it exists). `ResolveAsync` already matches
  on `PostId` (`FeedbackService.cs:72-73`); use the same shape.
Tests: author deletes own feedback through another post's URL → 404, row remains; through the
right URL → 204. Break-it-once: drop the `PostId` comparison → the wrong-URL test gets 204.

## Item 5: which public reads go to the read host (investigate, then decide)
13f's implementer observed: an anonymous `GET /api/search` was served by a `croft-api-read`
connection, but an anonymous `GET /api/posts?q=...&page=4` (not in the read cache) was not.
- First explain it from the code (`BlogDbConnectionString.ForRequest`, `PrimaryRefill`,
  `PublicReadCache`): is the posts list a cache refill inside the 60 s `PrimaryAfterInvalidation`
  window after a write (then it is correct), or does the list endpoint never use the read host?
- If it is the window: add a test that pins it (list read outside the window → read host, inside
  → primary) and change no code. If the list endpoint bypasses the read host for no reason: route
  it like the other public reads and test both sides. Report what you found before changing code.
Break-it-once: per the chosen outcome, route the list read to the other connection → its test fails.

## Out of scope
- The `/Account/AccessDenied` → 404 behaviour 13f pinned (an Editor on `/admin/*`): leave as is.
- Anything in 13g or 13b.
