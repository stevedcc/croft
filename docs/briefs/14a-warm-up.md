# Brief 14a: a pod is warm before it takes readers

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:** after a deploy or any pod restart, the first reader to open the home
page or a post gets it as fast as the hundredth (today 1–3.4 s for the first few, 0.03–0.15 s
after). Rollouts take a little longer instead; readers never meet a cold pod.

**CSP impact: none.** No markup, script or style changes. `CspSnapshots.txt` byte-identical.

## The evidence (production, 0.6.5, 2026-10-21)
Both Deployments restarted, then each page requested six times through crawford.dev, with
traces captured per pod (web server span → HttpClient → API server span → SQL):

| Page | first hits (s) | warm (s) |
|---|---|---|
| `/` | 2.7, 1.2, 2.4, 1.3 | 0.09–0.15 |
| `/post/load-testing-the-blog` | 3.4, 1.7, 1.8 | 0.11–0.16 |
| `/post/blog-online` | 0.5, 0.5 | 0.04–0.2 |
| `/privacy-policy` | 0.5, 0.3 | 0.03–0.06 |
| `/impressum` | 0.11–0.16 | 0.03–0.05 |

- **SQL is never the cause:** 20–340 ms in the worst cold case.
- **API:** the first call of each endpoint *on each API pod* spends 1.3–2.3 s in managed code
  around ~0.1 s of SQL (`GET api/posts` on a fresh pod: 2301 ms, SQL 123 ms; `api/posts/{slug}`:
  1772 ms, SQL 210 ms). That is EF Core's model build and query compilation plus first-use JIT that
  ReadyToRun cannot cover.
- **Web:** the first render of each page type *on each Web pod* spends 0.5–1.6 s of web-only time.
- **Not CPU starvation:** the site sets no CPU limit and the nodes had 2.5–3 of 4 cores idle. Each pod
  uses 20–27 CPU-s to start. The work is first-use and mostly single-threaded on slow arm64 cores.
- The pods report ready (`/health/ready`) long before that work is done. Readers pay for it.

## The design (decided; don't re-open it)
Each process warms itself up **after Kestrel is listening and before `/health/ready` says ready**:
it sends its own public read requests to itself over loopback, once each and **sequentially**,
then opens the readiness gate. The Web warms its page types and the API warms its query shapes.
Every API pod must warm itself, because the Web's warm-up reaches only the API pods the Service
happens to pick.

- **Settings**, the same for both apps:
  - `Warmup:Enabled` (default `true`).
  - `Warmup:Timeout` (default `00:01:00`): the whole warm-up's budget.
  - `Warmup:RequestTimeout` (default `00:00:15`): each request's budget.
- **The gate never blocks forever.** When the warm-up finishes, fails or runs out of time, the gate
  opens. A failed or skipped request is logged and the warm-up carries on. The pod's other
  readiness checks are unchanged: the Web still needs its database and the API, the API still
  needs its database. The gate is one more condition, checked first.
- **Logging:**
  - One Information line at the end: `Warm-up finished in {ElapsedMs} ms: {Succeeded} of {Total}
    requests succeeded`.
  - One Warning when anything failed or the budget ran out, naming each failed or not-reached path
    with its status code or exception type.
  - Content that doesn't exist (an empty site has no post to warm) is **skipped, not failed**: no
    Warning.
- **Where requests go:** to the process's own listening port (from `IServerAddressesFeature`,
  with the host replaced by `127.0.0.1`), through a named HttpClient `"CroftWarmup"` registered in
  DI. Tests replace its primary handler with the TestServer's `CreateHandler()`, so they need no
  socket.
  - The requests are plain anonymous GETs, the path a reader takes. Nothing is sent with a
    session or a key.
  - Page views are counted only by the browser's JS beacon (`analytics.js`), so warm-up requests
    are never counted. Keep it that way, and pin it with a test (item 2).
- **Rate limits:** the limiters are concurrency limits (Web: 16 per client, `RequestLimits.cs`). A
  sequential warm-up holds one permit, so no exemption is needed and none is added.
- **Telemetry:** the warm-up runs inside an Activity named `croft.warmup`, from Croft's existing
  ActivitySource (or a new one registered in `CroftTelemetry.cs` with `AddSource`), so all its
  requests group into one trace. The orchestrator uses this to check production.
- **Shared code** goes in `Croft.Shared`: the gate, the runner (budget, per-request timeout,
  logging, Activity) and the settings. Each app supplies only its list of requests.

## Item 1: the readiness gate and the runner (both apps)
- A `WarmupGate` (singleton): `IsOpen` starts false, or true when `Warmup:Enabled` is false. Then
  `Open()`.
- The runner: a hosted service that starts on `IHostApplicationLifetime.ApplicationStarted`, runs
  the app's request list through `"CroftWarmup"` inside the budget, logs as above, and calls
  `Open()` in a `finally`.
- Web `/health/ready` (`Program.cs:902`): when the gate is closed it answers 503 with the text
  `Warming up` and checks nothing else. Keep the existing headers (`HealthHeaders`).
- API: a `WarmupHealthCheck` tagged `ready`, Unhealthy with the description `Warming up` while
  the gate is closed. It is registered beside `DatabaseHealthCheck` (`Program.cs:38`), so
  `/health/ready` and `/health` both include it. `/health/live` never includes it.
- Test factories: the shared `WebApplicationFactory` setups set `Warmup:Enabled=false`, so
  existing tests are unaffected. The warm-up tests enable it.

Tests (both apps, request lists supplied by the test):
- **Gate holds, then opens:** the warm-up is held on a `TaskCompletionSource` inside the test
  handler, so `/health/ready` returns 503 `Warming up`. Release it and `/health/ready` returns 200.
- **Liveness ignores the warm-up:** while it's held, `/health/live` returns 200.
- **Disabled:** with `Warmup:Enabled=false`, the first `/health/ready` returns 200 and no warm-up
  request is sent.
- **Budget:** a `Warmup:Timeout` shorter than a held request opens the gate, and one Warning
  names the paths not done. Use `TimeProvider`/`FakeTimeProvider` for the budget; no sleeps.
- **Failures don't stop it:** one request answering 500 and one throwing `HttpRequestException`
  are named in one Warning. The other requests are still sent, the gate opens, and the
  Information line says `n-2 of n`.
- **Trace:** with an `ActivityListener`, every warm-up request's client Activity has the
  `croft.warmup` Activity as an ancestor.

Break-it-once: remove the gate check from the Web's `/health/ready`, and the 503 test fails.

## Item 2: the Web's request list
Built at run time from the site's own content, so it works for any site. Each request goes to
the Web itself:
1. `/` (its HTML also gives "the post": the first `/post/{slug}` link in it, usually the newest;
   any post warms the post page).
2. `/sitemap.xml` (gives the first static page, category and tag: the first `<loc>` of each kind,
   by path shape. Use their paths, never the absolute host, which is `Blog:BaseUrl`).
3. The post `/post/{slug}`.
4. The first `/api/media/...` URL in that post's HTML, exactly as written, including its query
   (13e's sized-image parameters). This warms the image path.
5. The static page `/{slug}`.
6. The category `/category/{slug}`.
7. The tag `/tag/{slug}`.
8. `/categories`.
9. `/search?q=<the first word of the post's title>`, or `/search?q=blog` without a post.
10. `/feed.xml`.

Any step whose source is missing is skipped (an empty site warms only 1, 2, 8, 9 and 10, with no
Warning).

Tests (each test creates its own content, as the Web tests already do; never rely on DB state):
- **Full site:** with one post (with an image), one page, one category and one tag, the warm-up
  requests exactly these ten paths, in this order, once each.
- **Empty site:** only the five paths listed above, the gate opens, and no Warning is logged.
- **No page views:** after a warm-up over the full site, the API's page-view store (or the stub
  API's recorded calls) holds no page view.

Break-it-once: drop step 4, and the full-site test fails.

## Item 3: the API's request list, from the routes the Web uses
The API warms the **same query shapes the Web asks for**. EF compiles per shape, and a
`?category=` list is a different shape from the plain list.
- Move the URL building of `BlogApiClient`'s public (anonymous) read methods into `Croft.Shared`
  as `PublicReadRoutes`: one static method per route, with the same parameters.
  `BlogApiClient` calls them. `FeedService` and `SitemapService` (they build their own URLs) call
  them too.
  - The methods: posts list (each parameter combination the Home, Category and Tag pages, the
    feed and the sitemap use), post by slug, related, tags, tag by slug, categories, top
    categories, category by slug, search (`publicView=true`), page by slug, pages,
    navigation/all.
  - Admin and staff calls stay where they are.
- The API's warm-up list is built from `PublicReadRoutes` with values from the database: the
  newest published post (slug), its first category and tag, the first published page. It uses the
  same page sizes the Web uses: those become shared constants, not literals in two places.
  - Add the newest post's banner image through the API's media GET, with the parameters the post
    page uses, when the post has one.
  - Anything missing (empty database) is skipped without a Warning.
- **Drift guard (the key test):** a table test calls each public read method of `BlogApiClient`
  against a capturing handler, normalises the URL (slug/id → `{slug}`/`{id}`, query values →
  `{v}`, keys kept), and asserts each shape appears in the API warm-up's list (normalised the same
  way). A second assertion fails when `BlogApiClient` gains a public method that is neither in the
  table nor in an explicit "not warmed" list with a reason (staff-only, writes).

Tests:
- **Seeded:** with one post (with category, tag and banner), one page and one category/tag, every
  warm-up request returns 2xx, and the paths are the expected set.
- **Empty database:** only the content-free routes, the gate opens, no Warning.
- **Same shape as the Web:** the drift guard above.

Break-it-once: delete the `?category=` list shape from the warm-up list, and the drift guard
fails.

## Item 4: docs, manifests and the local stack
- `k8s/README.md`: a "Warm-up" section covering:
  - what it does, and why (the measured numbers above, briefly);
  - the three settings;
  - that a rollout now takes about the warm-up's time longer per pod;
  - that readiness, not liveness, carries it.
- `k8s/configmap.yaml`: the three settings, commented, at their defaults (both ConfigMaps).
- Probes stay as they are (readiness every 10 s; a pod joins ≤ 10 s after its warm-up).
  `maxUnavailable: 0` already keeps old pods serving until the new ones are ready.
- `dev/start-local.sh` waits for Web readiness already (`run-e2e.sh` header). Confirm it waits for
  `/health/ready` (not `/health/live` or the port), so E2E runs against warm instances. If it
  doesn't, make it.
- E2E: one test that the instance's `/health/ready` is 200 and the Web's log holds the warm-up's
  Information line with `n of n` (read the log the way existing E2E tests read instance logs, if
  any do; otherwise assert the readiness and say so in the commit).

## Item 5: measured before/after (no assertions)
On a local fresh start (`dev/start-local.sh`, one instance, seeded content), record the
first-request time of `/` and of a post, with `Warmup:Enabled=false` and then `true` (measured
after `/health/ready` turns 200). Put the four numbers and the warm-up's own duration (from its
Information line) in this item's commit message. No test: timing on a dev machine is evidence,
not a contract.

## Out of scope
- Output caching, shared caches, rendering Markdown at save: the later performance brief.
- EF Core compiled models and precompiled queries: a separate brief if the production
  measurement after this one still shows API first-call cost.
- Warming staff pages or Blazor circuits (staff only).
- Any change to probes' timings, CPU requests or limits.
