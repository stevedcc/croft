# Architecture review: Croft 0.6.2 (2026-10-19)

Reviewed at commit `0e9e697` (Croft 0.6.2) as a principal engineer would assess a portfolio
project for a senior architect role. Scope, as briefed: the Web/API split and the `Croft.Shared`
contract, the rendering split (static SSR reading pages versus the interactive admin), the public
read path (per-instance cache, read replicas, LISTEN/NOTIFY invalidation) under several instances
and failover, migrations (expand/contract), background services, configuration and deploy-time
branding, the kustomize base versus the site overlay, and observability.

Severity: Critical / Important / Minor. Each finding says whether it is **wrong** (the design does
not do what it claims, or fails under a realistic scenario) or **a trade-off I would make
differently** (defensible, but I would argue the other way in a design review). Evidence is quoted
from the code or from running it; where a claim could not be checked here it says "unverified".

How the claims were checked (details under "Verification record" at the end): the solution was
built from a clean checkout and the Web unit suite was run. The API integration suite needs a
Postgres Testcontainer and no container runtime exists in this environment, so its results are
unverified here; the E2E suite was not run. No code, test or document other than this file was
changed. A security review of the same commit exists on `review/security`; where a finding below
touches the same code it says so and does not repeat the security argument.

## Where it breaks first

Ordered by what gives way first as the site grows, not by severity:

1. **Partial failure, not load.** The Web app calls the API with no timeout, retry budget or
   circuit breaker, behind a 64-permit concurrency limiter. An API pod that stalls (database
   failover, a slow replica, a GC pause) converts into site-wide 503s within seconds (finding 1).
2. **Writes, not reads, under traffic.** Every anonymous page view becomes a write transaction on
   the primary, including an UPDATE of the `Posts` row. The cache protects reads; nothing protects
   the write path (finding 4).
3. **Bytes, not pages.** Images are served through two application hops with two database queries
   each and no validators, so the heaviest part of every page is the least cacheable (finding 5).
4. **Correctness as instances grow.** Cache invalidation is a separate statement after the commit,
   and a refill after invalidation reads a replica that may not have replayed the write. Both are
   cheap to fix now and expensive to debug later (findings 2 and 3).
5. **Authors.** The sitemap walks the whole catalogue through the API on every crawler request,
   and the summary backfill holds one transaction over every post (findings 12 and 9).

## Findings

### 1. Important: Web → API calls have no timeout, retry budget or breaker, so an API stall becomes a site-wide outage

**Classification:** wrong (a missing control, not a choice that was made).

**Where.** `src/Croft.Web/Program.cs:193-196` (the named client), `src/Croft.Web/Program.cs:245-258`
(the request limiter), `src/Croft.Web/Services/BlogApiClient.cs:24-51` (every call goes through
`SendAsync`), `src/Croft.Web/Components/Layout/MainLayout.razor:95-96` and
`src/Croft.Web/Components/Pages/Post.razor:78-97` (the calls per page).

**What is wrong.** The only configuration the `"BlogApi"` client gets is its base address:

```csharp
// src/Croft.Web/Program.cs:193-196
builder.Services.AddHttpClient("BlogApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"]!);
});
```

`HttpClient.Timeout` therefore stays at its 100-second default, there is no per-attempt timeout,
no retry, no circuit breaker and no bulkhead (`Microsoft.Extensions.Http.Resilience` is not
referenced; `src/Croft.Web/Croft.Web.csproj`). A post page makes four sequential API calls
(top categories and page navigation in the layout, the post and its related posts in the page),
each holding the page's single request permit:

```csharp
// src/Croft.Web/Program.cs:252-256
: RateLimitPartition.GetConcurrencyLimiter("pages", _ => new ConcurrencyLimiterOptions
{
    PermitLimit = builder.Configuration.GetValue("Croft:Web:MaxConcurrentRequests", 64),
    QueueLimit = builder.Configuration.GetValue("Croft:Web:RequestQueueLimit", 64),
```

**Why it matters.** Scenario: the Postgres primary fails over (the HA design the repository
deploys, CloudNativePG). For the 10 to 30 seconds a switchover takes, every API request that
needs the database blocks on a connection; the API's own limiter queues them. On the Web side
each page request now holds its permit for as long as the API takes, up to 100 seconds. At even
modest traffic (the README's 160 req/s figure, `README.md:25`, unverified) the 64 permits and the
64-deep queue are consumed in well under a second, and every reader, including readers of pages
whose content is sitting in the API's cache, gets a 503. The same happens for an API pod that is
merely slow. The readiness probe does not help: `/health/ready` (`src/Croft.Web/Program.cs:776-805`)
calls the API's `/health`, which checks nothing (`src/Croft.Api/Program.cs:30`, `:235`), so the
Web pod stays Ready while it answers 503. The limiter is also one global concurrency partition
with no per-client fairness, so a single slow or abusive client takes the same permits from
everyone; the security review covers that angle for the media proxy.

**What would fix it.** Give the `"BlogApi"` client a short total timeout (a page read should be
abandoned in 2 to 5 s, not 100), a per-attempt timeout with one retry for idempotent GETs, and a
circuit breaker so a stalled API fails fast and the layout degrades (the layout already catches
`HttpRequestException` and renders without the sidebar, `MainLayout.razor:100-104`). The standard
resilience handler does all of this in one registration. Then make the limiter's permits mean
something: a request that fails fast returns its permit in milliseconds instead of seconds.

### 2. Important: the per-instance cache turns replica lag into minutes of staleness

**Classification:** a trade-off I would make differently. The owner accepted replica lag
(`src/Croft.Api/Data/BlogDbConnectionString.cs:64-67`, brief 09d); the code makes the accepted
"briefly" into "until the next content write or five minutes", and the accepted-consequence note
does not say so.

**Where.** `src/Croft.Api/Data/BlogDbConnectionString.cs:47-76` (routing), `src/Croft.Api/Services/PublicReadCache.cs:47`
(expiry), `src/Croft.Api/Services/PublicReadCache.cs:69-111` (refill), `src/Croft.Api/Services/CacheListenerService.cs:75-85`
(the invalidation on NOTIFY).

**What is wrong.** The write path commits on the primary, then notifies; every instance's listener
drops the affected groups. The next anonymous request on each instance refills the entry by
running the query again, on whichever connection the routing rule picks, which for an anonymous
GET with `Database:ReadHost` set is the replica:

```csharp
// src/Croft.Api/Data/BlogDbConnectionString.cs:72-75
return !string.IsNullOrWhiteSpace(configuration["Database:ReadHost"])
        && PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext)
    ? Read(configuration).ConnectionString
    : Build(configuration).ConnectionString;
```

Nothing compares the replica's replay position with the write. The generation counter in
`GetOrCreateAsync` (`PublicReadCache.cs:74-86`) only protects a refill that *started before* the
invalidation; a refill that starts after it and reads a lagging replica stores the pre-write row
with the full five-minute expiry (`PublicReadCache.cs:47`, `Expiry = TimeSpan.FromMinutes(5)`).
The accepted-consequence comment describes the lag as something a reader "may briefly see"; the
cache pins it.

**Why it matters.** Scenario: an editor fixes a factual error in a post and hits save. The
invalidation reaches both API pods in milliseconds; the first reader on each pod refills from a
replica that is 200 ms behind, and both pods now serve the wrong text for up to five minutes, or
until someone edits something else. The editor, routed to the primary, sees the fix immediately
and cannot reproduce the complaint. With a replica that falls seconds behind under load (the
Raspberry Pi cluster the README describes), this will happen on exactly the posts that are being
edited while they are being read, which is the only time staleness is visible.

The replica buys less than it costs here: because every anonymous read is cached per instance,
the replica only serves cache misses (one per key per instance per invalidation), uncached search
(finding 11) and media metadata (finding 5). The cache, not the replica, is what keeps the primary
idle.

**What would fix it.** Any one of: (a) route cache *refills* to the primary and keep the replica
for the uncached anonymous paths, so a refill never reads behind the write that invalidated it;
(b) carry the primary's `pg_current_wal_lsn()` in the notification payload and have the refill
check `pg_last_wal_replay_lsn()` on the replica before storing, falling back to the primary when
behind; (c) at minimum, document the real bound ("up to five minutes after an edit") at the routing
rule and in the k8s README so the owner's acceptance covers what the code does.

### 3. Important: cache invalidation is not atomic with the write

**Classification:** wrong. The fix is one transaction boundary.

**Where.** `src/Croft.Api/Services/CacheNotifier.cs:24-38`, `src/Croft.Api/Services/PostService.cs:378-379`
(and `:423-424`, `:439-440`, `:488-489`, `:517-518`), `src/Croft.Api/Services/CategoryService.cs:111-112`,
`src/Croft.Api/Services/TagService.cs:80-81`, `src/Croft.Api/Services/MediaService.cs:156-158`.

**What is wrong.** Every content write is `SaveChangesAsync` (autocommit) followed by a second,
separate autocommit statement per cache group:

```csharp
// src/Croft.Api/Services/PostService.cs:378-379
await db.SaveChangesAsync(ct);
await cacheNotifier.ContentChangedAsync(db, CacheGroup.All, ct);
```

```csharp
// src/Croft.Api/Services/CacheNotifier.cs:33-36
await cache.InvalidateAsync(group, ct);

var payload = group.ToString().ToLowerInvariant();
await db.Database.ExecuteSqlAsync($"SELECT pg_notify({Channel}, {payload})", ct);
```

Postgres delivers `NOTIFY` at commit when it runs inside a transaction, which is the mechanism that
would make "the write is visible" and "the other instances are told" one event. Here they are
five statements and four round trips, and the write is durable before the first notify is sent.

**Why it matters.** Scenario: the connection drops between `SaveChangesAsync` and the first
`pg_notify` (a primary failover, a pool connection reset, the pod being SIGTERMed mid-request).
The post is saved; the author's request fails with a 500; the author retries and gets a slug
conflict or a duplicate; and every other API instance serves the old content for up to five
minutes because the notification was never sent. The `CacheGroup.All` rule ("one rule is safer
than a map") also means a tag rename costs four notifications and four full-group clears on every
instance, which is fine at today's write rate but is the shape that will be made "smarter" later;
it is better to have it atomic first.

**What would fix it.** Run the write and the notifies in one explicit transaction (`db.Database.BeginTransactionAsync`
around `SaveChangesAsync` and the `pg_notify` calls, then commit); Postgres then delivers all of
them exactly when the write becomes visible, or not at all. Send one notification with a
multi-group payload instead of four. The local `InvalidateAsync` can stay where it is; it is
idempotent with the listener's own clear.

### 4. Important: every anonymous page view is a write transaction on the primary

**Classification:** a trade-off I would make differently. The analytics design is honest and
privacy-conscious; its cost model is the opposite of the read path's.

**Where.** `src/Croft.Web/Program.cs:488-556` (the beacon forward), `src/Croft.Api/Controllers/AnalyticsController.cs:14-22`,
`src/Croft.Api/Services/AnalyticsService.cs:19-44`, `src/Croft.Api/Data/Configurations/PostConfiguration.cs:75-83`
(the stored `tsvector` and GIN index on the same table).

**What is wrong.** Each page view the browser beacons is forwarded Web → API and becomes three
statements on the primary: an existence check, an `INSERT ... ON CONFLICT DO NOTHING` into
`PageViews`, and an `UPDATE` of the `Posts` row:

```csharp
// src/Croft.Api/Services/AnalyticsService.cs:25-43
var countable = await db.Posts.AnyAsync(
    p => p.Id == postId && p.Status == PostStatus.Published && p.PublishedAt <= now, ct);
...
if (await InsertPageViewOnceAsync(postId, now, day, HashIp(ipAddress ?? "unknown", day), userAgent, referrer, ct) == 0)
...
await db.Posts
    .Where(p => p.Id == postId)
    .ExecuteUpdateAsync(s => s.SetProperty(p => p.ViewCount, p => p.ViewCount + 1), ct);
```

`Posts` is the widest, most-indexed table in the schema (the 200,000-character Markdown body, a
stored generated `tsvector` with a GIN index, two partial indexes, the unique slug index). Every
view of a popular post rewrites that row's tuple.

**Why it matters.** Scenario: one post is shared and gets 50 views a second for an hour. The read
path is fine: the cache absorbs it. The primary takes 150 statements a second and 50 row versions
a second on one hot `Posts` row, plus autovacuum work to keep the table and its GIN index from
bloating, on the Raspberry Pi the README describes. The replica then lags on exactly that table,
which feeds finding 2. The first thing an owner will see is not an error but a slow editor (the
staff pages use the primary) and growing replica lag, with no metric that names the cause
(finding 7).

**What would fix it.** Stop touching `Posts` per view: `ViewCount` is derivable from `PageViews`
(or from a narrow `PostViewCounts` table) and is only read on admin pages and summaries. Batch the
beacon on the API side (a bounded channel drained every second into one multi-row insert) so a
burst costs one transaction, not one per view. Keep the unique index dedupe; it is the right
primitive.

### 5. Important: media is served through two application hops, two database queries per image, and no validators

**Classification:** a trade-off I would make differently (the proxy exists so draft images work
for signed-in staff, which is a real requirement); the missing validators are simply wrong.

**Where.** `src/Croft.Api/Controllers/MediaController.cs:61-92`, `src/Croft.Web/Program.cs:454-486`,
`src/Croft.Api/Services/LocalFileStorage.cs:21-25`. The security review's first finding covers the
permit-holding aspect of the same proxy; this finding is about the data path.

**What is wrong.** A public image request is: browser → Web proxy → API → two queries
(`MediaAssets`, then `Posts` for post-specific assets) → `File.OpenRead` → streamed back through
both processes:

```csharp
// src/Croft.Api/Controllers/MediaController.cs:65-72
var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
...
    post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == asset.PostId, ct);
```

```csharp
// src/Croft.Api/Controllers/MediaController.cs:87-91
Response.Headers.CacheControl = isPublic
    ? "public, max-age=86400"
    : "no-store";

return File(stream, asset.ContentType);
```

No `ETag`, no `Last-Modified`, no range support; and the Web proxy copies only `Cache-Control`,
`Content-Type` and `Content-Length` (`Program.cs:476-483`), so even if the API added validators the
browser would never see them. Media metadata does not go through the public read cache either, so
the two queries run on every request, on the replica for anonymous readers.

**Why it matters.** Scenario: a post with six images is read by 1,000 readers over a day. That is
6,000 full-body transfers through two Kestrel processes and 12,000 queries, where a static file
server would answer most of them with a 304 and the rest from the page cache. After the 24-hour
`max-age` every returning reader downloads everything again. As the site grows, image bytes, not
HTML, dominate the Web pod's CPU and the API's connection pool (capped at 20 per pod,
`BlogDbConnectionString.cs:19`).

**What would fix it.** Short term: a strong `ETag` derived from the asset id and `UpdatedAt` (or
the file's length and mtime), `Last-Modified`, `If-None-Match` handling, and the proxy forwarding
those headers both ways. Medium term: public (Global, or attached to a published post) media is
immutable by id, so give it an `immutable, max-age=1y` policy and let the ingress or a CDN cache it;
keep the authenticated proxy path for drafts only. Longer term, serve the RWX volume from a static
file server or object storage and keep the API out of the byte path.

### 6. Important: the reading pages are not cacheable, and the Web app re-renders Markdown on every request

**Classification:** a trade-off I would make differently. The README describes the pages as
cacheable (no nonces "so pages stay cacheable", `README.md:16`, `:204`), but nothing sets a policy
that lets anything cache them.

**Where.** `src/Croft.Web/Security/StaticDocumentAntiforgery.cs:1-21` (the one place that mentions
page caching), `src/Croft.Web/Program.cs:592-604` (sitemap and feed), `src/Croft.Web/Components/Pages/Post.razor:95`,
`src/Croft.Web/Components/Pages/StaticPage.razor:68`, `src/Croft.Web/Services/MarkdownRenderer.cs:22-46`.

**What is wrong.** The antiforgery workaround (W-092) removes the `no-store` headers the framework
would add, which is the right first step, but no reading page, nor `/feed.xml` nor `/sitemap.xml`,
sends a `Cache-Control`, `ETag` or `Last-Modified` of its own (checked by grep across
`src/Croft.Web`: the only `Cache-Control` writers are the branding, manifest, site-file, health
and media endpoints). A browser or CDN has nothing to key on, so every reader of every page costs
a full render. That render includes parsing the post's Markdown with Markdig on every request:

```csharp
// src/Croft.Web/Components/Pages/Post.razor:95
_rendered = Markdown.Render(post.MarkdownContent);
```

The API caches the DTO, including the raw Markdown, so the expensive step (parse, auto-identifiers,
heading extraction, HTML emit, up to 200,000 characters) is repeated by every Web pod for every
view, and four API round trips precede it (finding 1).

**Why it matters.** Scenario: a long post (the editor is sized for 200,000 characters, W-010) gets
the same 50 views a second as above. Each view parses it again on a Raspberry Pi core; Markdig is
fast, but at that length and rate it is the Web pod's CPU budget, and the Workstation GC choice
(W-053) was made for memory, not for this. Everything the CSP work did to keep pages hash-pinned
and nonce-free is wasted if the response carries no cache policy.

**What would fix it.** Set `Cache-Control: public, max-age=60, stale-while-revalidate=300` (or a
short `s-maxage` for the ingress) on the static reading documents, with an `ETag` from the post's
`UpdatedAt` and the layout inputs; add the same to the feed and sitemap. Cache the rendered HTML in
the Web app keyed by post id and `ContentEditedAt` (the field already exists for exactly this), so
Markdig runs once per edit per pod. Both are small changes; together they move the Web app from
"renders every view" to "renders every edit".

### 7. Important: the two mechanisms that hold the public site up are invisible, and the health contract is documented wrongly

**Classification:** wrong where the documentation and the probes disagree; a gap elsewhere.

**Where.** `src/Croft.Api/Program.cs:30` and `:235` (`AddHealthChecks()` with no checks),
`k8s/api-deployment.yaml:204-215` (the same endpoint for liveness and readiness), `k8s/README.md:230-236`,
`src/Croft.Api/Telemetry/ApiMetrics.cs:14-22` (the API's only two counters),
`src/Croft.Api/Services/CacheListenerService.cs:23-24`, `:59` (reconnect and clear, logged only),
`src/Croft.Web/Program.cs:776-805`.

**What is wrong.** The k8s README promises:

> an API or database outage drains the Web pods' traffic (they answer unready) but never restarts
> them (`k8s/README.md:234-236`)

The API's `/health` is a health-check pipeline with no registered checks, so it answers 200 while
the database is unreachable; the Web readiness probe calls it and stays Ready. A database outage
drains nothing. (For the public pages that is arguably the right outcome, since the cache can keep
serving; but then the README should say so, and the admin area should say "degraded".)

Beyond the probes, the design's two load-bearing mechanisms emit no signal: the public read cache
has no hit/miss/refill counter and no size gauge; the listener has no "connected" gauge and no
counter for "cache cleared on reconnect" (the event that explains a sudden latency spike on one
pod); there is no replica-lag reading and no "scheduled post published N seconds late" measure.
`ApiMetrics` carries two counters (page views, posts published). The listener's `BackendPid`
(`CacheListenerService.cs:35`) exists for tests, not for operators.

**Why it matters.** Scenario: the listener on pod B loses its connection during a failover and
reconnects 30 seconds later (`MaxBackoff`, `CacheListenerService.cs:24`); for those 30 seconds pod
B serves stale content for every edit, then clears its whole cache and takes a miss storm. The
owner sees a latency blip on half the requests and a complaint about an edit that "did not take",
and has one Warning log line per pod to go on. With findings 2 and 3 in play, the same symptom has
three different causes and nothing distinguishes them.

**What would fix it.** An `IHealthCheck` on the API that reports the database (as Degraded, not
Unhealthy, so the cache keeps serving) and the listener's connection state, exposed on a separate
`/health/ready` while `/health` stays the liveness signal; the k8s README corrected to match. On
the `Croft.Api` meter: `croft.cache.requests{outcome=hit|miss}`, `croft.cache.entries`,
`croft.cache.cleared{reason=notify|reconnect|expiry}`, `croft.cache.listener.connected` (gauge),
and `croft.posts.publish_delay` (histogram, seconds late). These are an afternoon's work and turn
the cleverest part of the design into something an on-call person can reason about.

### 8. Important: access, refresh and ID tokens are stored in plaintext in the Web database

**Classification:** a trade-off I would make differently. Sharing sessions across pods is the
right call (HA design D1); storing the credentials unprotected is the part I would challenge.

**Where.** `src/Croft.Web/Data/UserSession.cs:13-15`, `src/Croft.Web/Services/PostgresTokenStore.cs:90-102`,
`src/Croft.Web/Program.cs:311-313` (Data Protection keys in the same database).

**What is wrong.** The row holds the tokens "exactly as the entry holds them":

```csharp
// src/Croft.Web/Data/UserSession.cs:13-15
public string AccessToken { get; set; } = "";
public string? RefreshToken { get; set; }
public string? IdToken { get; set; }
```

```sql
-- src/Croft.Web/Services/PostgresTokenStore.cs:92-93
INSERT INTO "UserSessions" ("SessionId", "Sid", "Sub", "AccessToken", "RefreshToken", "IdToken", "ExpiresAt", "SessionExpiresAt")
VALUES (...)
```

A refresh token is a long-lived credential (30 days by default, `AuthSettings.cs:13`) that mints
new access tokens at the IdP. The in-memory store kept it in one process's memory; the HA change
put it in a table that is backed up, replicated, exported, and readable by any role with SELECT.

**Why it matters.** Scenario: a nightly `pg_dump` of `croft_web` lands in the same backup bucket
as the content export; a misconfigured bucket policy or a stale restore on a test cluster exposes
every staff member's refresh token, valid for weeks, with no IdP event to notice. The Data
Protection keys are in the same database, so protecting the tokens with the default protector only
helps against a partial exposure (one table, a read replica, a backup of the table) and not against
a full dump; it still raises the bar from "read a table" to "read two tables and run the unprotect
code", and a site can move the key ring elsewhere later without a schema change.

**What would fix it.** Protect the three token columns with `IDataProtector` (purpose
`"UserSession.Tokens"`) in `UpsertAsync` and the two readers; it is one `Protect`/`Unprotect` pair
and the existing `keys import` command keeps old file-ring keys valid. Rotate the keys out of the
Web database to a Secret or KMS-backed store when the site has one.

### 9. Minor: the expand/contract discipline has three gaps the guard does not see

**Classification:** wrong in the narrow sense (the guard claims to enforce a rule it only partly
checks); each gap is small today.

**Where.** `tests/Croft.Api.Tests/Fixtures/MigrationContractGuard.cs:69-80`,
`src/Croft.Api/Migrations/20261018114827_NavigationPlacement.cs:15-30`,
`src/Croft.Api/Data/Configurations/PostConfiguration.cs:39`,
`src/Croft.Api/Migrations/20261018091156_RecomputePostSummaries.cs:17-21`,
`src/Croft.Api/Services/PostSummaryBackfillService.cs:35-38`.

**What is wrong.**

(a) The guard pattern-matches four operation types and ignores `SqlOperation`
(`migrationBuilder.Sql(...)`), so `ALTER TABLE ... DROP COLUMN` written as raw SQL passes, as does
an `AddColumn` that is `nullable: false` with no default (which breaks the previous release's
INSERTs, the other half of the rule).

(b) The `NavigationPlacement` migration adds the new column and backfills it once; the new release
never writes the old `ShowInNavigation` column (it is a shadow property with a default,
`PostConfiguration.cs:39`, and no service touches it). During the rollout overlap an edit through
an old pod sets `ShowInNavigation` and leaves `NavigationPlacement` unchanged, so the page loses
its placement when the rollout completes; an edit through a new pod is invisible to the old pods'
navigation. Expand/contract needs dual-write for the overlap, not just a one-off backfill.

(c) `RecomputePostSummaries` sets every `Summary` to NULL in the init container, before the new
pods start, and the backfill that refills them runs once at startup, in one transaction holding a
row lock on every post it touches (`PostSummaryBackfillService.cs:35-38`, acknowledged in the
comment). Between the migration and the backfill's commit, every public list shows empty
summaries, served by the old pods for the whole rollout.

**Why it matters.** Rolling deploys are the normal case with two replicas and `maxUnavailable: 0`;
these are the exact windows the rule exists for. Today's post count makes (c) seconds long; (a) is
the one that will bite when someone reaches for raw SQL because EF's operation does not exist.

**What would fix it.** Teach the guard to flag `SqlOperation` whose text matches
`DROP|RENAME|SET NOT NULL|ALTER COLUMN .* TYPE` (require the attribute to override) and
`AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null }`. Make the new
release write both columns until the contract migration lands. Run data migrations that degrade
public output (c) as a backfill with a "not yet computed" fallback that renders the old value, or
compute the new summaries in the migration itself; commit per batch.

### 10. Minor: the shared contract serves two audiences with one shape and leaks identity

**Classification:** a trade-off I would make differently.

**Where.** `src/Croft.Shared/Contracts/PostDto.cs:5-13`, `src/Croft.Shared/Contracts/PostListRequest.cs:5-13`,
`src/Croft.Api/Controllers/PostsController.cs:13-34`.

**What is wrong.** `PostDto` is returned unchanged to anonymous callers and to the admin editor. It
carries `AuthorId` (the OIDC `sub`) and the raw `MarkdownContent`; `GET /api/posts/{slug}` is
`[AllowAnonymous]`, so both are public. The same record is the Web app's deserialisation target
and the API's serialisation source, so any field added for the editor (`ContentEditedAt`, the
feedback counts on `PostSummaryDto`) is on the public wire the moment it exists. `PostListRequest`
is both the shared wire contract and the `[FromQuery]` binding model, so a renamed property is a
silent query-string break rather than a compile error.

**Why it matters.** Scenario: a future field such as an internal editorial note or an author's
email lands on `PostDto` because the editor needs it, and is public the same day. The `sub` leak
is small but real: it is a stable identifier at the IdP that the privacy statement does not
mention.

**What would fix it.** A `PublicPostDto` (no `AuthorId`, no `MarkdownContent` unless the public
page needs it; it does, today, because the Web app renders it, see finding 6) and a staff variant
that extends it; the controller picks by role. Keep `PostListRequest` as the contract and bind a
separate query model in the controller.

### 11. Minor: search is uncached, unauthenticated and one query per request; search terms also expand the cache's key space

**Classification:** wrong by omission.

**Where.** `src/Croft.Api/Services/SearchService.cs:10-18`, `src/Croft.Api/Services/PostService.cs:276-280`,
`src/Croft.Api/Program.cs:92`.

**What is wrong.** `SearchService` is the one public read that bypasses `IPublicReadCache`; each
anonymous `GET /api/search?q=` runs a ranked full-text query on the replica (or primary). The post
list cache key, separately, includes `request.SearchTerm` (`PostService.cs:278`), so an anonymous
list request with a term creates a cache entry per distinct term; the 2,000-entry size limit
(`Program.cs:92`) bounds memory, but the eviction it triggers removes legitimate entries.

**Why it matters.** A crawler or a bored script with a word list makes the database do work the
rest of the public path was built to avoid, and can push the home-page entries out of the cache
while doing so. The concurrency limiter bounds the rate, not the cost.

**What would fix it.** Cache search results for anonymous callers under the same group as posts
(invalidation already covers them); exclude free-text terms from the list cache key (or cap the
number of such entries separately); consider a per-client limiter on `/search`.

### 12. Minor: the sitemap walks the whole catalogue through the API on every crawler request

**Classification:** wrong by omission; it is correct output built the expensive way.

**Where.** `src/Croft.Web/Services/SitemapService.cs:25-32`, `:72-93`, `src/Croft.Web/Program.cs:592-597`.

**What is wrong.** Each `/sitemap.xml` request pages through `/api/posts`, `/api/pages`,
`/api/categories` and `/api/tags` 100 rows at a time, sequentially, over HTTP, and the result is
not cached on the Web side nor given a cache header. With 3,000 posts that is more than 30 API
round trips per crawler fetch, and crawlers fetch sitemaps often.

**What would fix it.** Cache the built document per pod with the same five-minute expiry the API
uses, send `Cache-Control` and an `ETag`, and add a slim API endpoint that returns slugs and
`UpdatedAt` only (the sitemap needs nothing else) in one page.

### 13. Minor: smaller gaps in the base, the session layer and the documentation

**Classification:** a mix; each is a sentence to fix.

- **The kustomize base is not quite generic.** The optional network-policy component hard-codes a
  Traefik pod in `kube-system` (`k8s/components/network-policies/api-ingress.yaml:19-24`,
  `web-ingress.yaml:17-22`), which is the owner's k3s cluster leaking into "plain Kubernetes".
  Fine as a component, but it should say so or take the ingress selector as a patch point.
- **The k8s README describes the pre-09f world.** "Every reader holds a Blazor circuit in the Web
  pod's memory" (`k8s/README.md:95`) has not been true since the reading pages went static; the
  sizing rule that follows now applies to staff only. The root README's secrets table
  (`README.md`, "Deploying a site") omits `croft-web-db`, which the base requires.
- **Cookie and session lifetimes disagree.** The auth cookie keeps the framework default (14 days
  sliding; `src/Croft.Web/Program.cs:102-109` sets no `ExpireTimeSpan`) while the session row lives
  30 days (`AuthSettings.cs:13`): a staff member is signed out after a fortnight of absence with a
  still-valid refresh token sitting in the table until the purge.
- **Two Web-database reads per staff request.** `OnValidatePrincipal` reads the session row on
  every authenticated HTTP request (`TokenSessionEvents.cs:73-87`) and `GetAccessTokenAsync` reads
  it again per API call (`UserAccessTokenService.cs:28-36`). Harmless at staff volumes; a short
  per-request memo would remove the second read.
- **An unbounded dictionary.** `_refreshLocks` (`UserAccessTokenService.cs:26`, `:51`) adds one
  `SemaphoreSlim` per session that ever needed a refresh and never removes it. Negligible for a
  handful of staff; wrong in shape.
- **The scheduler's wake-up is per instance.** By design (`ScheduledPublisherHostedService.cs:17-21`)
  a post scheduled through pod A publishes up to five minutes late if A dies before it is due.
  Documented and acceptable; it belongs in the publish-delay metric from finding 7 so the owner can
  see it when it happens.

## Checked and found sound

- **The Web/API split with `Croft.Shared` records.** Thin controllers, services behind interfaces,
  DTOs never entities, the Web never touching the API's database; the shared records keep the two
  compiling together without a code generator. Finding 10 is about audience, not the split.
- **The rendering split is enforced in one place.** `App.razor:91-94` picks the render mode from
  `[ExcludeFromInteractiveRouting]`; the layout agrees (`MainLayout.razor:80`); MudBlazor assets are
  linked only for interactive documents; the reading pages load data once in
  `OnParametersSetAsync` and set a real 404 through `Navigation.NotFound()`.
- **The cache's own concurrency.** The generation counter and the gate (`PublicReadCache.cs:57-64`,
  `:83-104`) correctly reject a refill that straddles an invalidation; the W-079 eviction callback
  handles the replaced-entry race; `SizeLimit` bounds memory. Findings 2 and 3 are about what feeds
  the cache, not the cache.
- **The listener's failure model.** Own connection outside EF's pool, keep-alive, synchronous event
  handed to a channel, clear-on-reconnect because notifications sent while down are lost, capped
  backoff (`CacheListenerService.cs`). The only thing missing is a signal (finding 7).
- **Background jobs under Postgres advisory locks.** `BackgroundJobLock.TryRunAsync` for ticks and
  the session-level `RunAsync` for the migrate entries, with the command timeout lifted only around
  the acquire; the scheduled publisher's conditional UPDATE is safe even with a broken lock
  (`ScheduledPublisherHostedService.cs:101-108`); job ids are stable across releases.
- **Migrations as an init container with retry.** `MigrationRetry` distinguishes transient from
  real failures, never throws at PID 1, and exits 1 so Kubernetes restarts the init container; the
  contract-guard test exists and runs on every build (finding 9 is about its coverage).
- **Configuration fails at boot, not at first request.** Pool settings, circuit settings, session
  lifetime, analytics keys, audience, base URL and branding are all validated in `Program.cs`
  before serving; branding is read once into an immutable singleton with content-hashed URLs.
- **Deploy-time branding and site files.** A folder, a ConfigMap, hashes in the links, defaults
  for every missing file, startup failure for a malformed one; nothing in the repository names a
  site.
- **The base/overlay boundary.** Two replicas, surge-only rolling updates, anti-affinity and
  spread as preferences, PDBs, a preStop sleep sized against the grace period, ndots, numeric
  non-root user, `optional: true` ConfigMap mounts; everything site-specific (host, images, TLS,
  stickiness, branding, secrets) is a patch or a generator in the overlay. Finding 13's first bullet
  is the one leak.
- **Token refresh across pods.** `SELECT ... FOR UPDATE` in `PostgresTokenStore.RunLockedAsync`
  plus the per-process semaphore means a rotated refresh token is never spent twice; transient IdP
  failures keep the entry. Back-channel logout removes by `sid`, then by `sub`.
- **Telemetry is opt-in, scrubbed, and names the pod.** Off without the OTLP endpoint; the privacy
  processors drop credential-shaped keys and strip query strings; resource attributes come from the
  downward API. Finding 7 is about what is measured, not how it is exported.
- **The content export** is a read-only `REPEATABLE READ` transaction with an atomic swap, usable
  against a replica.

## Verification record

| Check | Result |
|---|---|
| `dotnet build Croft.slnx` (SDK 10.0.402 installed into `~/dotnet`; the pinned 10.0.401 rolls forward) | Build succeeded, 0 warnings, 0 errors (`TreatWarningsAsErrors` is on) |
| `dotnet test tests/Croft.Web.Tests --no-build` | 1,088 tests: 1,057 passed, 31 failed. All 31 are in classes that start a Postgres Testcontainer (`BackgroundJobLockTests`, `ConcurrentWebMigrationTests`, `DataProtectionKeyImportTests`, `HealthEndpointTests`, `SessionLifetimeTests`, `SessionPurgeTests`, `SessionRemovalTests`, `SharedDataProtectionKeysTests`, `SharedSessionRefreshTests`, `SharedSessionStoreTests`) and fail with "connect to Docker endpoint at unix:///var/run/docker.sock"; no container runtime exists in this environment. The remaining 1,057, including the CSP snapshots, the branding, the markdown renderer and the token-refresh unit tests, pass. |
| `dotnet test tests/Croft.Api.Tests` | Not run: the suite needs a Postgres Testcontainer. **Unverified.** The findings above that the API suite would exercise (cache invalidation, replica routing, the migration guard) are argued from the code; `PublicReadCacheTests.cs` was read and covers notify, burst, and cross-instance invalidation, not replica lag or a failed notify. |
| E2E (`dev/run-e2e.sh`) | Not run (needs the stack and Chromium against a database). **Unverified.** |
| README's "about 160 requests a second" | **Unverified.** |
| Kubernetes rollout and failover behaviour | Argued from the manifests and the code; not exercised on a cluster. **Unverified.** |

No file other than this report was created or changed.
