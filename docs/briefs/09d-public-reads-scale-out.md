# Brief 09d: public reads come from a cache and the read replicas, not the primary

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item, TDD,
break-it-once for each key test, E2E twice at the end, the final whole-branch review. No other brief
runs at the same time.

**Owner-visible outcome:** a reader's page costs the database primary almost nothing: repeated
public reads are answered from the API's memory, and the rest go to the read replicas. Admin and
editor work keeps reading from the primary, so an editor always sees what they just saved. A site
sets its connection pool size as a plain setting instead of patching connection strings.

**Evidence (load tests 2026-10-15, `~/sites/observability/loadtest/results/2026-10-15-*`, k6
anonymous page GETs, 2 API + 2 Web pods, Croft 0.5.0):**
- With Npgsql's default pool (100 per process) the site broke at ~55 requests/s in Postgres
  `53300 too many clients` (max_connections 100) while the nodes sat at 25–65 % CPU. Capping the
  pool at 30 per pod (crawford.dev patches the whole connection string to do it) moved the limit
  to ~115–120 requests/s.
- The primary then used ~1.6 cores at 120 requests/s (≈13 ms of database CPU per page), and its
  node decided the limit: alone ~120 requests/s, sharing with one API pod ~70–75. Every public read
  went to the primary; the two replicas idled.
- croft-web logged four `System.Net.Http.HttpClient` Information lines per API call (~31 000
  lines per pod in 30 minutes).

## Contracts (decided here; tests depend on these names)

| Setting | Default | Meaning |
|---|---|---|
| `Database:MaxPoolSize` | `20` | Npgsql `Maximum Pool Size` for every Croft.Api database connection string (primary and read) |
| `Database:MaxAutoPrepare` | `0` | Npgsql `Max Auto Prepare` (0 = off, Npgsql's default) |
| `Database:ReadHost` | unset | Host of the read replicas. Unset: everything uses `ConnectionStrings:BlogDb` exactly as today |

- The read connection string is `ConnectionStrings:BlogDb` with `Host` (and only `Host`) replaced by
  `Database:ReadHost`, plus `Application Name=croft-api-read` and
  `Options=-c default_transaction_read_only=on`. The last one makes any write on the read path fail
  in tests exactly as it would on a real replica.
- **Routing rule (one place, no per-query decisions):** `BlogDbContext` is created on the read
  connection when, and only when, `Database:ReadHost` is set **and** the current HTTP request is a
  `GET` (or `HEAD`) **and** its user is not authenticated. Everything else uses the primary:
  any authenticated request (admin, editor, a signed-in reader), every POST/PUT/DELETE (including
  the analytics beacon), background services (scheduler, retention, cache listener, migrations),
  and anything without an `HttpContext`. Implement it in the `AddDbContext<BlogDbContext>` options
  callback (`src/Croft.Api/Program.cs:33`) via `IHttpContextAccessor` (already registered, line 29);
  services do not change.
- **Cache groups:** `CacheGroup` (`src/Croft.Api/Services/CountCache.cs`) gains `Posts = 4` and
  `Pages = 8`; notification payloads `posts` and `pages`. Rename `ICountCache`/`MemoryCountCache` to
  `IPublicReadCache`/`MemoryPublicReadCache` and `ICacheNotifier.CountsChangedAsync` to
  `ContentChangedAsync` (one rename commit inside item 2).

## Item 1: pool size and auto-prepare are settings; quieter HttpClient logs

**Now:** `src/Croft.Api/Program.cs:33-34` passes `GetConnectionString("BlogDb")` straight to
`UseNpgsql`, so the pool is Npgsql's default 100. `CacheListenerService` builds its own connection
string from `BlogDb`. croft-web's `appsettings.json` leaves `System.Net.Http.HttpClient` at
Information.

**Do:**
- One helper builds every Croft.Api connection string from `BlogDb` with
  `NpgsqlConnectionStringBuilder`: sets `MaxPoolSize` from `Database:MaxPoolSize` and
  `MaxAutoPrepare` from `Database:MaxAutoPrepare`. The `DbContext` and `CacheListenerService` (which
  keeps its own `Application Name`, `Keep Alive` and a pool of its own) both use it.
- `src/Croft.Web/appsettings.json`: `"System.Net.Http.HttpClient": "Warning"` under
  `Logging:LogLevel`; `appsettings.Development.json` keeps Information.
- `k8s/README.md`: a "Database connections" section: the three settings, and the budget rule
  `API pods × MaxPoolSize × (1, or 2 with ReadHost) + Web pods × its WebDb pool + 2 listener
  connections < max_connections − 3` per Postgres instance (the read pool's connections land on the
  replicas, not the primary), with the 2026-10-15 numbers as the example.

**Tests (`tests/Croft.Api.Tests`):**
- With `Database:MaxPoolSize=3`, four concurrent requests that each hold a connection (block inside
  a `DbCommandInterceptor` the way `CountCacheTests` blocks commands) open at most 3 Postgres
  backends with this test's `Application Name` (count them in `pg_stat_activity`). With the setting
  unset, the effective `MaxPoolSize` is 20 (assert on the built connection string).
- `Database:MaxAutoPrepare=10` appears as `Max Auto Prepare=10` in the built string; unset: 0.
- Web: a configuration test asserts the HttpClient category's level per environment (like 09b's EF
  command test, `tests/Croft.Web.Tests/EfCommandLoggingTests.cs`).
- Break-it-once: drop the `MaxPoolSize` assignment; the 3-backend test fails.

## Item 2: the API caches public post and page reads

**Now:** 09b caches categories and tags (counts included) per replica and clears them on every
replica through LISTEN/NOTIFY. Posts and pages are read from the database on every request:
`PostService.ListAsync` (`PostService.cs:177`), `GetBySlugAsync` (:62), `RelatedAsync` (:137),
`ListPagesAsync`, `GetPageNavigationAsync`, `GetPageBySlugAsync`. The home page alone is ~40 % of
reader traffic, and every reader gets the same answer.

**Do:**
- Cache, in the same cache, only the **anonymous** variants (`publicOnly == true`,
  `includeAdminCounts == false`) of those six reads, keyed by every input that changes the answer
  (page, pageSize, category and tag slug, slug, count, and any other `PostListRequest` field).
  Authenticated requests never read from or write to the cache. Don't cache `null` (not found):
  random slugs must not fill memory.
- Bound the cache: `SizeLimit` 2 000 entries, each entry `Size = 1`; the 5-minute expiry stays.
- **Invalidation:** every content write calls `ContentChangedAsync` with **all four groups** (writes
  are rare; one rule is safer than a map of which write affects which read): post and page
  create/update/delete/publish/unpublish/schedule, the scheduled publisher when it publishes,
  category and tag create/rename/delete (their names appear inside post DTOs), media
  update/delete (banners appear inside post DTOs). Page views and feedback do **not** invalidate
  (public DTOs carry neither; `ViewCount`/`OpenFeedbackCount` are admin-only).
- A reconnecting listener already clears the whole cache; keep it.

**Tests:**
- For each of the six reads: the second identical anonymous request on one instance runs **0** SQL
  commands (`SqlCommandCounter`, as in `CountCacheTests`), and returns an equal body.
- Authenticated (Admin) request after a cached anonymous one: runs its query and sees live data
  (a draft post the anonymous list doesn't show).
- **Across replicas** (two `BlogApiFactory` instances on one database, B with the listener, as
  `CountCacheTests.PublishingThroughOneInstance_UpdatesTheOtherInstancesCachedCounts`): B caches
  the anonymous home list and the post; an Admin on A renames the post's title; within 2 s B's
  anonymous list and post show the new title. The same for: renaming the post's category (B's
  cached post shows the new category name), the scheduler publishing a due post (B's list gains
  it), updating a page's navigation flag (B's navigation changes).
- A page view (`POST /api/analytics/pageview`) does not clear B's cache (B runs 0 commands for the
  next identical read).
- 2 001 distinct anonymous `?page=N` reads leave at most 2 000 entries (assert via the cache's
  count or `MemoryCache.GetCurrentStatistics()`).
- Break-it-once: remove the `posts` invalidation from the post update path; the across-replicas
  title test fails.

## Item 3: anonymous reads go to the replicas

**Now:** every query uses `ConnectionStrings:BlogDb` (the primary's read-write service).

**Do:** the routing rule and read connection string from Contracts. Log once at startup, at
Information, whether reads are routed and to which host (never the password). Health checks stay
as they are (the API's `/health`, `Program.cs:179`, checks no database today).

**Accepted consequence (owner, 2026-10-15: "eventual consistency is the price of distributed
systems; admin/editor pages can always use the primary"):** a replica can lag the primary by
milliseconds to seconds, so an anonymous reader, or a cache refill triggered by an anonymous
request, may briefly see the previous version. Don't add code to prevent that; write the comment
that says so at the routing rule.

**Tests** (one Postgres container: `ReadHost` points at the same server, so the routing is visible
through `Application Name` and `default_transaction_read_only`):
- With `ReadHost` set: an anonymous `GET /api/posts` runs its SQL on a connection whose
  `Application Name` is `croft-api-read` (capture it in a `DbCommandInterceptor` from
  `command.Connection`); an Admin `GET /api/posts` runs on the primary's; an anonymous
  `POST /api/analytics/pageview` writes successfully (primary); the scheduler's publish runs on the
  primary.
- **No anonymous GET writes:** call every `[AllowAnonymous]` GET endpoint in Croft.Api once with
  `ReadHost` set (enumerate them from the endpoint data source, so a new one is covered
  automatically) with valid inputs created by the test; none returns 500 and none hits a
  read-only-transaction error. (The read connection's `default_transaction_read_only=on` makes a
  write fail just like a replica would.)
- With `ReadHost` unset: everything uses the primary's connection (today's behaviour).
- **Read-your-writes in the editor (E2E, `tests/Croft.Web.E2E`):** with the E2E API configured with
  `Database:ReadHost` pointing at the same database, an editor saves a change to a post's body, the
  editor reloads, and the new body is shown; the public post page shows it too.
- Break-it-once: route authenticated GETs to the read connection; the Admin-routing test fails.

## Out of scope

- The CloudNativePG side (a replicas-only Service with `trafficDistribution: PreferSameNode`) and
  the site's `Database:ReadHost` and pool settings: the orchestrator does those in the site repo.
- croft-web's own caching (output caching, static SSR), search caching, PgBouncer, Npgsql
  multiplexing.
- Changing any query's results: every page renders exactly as before.
