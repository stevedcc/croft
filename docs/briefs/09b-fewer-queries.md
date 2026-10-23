# Brief 09b: fewer database queries per page, and overload degrades instead of crashing

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item, TDD,
break-it-once for each key test, E2E twice at the end, the final whole-branch review. Other briefs
may run at the same time; when the orchestrator says another brief was accepted, `git fetch origin`
and `git merge --no-edit origin/blog-framework` (never rebase).

**Owner-visible outcome:** a public page costs the database far fewer queries, and a new or changed
category or tag shows on every replica at once; the API stops
polling the database every minute; a burst of readers gets fast 503s instead of croft-web running
out of memory; page-view rows are deleted after 90 days, as the privacy page will promise.

**Evidence (production, 2026-10-12 and 2026-10-14):**
- `pg_stat_user_tables` after ~90 h with 1 post, 1 category, 2 tags and 9 page views: Posts 7,258
  sequential scans + 5,453 index scans; Categories 2,224 sequential scans; `IX_Posts_CategoryId`
  5,197 scans. About 5,400 of the Posts scans are `ScheduledPublisherHostedService` polling
  (`Task.Delay(TimeSpan.FromMinutes(1))`, `src/Croft.Api/Services/ScheduledPublisherHostedService.cs:27`),
  times two API replicas. Most of the rest is page renders: every render recounts published posts
  per category and tag (`CategoryService.cs:26,46,59,60,68,83`, `TagService.cs:23,45,59`).
- Load test 2026-10-14 (k6, anonymous page GETs, prerender only): at 40 requests/s the database
  primary used ~0.5–1.1 CPU cores (≈13–27 ms of Postgres CPU per page) and croft-api 0.6–1.7 cores
  per replica; the site broke at ~50 requests/s on CPU. Earlier the same day, with slow DNS, queued
  requests grew croft-web from ~220 Mi to 496 Mi and it was OOM-killed at its 512 Mi limit; on the
  second run it reached 490 Mi without being killed and stayed at 400–450 Mi when idle.

## Item 1: measure queries per public page (the baseline)

**Do:** a test helper that counts SQL commands per HTTP request against the API (there is a
`DbCommandInterceptor` pattern already: `tests/Croft.Api.Tests/AnalyticsControllerTests.cs:287`,
`BlogApiFactory`'s `commandInterceptor` parameter), and a Web-side count of API calls per page
render (a counting `DelegatingHandler` on the `BlogApi` HttpClient in a Web test). Seed: 3 published
posts in 2 categories with 3 tags, 1 scheduled, 1 draft.

**Tests:** for the anonymous renders of `/`, `/post/{slug}`, `/category/{slug}`, `/tag/{slug}`:
the number of API calls Web makes, and for each API endpoint those calls hit, its SQL command
count. Print them as a table in the test output and paste the table into the commit message: this
is the "before". No budget assertion yet (item 6 adds it).

## Item 2: the scheduler sleeps until the next post is due

**Now:** each API replica queries for due posts every minute, forever.

**Do:**
- After each run, read the earliest `PublishedAt` of any `Scheduled` post and sleep until then,
  capped at 5 minutes (the cap covers posts another replica scheduled, and a replica that died
  before its post was due). No scheduled posts: sleep the cap.
- A singleton signal (e.g. `IScheduledPublishWakeup`) that the post service triggers when a post
  is scheduled, rescheduled or unscheduled; the scheduler wakes, re-reads, and sleeps again.
- Keep `PublishDuePostsAsync` and `BackgroundJobLock.TryRunAsync` exactly as they are: the
  multi-replica guarantees (one publisher per tick, never published twice) must not change.

**Tests** (`FakeTimeProvider`; the existing `PublishDuePostsAsync` tests stay green):
- No scheduled posts: over 30 simulated minutes the scheduler runs 6–7 times, not 30.
- A post scheduled for T+90 s while the scheduler sleeps its cap: it is published at T+90 s (±1 s
  simulated), because scheduling it woke the scheduler.
- A post due at T+3 min that this replica did not schedule (inserted directly): published by T+5
  min at the latest.
- Break-it-once: remove the wake-up trigger from the post service; the T+90 s test must fail.

## Item 3: partial indexes for published and scheduled posts

**Do:** one expand-only migration (AGENTS.md / 08b expand-contract rule: add, never drop or
rename) adding
- `IX_Posts_Scheduled_PublishedAt` on `("PublishedAt") WHERE "Status" = <Scheduled>`, and
- `IX_Posts_Published_PublishedAt` on `("PublishedAt" DESC) WHERE "Status" = <Published> AND
  "Kind" = <Post>`,

using the enum's stored values (check how `PostStatus`/`PostKind` are mapped before writing the
filters). In `PostService.ListAsync` (`PostService.cs:210`), order the **public** branch by
`PublishedAt` descending (a published post always has one), so the second index can serve the
sort; the admin branch keeps `PublishedAt ?? CreatedAt` (drafts have no `PublishedAt`).

**Tests:**
- Both indexes exist after migrating (read `pg_indexes`), with the WHERE clauses above.
- The SQL the public list sends orders by `"PublishedAt" DESC` with no `COALESCE` (capture it
  with the interceptor); the admin list still orders drafts by `CreatedAt`.
- With `SET enable_seqscan = off` in the test session, `EXPLAIN` of the scheduler's due-post
  query and of the public first page uses the new indexes (assert the index names in the plan).
- Break-it-once: drop the public branch's ordering change; the SQL test must fail.

## Item 4: published-post counts for categories and tags are cached in the API, invalidated on every replica

(Amended 2026-10-14 before work on it started: cross-replica invalidation through Postgres
`LISTEN`/`NOTIFY` instead of accepting up to 60 s of staleness on the other replica.)

**Now:** every public render recounts published posts per category and per tag.

**Do:**
- Cache the **public** results of the category list, the top-categories list, the tag list and
  the by-slug lookups (the DTOs with `PostCount`) in `IMemoryCache`, with a 5-minute expiry as a
  safety net only. Admin reads (anything that passes `includeAdminCounts` or sits behind the admin
  role) are never cached.
- Any write that can change a count invalidates the cache **on every API replica**: post create,
  update, delete, publish, unpublish, schedule, the scheduler publishing a post; category and tag
  create, rename, delete. Mechanism: after the write commits, `SELECT pg_notify('croft_cache',
  '<what changed>')` (e.g. `categories`, `tags`; keep the payload small and documented). Each
  replica runs a hosted listener on its own dedicated `NpgsqlConnection` (from the `BlogDb`
  connection string, not EF's pool, `Application Name=croft-cache-listener`, keepalive on) that
  `LISTEN croft_cache`s and clears the matching local entries on each notification (its own
  notifications included; clearing twice is harmless).
- When the listener's connection drops (database failover, network): reconnect with backoff
  (1 s doubling to 30 s), and on every (re)connect clear the whole cache, since notifications sent
  while disconnected are lost. Log the disconnect at Warning and the reconnect at Information.
- No Valkey, no HybridCache, no new packages (`Npgsql` is already referenced).

**Tests** (two API instances = two `BlogApiFactory`s on the same test database):
- Two identical anonymous category-list requests on one instance: the second runs 0 SQL commands.
- Instance B serves the list (cached); publish a post in a category through instance A; B's next
  list shows the new count, waited for as a condition with a 5 s timeout (no sleeps, AGENTS.md
  rule 6). Same for a new tag, and for A's scheduler publishing.
- Kill B's listener backend (`pg_terminate_backend` on the `croft-cache-listener` row in
  `pg_stat_activity`), then publish through A: B shows the new count within 35 s (reconnect clears
  the cache), and B logged the reconnect.
- An admin request after a cached public one still runs its query and sees live counts.
- Break-it-once: remove the `pg_notify` call from publish; the cross-instance test fails while the
  same-instance case still passes.

## Item 5: page views older than 90 days are deleted

**Now:** `PageViews` rows (IpHash, UserAgent, Referrer) are kept forever. The privacy page will
promise deletion, so this must ship before that sentence goes live.

**Do:** a background service in the API, once a day, deletes `PageViews` rows whose `Day` is more
than `Croft:Analytics:RetentionDays` (default 90) days before today, in batches of at most 5,000
rows per statement, under `BackgroundJobLock.TryRunAsync` with a new job id (one replica deletes;
the others skip). `Posts.ViewCount` is not touched: per-post totals stay. Log the number of rows
deleted at Information when it is more than 0.

**Tests** (`FakeTimeProvider`, rows the test inserts itself):
- Rows on day −91 and −90 and −1: after a run only −90 and −1 remain; `ViewCount` unchanged.
- `RetentionDays = 30` is honoured.
- 12,000 old rows are all deleted in one run, in at least 3 statements (count with the
  interceptor).
- Two instances running at the same moment: one deletes, one skips (the existing
  `RendezvousCommandInterceptor` pattern).
- Break-it-once: off-by-one the cutoff to `>=`; the −90 row test fails.

## Item 6: overload gets a fast 503, and a query budget

**Now:** croft-web accepts every request and queues it; under a burst it grows to its 512 Mi
memory limit and is OOM-killed, taking all its readers' circuits with it.

**Do:**
- In croft-web, the ASP.NET Core rate limiter with a global **concurrency** limiter for page
  requests: at most `Croft:Web:MaxConcurrentRequests` (default 64) in progress and
  `Croft:Web:RequestQueueLimit` (default 64) waiting; beyond that, 503 with `Retry-After: 5` and
  a minimal body, returned without calling the API. Exempt: `/health*`, `/_blazor*` (circuits are
  long-lived connections, not requests), `/_framework/*`, static files, `/csp-report` (it has its
  own limiter).
- The same limiter in croft-api for `/api/*` (defaults 64/64), exempting `/health*`.
- Item 1's test now asserts a budget: each page's API calls and SQL commands at most the values
  measured after items 2–4 (put the table, before and after, in the commit message).

**Tests:**
- With the limits set to 1 and 0, a page request held inside the handler (block the stubbed API
  call) makes a second page request return 503 with `Retry-After` within 1 s; `/health/ready` and
  a static file still return 200 meanwhile; after release, a new page request succeeds.
- Same for the API.
- Break-it-once: remove the `/health*` exemption; the health test fails.

## Item 7: production defaults for logs and DNS

**Do:**
- Both `appsettings.json`: `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"` under
  `Logging:LogLevel` (every SQL statement is currently logged at Information: thousands of lines an
  hour, the bulk of all log volume). `appsettings.Development.json` keeps Information for it.
- Both deployments in `k8s/`: `dnsConfig.options: [{name: ndots, value: "2"}]`, so the
  fully-qualified service names Croft uses resolve in one query instead of walking the search list
  first (the cluster's DNS stalls of 2026-10-14 hit the search-list queries).

**Tests:** `K8sBaseTests` (`tests/Croft.Web.Tests/K8sBaseTests.cs`) asserts `ndots` "2" on both
deployments; a configuration test asserts the EF command category's level per environment.

## Out of scope

- `[PersistentState]` to stop the prerender + interactive double load (render-modes work).
- Valkey or any shared cache; output caching of whole pages.
- croft-web's memory settings (GC mode, limits): the owner and orchestrator will tune those in the
  site repo with the next load test.
- Anti-affinity and placement (a separate change in the k8s base after this brief).
- Changing any query's results: every list, count and page must render exactly as before.
