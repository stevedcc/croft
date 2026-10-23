# Brief 04: make page-view analytics correct

Read AGENTS.md first and follow it, including "Finishing a task". Implement the design below; if it
doesn't fit, stop and explain. One commit per item.

## Problems

1. `Post.razor` records views server-to-server, so the API sees the Web pod's IP for every visitor.
   Per-IP-per-day dedupe then collapses each post to about one view per day. It also sends the
   current page URL as the "referrer".
2. The IP hash is unsalted SHA-256, which is reversible for IPv4 (pseudonymous personal data under GDPR).
3. Views on drafts are counted, including admins previewing their own posts.
4. `ViewCount++` is read-modify-write and loses updates under concurrency; the dedupe check-then-insert races.

## Design (decided)

### Browser beacon through the Web app
- `Post.razor`: stop calling `RecordPageViewAsync` in `OnInitializedAsync`. In
  `OnAfterRenderAsync(firstRender: true)` (interactive only, so it runs once, not during prerender),
  call a small JS function that sends `navigator.sendBeacon('/api/analytics/pageview', JSON)` with
  `{ postId, referrer: document.referrer }`.
- Web: a minimal API endpoint `POST /api/analytics/pageview` (plain HTTP request, so the visitor's IP
  is `HttpContext.Connection.RemoteIpAddress`, after forwarded headers):
  - if the user is authenticated with role Admin or Editor, return 204 and record nothing;
  - otherwise forward to the API's `POST /api/analytics/pageview` with headers
    `X-Visitor-IP: <ip>` and `X-Analytics-Key: <Analytics:IngestKey>`, and the user agent;
  - return 204 regardless of the API result (analytics must never break pages).
- API: honour `X-Visitor-IP` ONLY when `X-Analytics-Key` matches the configured
  `Analytics:IngestKey` (fixed-time comparison). Otherwise use `RemoteIpAddress` as today.
  Configure the same key in both apps (user secrets / `.env` locally, a secret in k8s).

### Hashing and dedupe
- `IpHash = HMAC-SHA256(key: Analytics:HashKey, message: "{yyyy-MM-dd}|{ip}")`, hex-encoded. Including
  the date means hashes can't be linked across days. The API fails at startup outside Development if
  `Analytics:HashKey` is missing. For Development, `.env.example` gets an obvious dev value.
- Add a `Day` column (`DateOnly`, UTC date) to `PageView`, with a UNIQUE index on
  `(PostId, IpHash, Day)`. Migration required. Drop the old non-unique index if it's now redundant.
- Record a view by inserting the row. On a unique violation (23505), it's a duplicate: do nothing.
  Only when the insert succeeds, increment atomically with
  `ExecuteUpdateAsync(s => s.SetProperty(p => p.ViewCount, p => p.ViewCount + 1))`.
- Only count posts that are Published with `PublishedAt <= now`. Otherwise do nothing (204).

### Summary query
- `GetSummaryAsync` groups by `Day` in SQL instead of loading every row into memory.

## Tests and evidence
- API tests:
  - the same IP on the same day → one view; the next day → two;
  - two different IPs → two views;
  - a draft post → zero views;
  - `X-Visitor-IP` is ignored without a valid key and honoured with it;
  - 20 concurrent identical requests → `ViewCount` is exactly 1;
  - two different `HashKey` values produce different hashes for the same IP and day.
- E2E: an anonymous visit to a published post increments its view count by 1 (check via the admin
  analytics API or page); reloading the page doesn't increment it again; a logged-in admin's visit
  doesn't increment it.
- Mutation check (rule 7): remove the role check in the Web endpoint and show the admin-visit E2E
  test fails, then revert.

## Out of scope
Bot filtering, dashboards, changes to the analytics admin UI beyond what's needed.
