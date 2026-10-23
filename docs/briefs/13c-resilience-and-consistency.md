# Brief 13c: resilience and consistency (architecture review findings 1, 2, 3, 4, 7)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-architecture.md`, findings 1, 2, 3, 4 and 7, and
`docs/reviews/2026-10-19-dotnet-code-review.md` I-1, I-6, M-2 and M-3. Read each
completely before its item. The owner decided 2 and 4 (2026-10-19): "a" for both — this brief
says what that means.

**Owner-visible outcome:** a slow or failed API or database failover makes pages fail fast and
recover by themselves instead of turning into site-wide 503s; an edited post shows its new
version on every instance within seconds, not minutes; an author's save never succeeds while
the other instances keep the old version; page views no longer rewrite the posts table; and
Kubernetes takes an API pod out of service when its database is unreachable.

**CSP impact: none.** No page markup or headers change.

## Item 1: Web → API calls fail fast (finding 1)

- The `BlogApi` HttpClient gets `Microsoft.Extensions.Http.Resilience`'s standard resilience
  handler, configured: per-attempt timeout 5 s, total timeout 10 s (both configurable under
  `Croft:Web:Api:*`), retries **only for GET/HEAD** (at most 2, jittered exponential backoff,
  on transient failures: connection errors, 408, 429 with Retry-After honoured, 5xx except 501),
  never for POST/PUT/PATCH/DELETE; a circuit breaker (failure ratio 0.5 over 30 s, minimum 20
  calls, break 15 s). Other HttpClients the Web app registers for the API (media proxy, beacon)
  get the same timeouts; the media proxy's timeout covers getting the response headers, not the
  body stream (a large image may take longer to stream).
- When the API can't be reached, reading pages render the existing error page with 503 quickly;
  check what the user sees and that no unhandled exception reaches the log as Error for an
  expected, already-handled outage (Warning is right).
- The error page must not depend on the API: today `/Error` renders `MainLayout`, which calls the API, so with the API down the exception handler itself throws and the reader gets a bare 500 (found in 13a, commit 6165d17). The error and not-found pages render with a layout that needs no API data (or MainLayout degrades without it). Test: API unreachable → a reading page answers 503/500 with the error copy, not a bare 500.
- README configuration table: the new settings.

Tests: a fake API that hangs → the Web call fails after the attempt timeout, total within the
total timeout (fake TimeProvider where the library allows, otherwise short configured timeouts);
a GET that fails twice then succeeds → succeeds with exactly 3 attempts; a POST that fails →
exactly 1 attempt; the breaker opens after the configured failures and short-circuits. Break-it-once:
remove the handler; the hang test must fail.

## Item 2: no minutes of staleness after an edit (finding 2, decision "a")

- After an invalidation (local or via NOTIFY) of a cache key, the next refill of that key reads
  from the **primary**, not a replica, for a window (`Croft:Api:Cache:PrimaryAfterInvalidation`,
  default 60 s). Outside that window refills use the replica as now.
- Keep it simple: a per-key "invalidated at" timestamp, checked at refill. Make sure a refill that
  started before the invalidation can't store its (older) result after it (the cache's existing
  race handling — keep it working, the review lists it as sound).

Tests: invalidate → the next refill uses the primary connection (observable via the read-host
routing in tests); after the window → replica again; a refill in flight across an invalidation
does not store the old row. Break-it-once: drop the primary-after-invalidation check; the first
test must fail.

## Item 3: the invalidation is atomic with the write (finding 3)

- Every write that notifies (`PostService`, `CategoryService`, `TagService`, `MediaService` —
  all call sites the review lists, and any it missed) runs `SaveChangesAsync` and `pg_notify` in
  **one transaction**, so the NOTIFY is delivered on commit and only on commit. Use the
  execution strategy correctly (EF retrying strategies require the transaction inside
  `strategy.ExecuteAsync`).
- If you can make it a single helper (`SaveAndNotifyAsync`) used everywhere, do.
- (.NET review I-1) Nothing after the commit may be cancelled by the request: the local cache
  invalidation that follows a committed write runs with `CancellationToken.None` (a closed tab must
  not leave this instance's cache stale). Test: cancel the request token right after the commit →
  the local cache is still invalidated.
- (.NET review M-3) One write sends one invalidation round trip per affected key set, not four:
  bundle the keys into one NOTIFY payload (keep the payload under Postgres's 8000-byte limit; fall
  back to a "clear all" message above it). Test: a post save sends exactly one NOTIFY.

Tests (integration, real Postgres): a write commits and a LISTENing connection receives the
notification; a write whose transaction rolls back (forced failure after SaveChanges) sends **no**
notification; a NOTIFY failure rolls the write back. Break-it-once: move the NOTIFY back outside
the transaction; the rollback test must fail (a notification arrives for a write that didn't
commit, or the other way round — pin which).

## Item 3b: cache keys can't be used to evict the hot set (.NET review I-6, M-2)

- Clamp every user-controlled part of a public cache key before it becomes a key: page size to the
  API's allowed maximum (and the same clamp the query uses, so key and result agree), page number
  to a sane maximum (e.g. 1000; beyond it the API answers with an empty page without caching),
  search terms normalised (trimmed, collapsed whitespace, lower-cased) and capped in length;
  search results above a small page count are not cached at all.
- Per-key stampede protection: concurrent misses for the same key share one refill (a per-key
  in-flight task), and an empty/not-found result is cached briefly (e.g. 30 s) so repeated misses
  don't each hit the database.
- Tests: two requests for page size 10000 and 50 produce the same key; page 999999 doesn't create
  an entry; 50 concurrent misses for one key cause one database query; a not-found is served from
  cache within its short TTL. Break-it-once: remove the clamp → the first test fails.

## Item 4: page views don't rewrite the posts table (finding 4, decision "a")

- A new table `PostViewCounts` (`PostId` PK, FK to Posts with cascade delete, `ViewCount bigint`),
  migration fills it from `Posts.ViewCount`. Counting a view updates/upserts that row
  (`INSERT … ON CONFLICT DO UPDATE SET ViewCount = ViewCount + 1`), never `Posts`.
- Reads that show view counts read the new table.
- **Expand/contract:** `Posts.ViewCount` stays (a 0.6.x pod still updates it during the rolling
  deploy); views counted by an old pod during the rollout land only in the old column — accept and
  document that small loss in the migration's comment. Add `Posts.ViewCount` as a **pending
  contract** line in AGENTS.md.
- Check the export (brief 11c): `viewCount` must still round-trip (the completeness guard will tell
  you; export from the new table, import into it).

Tests: a view increments `PostViewCounts` and leaves `Posts` untouched (assert `xmin` or
`UpdatedAt`/row version of the Posts row unchanged); concurrent views from different addresses
count exactly; the migration copies existing counts; the content export/import round trip still
holds. Break-it-once: point the increment back at `Posts`; the "Posts untouched" test must fail.

## Item 5: health and visibility (finding 7)

- API: `/health/live` (process only, no dependencies) and `/health/ready` (a cheap database query
  with a short timeout, e.g. 2 s; reports unhealthy when the primary connection can't be opened),
  `/health` kept as an alias of ready for compatibility. `k8s/api-deployment.yaml`: liveness →
  `/health/live`, readiness → `/health/ready`. Make `k8s/README.md`'s claims true and exact.
- Metrics (the existing `ApiMetrics` meter): public read cache hits, misses, invalidations, refills
  from primary vs replica (item 2), and the cache listener's connection state and reconnects.
  Name and document them.
- Web: check its own readiness claims in README against the code and fix whichever is wrong.

Tests: ready is 503 with the database unreachable and 200 with it reachable, live is 200 in both;
the k8s manifests point the probes at the right paths (`K8sBaseTests`); the metrics are emitted
(MeterListener). Break-it-once: make ready return healthy unconditionally; the unreachable-database
test must fail.

## Out of scope
- Findings 5 and 6 (media validators, output caching): the output-cache brief.
- Finding 8 (tokens at rest): brief 13b.
- Findings 9–13: later.
- The stats service (owner's 4b, later).
