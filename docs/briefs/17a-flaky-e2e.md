# Brief 17a: the E2E flakes on the board (issues #57, #62, #64, #69, #24)

Read AGENTS.md first and follow it. One commit per item. Use the `systematic-debugging` skill for
each: the cause first, then the fix. **Never** fix a flake with `Task.Delay`, a sleep, a longer
timeout, a retry loop or a looser assertion (AGENTS.md rule 6); Playwright's auto-waiting
`Expect`, `WaitForRequest`/`Route`, bUnit's `WaitForAssertionAsync` are the primitives.
No pushes except `brief/17a` to `origin` and ONE draft PR. E2E instances 1..3 are yours
(`dev/run-e2e.sh --instances 3`). Other briefs run on this machine at the same time:
`pgrep -af "dotnet test|run-e2e"` (excluding your own shell) before every suite run; never run a
full suite while another one runs; wait and retry. Repeated single-test runs
(`dotnet test tests/Croft.Web.E2E --no-build --filter … ` against one instance you started with
`./start-local.sh --e2e --instance 1`, `E2E_WEB_URL`/`E2E_API_URL` from `dev/instance-params.sh`)
are fine alongside others.

Shared helpers: `tests/Croft.Web.E2E/WebTest.cs` (base: `LoginAsync`, `SignOutAsync` :232-246,
`WaitForInteractiveAsync` :347-353, `SlowLoadTimeoutMs`), `StaticReadingTest.cs`, `MockIdpApi.cs`.

## Item 1 (#62): `AdminTests.PostsList_RowActions_NavigateToEditAnalyticsAndFeedbackTab`

Facts nobody had read until now. The W-066 capture (`AdminTests.cs:603-628`, "unexplained rare
hang in `WaitForURLAsync`, ~1 in 15 full runs") has fired three times and left two text dumps on
this machine; read both before anything else:

- `/tmp/diag-edit-100759381.txt` (2026-10-18, before 15e): after the **Edit** click the URL was
  still `/admin/posts`; `readyState=complete`, the `/admin/posts` document fully loaded
  (`loadEventEnd` 216 ms, `_blazor/negotiate` done at 230 ms). The click did nothing: the row's
  `<button @onclick>` (`Components/Admin/PostsGrid.razor:93-94`, an `EventCallback` the page
  answers with `NavigationManager.NavigateTo`) had no handler yet, or the handler ran and the
  navigation did not happen. 15e added `WaitForInteractiveAsync` to `SearchForRowAsync` (:713-719)
  for #43 after this capture, so this shape may be fixed; confirm or refute it.
- `/tmp/diag-feedback-093554877.txt` (2026-10-25, after 15e, under memory pressure): the URL **was**
  `…/edit?tab=feedback`, i.e. the predicate matched, `readyState=complete`, and
  `WaitForURLAsync(predicate, Timeout 15000)` still timed out. The navigation entry is the
  original `/admin/posts` document (a same-document navigation: Blazor's `NavigateTo` inside the
  circuit is a `pushState`); one in-flight request, a stylesheet (`/css/highlight-theme.css`).
- CI run 40103402468 (2026-10-24) uploaded only `diag-edit.png` (the edit page fully rendered, so
  the second shape); its `.txt` was lost because `.github/workflows/ci.yml:179` uploads
  `/tmp/diag-*.png` only. Add `/tmp/diag-*.txt` to that list in this item's commit, whatever else
  you find.

So the hang is Playwright's `Page.WaitForURLAsync` not resolving after a **same-document**
navigation whose URL already matches. Read the implementation in the worktree's package
(`tests/Croft.Web.E2E/bin/Debug/net11.0/.playwright/package/lib/server/frames.js`, `waitForURL`
and what it awaits for `waitUntil: 'load'` when the frame's URL matches on entry and when the
navigation is `navigatedWithinDocument`) and explain, in the commit message, the sequence that
leaves it waiting. Reproduce: CPU throttle (`Page.Context.NewCDPSessionAsync` +
`Emulation.setCPUThrottlingRate` 6–8×) and the test in a loop of 20 on one instance; record how
many hang before the fix.

Fix: the test needs the URL and the page, not a load event that a `pushState` never raises.
Replace the three `WaitForUrlCapturingAsync` calls with `Expect(Page).ToHaveURLAsync(<exact url>,
new() { Timeout = 15000 })` (a retrying assertion on the URL, no lifecycle involved), keep the
heading `Expect`s that follow. Then retire W-066: delete `WaitForUrlCapturingAsync`,
`TrackRequestsForCapture`, `HangStateAsync` and the `_requestsInFlight` plumbing (:603-700) if
nothing else uses them, move W-066 to "Retired" in `docs/workarounds.md` (IDs are never reused),
`WorkaroundRegisterTests` green. Check the other `WaitForURLAsync` calls in the class
(:152, :505, :533, :731, :771): the ones after **Save draft** / **Publish now** (:505, :731, :771)
also follow a `NavigateTo` within the circuit, so they have the same exposure; convert them the
same way (`ToHaveURLAsync` with a regex or predicate), and leave the two that follow a real
document navigation (`/admin` redirect :533, login :152) as they are, saying so. After the fix:
20/20 under the same throttle.

If the package source shows a different cause (for example the click is still lost), fix that
cause instead and say so; do not change the wait as a guess.

## Item 2 (#64): `ReadingPageScriptsTests` enumerates a list the page is still appending to

`tests/Croft.Web.E2E/ReadingPageScriptsTests.cs:129-140`: `TrackRequests` locks the `Add` only;
`:83-98` and `:106-124` enumerate `requests` (`Where`, `Has.Some.Matches`) while the handler keeps
appending (an interactive page's circuit and websocket requests arrive after
`WaitForInteractiveAsync`). Replace `List<string>` with a small `RequestLog` (in the test class or
`WebTest`): `Add` under the lock, `Snapshot()` returns a copy under the lock, and `Stop()`
unsubscribes the handler. Every assertion enumerates a `Snapshot()`. Same pattern for
`FirstVisitTest.cs:98-100` (`responses` and `warnings`, no lock at all) and any other
`page.Request/Response/Console += … list.Add` you find in `tests/Croft.Web.E2E` (grep; list them
in the commit). Evidence: a probe (not committed) that appends from a background task while
`Where` enumerates reproduces `Collection was modified`; the `RequestLog` under the same probe does
not. Break-it-once for the committed tests is not possible without the race; the probe is the evidence.

## Item 3 (#69): `AnalyticsViewCountTests.ReloadingPostPage_DoesNotIncrementViewCountAgain`

`tests/Croft.Web.E2E/AnalyticsViewCountTests.cs:69-94` arms `page.WaitForResponseAsync` for
`POST /api/analytics/pageview` before `GotoAsync`/`ReloadAsync` and asserts the 204; it timed out
(30 s) in 2 of 7 full runs during 16a and once in 15f, never alone. The beacon is
`navigator.sendBeacon` (`src/Croft.Web/wwwroot/js/analytics.js:3-17`), sent once per `boot()`
(`static-glue.js:43, 70-78`); the Web endpoint (`src/Croft.Web/Program.cs:597-676`) answers 204
only after forwarding to the API. Two candidates; find which with evidence:

- (a) Playwright does not reliably raise `Response` for a `sendBeacon` request (keepalive
  requests are owned by the browser process). Probe: a page that reloads 50 times under CPU
  throttle, counting `Request` vs `Response` events for the beacon. If `Response` is missed while
  `Request` is not, that is the cause.
- (b) the Web → API forward is slow under load (3 workers, memory pressure) and 30 s passes.
  Probe: the instance's Web and API logs around a slow run; the `pageview` forward's duration.

Fix for (a): observe the beacon with `page.RouteAsync("**/api/analytics/pageview", …)`: in the
handler, `await route.FetchAsync()` (Playwright performs the request itself and hands back the
response), record the status in a `TaskCompletionSource<int>` the test awaits, then
`route.FulfillAsync(new() { Response = response })`. The test still asserts 204 per visit and
waits on a concrete condition that cannot be missed. Fix for (b): report with the numbers; a
timeout change is not a fix (the root cause is elsewhere), so stop and let the orchestrator rule.
Either way, the test's final `Expect("Total views: 1")` stays. Note in the commit: the dedupe key
includes the UTC day, so a run across midnight counts 2 (`AnalyticsService.cs:24-51`); out of
scope, the orchestrator files it.

## Item 4 (#24): the remaining raw `/logout` navigations use `SignOutAsync`

`WebTest.SignOutAsync` (16a) waits for `/` and the login link; these still do
`GotoAsync("/logout")` + `Expect("Log in")` and then navigate at once (the #55 race):
`AdminTests.cs:410, 422`, `MediaProxyTests.cs:32, 78, 116`, `MobileContentsPanelTests.cs:998`,
`ReadYourWritesTests.cs:90`, `PostRailStickyTests.cs:306`, `RelatedSectionTests.cs:377`,
`EditorTests.cs:200`, `TagRelatedToggleTests.cs:173`. Apply the pattern (one commit, list them);
`SidebarAuthLinksTests.cs:115` is different on purpose (the interstitial) and stays. The
`AccessibilityTests` fixtures (:479-525, :545-590, :602-658) already use `SignOutAsync`; their
15 s Publish waits are left alone (the owner watches #24 for a week before closing). Also from
#24: `tests/Croft.Web.Tests/OutputCache/ResponseCompressionTests.cs:155-170`
`EverythingElse_IsNotCompressedByThisCode` asserts no `Content-Encoding` but not the status; add
`response.StatusCode.Should().Be(…)` per case (its neighbours do, :150), so a 500 cannot pass as
"not compressed". The home total-bytes budget (`FirstVisitMeasurementTests.cs:27-38`) is out of
scope (it depends on what the suite has published; the orchestrator files it).

## Item 5 (#57): the first admin token of a CI run lacks the Admin group

Facts: 5 CI runs on 2026-10-24, always the first test of the first accessibility-matrix fixture
(`AccessibilityMatrixTests.cs:52-64`: one token flow, `CreateCategoryAsync` with a second fresh
token succeeds, then `UploadImageAsync` with the **first** token gets 403, which only
`[Authorize(Roles="Admin")]` returns). Never locally in 200 flows. Since 801203b
`MockIdpApi.GetAdminAccessTokenAsync` (`MockIdpApi.cs:23-60`) decodes the token and throws with
the payload when `sub`/`groups` are missing (`:67-83`); no occurrence since to read. The IdP
(`compose.yaml:15-27`, `ghcr.io/navikt/mock-oauth2-server:2.3.0`, `interactiveLogin: true`,
`dev/mock-oauth2-config.json`) is only ever asked for its discovery document before the suite
(`dev/run-e2e.sh:145-157`, `.github/workflows/ci.yml:151-160`); no `/authorize` or `/token` call
touches it before two workers start their first flows at once on a cold JVM. The flow itself never
checks the `/authorize` GET's status (`:28`).

1. Read mock-oauth2-server 2.3.0's source for the interactive login path (the `/authorize` GET,
   the login POST with the `claims` form field, how the code is bound to the posted claims, and
   the token endpoint): `https://github.com/navikt/mock-oauth2-server` (reading upstream source is
   allowed for this item). Report whether anything there can yield a token with the default
   claims instead of the posted ones under concurrency or on a first request (a per-request
   session, a cache warmed by the first call, a race on a shared map). One paragraph with file
   names in the commit; "nothing found" is a valid answer.
2. `MockIdpApi.GetAdminAccessTokenAsync`: `EnsureSuccessStatusCode()` on the `/authorize` GET and
   on the login POST's redirect status (303/302 expected: assert it is a redirect), so a wrong
   first answer is named rather than carried into the token call; include both statuses in the
   `EnsureAdminClaims` message. Unit-level check: none possible without the IdP; show one local
   run of `AccessibilityMatrixTests` passing.
3. A readiness gate in `dev/run-e2e.sh` after the discovery wait (and nowhere else: CI runs the
   same script): run the code flow with `curl` (the shape of `get_admin_token` in
   `dev/seed-dev-data.sh:77-108`), decode the payload (`base64 -d` on the middle segment, `jq`
   is not guaranteed: use `grep`), and require `"sub":"admin-id"` and `"Admin"` in `groups`; poll
   up to 30 × 1 s until a good token comes back, then run the flow **three times concurrently**
   once more and require all three good. On failure: print the three payloads and
   `docker compose logs --tail 50 oauth2`, exit 1. This is a readiness check like the discovery
   loop, not a test retry: the suite starts only once the IdP demonstrably issues the posted
   claims. Evidence (scripts are not unit-tested, rule 1): the gate's output on a normal run, and
   break-it-once: change the required group to `"Nope"` and show the run refusing to start.

## Item 6: suites
`dotnet build Croft.slnx`; `dotnet test tests/Croft.Web.Tests --no-build` (item 4 touched it);
E2E **twice** through `dev/run-e2e.sh --instances 3` (flake work gets two full passes). API suite
not needed (no `src/Croft.Api` change). Summary lines verbatim into the last commit message and
the report.

## Report
Per item: cause in one or two sentences, fix, evidence (the probe results, the 20/20 loops, the
gate output), suites, the PR's `ci` run URL (`gh pr checks --watch`). What is left undone.

## Out of scope
`dev/run-e2e.sh`'s exit code and the suite lock (brief 17b owns the scripts; your item 5 adds a
block to `run-e2e.sh` after :157, 17b edits :215-249: tell the orchestrator if you need more).
Anything else that flakes while you work: file the evidence in the report, move on.
