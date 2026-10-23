# Test-suite review: `tests/` on Croft 0.6.2

Date: 2026-10-19. Commit reviewed: `0e9e697` ("Croft 0.6.2"). Scope: `tests/Croft.Web.Tests`
(bUnit and xUnit, 23.8k lines), `tests/Croft.Api.Tests` (xUnit + `WebApplicationFactory` +
Testcontainers, 12.1k lines) and `tests/Croft.Web.E2E` (Playwright + NUnit, 19.4k lines), read
against the code in `src/` and the suite plumbing in `dev/`. The brief: tests that would still
pass if the feature were broken, order and timing dependence, shared-state leaks, sleeps and weak
polling, slow tests, and important behaviour in `src/` with no test; plus the specific cause of
three tests known to be flaky under load. Nothing outside this file was changed.

**How claims were checked.** Every finding quotes the lines it is about. Where a claim rests on
running something, the run is named; where it could not be run, the finding says "unverified".

| Check | Result |
|---|---|
| `dotnet build Croft.slnx` (SDK 10.0.402; `global.json` wants 10.0.4xx) | 0 warnings, 0 errors |
| `dotnet test tests/Croft.Web.Tests --no-build` | Passed 1057, Failed 31, Total 1088, 26 s |
| `dotnet test tests/Croft.Api.Tests` | Not run: no Docker/Podman socket in this container |
| `dev/run-e2e.sh` | Not run: no Postgres, no mock IdP, no stack |

The 31 Web failures are all `Docker.DotNet` "Failed to connect to Docker endpoint" at fixture
start (`BackgroundJobLockTests`, `ConcurrentWebMigrationTests`, `DataProtectionKeyImportTests`,
`HealthEndpointTests`, `SessionLifetimeTests`, `SessionPurgeTests`, `SessionRemovalTests`,
`SharedDataProtectionKeysTests`, `SharedSessionRefreshTests`, `SharedSessionStoreTests`). None
failed on an assertion. The public-release audit already carries that as its finding I-5
([`2026-10-19-public-release-readiness.md`](2026-10-19-public-release-readiness.md), draft PR #3),
so it is not repeated here. Everything said below about the API suite, those ten Web classes and
the E2E suite is from reading the code, traced into `src/` where the claim needs it.

Cross-references: the .NET code review ([`2026-10-19-dotnet-code-review.md`](2026-10-19-dotnet-code-review.md),
draft PR #4) is cited as "PR #4 I-n"; its code findings are not repeated, only the test gaps
behind them.

Severity: **Critical** = the suite gives false confidence about a security or data-loss
behaviour, or is structurally unsound; **Important** = a meaningful behaviour is unprotected, or a
test is flaky, order-dependent or vacuous in a way that will mislead; **Minor** = everything else
worth fixing.

---

## Critical

### C-1. Authorisation is effectively untested: 18 of 21 role-gated endpoints have no denial test, every test client is the same user, and audience validation is off in every API test

- **File:** `tests/Croft.Api.Tests/TestAuthHandler.cs:31-37`; `tests/Croft.Api.Tests/AuthTests.cs:13-20`;
  `tests/Croft.Api.Tests/Fixtures/BlogApiFactory.cs` (no `UseEnvironment`);
  `tests/Croft.Api.Tests/RoleGroupAuthTests.cs:95`; `src/Croft.Api/Program.cs:55-56`;
  `src/Croft.Api/Services/PostService.cs:396-400`; `src/Croft.Api/Services/FeedbackService.cs:43-50`
- **What is wrong.** Four things that together mean an authorisation regression passes every suite:
  1. The API suite asserts `Forbidden` in five places (`AuthTests.cs:19` POST /api/posts with no
     role; `PostsControllerTests.cs:167, 587` the editor-on-published-post service rules;
     `MediaControllerTests.cs:62` a no-role download of a draft asset; `RoleGroupAuthTests.cs:47`
     the group mapping on POST /api/posts). It asserts `Unauthorized` nowhere (`grep -rn
     Unauthorized tests/Croft.Api.Tests/*.cs` → 0). `AsAnonymous()` is used only in
     `PublicReadCacheTests` and `ReadReplicaRoutingTests`, for caching and routing, never for
     a 401. No test shows a wrong role or no role refused on: `DELETE /api/posts/{id}`,
     `POST .../publish` (`PostsController.cs:73-83`), `POST/GET/PUT/DELETE /api/media` and
     `GET /api/posts/{id}/media` (`MediaController.cs:16-58, 99-101`), `POST/PUT/DELETE
     /api/categories` and `/api/tags` (`CategoriesController.cs:37-55`, `TagsController.cs:33-51`),
     `GET /api/analytics` (`AnalyticsController.cs:36-38`), feedback `GET/POST/DELETE` for a
     no-role user and `POST .../resolve` for an Editor (`FeedbackController.cs:13-45`).
  2. `TestAuthHandler` issues `sub = "test-user-id"` to every client whatever the role, so the
     two author-only rules (an Editor may update only their own draft, `PostService.cs:396-397`;
     a non-admin may delete only their own feedback, `FeedbackService.cs:49-50`) cannot be
     tested with this handler and are not. `Editor_CannotUpdatePublishedPost`
     (`PostsControllerTests.cs:154-168`) passes only because the Editor *is* the author.
  3. `DELETE /api/posts/{postId}/feedback/{feedbackId}` is not sent by any test
     (`grep -n Delete tests/Croft.Api.Tests/FeedbackControllerTests.cs` → 0 hits). The service
     also ignores `postId` (`FeedbackService.cs:45`), so a feedback id from another post is
     deletable through any post's URL; untested either way.
  4. `ValidateAudience = !isDevelopment` (`Program.cs:55`). `WebApplicationFactory` runs in
     Development and `BlogApiFactory` never sets an environment, so audience validation is off
     in every API test; the only tests on the real JwtBearer path (`RoleGroupAuthTests`) switch
     it off again explicitly. No test presents a token with a wrong or missing `aud` and expects
     401. AGENTS.md rule 4 exists because of exactly this class of regression and nothing
     enforces it.
  On the Web side, every `/admin/*` page carries `[Authorize(Roles = "Admin")]` and
  `Routes.razor` renders `<NotAuthorized>`, but no test has an Editor open an `/admin/...` URL;
  `AuthenticationTests.EditorLogin_ShowsEditorNav` only asserts the "Posts" link is absent from
  the sidebar.
- **Why it matters.** Change `[Authorize(Roles = "Admin")]` on `Publish` or `Delete` to
  `"Admin,Editor"`, drop `[Authorize]` from `MediaController.Upload`, delete the author check at
  `PostService.cs:396-397` (an Editor then edits any other author's draft), or set
  `ValidateAudience = false`: every suite stays green. The repository's own history (AGENTS.md
  rule 8: "how a hard-coded admin backdoor got in here once") is the scenario.
- **Evidence:**
  ```csharp
  // tests/Croft.Api.Tests/TestAuthHandler.cs:31-35
  var role = Request.Headers[RoleHeader].FirstOrDefault() ?? string.Empty;
  var claims = new List<Claim>
  {
      new("name", "Test User"),
      new("sub", "test-user-id"),
  ```
  ```csharp
  // src/Croft.Api/Services/PostService.cs:396-400
  if (!isAdmin && post.AuthorId != userId)
      return null;
  if (!isAdmin && post.Status != PostStatus.Draft)
      throw new ForbiddenException("Editors can only update draft posts.");
  ```
  ```csharp
  // src/Croft.Api/Program.cs:55-56
  ValidateAudience = !isDevelopment,
  ValidAudience = audience
  ```
  ```csharp
  // tests/Croft.Api.Tests/RoleGroupAuthTests.cs:95
  options.TokenValidationParameters.ValidateAudience = false;
  ```
- **Fix.** (a) One sweep test in the style of
  `ReadReplicaRoutingTests.WithReadHost_NoAllowAnonymousGetEndpoint_WritesOnTheReadConnection`:
  enumerate every `RouteEndpoint` whose metadata carries `IAuthorizeData` with roles, pin the
  expected list, and for each assert `AsAnonymous()` → 401 and each role not in the list → 403,
  with a row or file count before and after proving no side effect. (b) An `X-Test-Sub` header on
  `TestAuthHandler` (`WithUser(sub)` beside `WithRole`), then: Editor B PUTs Editor A's draft →
  404 and the row unchanged; A PUTs it → 200; B DELETEs A's feedback → 404 and the row remains;
  admin DELETE → 204. (c) A `RoleGroupAuthTests`-style factory with `UseEnvironment("Production")`,
  `Oidc:Audience` set and `ValidateAudience` left as the app sets it; `aud` right → 201, wrong or
  missing → 401. (d) One E2E or cookie test: an Editor opens `/admin/posts/new` and gets the
  not-authorised document. Unverified by execution (no container runtime).

---

## Important

### I-1. `PublicReadCacheTests.UpdatingPageNavigationThroughOneInstance_UpdatesTheOtherInstancesCachedNavigation`: a 2 s window for a cross-process LISTEN/NOTIFY round trip, while the class runs in parallel with 36 other database classes

- **File:** `tests/Croft.Api.Tests/PublicReadCacheTests.cs:173-195, 683-694`;
  `tests/Croft.Api.Tests/Fixtures/PostgresDatabaseFixture.cs`; `src/Croft.Api/Services/CacheNotifier.cs:24-38`;
  `src/Croft.Api/Services/CacheListenerService.cs:47-75, 99-112`
- **What is wrong.** The test hosts two full API instances (`a`, `b`), primes B's cache with
  `/api/pages/navigation`, saves the page through A, then polls B for up to **2 s** for the page
  to drop out. The path it waits on is: A's `SaveChangesAsync` → four separate `pg_notify`
  statements (`CacheNotifier.cs:26-37`, `Pages` is the fourth) → B's dedicated listener
  connection wakes from `connection.WaitAsync` (`CacheListenerService.cs:75`) → the
  `Channel<CacheGroup>` drain loop → `InvalidateAsync(Pages)` → the test's next poll re-queries.
  Every hop is a thread-pool continuation. The class has no `CollectionBehavior` cap
  (`tests/Croft.Api.Tests` has no `AssemblyInfo.cs`; `MaxParallelThreads` is only set in
  `Croft.Web.Tests`), so xUnit runs it alongside the other 36 `PostgresDatabaseFixture` classes
  on one Postgres container, and each test in this class builds **two** `WebApplicationFactory`
  hosts. Under that load a 2 s bound on a four-notification cross-process round trip is the
  cause: sibling tests in the same file give the same path 5 s (`BurstOfNotifications_AreAllApplied`
  `:383`, `HeldReadStraddlingARemoteWrite… :452`) and the listener itself 30 s (`:662`). The
  navigation test is simply the one of the six 2 s cross-instance tests that is least often
  already warm (its primed read is a single key, its write is the last of the class's PUTs to
  run). Nothing in the code path is wrong: `pg_notify` runs after the commit (`PostService.cs:423-424`),
  the generation counter in `MemoryPublicReadCache` stops a straddling refill from re-caching
  the old value, and NOTIFY is database-scoped so other classes cannot swallow it. The window is
  the fault. Unverified by execution.
- **Why it matters.** A red run on a loaded machine that says nothing about the feature; the
  habitual fix ("rerun") is how a real invalidation bug would later slip through.
- **Evidence:**
  ```csharp
  // tests/Croft.Api.Tests/PublicReadCacheTests.cs:190-194
  await WaitUntilAsync(
      async () => !(await anonymousB.GetStringAsync("/api/pages/navigation"))
          .Contains(page.Slug, StringComparison.Ordinal),
      TimeSpan.FromSeconds(2),
      "B's cached navigation drops the page after A clears its navigation flag");
  ```
  ```csharp
  // tests/Croft.Api.Tests/PublicReadCacheTests.cs:381-384 (same path, 5 s)
  await WaitUntilAsync(
      () => Task.FromResult(bCache.Count == 0),
      TimeSpan.FromSeconds(5),
  ```
- **Fix.** Wait on the concrete event, not the clock: resolve `MemoryPublicReadCache` from `b`
  and wait for `Count` to drop (or for the `pages:nav` key to be gone) with the 30 s bound the
  listener tests already use, then make the one HTTP read and assert. If the HTTP poll stays,
  make the bound 30 s: a bound is a failure threshold, not a measurement. Separately, give the
  API project a `[assembly: CollectionBehavior(MaxParallelThreads = N)]` as the Web project has.

### I-2. `SidebarAuthLinksTests.SidebarLogInAndLogOutLinks_CompleteAFullLoginLogoutRoundTrip`: two IdP round trips bounded by Playwright's 5 s default assertion timeout, and a `Single()` that throws on a retried end-session request

- **File:** `tests/Croft.Web.E2E/SidebarAuthLinksTests.cs:36-38, 57-60, 67`; compare `tests/Croft.Web.E2E/WebTest.cs:148, 159, 185-186`
- **What is wrong.** The suite sets no default expect timeout anywhere (`grep -rn
  SetDefaultExpectTimeout tests/` → nothing), so every `Expect(...).ToBeVisibleAsync()` is bounded
  at Playwright's 5 s. This test's two outcomes are each the end of a multi-document chain through
  the **shared** mock IdP (one container for all three instances): sign-in is IdP form → `/signin-oidc`
  → server-side token request to the IdP → cookie → redirect to `/` → render; sign-out is `/logout`
  → interstitial → `/logout/complete` → IdP end-session → `/signout-callback-oidc` → `/`. Both are
  gated by a 5 s `Expect`. The shared `LoginAsync` helper gives the same sign-in 10 s
  (`WebTest.cs:185-186`) and `SignInAgainAtTheMockIdpAsync` 30 s (`:148, 159`); this test does
  not use the helper and keeps the default. With three instances and one IdP under load, 5 s is
  the cause of the sign-in or sign-out leg timing out. Secondly, `endSessionUrls.SingleOrDefault()`
  (`:67`) throws `InvalidOperationException` (not an assertion failure) if the browser issued the
  end-session request twice, e.g. a retry after a slow IdP answer; the message then says nothing
  about logout. Unverified by execution.
- **Why it matters.** The test is red on a slow run with "locator not visible" and no diagnosis;
  the real behaviours (the links are real endpoints, `id_token_hint` is sent) are fine.
- **Evidence:**
  ```csharp
  // tests/Croft.Web.E2E/SidebarAuthLinksTests.cs:36-38
  await Expect(Page.Locator("#sidebar-nav").GetByRole(
      AriaRole.Link, new LocatorGetByRoleOptions { Name = "Posts" })).ToBeVisibleAsync();
  await Expect(Page.Locator("#sidebar-nav a[href='/logout']")).ToBeVisibleAsync();
  ```
  ```csharp
  // tests/Croft.Web.E2E/SidebarAuthLinksTests.cs:57-60
  await Expect(Page.Locator("#sidebar-nav a[href='/login']")).ToBeVisibleAsync();
  await Expect(Page.Locator("#sidebar-nav a[href='/logout']")).ToHaveCountAsync(0);
  Assert.That(Page.Url, Is.EqualTo($"{BaseUrl}/"),
  ```
  ```csharp
  // tests/Croft.Web.E2E/WebTest.cs:185-186 (the helper's bound for the same chain)
  await page.Locator("a[href='/logout']").First
      .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10000 });
  ```
- **Fix.** `await Page.WaitForURLAsync(url => url.StartsWith(BaseUrl), new() { Timeout = 30000 })`
  after each IdP leg (the helper's shape), then the `Expect`s; or set a suite-wide
  `SetDefaultExpectTimeout` in `WebTest` once. Replace `SingleOrDefault()` with
  `Assert.That(endSessionUrls, Has.Count.EqualTo(1), ...)` so a duplicate fails with the URLs.

### I-3. `AdminPagesListTests.AdminPages_ListsPagesWithPlacementAndOrder_SortedByPlacementThenOrder_AndNoPosts`: cause not pinned; the two candidates are the 5 s default on the post-search reload and a superseded-load race, and nothing in the test tells them apart

- **File:** `tests/Croft.Web.E2E/AdminPagesListTests.cs:29-57, 86-93`;
  `src/Croft.Web/Components/Admin/PostsList.razor:28-31, 60-101`; `src/Croft.Web/Components/Admin/PostsGrid.razor:11`
- **What is wrong.** The repository has no record of the failure (one squashed commit, no CI
  logs, nothing in `docs/`), so this is from reading. The test logs in through the IdP, opens
  `/admin/pages`, waits for `NetworkIdle` then `data-interactive`, types the run id into the
  search field and presses Enter, then `Expect(rows).ToHaveCountAsync(4)` with the 5 s default.
  What has to happen inside those 5 s: the grid's **interactive** first load (`MudDataGrid`
  calls `ServerData` again once the circuit is up; the prerender's load is a different component
  instance) and the search reload, both through `IBlogApiClient` with a token, against an API
  shared with the fixture's own `PublishAsync` traffic and whatever the other two instances'
  fixtures are doing to the same Postgres. The same 5 s bounds the `"Edit Page"` heading at
  `:56`, which is a full editor page load (post read, tags, feedback) after the click. That is
  the first candidate, and it matches "flaky under load" with no code fault. The second
  candidate is the superseded-load path in `PostsList.LoadAsync`: a search reload issued while
  the interactive first load is in flight relies on MudBlazor cancelling the earlier load (the
  comment at `:83` says it does; unverified for MudBlazor 9.11) and on the `_loadKey` guard. If
  the earlier load is **not** cancelled and completes after the search load, it returns
  `_result?.Items` — by then the filtered four — so the grid re-renders the same rows; if it
  completes before, it returns `[]` and the grid shows "No pages found." until the search load
  lands. Neither of those breaks `ToHaveCount(4)`, which polls; but the `AllTextContentsAsync`
  and the four `RowAsync` reads that follow are one-shot reads of a grid that may still be
  re-rendering from the late load, and a row read mid-patch can come back empty. I could not
  construct an interleaving in which the **order** assertion sees wrong data (the API sort is
  deterministic, `PostService.cs:326-329`, and the filtered and unfiltered sets of this run's
  pages are the same four), so the order assertion is probably not where it fails. Unverified:
  the NUnit failure message names the assertion, and that settles it.
- **Why it matters.** As I-2: a red run with no diagnosis, on a test whose behaviours are fine.
- **Evidence:**
  ```csharp
  // tests/Croft.Web.E2E/AdminPagesListTests.cs:33-36
  var rows = Page.Locator(".posts-grid__row");
  await Expect(rows).ToHaveCountAsync(4);
  var titles = await rows.Locator(".posts-grid__title").AllTextContentsAsync();
  Assert.That(titles.Select(t => t.Trim()), Is.EqualTo(new[] { SidebarTitle, FooterEarlyTitle, FooterLateTitle, NowhereTitle }),
  ```
  ```razor
  @* src/Croft.Web/Components/Admin/PostsList.razor:81-84, 96-100 *@
  catch (OperationCanceledException)
  {
      // The grid cancels a load's token when a newer one supersedes it.
  }
  ...
  return new GridData<PostSummaryDto>
  {
      Items = _result?.Items ?? [],
  ```
- **Fix.** Make the three one-shot reads retrying `Expect`s: `Expect(rows.Locator(".posts-grid__title")).ToHaveTextAsync(new[] {...})`
  (ordered, polls) and `Expect(row.Locator(".posts-grid__placement")).ToHaveTextAsync("Sidebar")`,
  with a 15 s timeout, and the same for the `"Edit Page"` heading. Then the failure, if it
  recurs, is a timeout naming the assertion. Once the owner has that message the remaining
  candidate can be confirmed or dropped. The `_grid.ReloadAsync()` while the first load is in
  flight is a product question too (see PR #4 I-8: the admin pages have no `[PersistentState]`,
  so the circuit always re-loads).

### I-4. Fifteen editor fixtures use `NetworkIdle` as the prerender-to-interactive signal although the suite documents `data-interactive` (W-011) for exactly that; the AdminPages/Sidebar class of flake is built into them

- **File:** `tests/Croft.Web.E2E/AdminTests.cs:22-29` (25 uses in that file, 2 `WaitForInteractiveAsync`),
  `PostCreationTests.cs:14-21`, `SchedulingTests.cs:14-22`, `AnalyticsViewCountTests.cs:52-59`,
  `MediaProxyTests.cs:14-21, 52-59, 95-102`, `EditorTests.cs:187-192`, `BrandingFilesTests.cs:71-77`,
  `ReadYourWritesTests.cs:52-57`; `tests/Croft.Web.E2E/WebTest.cs:242-248`
- **What is wrong.** `WebTest.WaitForInteractiveAsync` exists because "events dispatched before
  the first interactive render are dropped" (W-011). These fixtures instead do `GotoAsync` →
  `WaitForLoadStateAsync(NetworkIdle)` → `Expect(heading)` → `FillAsync`. The heading is in the
  prerendered HTML; `NetworkIdle` is "no HTTP request for 500 ms" and ignores the circuit's
  WebSocket, so it says nothing about the first interactive render. Under the three-instance run
  the first interactive render can lag the last HTTP response by more than 500 ms; the typed
  title then lands in DOM that Blazor replaces on its first render, the model stays empty,
  "Publish now" posts an empty title (400) and `WaitForURLAsync` times out. W-066 at
  `AdminTests.cs:553-561` records an unexplained 1-in-15 hang of exactly this shape. The suite
  already fixed this in the newer fixtures (`EditorTitleTests:24`, `PageWritingTests:313`,
  `DraftBackupTests:190`, `LongPostEditorTests:274`, `EveryFieldScenario:65`).
- **Evidence:**
  ```csharp
  // tests/Croft.Web.E2E/AdminTests.cs:23-29
  // Wait for the Blazor circuit to attach before dispatching DOM events: during the
  // prerender → interactive handoff a change/click event is lost. NetworkIdle is this
  // suite's established signal that the handoff is done.
  await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
  await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Media library" })).ToBeVisibleAsync();
  await Page.GetByLabel("Upload images").SetInputFilesAsync([one, two]);
  ```
- **Fix.** `WaitForInteractiveAsync(Page)` before the first event on every interactive page (one
  shared `OpenNewPostEditorAsync` helper in `WebTest` replaces ~15 copies of the fixture block).
  On the static reading pages `NetworkIdle` is only dead time (about 45 calls across `HomeTests`,
  `PostPageTests`, `RelatedSectionTests`, `StaticPageTests`, `ReadingPage*Tests`, up to 500 ms
  each): drop it there and rely on the `Expect` that follows.

### I-5. `ReadingPageApiCountTests` counts the whole instance log with no baseline; it passes only because NUnit runs its tests alphabetically

- **File:** `tests/Croft.Web.E2E/ReadingPageApiCountTests.cs:40-48, 51-72, 82-89, 104-112`
- **What is wrong.** `PostPage_FullLoad_MakesExactlyOneApiRequest` and
  `CategoryPage_FullLoad_MakesExactlyOneApiRequest` assert `CountApiRequestsFromWebLog(...) == 1`,
  where the helper counts every matching "Start processing HTTP request" line in
  `/tmp/blog-e2e-N-web.log` since the instance started (`File.ReadAllText`, no "before"
  snapshot). The third test in the fixture, `PostPage_FullLoad_ReadsTheSidebarAndFooterPagesInOneApiRequest`,
  loads the same `/post/{_postSlug}` again (and does take a baseline for its own counter,
  `:82-83`). The first test sees exactly 1 only because `…_MakesExactlyOneApiRequest` sorts before
  `…_ReadsTheSidebar…`. Rename either, add a test, or filter so the sidebar test runs first, and
  the count is 2.
- **Why it matters.** Order-dependent test (rule: xUnit/NUnit order is not a contract), and an
  instance-global count that any future fixture reading the same slug through the Web would
  break.
- **Evidence:**
  ```csharp
  // tests/Croft.Web.E2E/ReadingPageApiCountTests.cs:42-46
  var requestCount = CountApiRequestsFromWebLog($"posts/{_postSlug}");
  // Assert exactly one API request for this post
  Assert.That(requestCount, Is.EqualTo(1),
  ```
  ```csharp
  // tests/Croft.Web.E2E/ReadingPageApiCountTests.cs:82-83 (the third test does it right)
  var combinedBefore = CountApiRequestsFromWebLog("pages/navigation/all");
  ```
- **Fix.** Take `before = CountApiRequestsFromWebLog(...)` immediately before `GotoAsync` in both
  exact-count tests and assert the delta, as the third test does.

### I-6. `ReadYourWritesTests` claims to exercise the read-replica connection; no E2E instance configures `Database:ReadHost`, so it tests the primary like every other editor test

- **File:** `tests/Croft.Web.E2E/ReadYourWritesTests.cs:4-9, 17-47`; `src/Croft.Api/Program.cs:140-141, 244`;
  `start-local.sh`, `dev/*.sh`, `.env.example` (`grep -rn ReadHost` → nothing)
- **What is wrong.** The class comment: "With the E2E API configured with Database:ReadHost
  pointing at the same database … the public post page shows it too (its anonymous read goes
  through the cache and the read connection)." Nothing sets `Database__ReadHost` for an E2E
  instance; the API logs "Public reads use the primary connection: Database:ReadHost is not
  set." (`Program.cs:244`). The test proves a save shows on reload and on the public page over
  one primary connection, which `EditorTests` and `PageWritingTests` already prove.
- **Why it matters.** A regression in read-host routing (a public read on a stale replica
  string, the cache not invalidated on the read path) would not fail this test while its name
  says it would. The `ReadReplicaRoutingTests` API class is the real protection; this E2E test
  is a label, not a check.
- **Fix.** Export `Database__ReadHost` (same host) in `start-local.sh --e2e` so the API really
  builds the read connection, and assert the instance's API log carries the "read host" startup
  line, so the premise fails loudly if lost again; or rename the test and drop the claim.

### I-7. The E2E CSP measurements are one-sided: a loosened policy produces fewer violations, and the only browser-blocks-it control covers `script-src-elem`

- **File:** `tests/Croft.Web.E2E/CspTests.cs:55-77, 207, 497`; `tests/Croft.Web.E2E/CspPublicPagesTests.cs:215-224`;
  `tests/Croft.Web.E2E/CspScriptTests.cs:60-112`
- **What is wrong.** Every measurement asserts `probe.Violations` is empty. An empty list is
  exactly what a looser policy produces. The E2E checks on the policy *text* are: the header
  exists (`E2EStack_ServesTheEnforcedPolicy_NotReportOnly`, name only) and `style-src-attr`
  per area in `AreaBoundary_Crossings…`. The one test that proves the browser blocks anything
  is `CspScriptTests.ASameOriginScriptOutsideTheAllowedSet_IsRefused_AndReported`, for
  `script-src-elem` only. Nothing injects an inline `<style>` or a `style=""` attribute on a
  reading page and checks refusal; nothing checks `script-src`, `style-src-elem`, `img-src`,
  `connect-src`, `font-src`. The unit snapshot (`tests/Croft.Web.Tests/CspPolicySnapshotTests.cs`)
  pins the header text, so the regression is not unprotected; but the suite that runs the real
  browser gives no signal.
- **Why it matters.** `style-src-elem 'unsafe-inline'` reaches the reading pages through a
  contributor regression; every `Category=Csp` E2E test stays green.
- **Evidence:**
  ```csharp
  // tests/Croft.Web.E2E/CspTests.cs:497
  Assert.That(probe.Violations, Has.Count.EqualTo(0), findings);
  ```
- **Fix.** One positive-control test per directive the public policy promises (inject a `<style>`
  element, a `style` attribute, an `<img>` from another origin, a cross-origin `fetch()`) asserting
  the matching `securitypolicyviolation` with `disposition === 'enforce'`; or pin the full public
  and admin header text once in E2E the way `TrustedTypesTests` pins the report-only header.

### I-8. In Chromium (the default run) the CSP report test proves the browser *queued* a report, never that `/csp-report` accepted it

- **File:** `tests/Croft.Web.E2E/CspScriptTests.cs:102-112`
- **What is wrong.** For `chromium` the test waits for the CDP `Network.reportingApiReportAdded`
  event and returns. The server-side check (`ReportedCspViolationCount` rising, i.e. the Web log
  line from the report endpoint) runs only for Firefox/WebKit, which are not part of the default
  `dev/run-e2e.sh` run.
- **Why it matters.** `/csp-report` starts answering 404 or 415 (route renamed, content type no
  longer accepted, endpoint behind auth, `report-to` group renamed in the header only): Chromium
  still queues; the default run is green; no violation ever reaches the log or the metric.
- **Evidence:**
  ```csharp
  // tests/Croft.Web.E2E/CspScriptTests.cs:102-107
  if (Browser.BrowserType.Name == "chromium")
  {
      Assert.That(() => { lock (queuedReports) { return queuedReports.Any(r => r.StartsWith("csp|csp-violation|", ...) ...); } },
          Is.True.After(15000, 250), "Chromium queued a csp-violation report for the 'csp' group (/csp-report)");
      return;
  }
  ```
- **Fix.** In the Chromium branch, POST the captured report body to `{BaseUrl}/csp-report` with
  `Content-Type: application/reports+json` through `Page.APIRequest` and assert 2xx plus the log
  count rising; or drop the `return` (Chromium also honours `report-uri` on the same header).

### I-9. Nothing pins `SaveTokens = false` or that the auth cookie carries no token

- **File:** `tests/Croft.Web.Tests/TokenSessionEventsTests.cs:64-74`; `src/Croft.Web/Program.cs:134-136`
- **What is wrong.** The invariant "tokens never live in the cookie" is one configuration line.
  No test reads the configured `OpenIdConnectOptions`, and no test inspects
  `AuthenticationProperties.GetTokens()` after `OnTokenValidated`. The nearest test checks that
  no *claim* named `access_token` is added, which is not where ASP.NET Core stores tokens (it
  stores them in `Properties`). `grep -rn SaveTokens tests/` → nothing.
- **Why it matters.** Flip `SaveTokens` to `true`, or a refactor calls `properties.StoreTokens(...)`,
  and the access and refresh tokens go into the data-protected cookie with every test green.
- **Evidence:**
  ```csharp
  // src/Croft.Web/Program.cs:136
  options.SaveTokens = false;
  // tests/Croft.Web.Tests/TokenSessionEventsTests.cs:73
  identity.FindFirst("access_token").Should().BeNull(...);
  ```
- **Fix.** In a factory test resolve `IOptionsMonitor<OpenIdConnectOptions>.Get(OpenIdConnectDefaults.AuthenticationScheme).SaveTokens`
  and assert `false`; in `TokenSessionEventsTests` assert `context.Properties.GetTokens()` is
  empty after `OnTokenValidated`.

### I-10. The production token store's `RemoveBySubAsync` (sub-only back-channel logout) is tested only on the in-memory store

- **File:** `tests/Croft.Web.Tests/InMemoryTokenStoreTests.cs:74-87`; `tests/Croft.Web.Tests/BackchannelLogoutServiceTests.cs:46-73`;
  `tests/Croft.Web.Tests/SharedSessionStoreTests.cs:338-356`
- **What is wrong.** "Remove only the matching rows, leave the others" is asserted against
  `InMemoryTokenStore` only. The Postgres path through `/backchannel-logout`
  (`SharedSessionStoreTests`) exercises the `sid` branch only; the `sub`-only token is covered
  by `BackchannelLogoutServiceTests`, on the in-memory store. `grep -rln PostgresTokenStore tests/`
  → `SessionLifetimeTests.cs`, `WebDbRegistrationTests.cs`; neither calls `RemoveBySubAsync`.
- **Why it matters.** A `WHERE` bug in `PostgresTokenStore.RemoveBySubAsync` (all rows, or none)
  is invisible. Unverified by execution.
- **Fix.** A sub-only logout-token case in `SharedSessionStoreTests`: two users, two sessions
  each; post a token with `sub` only; assert exactly that user's rows are gone.

### I-11. The tag and category pages' listing filter is never asserted: the fake ignores `TagSlug`/`CategorySlug`

- **File:** `tests/Croft.Web.Tests/FakeBlogApiClient.cs:268-290`; `tests/Croft.Web.Tests/Components/TagPageTests.cs:42`,
  `CategoryPageTests.cs:16-43`, `TagSeoHeadTests.cs:24-25`; `src/Croft.Web/Components/Pages/Tag.razor:100`
- **What is wrong.** `FakeBlogApiClient.GetPostsAsync` filters by `Status`, `Kind` and
  `SearchTerm` only. No component test asserts the request the Tag or Category page sends carries
  `TagSlug == slug` / `CategorySlug == slug`; the only `PostRequests` assertion for these pages is
  `OnlyContain(r => r.PublicView)`. `TagSeoHeadTests.cs:24-25` says so in a comment.
- **Why it matters.** Lose `TagSlug: Slug` at `Tag.razor:100` and every tag page lists every
  published post on the site; all component tests for those pages pass. E2E may cover the
  outcome (unverified).
- **Evidence:**
  ```csharp
  // tests/Croft.Web.Tests/FakeBlogApiClient.cs:273-286 (no TagSlug/CategorySlug branch)
  if (request.Status is { } status) { filtered = filtered.Where(p => p.Status == status); }
  if (request.Kind is { } kind) { filtered = filtered.Where(p => p.Kind == kind); }
  if (!string.IsNullOrWhiteSpace(request.SearchTerm)) { ... }
  ```
- **Fix.** Make the fake honour both slugs against the summary's `Tags`/`Category` and add one
  assertion per page that a post without the tag/category is not rendered.

### I-12. `TagsPage` load/add and `MediaGrid` delete catch only `SessionExpiredException`, and the tests throw only that type (the test gap behind PR #4 I-2)

- **File:** `tests/Croft.Web.Tests/Components/AdminPagesSessionExpiryTests.cs:23-48`;
  `src/Croft.Web/Components/Pages/Admin/TagsPage.razor:197, 215, 304, 324`; `src/Croft.Web/Components/Admin/MediaGrid.razor:253`;
  `tests/Croft.Web.Tests/FakeBlogApiClient.cs:431-471` (no create-failure hook)
- **What is wrong.** The four `TagsPage` methods and `MediaGrid.ConfirmDeleteAsync` catch the
  narrow type; the tests seed exactly that type, so they prove the narrow path and nothing
  else. No test makes `GetCategoriesAsync`, `GetTagsAsync`, `CreateCategoryAsync`,
  `CreateTagAsync` or `DeleteMediaAsync` throw a plain `HttpRequestException`. The fake's create
  methods have no failure hook, so the add paths cannot be failure-tested as it stands.
  `TagsPageTests.cs:197, 252` do test `UpdateFailure`, which drives methods that already catch
  `HttpRequestException` (`:350, :423`).
- **Why it matters.** An admin opens Tags during an API restart: `GetCategoriesAsync` throws,
  nothing catches it, the circuit dies. Every test passes. PR #4 I-2 names the code fix; nothing
  will hold it.
- **Evidence:**
  ```razor
  @* src/Croft.Web/Components/Pages/Admin/TagsPage.razor:197 *@
  catch (SessionExpiredException ex)
  ```
  ```csharp
  // tests/Croft.Web.Tests/Components/AdminPagesSessionExpiryTests.cs:26
  var fake = new FakeBlogApiClient { CategoriesFailure = new SessionExpiredException() };
  ```
- **Fix.** `CreateFailure`/`DeleteFailure` hooks on the fake and red-first tests with
  `new HttpRequestException("...")` for load, add and delete, asserting the error line and that the
  rows/tiles are kept. Also `PostEditPage.LoadTagSuggestionsAsync`/`SearchCategoriesAsync`
  (`PostEditPage.razor:308, 322`), same shape, no failure test.

### I-13. `MainLayout`'s `[PersistentState]` handoff (W-023) is never exercised, and its positive test asserts only that the fake was called

- **File:** `tests/Croft.Web.Tests/Components/MainLayoutTests.cs:20-39`; `tests/Croft.Web.Tests/BlogBunitContext.cs:38-42`;
  `src/Croft.Web/Components/Layout/MainLayout.razor:52-57, 83-89, 107`
- **What is wrong.** `BlogBunitContext` registers `BunitPersistentComponentState` "to verify
  [PersistentState] restoration", but `grep -rn "PersistentState\." tests/Croft.Web.Tests/` → 0:
  no test calls `Persist(...)` before a render or `TriggerOnPersisting()` after. `MainLayout` is
  the only component in `src/Croft.Web/Components` with `[PersistentState]`, so its restore branch
  (`:83-89`) and its assignment (`:107`) never run under test. The positive test
  (`SidebarCategories_FetchIsBoundedToTheTop_NeverTheWholeTable`) asserts
  `fake.TopCategoryRequests.Should().ContainSingle()` and never that a category link rendered;
  the only category-link assertion in the file is the negative one after a failure (`:50-51`).
- **Why it matters.** (a) Delete the restore branch: every admin/editor page load fetches top
  categories and page navigation twice; green. (b) Restore `State` but forget to assign `_pages`:
  empty sidebar on every interactive page; green. (c) Drop `Categories="@_categories"` from the
  `<SidebarNav>` tag: no categories anywhere; green.
- **Evidence:**
  ```csharp
  // tests/Croft.Web.Tests/Components/MainLayoutTests.cs:34-38
  Render<MainLayout>();
  fake.TopCategoryRequests.Should().ContainSingle().Which.Should().Be(12, ...);
  fake.CategoryPageRequests.Should().BeEmpty(...);
  ```
- **Fix.** Two tests: `PersistentState.Persist(key, new MainLayoutState(...))` then render, assert
  no fetch and the persisted links render; render without state, `TriggerOnPersisting()`, assert
  the persisted state equals what was fetched. In the bounded-fetch test also assert the
  `#sidebar-nav a[href^='/category/']` hrefs.

### I-14. `PostsControllerTests` reads page 1 of the default 10-item public list on a class-shared database; one more published post makes it order-dependent

- **File:** `tests/Croft.Api.Tests/PostsControllerTests.cs:144-150` (also `:47-52, :70-75, :343-349, :406-410, :568-574`);
  `src/Croft.Shared/Contracts/PostListRequest.cs:15`; `src/Croft.Api/Services/PostService.cs:322-323`
- **What is wrong.** These tests call `GET /api/posts` with no `pageSize` and `Single`/`ContainSingle`/`NotContain`
  on `Items`. The class database accumulates every test's posts; the public list is `PublishedAt
  DESC`, page size 10. `PublicList_SummaryCarriesUpdatedAt` publishes on a fake clock at
  2026-10-05, older than every other test's now-published post; by reading, nine class posts are
  newer, so when it runs last its post is item 10 of 10. Any further published post in the class
  pushes it to page 2 and `Single` throws, depending on xUnit's method order.
  `Unpublish_SetsDraft_ClearsPublishedAt_DisappearsFromPublicList` (`:568-574`, published at
  now − 1 h) is the mirror: its `NotContain` goes vacuous the moment the post would be on page 2
  anyway. `PublicList_OnlyReturnsPublishedPosts` (`:37-53`) also never asserts the draft is
  absent. Unverified by execution.
- **Evidence:**
  ```csharp
  // tests/Croft.Api.Tests/PostsControllerTests.cs:145-149
  var listResponse = await anonymous.GetAsync("/api/posts");
  ...
  result!.Items.Single(p => p.Slug == "timestamped").UpdatedAt
  ```
- **Fix.** `?pageSize=100` (the cap) or `?q=<unique token>` in every list assertion of the class,
  as `PublicList_ListsPostTagsSortedByName` (`:262`) and `PostKindTests` already do; add
  `NotContain(p => p.Slug == "draft-post")` to `PublicList_OnlyReturnsPublishedPosts`.

### I-15. The Web analytics proxy's `X-Visitor-IP` / `X-Analytics-Key` forwarding has no test, and E2E cannot see it break

- **File:** `src/Croft.Web/Program.cs:527-537`; `src/Croft.Api/Controllers/AnalyticsController.cs:26-33`;
  `tests/Croft.Web.E2E/AnalyticsViewCountTests.cs`, `StaticBeaconTests.cs`
- **What is wrong.** Per-visitor dedupe depends on the Web app forwarding the browser's address
  with the ingest key; without it the API keys every view on the Web pod's IP. `grep -rn
  "X-Visitor-IP\|X-Analytics-Key" tests/Croft.Web.Tests/*.cs` → 0. The E2E tests run browser and
  Web pod on 127.0.0.1, so forwarded IP and socket IP are the same string and `expected: 1` holds
  whether or not the header is sent. The API side is well tested; the Web side that makes it
  work is not.
- **Why it matters.** Rename the config key or drop the `forward.Headers.Add("X-Visitor-IP", …)`
  line: every suite green, production view counts collapse to one view per post per day across
  all readers.
- **Fix.** A `WebApplicationFactory` test with a capturing BlogApi handler (`StubBlogApiHandler`
  exists): POST a beacon with a fake `RemoteIpAddress`, assert the forwarded request carries that
  `X-Visitor-IP`, the configured key, the body, and that a staff cookie produces no forwarded
  request (`Program.cs:496-501`).

### I-16. Test gaps behind PR #4's other Important findings

Each of these is a code finding in PR #4 that no test would have caught and none will hold once
fixed:

- **PR #4 I-1** (invalidation and `pg_notify` under the request token): no test aborts a write
  between `SaveChangesAsync` and `ContentChangedAsync` (`PostService.cs:423-424`); cancellation in
  the API suite is confined to hosted-service tests. Fix: a `PostService.PublishAsync` test with a
  fake `ICacheNotifier` and a `SaveChangesInterceptor` that cancels the caller's token as a side
  effect; assert the notifier still ran for all four groups.
- **PR #4 I-3** (`CategoryService.CreateAsync`/`TagService.CreateAsync` skip validation): the
  blank-name tests are `UpdateCategory_BlankName_Returns400` and `RenameTag_BlankName_Returns400`,
  both PUTs; no test POSTs a blank or 129-char name. `CategoryService.cs:102-104` calls no
  validation. Fix: `CreateCategory_BlankName_Returns400`, `CreateCategory_NameOver128_Returns400_AndStoresNoRow`
  and the tag twins; they fail today for the right reason (201 and 500).
- **PR #4 I-9** (missing file on disk → 500 at `MediaController.cs:80`): `grep -rn FileNotFound
  tests/Croft.Api.Tests` → 0; `MediaControllerTests` deletes files only through the service.
  Fix: upload, `File.Delete` the stored file, GET → 404 and nothing above Information in
  `LogCollector`.
- **PR #4 I-6, I-7, I-8**: no test on cache-key normalisation (the bound test at
  `PublicReadCacheTests.cs:503` varies `page` only), none on the Web media proxy's disposal or
  `Cache-Control` pass-through (`Program.cs:476-480`; E2E `MediaProxyTests` covers 200/403 and
  decoded images), and no API-call baseline for any admin page load (`PublicPageApiCallBaselineTests`
  and `ReadingPageApiCountTests` cover public pages only). Minor individually; listed so the
  fixes land with tests.

---

## Minor

### M-1. `MobileContentsPanelTests` keeps a `WaitForTimeoutAsync(300)` that its own comment says is not there

- **File:** `tests/Croft.Web.E2E/MobileContentsPanelTests.cs:776-785`
- The summary two lines above says "never a sleep"; line 784 is a 300 ms sleep before the
  concrete `WaitForPanelSettledAsync`, which already waits for `getAnimations().length === 0`.
  Redundant (300 ms × 8 reads), the only `WaitForTimeout` in the suite, and the one that will be
  copied (AGENTS.md rule 6). Fix: delete line 784.

### M-2. `DraftBackupTests` disguises a 1 s sleep as a wait

- **File:** `tests/Croft.Web.E2E/DraftBackupTests.cs:152-154`
- `WaitForFunctionAsync("() => new Promise(resolve => setTimeout(() => resolve(true), 1000)))"` is
  `Task.Delay(1000)` with a negative assertion after it. A regression that writes editor B's text
  over the waiting backup after >1 s passes. Fix: a positive sentinel that proves the same
  debounce window elapsed (type one character into B's title, wait for *that* backup under
  another key, then assert `keyB` is untouched), or instrument `Storage.prototype.setItem` as
  `SessionLossTests.cs:124-140` does.

### M-3. Two gate waits in the API suite are unbounded

- **File:** `tests/Croft.Api.Tests/PageViewRetentionTests.cs:94`; `tests/Croft.Api.Tests/ApiRateLimiterTests.cs:328`
- `await held.Arrived;` / `await gate.Arrived;` with no timeout; every other use is
  `.WaitAsync(TimeSpan.FromSeconds(30))` (`ScheduledPublisherTests.cs:295`, `PostSummaryBackfillTests.cs:194`).
  If the purge issues no DELETE or the read never reaches the interceptor, the run hangs instead
  of failing. Fix: `.WaitAsync(TimeSpan.FromSeconds(30))`.

### M-4. `BackgroundJobLockTests` waiter test passes even if the lock is never taken, and spends 35 s doing it

- **File:** `tests/Croft.Web.Tests/BackgroundJobLockTests.cs:36-49`
- The assertion is `workRan.Should().BeTrue()` after the holder unlocked. If `RunAsync` skipped
  `pg_advisory_lock`, the work runs immediately and the assertion holds. The 35 s `pg_sleep`
  proves the waiter survives the 30 s command timeout but nothing checks the work ran *after* the
  unlock. Fix: assert `workRan` is false while the holder sleeps and true after; set
  `Command Timeout=2` on the test connection and hold for 3 s. Unverified (container).

### M-5. `ScheduledPublisherTests.TwoInstancesTickingTogether…`: both instances share one clock, so the `UpdatedAt == now` assertion cannot see a double publish

- **File:** `tests/Croft.Api.Tests/ScheduledPublisherTests.cs:89, 136-137, 155-156`
- Both ticks get the same `FakeTimeProvider`, so a post published twice carries the same stamp
  as one published once; the reason text ("would carry more than one instance's write") is
  false. The log-count assertion at `:165-171` still protects the test. Fix: give instance B
  `now.AddSeconds(1)` and assert the stamps partition the 20 ids.

### M-6. `WebTelemetryTests` collects the request-duration metric synchronously after the response

- **File:** `tests/Croft.Web.Tests/WebTelemetryTests.cs:91-93, 112, 167`; `tests/Croft.Web.Tests/Fixtures/TelemetryCapture.cs:76-96`
- `http.server.request.duration` is recorded in the hosting layer's `DisposeContext`, after
  `GetAsync` has returned; the tests call `CollectMetrics` at once while `WaitForMetricAsync`
  exists in the same fixture (used by `BlazorTelemetryTests.cs:209`). In the privacy test a
  missing point makes the "no PII in tags" loop pass vacuously. Ran it 8×, 8/8 passed, so
  latent. Fix: `WaitForMetricAsync` and `points.Should().NotBeEmpty()` before the loop.

### M-7. `AdminTests` icon-picker test mutates whichever category is first in the sidebar, usually another fixture's

- **File:** `tests/Croft.Web.E2E/AdminTests.cs:161-175, 193-200`; `tests/Croft.Web.E2E/WebTest.cs:275-282`
- The sidebar lists the top 12 by published-post count; after `HomeTests` (13 posts in one
  category) the first entry is that fixture's category. The test is self-consistent but writes
  shared instance state that later visual/accessibility fixtures read (sidebar icons). Fix: assert
  through the test's own category page or the public API; at minimum restore the icon.

### M-8. `CircuitAdmissionTests` writes its limited-Web log to one fixed path shared by every concurrent suite run

- **File:** `tests/Croft.Web.E2E/CircuitAdmissionTests.cs:48-51, 128-129`
- Port is instance-scoped (`5400 + N`); the log is `/tmp/blog-circuits-limit-web.log` with
  `FileMode.Create`. AGENTS.md allows parallel briefs on disjoint instance ranges; two runs
  truncate and read the same file (false pass or false fail on "Circuit start refused"). Fix:
  suffix the instance number.

### M-9. `TagRelatedToggleTests.WaitForPersistedAsync` polls the API in a tight loop

- **File:** `tests/Croft.Web.E2E/TagRelatedToggleTests.cs:90-112`
- `while (DateTime.UtcNow < deadline)` issuing `GET /api/tags?q=` back to back for up to 15 s
  against the shared Postgres and host; `.Single(...)` throws rather than asserts if the tag is
  missing. Fix: wait on the page's own outcome (the switch state at `:58-59`), then one read-back,
  or poll with `PollingInterval = 250` as `PageWritingTests:89-100` does.

### M-10. `LongPostEditorTests`' reconnect-modal watch is vacuous after every `GotoAsync`

- **File:** `tests/Croft.Web.E2E/LongPostEditorTests.cs:298-324`
- `WatchReconnectModalAsync` is installed with `EvaluateAsync` (not an init script) and only in
  `OpenNewPostWithBodyAsync`; each test then reloads the editor and calls
  `AssertNoReconnectAsync("after reloading the editor")`, where
  `window.__reconnectModalOpened === true` is `undefined === true`. The WebSocket counter (an init
  script) is the live check. Fix: move the observer into the `[SetUp]` init script.

### M-11. `ReadingPageCookieTests`' positive control does not prove `/admin/posts` issued the antiforgery cookie

- **File:** `tests/Croft.Web.E2E/ReadingPageCookieTests.cs:58-62`
- After the OIDC login (several documents) the context jar is inspected; the `/admin/posts`
  response's `Set-Cookie` is never read though `response` is in hand. Fix: clear the antiforgery
  cookie after login and assert `set-cookie` on the admin response itself, mirroring `:40-42`.

### M-12. `PostCreationTests` checks the banner by HTTP status only

- **File:** `tests/Croft.Web.E2E/PostCreationTests.cs:53-57`
- 200 from `Page.APIRequest`, no `complete && naturalWidth > 0`, no content type; a 200 HTML
  error page passes (rule 7). `WebTest.AssertImageLoadsAsync` exists. Fix: use it.

### M-13. `StaticBeaconTests` reads exact beacon counts right after an unrelated sync point (unverified)

- **File:** `tests/Croft.Web.E2E/StaticBeaconTests.cs:33-37, 77-79, 97-106`
- The beacon is sent before `data-interactive` is set (`static-glue.js:42` vs `:64`), but the
  count is filled by Playwright's `Request` event and read right after the attribute wait; I
  could not confirm the CDP ordering guarantees `requestWillBeSent` lands before the
  `Runtime.evaluate` result. Positive test may fail spuriously; negative tests may pass while a
  beacon went. Fix: `WaitForRequestAsync` registered before navigation in the positive tests (as
  `:132-141` does with the response); a positive beacon action after the negative navigations.

### M-14. `MastheadTests.Page2_HasNoMasthead` is vacuous unless other fixtures filled page 1

- **File:** `tests/Croft.Web.E2E/MastheadTests.cs:110-118, 173-175`; `dev/seed-e2e.sh:22`
- The fixture publishes two posts; page 1 holds 10; the seed is empty; Home renders `?page=2`
  empty rather than 404. On a filtered run the test asserts "no masthead" on a page with no
  posts. Fix: publish 11 run-keyed posts through the API and assert a `.post-card` on page 2
  first.

### M-15. `KeyboardFocusTests.MouseClick_OnAShellButton_LeavesNoOutline` is vacuous for the two phone cases

- **File:** `tests/Croft.Web.E2E/KeyboardFocusTests.cs:77-85`
- For `.menu-button` the focus precondition is skipped (focus moved into the drawer) and the
  outline of the now-unfocused button is asserted `none`, which an unfocused element always has.
  Fix: read `document.activeElement`'s outline, or drop the two cases.

### M-16. `AccessibilityTests.CardCssContract_ComputedStyles` pins the clamp count, not that the summary is clamped

- **File:** `tests/Croft.Web.E2E/AccessibilityTests.cs:330-331`; `src/Croft.Web/Components/Blog/PostCard.razor.css:151-161`
- `-webkit-line-clamp: 3` does nothing without `display: -webkit-box; overflow: hidden` from the
  base rule; lose those and every line renders while the count is still declared. Fix: assert
  `clientHeight <= 3 × lineHeight + 1` and `scrollHeight > clientHeight` on a long summary.

### M-17. `ListMarkerTests` passes with `list-style: none`

- **File:** `tests/Croft.Web.E2E/ListMarkerTests.cs:40-55`
- Only the box's left edge and `padding-left ≥ 1em` are asserted; a global reset that removes
  bullets keeps both. Fix: also assert `listStyleType !== 'none'` and a painted `::marker`.

### M-18. `ContentWrapperTests` accepts any width below the cap

- **File:** `tests/Croft.Web.E2E/ContentWrapperTests.cs:40-41`
- `<= 80rem + 1` at 1920/2560 where it should be exactly 80rem; a `max-width` of 60rem passes.
  Fix: `Is.EqualTo(g.Rem * 80).Within(1)` where main's content box exceeds 80rem.

### M-19. `ScrollSpyTests` reads "still active after the jump" before the spy's own scroll frame

- **File:** `tests/Croft.Web.E2E/ScrollSpyTests.cs:53-57`
- The clicked link gets `aria-current` synchronously; the regression the comment describes
  ("the previous entry staying active") shows on the scroll handler's next animation frame, which
  nothing waits for (`StaticGlueTests.cs:198-210` has the helper). Fix: wait for the scroll event
  plus two frames before the one-shot read.

### M-20. `PostHeroGradientTests.TitleFirstLine_SitsOnADarkEnoughBackdrop` passes vacuously when the banner fails to load

- **File:** `tests/Croft.Web.E2E/PostHeroGradientTests.cs:132-141`
- `ShowPostAsync` waits for title and subtitle, never for the banner `<img>`; the fallback
  tartan is ≤118 per channel everywhere, so the "dark enough" sample holds with no gradient.
  Fix: `AssertImageLoadsAsync(Page.Locator(".post-hero__media img"))` in `ShowPostAsync`.

### M-21. `ProfileLinksTests.AdminPage_CarriesTheProfileLinksToo` never reaches the interactive render it claims to cover

- **File:** `tests/Croft.Web.E2E/ProfileLinksTests.cs:37-43`
- The head is read right after `GotoAsync`, no `WaitForInteractiveAsync`; a `HeadOutlet`
  duplicate after the circuit connects is invisible. Fix: wait for interactive first.

### M-22. `ThemeTests.DarkMode_TextButton_ContrastsAtLeast45AgainstSurface` ignores alpha

- **File:** `tests/Croft.Web.E2E/ThemeTests.cs:294-295`
- A local `rgba?(r, g, b` parser drops alpha although the alpha-aware `ColorContrast` helper
  exists; a semi-transparent button colour is rated as opaque. Fix: use `ColorContrast`.

### M-23. Slow by construction in the E2E suite

- **File:** `tests/Croft.Web.E2E/MobileContentsPanelTests.cs:974-995`, `MobileOutlineTests.cs:186-194, 223-278, 629-650`,
  `PostRailStickyTests.cs:315-334`, `ScrollSpyTests.cs:98-113`, `ScreenshotTests.cs:39, 243-282`
- `MobileContentsPanelTests` (17 tests), `MobileOutlineTests` (6), `PostRailStickyTests` (4) and
  `ScrollSpyTests` publish a fresh post **per test** by logging in through the IdP and driving the
  Blazor editor, while `StaticReadingTest.PublishAsync` (one API call) exists and the newer
  fixtures use it: about 28 IdP logins for data no test cares about the shape of.
  `MobileOutlineTests`' sampler scans 61 steps × 300 ms both ways (≈37 s per test, twice) and
  `FindPinThresholdAsync` runs twice per test. `ScreenshotTests` is `ParallelScope.None`: 66
  captures (50 with an IdP login in a fresh context) run while two instances idle, and the only
  assertion is a non-empty PNG. None of it adds assertion strength. Fix: API-published fixtures;
  cache the pin threshold and narrow the band; move `ScreenshotTests` behind an opt-in category
  like `LargeData`, or reuse one `storageState` per role.

### M-24. Vacuous or mislabelled unit tests

- `tests/Croft.Web.Tests/Components/ParameterChangeTests.cs:16-118`: renders two independent
  instances and asserts the fake saw both slugs; that is two first renders, not a parameter
  change. The real path is pinned in `PostPageLoadTests.cs:110-115` and `PageLoadRaceTests.cs:76, 103`.
  Delete or rewrite as `cut.Render(p => p.Add(x => x.Slug, "b"))`.
- `tests/Croft.Web.Tests/Components/PostEditPageTests.cs:370-373`: two redirect tests synchronise
  with `await Task.Yield()`; they pass only because the fake completes synchronously. Fix:
  `WaitForAssertionAsync` on `navigation.Uri`.
- `tests/Croft.Web.Tests/Components/PostEditPageTests.cs:186-188, 448-461`: preview tests assert
  `.post-hero` exists (tautological: `Find` throws when absent), never the hero title. Fix: assert
  `.post-hero h1` text.
- `tests/Croft.Web.Tests/MarkdownRendererTests.cs:141-143`: `Render_RawHtmlStillDisabled` asserts
  only `NotContain("<script>")`; stripping silently passes, and `<img onerror>`, `javascript:`
  hrefs and `<iframe>` are not covered. Fix: assert the escaped form is present; add the vectors.
- `tests/Croft.Web.Tests/SharedDataProtectionKeysTests.cs:44-52`: the documented break-it-once
  fixture `CreateFactoryWithItsOwnFileKeyRing()` has no caller. Make it a negative test or delete.
- `tests/Croft.Web.Tests/BackchannelLogoutEndpointTests.cs:105-118, 192-205`: the "never logs the
  token" capture inspects the formatted message only, not structured state or scopes
  (`CspReportTests.cs:274-290` captures state pairs). Fix: capture state and scopes.
- `tests/Croft.Web.Tests/BackchannelLogoutTokenValidatorTests.cs`: no unsigned / `alg: none`
  case; a future `RequireSignedTokens = false` is unguarded. Fix: add one.
- `tests/Croft.Api.Tests/SecurityHeadersTests.cs:170-172`: the theory covers 200, `/health` and a
  routing 404, never a response from `UseExceptionHandler`, though the class says it does. Fix:
  add a 400 (`CreatePostRequest("", "x")`) and a 500 (`ThrowingCategoryService`) case.
- `tests/Croft.Api.Tests/SearchControllerTests.cs:15-40`: "ReturnsMatchingPublishedPosts" creates
  no draft; `publicView` is untested. Fix: add a draft with the same term.
- `tests/Croft.Api.Tests/AnalyticsControllerTests.cs:72-74`: `Summary_AcceptsLocalDateTime`
  asserts only success, not that the recorded view is inside the range; and the "no key
  configured → header ignored" branch (`AnalyticsIngestKey.cs:62-63`) has no test. Fix: assert
  `TotalViews == 1`; a factory without `Analytics:IngestKey`.
- `tests/Croft.Api.Tests/RoleGroupAuthTests.cs:50-106`: `"Blog Editors"` is configured but never
  sent in a token; the Editor wiring of `Oidc:RoleGroups` is untested at the HTTP layer.
- `tests/Croft.Api.Tests/MediaControllerTests.cs:85-86`: `Upload_MaliciousFilename_IsSanitized`
  inspects the DTO only; `SaveAsync` names files by GUID so the DTO can never contain `..`
  whatever the storage did. Fix: assert the row's `FilePath` resolves under the storage root and
  exactly one file exists there. Also `LocalFileStorage.cs:39`'s containment check has no
  `OpenRead("../x")` test (PR #4 M-10).
- `tests/Croft.Api.Tests/MediaControllerTests.cs:35, 60-62, 137`, `AnalyticsControllerTests.cs:29, 87, 104`:
  variables named `anonymous` are `factory.CreateClient()`, an authenticated no-role user; the
  genuinely unauthenticated path is `AsAnonymous()` (see C-1).
- `tests/Croft.Web.Tests/Components/ViewsChartTests.cs:31-33` and `PostAnalyticsPage`: the chart
  test asserts a `.mud-chart` exists; the page's 30-day zero-fill and its two error branches
  have no test. Fix: a `PostAnalyticsPageTests` with `FakeTimeProvider`.
- `tests/Croft.Web.Tests/Components/PostTests.cs:45-51`: `OnNotFound` is asserted for one page
  only; Category, Tag, StaticPage and `NotFound.razor`'s catch-all branch have no component test.

### M-25. Leaks and clean-up

- `tests/Croft.Api.Tests/MediaControllerTests.cs:261, 283`: two factories never disposed
  (`Upload_DbSaveFails_LeavesNoFile`); their hosted services keep running against the class
  database while later tests run. Fix: `await using`.
- `tests/Croft.Web.Tests/BrandingEndpointTests.cs:51-60`, `FeedXmlTests.cs:294-302`,
  `DefaultFallbackBannerTests.cs:156-163`: `new WebApplicationFactory<Program>()...CreateClient()`
  drops the factory; hosts live to process exit under `MaxParallelThreads = 8` with the
  process-wide meter. Fix: hold and dispose.
- `tests/Croft.Web.Tests/AuthCookie.cs:55-60`, `DataProtectionKeysTests.cs:65-70`:
  `/tmp/blog-web-keys-{guid}` directories are never deleted (AGENTS.md rule 10); hard-coded
  `/tmp` rather than `Path.GetTempPath()`.

---

## Checked and found sound

- **Suite plumbing.** `WebTest.StackUrls` worker→instance mapping (W-064) and
  `[assembly: Parallelizable(ParallelScope.Fixtures)]` + `LevelOfParallelism(1)`: fixtures never
  share an instance at the same time; `dev/run-e2e.sh` refuses listening ports, stops exactly what
  it started, and unsets `E2E_WEB_URL`/`E2E_API_URL` for the mapped run. `PostgresDatabaseFixture`
  / `SharedPostgresContainer` (W-062): one DB per class, forced drop, lease released in `finally`.
  `BlogBunitContext`: per-test context, no shared state. `ReportedCspViolationCount` is
  per-instance and delta-based, so another fixture's violations cannot satisfy it.
- **Interceptors and clocks.** `Held*`, `Holding*`, `Rendezvous*`, `Throwing*`,
  `SqlCommandSignal`, `SqlCommandCounter`: every hold bounded at 30 s, concrete events, no sleeps
  (except the two unbounded arrivals in M-3). `FakeTimeProvider`/`FakeTimerTimeProvider`/`ManualTimeProvider`
  throughout the time-dependent tests; the 21 `Task.Delay` uses in `tests/` are all bounded polls
  of a concrete condition (rule 6) apart from M-1 and M-2.
- **API.** `MediaControllerTests` (magic bytes, extension, 10 MB, filename, DB-failure cleanup,
  draft-asset 403, row-and-file deletes), `PostsControllerTests` validation with stores-no-row
  assertions, `PostKindTests`, `PagesControllerTests`, `RelatedPostsControllerTests`,
  `CategoriesControllerTests`, `TagsControllerTests` (scoped by token/prefix/id),
  `AnalyticsControllerTests` (exact HMAC, date salt, 20-way dedupe, draft not counted, UTC buckets),
  `PageViewRetentionTests` (exact boundary, two-instance race), `ScheduledPublisherTests` /
  `ScheduledPublisherSleepTests` (lock skip on the real advisory lock, mid-tick foreign write,
  exact publish instants), `PostSummaryBackfillTests`, `MigrationRetryTests` (exact backoff on the
  fake clock), `ConcurrentMigrationTests` (two migrators meet at the lock), `MigrationContractGuardTests`,
  `NavigationPlacementMigrationTests`, `PostPartialIndexTests` (re-explained with `enable_seqscan=off`),
  `ReadReplicaRoutingTests` (pinned `[AllowAnonymous]` GET sweep on a read-only connection),
  `ApiRateLimiterTests`, `ConnectionPoolSettingsTests`, `CountCacheTests`, `SecurityHeadersTests`
  (bar M-24), `ExceptionMappingTests`/`ApiExceptionHandlerLogTests`, `ApiMetricsTests`,
  `ApiTelemetryTests`/`TraceCompositionTests`, `PublicPageQueryBaselineTests` (lower bound of 1 so
  a cache cannot hide it), `ContentExportTests`/`ContentImportTests`/`ContentExportCompletenessTests`
  (byte-identical round trip, consistent snapshot under `RepeatableRead`, refusals leave the target
  empty), `StartupConfigurationTests`, `StartupCommandTests`, `BlogDbConnectionStringTests`,
  `SlugServiceTests`, `MigrateContainerTests`/`ReadyToRunPublishTests` (opt-in; exit code and PE
  headers). `PublicReadCacheTests` apart from I-1: the six reads, cross-instance invalidation, the
  straddling-refill cases, the bound, not-found never cached.
- **Web unit.** `CookieTests` (real cookie flags, 400 without antiforgery), `CspHeaderTests` /
  `CspPolicySnapshotTests` / `CspPolicyTests` / `CspReportTests` (exact header strings, hashes from
  real renders, report limits and sanitising), `ScriptIntegrityTests` (SHA-256 of served bytes),
  `TokenSessionEventsTests` / `UserAccessTokenServiceTests` (single-flight refresh, 60 s window,
  rotation, disposal), `InMemoryTokenStoreTests`, `BackchannelLogout{Service,Endpoint,TokenValidator}Tests`
  (bar M-24), `SessionLifetimeTests` / `SessionPurgeTests` / `SessionRemovalTests` /
  `SharedSessionRefreshTests` / `SharedSessionStoreTests` / `SharedDataProtectionKeysTests` /
  `DataProtectionKeyImportTests` / `HealthEndpointTests` (by reading: outcomes asserted, no sleeps;
  unverified by execution), `WebMetricsTests`, `CircuitAdmissionTests`, `CircuitLimitTests`,
  `BlazorTelemetryTests`, `PageRequestRateLimiterTests`, `BrandingServiceTests` /
  `BrandingEndpointTests` / `BrandingContrastTests` / `BrandingThemeCssTests`, `SitemapServiceTests` /
  `SitemapXmlTests` / `FeedXmlTests` / `RobotsTxtTests` / `WebFingerDocumentTests` /
  `WebFingerEndpointTests` / `SiteFilesServiceTests` / `SiteFilesEndpointTests` / `SecurityTxt*Tests`,
  `K8sBaseTests` / `K8sPlacementAndPolicyTests`, `WorkaroundRegisterTests`, `RepositoryNamingTests`,
  `ReservedSlugsGuardTests`, `LocalUrlTests`, `RoleGroupMapperTests`, `BlogApiClientTests` /
  `StubBlogApiHandler` (the request the client made and the mapped DTO, not stub presence),
  `MarkdownRendererTests` (bar M-24), `IconCatalogTests`, `MudThemeStyleCspTests`,
  `ThemeBootstrapScriptCspTests`, `PublicPageApiCallBaselineTests`.
- **Components.** All 65 bUnit files read; 438 tests pass in 5 s. Draft backup
  (`DraftBackupDecisionTests`, `DraftBackupRoundTripTests` with a reflection guard over every
  `PostEditModel` property, `DraftRestorePromptTests`, `PostEditorFormDraftHooksTests`,
  `PostEditPageDraftTests`, `PostEditPageSessionTests`): outcomes on the editor value, the prompt
  text and the JS call arguments. `PostsPageTests` / `DraftsPageTests` / `PagesPageTests` (request
  fields and rendered rows together), `TagsPageTests` (bar I-12), `MediaGridTests` (gate + W-076,
  no sleep), `PostEditPageTests` (bar M-24), `PostEditorFormTests`, `MarkdownEditorTests`,
  `BannerPickerTests`, `IconPicker{,Dialog}Tests`, `MediaPickerDialogTests`, `FeedbackPanelTests`,
  `DraftReviewPageTests`, the SEO head family (exact attribute and JSON-LD values, injection cases
  parse the payload), `SidebarNavTests`, `AreaBoundaryTests`, `SiteFooterTests`,
  `NotFoundContentTests`, `ErrorPageTests`, `HomeMastheadTests`, the card/list/pager/hero/meta/rail/
  body/related/TOC/pills/chip/icon/banner/badge/surface components, `CategoriesPageTests`,
  `SearchPageTests`, `StaticPageTests`, `PageLoadRaceTests` (mechanically sound; its premise that a
  static SSR page instance is reused across query-only navigations is doubtful and worth the
  owner's one-line answer), `ReadingPageLoadTests`, `PostPageLoadTests`. The 17
  `WaitForAssertionAsync(() => cut.Find(...))` waits are sync points followed by value assertions.
- **E2E behaviour.** `SessionLossTests` (every field re-read via `EveryFieldScenario.AssertSavedAsync`,
  circuit deaths from the log, backup timing instrumented), `PageWritingTests` (outcomes re-read from
  a signed-out context, 404 on raw responses), `EditorTests`, `EditorTitleTests`, `EditorLayoutTests`,
  `DraftBackupTests` (bar M-2), `LongPostEditorTests` (bar M-10), `SchedulingTests` (fixed 2030
  target), `AnalyticsViewCountTests` and the analytics halves of `PostPageTests`/`AdminTests` (fresh
  post per test, beacon 204 awaited, count read from the admin page), `MediaProxyTests`,
  `BackchannelLogoutTests` (real signed token through the mock's JWKS; constant `jti` is fine, the
  validator enforces presence only), `AuthenticationTests` (open-redirect check on the final URL),
  `HealthEndpointsTests`, `NotFoundTests`, `SiteFilesTests`, `BrandingFilesTests`, `StructuredDataTests`,
  `StaticPageTests`, `StaticSearchTests` (run-keyed terms, exact counts, no WebSocket),
  `StaticReadingPagesTests`, `ReadingPageNavigationTests`, `ReadingPageCookieTests` negative half,
  `HomeTests` (counts derived from the public API at assertion time), `RelatedSectionTests` /
  `TagRelatedToggleTests` (ranking matches `PostService.cs:262-267`), `CircuitAdmissionTests` (bar
  M-8), `LargeDataTests` / `HaTests` (opt-in; bounded polls; failures recorded, never swallowed).
  Every `catch` in the E2E suite either rethrows with diagnostics, records the failure, or is a
  bounded readiness poll; no swallowed assertion. Background mutators: the scheduled publisher is
  inert in the normal run (the only scheduled fixtures target 2030), which the instance-global
  assertions in `AdminReadingPagesTests:27`, `HomeTests:64`, `AdminTests:366/379` rely on; worth one
  line in the suite docs so a future "now + N s" schedule does not break them.
- **E2E visual/CSP.** `CspViolationProbe` (init script, per-step drain, `disposition` split),
  `CspPublicPagesTests` (fonts loaded, icons decoded, highlighter ran, manifest parsed),
  `TrustedTypesTests` (pins the report-only header; zero reports on a code post, non-zero from
  `blazor.web.js` only), `CspScriptTests.EveryReadingPage_RendersEachScriptWithAnIntegrityDigest`,
  `CspTests` area-boundary and circuit-drop measurements, `SidebarFooterAlignmentTests`,
  `FooterProfileIconsTests`, `KeyboardFocusTests` Tab traversals (bar M-15), `MastheadTests` (bar
  M-14), `DefaultBannerPixelTests`, `ThemeTests` (bar M-22), `StaticGlueTests`, `LayoutTests`,
  `PhoneDrawerScrollTests`, `RailStaysPutTests`, `MobileContentsPanelTests` (bar M-1, M-23),
  `MobileOutlineTests` (bar M-23), `OutlineTouchSlopTests`, `PostHeroFitTests`, `PostRhythmTests`,
  `ReadingMeasureTests`, `FluidTypeTests`, `FeaturedCardFitTests`, `PostRailStickyTests`,
  `StaticAssetTests`, `ReadingPageScriptsTests`, `NoMudBlazorOnReadingPagesTests`,
  `AccessibilityTests` axe runs (no rule exclusions, real fixtures; bar M-16), `ColorContrast` /
  `ColorContrastTests`, `SiteFooterTests`, `BaseStylesTests`. Tolerances are 1–2 px or 1–2 ch with
  the reason written down. Theme preference, `EmulateMedia` and init scripts are per browser
  context, so no cross-test leak in this half.

## Not verified here

- The API suite, the ten Testcontainers Web classes and the E2E suite were not executed: every
  "passes"/"fails" statement about them is by reading. The three flaky-test causes (I-1 to I-3)
  are the strongest reading of the code; the NUnit/xUnit failure messages from a loaded run would
  confirm or refute them in one line each.
- Whether MudBlazor 9.11's `MudDataGrid.ReloadServerData` cancels an in-flight `ServerData` call
  (I-3 second candidate).
- CDP event ordering for `sendBeacon` requests relative to `Runtime.evaluate` (M-13).
