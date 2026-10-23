# Brief 14b: readers get cached pages; feed, sitemap and search stop doing full work per request

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Builds on 14a (warm-up):** it uses 14a's `PublicReadRoutes` and the API warm-up list. A new
public read added here goes into both, and 14a's drift guard fails until it does.

**Source:** `docs/reviews/2026-10-19-architecture.md` findings 6 (reading pages not cacheable),
11 (search uncached) and 12 (sitemap walks the catalogue); `docs/reviews/2026-10-19-dotnet-code-review.md`
M-18 (feed and sitemap uncached). Read each before its item. Already done elsewhere, so not
here: M-2 (single-flight refills and the negative cache, 13c `882e255`); the list cache's
search-term key flooding (13c item 3b).

**Owner-visible outcome:**
- An anonymous reader's page comes from the Web pod's memory: no API round trips and no Markdown
  render.
- After an edit or a publish, readers see the change **within one minute**. The Web pod that
  handled the write shows it at once, and staff always see it at once.
- Crawlers fetching the feed and sitemap cost one cached build per five minutes.
- Search no longer runs a full-text query for every repeat of the same search.

**CSP impact: none.** Cached responses replay the headers the page was rendered with, and the
reading pages' CSP is hash-based and nonce-free (that is what makes them cacheable).
`CspSnapshots.txt` byte-identical. Any change that seems to need a CSP change: stop and report.

## The design (decided; don't re-open it)
- **ASP.NET Core output caching** (`AddOutputCache`/`UseOutputCache`) in the Web app, one named
  policy for the reading pages.
- **What's cached (only these):** `GET`/`HEAD`, status 200, the request **not authenticated**, the
  document kind `CspDocument.KindOf(context) is CspDocumentKind.Static` (the same test W-092 uses),
  and a response without `Set-Cookie`.
  - The reading routes: `/`, `/post/{slug}`, `/{slug}` (static pages), `/category/{slug}`,
    `/tag/{slug}` and `/categories`.
  - **Not cached:** `/search` (free text; item 3 caches it in the API instead), `/not-found`
    and every non-200 answer, `/Error`, admin and staff pages, the health endpoints and
    everything authenticated.
- **Cache key:** the path, plus the query parameters the page reads and nothing else. Today that
  is `page` on Home, Category, Categories and Tag (`[SupplyParameterFromQuery]`). Other query
  strings map to the same entry, so `?utm_source=x` can't mint entries.
- **Enhanced navigation:** check whether a Blazor enhanced-navigation request (its `Accept`
  header / `blazor-enhanced-nav`) gets a different response from a full load. If it does, vary on
  that header. If it doesn't, say so in the item's commit with the evidence.
- **Lifetime and size**, as settings in `blog-web-config`'s style (`Croft:Web:OutputCache:*`):
  - `Duration`: default `00:01:00`. `0` turns page caching off.
  - `FeedDuration`: default `00:05:00`.
  - `SizeLimitMegabytes`: default `32`.
  - `MaximumBodyKilobytes`: default `1024`. A larger page is served uncached, not truncated.
- **Invalidation:** expiry only, plus tag eviction on the pod that handles a staff write. Every
  cached entry is tagged `content`. The Web's staff write paths (create, update, publish,
  unpublish, delete, schedule of posts and pages, category and tag edits, media deletes) evict
  `content` after the API call succeeds. Other pods catch up when their entries expire (≤
  `Duration`).
- **Browser and proxy caching of pages: unchanged.** The reading pages send no new
  `Cache-Control`: someone who signs in must not get their browser's cached anonymous copy. The
  server-side cache gives the speed. The feed and sitemap do get `Cache-Control: public,
  max-age=300`, because they're the same for every caller.
- **Warm-up (14a):** the warm-up's requests are anonymous and fill this cache. That is
  intended: a new pod starts with its reading pages cached.

## Item 1: output cache for anonymous reading pages (finding 6)
Tests (Web; each test creates its own content through the stub or seeded API, as the page
tests do):
- **Second hit cached:** an anonymous `GET /post/{slug}` twice makes **one** set of API
  calls (count the requests the API handler sees), and the second response is byte-identical,
  headers included (CSP, security headers).
- **Same for each route:** `/`, `/{slug}`, `/category/{slug}`, `/tag/{slug}`, `/categories`
  (parameterised).
- **Query parameters:** `/?page=2` is a separate entry from `/`. `/?utm_source=x` is served from
  `/`'s entry, with no API calls.
- **Drift guard:** every `[SupplyParameterFromQuery]` name on a cached page component is in that
  route's vary list (reflection over the page types). A new query parameter can't silently
  serve the wrong page.
- **Not cached:**
  - a signed-in staff request (twice → two sets of API calls), and it doesn't evict or read the
    anonymous entry;
  - `/search?q=x`;
  - a 404 slug (twice → the API is asked twice; the API's own negative cache answers it);
  - an admin page.
- **Expiry:** with a `FakeTimeProvider` (or the output cache's clock hook, whichever the
  framework offers; say which), after `Duration` the next hit calls the API again.
- **Staff write evicts this pod's cache:** an anonymous `GET /post/{slug}` is cached. A staff
  update through the Web's write path, then an anonymous `GET` shows the new title.
- **No cookie leak:** a cached anonymous response carries no `Set-Cookie`, even when the request
  that filled it arrived with an (invalid) auth cookie.
- **Size cap:** a page above `MaximumBodyKilobytes` (use a small test setting) is served whole and
  uncached.

Break-it-once: drop the `IsAuthenticated` condition from the policy, and the staff test fails.

## Item 2: feed and sitemap (M-18, finding 12)
- `/feed.xml` and `/sitemap.xml` go through the output cache with `FeedDuration` and send
  `Cache-Control: public, max-age=300`.
- A slim API read for the sitemap: `GET /api/sitemap`, anonymous, returning the slugs and
  `UpdatedAt` of published posts and pages, and the slugs of the categories and tags with at least
  one published post. One query per kind, one response, cached in `IPublicReadCache` (group
  `Posts`; every write invalidates all groups).
  - Added to `PublicReadRoutes` and to the API warm-up list (14a).
  - `SitemapService` makes **one** API call instead of paging through four lists.
- **The XML must not change:** before changing `SitemapService`, add a test that pins today's
  document for a seeded set (two posts, one page, one category, one tag, one draft, one scheduled
  post), byte for byte. That test is the exception that's green first, so say so. It must stay
  green after the change.

Tests:
- `/sitemap.xml` with 250 published posts makes exactly one API request (red today: it pages
  100 at a time over four lists).
- The second `/feed.xml` and `/sitemap.xml` within `FeedDuration` make no API request.
- Both send `Cache-Control: public, max-age=300`.
- `/api/sitemap` never lists a draft, a scheduled post or an empty category or tag. The API caches
  it: two calls → one set of queries (capture SQL as 13e item 4 did). After a publish, the new
  post is in it.

Break-it-once: make `SitemapService` page through `/api/posts` again, and the one-request test
fails.

## Item 3: anonymous search is cached in the API (finding 11)
- `SearchService.SearchAsync` for anonymous public callers (`includeAdminCounts == false` and the
  caller not staff; use the same `PublicReadRequest.IsAnonymousRead` gate `PostService` and 13i's
  category and tag reads use) goes through `IPublicReadCache`, group `Posts`.
  - The key uses the same normalisation and clamps as the list's search term (`NormalizeSearchTerm`,
    `PagingHelper`). Empty results go through the negative path (`isEmpty`), which is capped at 200
    entries, so random terms can't take more than that.
  - Pages above `MaxCachedSearchPage` aren't cached, like the list.
- Staff searches are never cached.

Tests:
- The same anonymous search twice → one full-text query.
- `"Pi  Cluster"` and `"pi cluster"` → one entry (one query).
- Staff search twice → two queries.
- A search with no results is cached as negative (the budget counter moves).
- After a publish that matches the term, the next anonymous search finds it (invalidation).

Break-it-once: remove the anonymous gate, and the staff test fails.

## Item 4: the small leftovers (13i and 13g reviews)
- **`UserAccessTokenService.cs:110`** (13i review Minor 1): the catch filter
  `when (ct.IsCancellationRequested && !refreshTask.IsCompleted)` skips logging a refresh that
  faulted just before the caller's cancellation was observed.
  - First, write the test that reproduces it: the refresh has faulted, and the caller's token is
    cancelled. Expect one Warning.
  - If you can show from the runtime source that `Task.WaitAsync` never throws
    `OperationCanceledException` for an already-completed task, the window doesn't exist. Then
    record a RULING with the evidence, keep the test as a pin, and simplify only the filter.
  - Otherwise, drop `!refreshTask.IsCompleted` (attaching an `OnlyOnFaulted` continuation to a
    completed task is harmless) so the fault is logged.
- **`PostEditorForm.razor`:** Save draft, Publish now and Unpublish, when disabled, link their
  reason with `aria-describedby`, the way 13i item 6 did for Schedule. Read 13i's commit for the
  pattern; apply the same to each disable reason those buttons have.
  - Test (bUnit), per button and reason: disabled → `aria-describedby` names an element whose text
    explains it. Enabled → no `aria-describedby`.

## Out of scope
- Browser/CDN caching of the reading pages (`Cache-Control`, `ETag`): see the design; a later
  brief if a CDN is ever put in front.
- A cross-pod invalidation signal for the Web's output cache (Web pods hearing the API's
  `NOTIFY`): one minute of staleness is the accepted trade-off.
- Caching the rendered Markdown by `ContentEditedAt`: the output cache already renders once per
  minute per page per pod.
- M-8's materialised published counts on `Category`/`Tag`: with the API cache and this output
  cache the count query runs about once per five minutes per API pod, so a schema change
  maintained by every write path isn't worth its drift risk now. Reconsider if a measurement says
  otherwise.
- A-10 (public and staff DTOs) and A-13 (session memo, scheduler metric): 0.7.0.
