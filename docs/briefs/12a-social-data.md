# Brief 12a: replies from Mastodon and Bluesky are fetched, filtered and stored (API)

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Spec (approved by the owner 2026-10-21):** `docs/superpowers/specs/2026-10-19-social-replies-design.md`.
Read it in full first; this brief is its first quarter. Where the brief is more specific than the
spec, the brief wins. 12b (rendering, Web), 12c (admin, Web) and 12d (announcing on publish) build on
the contracts fixed here; don't build their parts.

**Owner-visible outcome (nothing on the page yet; 12b renders it):**
- A post can carry a Mastodon and a Bluesky post URL (API fields; the editor UI is 12c).
- The API fetches the public replies to both, on a schedule, filtered by the platforms' opt-outs and
  the owner's blocklist, and serves them as one discussion read: `GET /api/posts/{slug}/discussion`.
- Staff can hide a reply or an account (API endpoints; the buttons are 12c), and a hidden reply is
  gone from the next read.
- With `Social:Enabled=false` (the default) nothing is fetched and the read returns no section.

**CSP impact: none** (API only; the Web gains one client method). `CspSnapshots.txt` byte-identical.

**Test fixtures:** `docs/briefs/12a-fixtures/` holds real API responses with every name, handle,
text, avatar and URL replaced by invented values (`mastodon-status.json`: a status; `mastodon-context.json`:
`GET /api/v1/statuses/{id}/context`; `bluesky-thread.json`: `app.bsky.feed.getPostThread`, depth 3,
6 top-level replies). Use them for the response *shapes*. Build each test's fixture from them in
`tests/Croft.Api.Tests/Fixtures/Social/` and edit in the case under test (a `noindex` account, a
`!no-unauthenticated` label, a hidden reply, …). Never commit real people's data; never call the
real platforms from a test.

## The design (decided; don't re-open it)

### Contracts (`Croft.Shared/Contracts/Social/`, fixed for 12b and 12c)
```csharp
public enum SocialPlatform { Mastodon = 1, Bluesky = 2 }
public enum SocialBlockKind { Reply = 1, Account = 2 }

public sealed record PostDiscussionDto(
    string? MastodonUrl, string? BlueskyUrl,           // the linked posts (reply actions); null = not linked
    int ReplyCount,                                    // every reply in the tree below
    IReadOnlyList<DiscussionReplyDto> Replies);        // top level, both platforms merged by CreatedAt

public sealed record DiscussionReplyDto(
    SocialPlatform Platform, string ExternalId,        // Mastodon status id / Bluesky at:// URI
    string AuthorHandle, string AuthorDisplayName, string AuthorUrl, string AuthorAccountId,
    string? AvatarHash,                                // /social/avatar/{hash}.webp (12b serves it); null = fallback
    DateTimeOffset CreatedAt, string Permalink,
    string ContentHtml,                                // sanitised here, ready to render (see Sanitising)
    string? ContentWarning, bool HasMedia,
    IReadOnlyList<DiscussionReplyDto> Replies);        // nested to depth 3, deeper ones flattened into level 3 by CreatedAt

public sealed record DiscussionThreadStatusDto(
    SocialPlatform Platform, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt,
    string? LastError, DateTimeOffset? NextDueAt, int StoredReplies, bool Stale);

public sealed record PostDiscussionAdminDto(PostDiscussionDto Discussion, IReadOnlyList<DiscussionThreadStatusDto> Threads);

public sealed record SocialBlockDto(Guid Id, SocialPlatform Platform, SocialBlockKind Kind,
    string ExternalId, string? Label, string? Note, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record CreateSocialBlockRequest(SocialPlatform Platform, SocialBlockKind Kind,
    string ExternalId, string? Label, string? Note);
```
`PostDto` gains `string? MastodonUrl` and `string? BlueskyUrl` (nullable, defaulted, at the end, like
`ContentEditedAt`). `CreatePostRequest` and `UpdatePostRequest` gain the same two, optional.

### Endpoints
| Route | Who | What |
|---|---|---|
| `GET /api/posts/{slug}/discussion` | anonymous | `PostDiscussionDto`; 404 for an unknown or unpublished post; `MastodonUrl`, `BlueskyUrl` null and `Replies` empty when `Social:Enabled` is false |
| `GET /api/posts/{id}/discussion/admin` | Admin, Editor | `PostDiscussionAdminDto` (every stored reply, plus each thread's status) |
| `POST /api/posts/{id}/discussion/refresh` | Admin, Editor | marks both threads due now; 202 |
| `GET /api/discussion/blocks` | Admin | all blocks, newest first |
| `POST /api/discussion/blocks` | Admin, Editor | `CreateSocialBlockRequest` → 201 `SocialBlockDto`; a duplicate (same platform, kind, id) returns the existing one with 200 |
| `DELETE /api/discussion/blocks/{id}` | Admin | 204 |

Add every new endpoint to `EndpointAuthorizationTable` and, for the anonymous read, to
`ReadReplicaRoutingTests` (it is a public read).

### Data (one migration `SocialReplies`, expand only)
- `Posts`: `MastodonUrl`, `MastodonStatusId`, `BlueskyUrl`, `BlueskyUri` (all nullable text), and
  `DiscussionLinkedAt` (nullable timestamptz: when a link was first set or changed; the schedule's
  clock for posts published before the link).
- `SocialThreads`: (`PostId`, `Platform`) primary key; `LastSuccessAt`, `LastAttemptAt`, `LastError`
  (≤ 500 chars, no stack trace), `SnapshotHash`, `NextDueAt`, `LeaseUntil`. Cascade on post delete.
- `SocialReplies`: (`PostId`, `Platform`, `ExternalId`) unique; `ParentExternalId`, `AuthorHandle`,
  `AuthorDisplayName`, `AuthorUrl`, `AuthorAccountId`, `AvatarHash`, `CreatedAt`, `Permalink`,
  `ContentHtml`, `ContentWarning`, `HasMedia`, `FetchedAt`. Cascade on post delete. Nothing else
  (spec: no counts, no other metadata).
- `SocialBlocks`: `Id`, `Platform`, `Kind`, `ExternalId` (status id / at:// URI, or account id / DID),
  `Label`, `Note`, `CreatedBy` (the staff user's name), `CreatedAt`. Unique (`Platform`, `Kind`, `ExternalId`).
  Not tied to a post; survives post deletion.
- `SocialAvatars`: `Hash` (primary key), `SourceUrl`, `StoragePath`, `FetchedAt`. The file goes through
  `IFileStorage` under `social-avatars/` (AGENTS.md: files never go in the database).

### Validation and resolution (on create/update of a post)
- Mastodon URL: `https://{Social:Mastodon:Server}/@{Social:Mastodon:Account}/{digits}` exactly (host
  and account compared case-insensitively). Otherwise 400 with a problem detail naming the field.
  `MastodonStatusId` = the digits.
- Bluesky URL: `https://bsky.app/profile/{handle-or-did}/post/{rkey}`. A handle is resolved with
  `com.atproto.identity.resolveHandle` (public API) **at save**; the DID must equal `Social:Bluesky:Did`,
  else 400. `BlueskyUri` = `at://{did}/app.bsky.feed.post/{rkey}`. A resolution failure (timeout, 5xx)
  is a 503 with a problem detail, not a silent save.
- Clearing a URL clears its stored id/URI, deletes that platform's thread and replies.
- Setting or changing a URL sets `DiscussionLinkedAt` = now and makes that thread due now.
- With `Social:Enabled=false` the fields still save and validate (the owner can prepare posts).

### Fetching (hosted service `SocialFetchHostedService`)
- One loop per API instance, a tick every minute (`TimeProvider`, no `Task.Delay` in tests: drive the
  tick directly as the scheduler tests do). New `BackgroundJobIds.SocialFetch = 5`.
- **Claim under the lock, fetch outside it:** inside `BackgroundJobLock.TryRunAsync`, select the due
  threads (`NextDueAt <= now` and `LeaseUntil` null or past) of published posts, set `LeaseUntil =
  now + 5 min`, commit. Then fetch each claimed thread outside any transaction, and write each result in
  its own short transaction. A replica that dies mid-fetch leaves a lease that expires.
- **Schedule** from the later of `PublishedAt` and `DiscussionLinkedAt`: younger than 2 days → every
  10 min; younger than 30 days → hourly; older → daily. A failure sets `NextDueAt` = now + the normal
  interval (no faster retries; the 48-hour rule covers long outages). Unpublished posts are never fetched.
- **One request at a time per platform** (a `SemaphoreSlim` per platform; awaited, AGENTS.md
  prefers awaitable locks). Honour 429 + `Retry-After` and Mastodon's `X-RateLimit-Remaining`/`-Reset`:
  when remaining is 0 or a 429 arrives, that platform's claimed threads are released (lease cleared,
  `NextDueAt` = the reset time) and the tick moves on.
- **Mastodon:** `GET https://{Server}/api/v1/statuses/{MastodonStatusId}/context`; use `descendants` only.
- **Bluesky:** `GET https://public.api.bsky.app/xrpc/app.bsky.feed.getPostThread?uri={BlueskyUri}&depth=10&parentHeight=0`.
- **Replace on success, compare first:** build the filtered snapshot; hash it (stable: replies sorted
  by external id, every stored field). Same hash as `SnapshotHash` → update only `LastSuccessAt`,
  `LastAttemptAt`, `NextDueAt` and **invalidate nothing**. Different → replace that thread's
  `SocialReplies` rows in one transaction, store the hash, and invalidate `CacheGroup.Discussion`.
- **Failure:** keep the snapshot, set `LastAttemptAt` and `LastError`; invalidate nothing.

### Filtering (before storing; filtered replies are never stored)
Mastodon: keep only `visibility` `public` or `unlisted`; drop accounts with `noindex: true`; keep
`indexable: false` (owner decision); `sensitive`/`spoiler_text` → `ContentWarning` (text, or
"Sensitive content" when `spoiler_text` is empty); `HasMedia` = any `media_attachments`.
Bluesky: drop `notFoundPost` and `blockedPost` (and their subtrees); drop replies whose author has the
label `!no-unauthenticated`; drop URIs listed in the root's `threadgate.record.hiddenReplies` (and
their subtrees); drop posts labelled `porn`, `sexual`, `nudity` or `graphic-media`; `HasMedia` = an
`embed` of images/video/recordWithMedia.
Both: drop replies (and their subtrees) whose reply id or account id is in `SocialBlocks`; drop the
owner's own replies? **No: keep them** (the owner answering in the thread is part of the discussion).

### Sanitising (here, once, at ingest)
`ContentHtml` is safe to render as-is in 12b. Mastodon HTML → only `p`, `br`, `a`; `a` keeps `href`
only when it is `http`/`https`, and always gets `rel="nofollow ugc noopener"`; every other element is
unwrapped to its text, every attribute dropped; custom emoji `:shortcode:` stay text. Bluesky `text` →
HTML-escaped, line breaks → `<br>`, paragraphs on blank lines → `<p>`, and `facets` turned into the
same `a` links (mention → `https://bsky.app/profile/{did}`, link → its URI, tag →
`https://bsky.app/hashtag/{tag}`). **Facet offsets are UTF-8 byte offsets**, not UTF-16 indices: test
it with an emoji and an umlaut before a link. Use a real HTML parser for Mastodon (AngleSharp is fine,
MIT), never regular expressions.

### Avatars
- Each distinct `account.avatar_static` (Mastodon) / `author.avatar` (Bluesky) URL is downloaded once
  (`SocialAvatars.SourceUrl`), decoded and re-encoded to a 48×48 WebP (centre-cropped square), stored by
  the SHA-256 of the WebP bytes (hex) as `social-avatars/{hash}.webp`. Unchanged URL → no download.
- Library: **SkiaSharp** (MIT) with `SkiaSharp.NativeAssets.Linux.NoDependencies` (linux-arm64 and x64).
  Add both to `THIRD-PARTY-NOTICES.md`. The API image must still build and start: run the
  `Category=Container` and `Category=ReadyToRun` tests (AGENTS.md) in the final review and report them.
- Limits: download ≤ 1 MB (stop reading past it), decode only PNG/JPEG/WebP/GIF (first frame), reject
  images over 4096×4096 before decoding fully. A failed avatar leaves `AvatarHash` null (fallback); it
  never fails the thread.
- After every successful refresh, delete `SocialAvatars` rows and files no stored reply references.
- `GET /api/social/avatars/{hash}.webp` (anonymous): the file with `Content-Type: image/webp` and
  `Cache-Control: public, max-age=31536000, immutable`; 404 for an unknown or malformed hash (64 hex
  chars only). 12b proxies it from the Web.

### Outbound HTTP and the SSRF guard
- Named clients `SocialMastodon`, `SocialBluesky`, `SocialAvatars`: timeout 10 s, response cap (2 MB for
  threads, 1 MB for avatars; read with a limit, never `ReadAsStringAsync` on an unbounded body), a
  `User-Agent: Croft/{version} (+{Blog:BaseUrl})`, the standard resilience handler with **no retries**
  (the schedule retries).
- **SSRF guard on all three** (avatar URLs come from remote servers): a `SocketsHttpHandler.ConnectCallback`
  that resolves the host and refuses loopback, private (RFC 1918, ULA `fc00::/7`), link-local
  (`169.254.0.0/16`, `fe80::/10`), CGNAT (`100.64.0.0/10`), multicast and unspecified addresses, then
  connects to the checked address (no second resolution). Only `https` URLs, port 443. Redirects off.
- **Telemetry privacy** (`k8s/README.md`, the telemetry rule): no handles, names or texts in logs or span
  attributes. HttpClient spans for these clients must not export the full URL path (it contains handles
  for avatars); redact `url.full`/`url.path` to the host for `SocialAvatars`.

### Caching
- `CacheGroup.Discussion = 16`, **not in `All`** (a post save must not flush discussions, a discussion
  refresh must not flush posts). Update `Singles`, the slot arrays, `Slot()`, and the notifier's payload
  names (`discussion`). An old pod receiving `discussion` ignores it (it serves no discussion): pin that
  with a test of `TryParsePayload` behaviour on an unknown name.
- The public read is cached in `Discussion` for anonymous callers (`PublicReadRequest.IsAnonymousRead`),
  key `discussion:{slug}`. The cached value holds each thread's `LastSuccessAt`; **the 48-hour rule is
  applied after the cache** (a thread whose last success is older than 48 h contributes no replies), so
  a cached entry can't outlive it.
- Block, unblock, a link change and a changed snapshot invalidate `Discussion` (all replicas, via the
  notifier; the fetcher has no HttpContext, so call `NotifyAsync` + `InvalidateLocalAsync` as the
  scheduler does). A post's publish/unpublish/delete already invalidates `All`; also invalidate
  `Discussion` there (an unpublished post's discussion must 404 at once).

### Blocking
- Creating a block deletes the matching stored replies **and their subtrees** at once (all posts), and
  invalidates `Discussion`. Deleting a block makes the affected threads due now (the replies come back
  on the next fetch).
- `Label` is what the admin saw (display name and handle) so the blocks page is readable later; it is
  the one place a handle is kept outside a stored reply, and it is the owner's moderation record.

### Web (only this; rendering is 12b)
- `IBlogApiClient.GetDiscussionAsync(string slug, CancellationToken)` → `PostDiscussionDto?` (null on 404),
  via a new `PublicReadRoutes.Discussion(slug)`. Add it to the API warm-up (`ApiWarmupRoutes.For`, the
  newest post) and to `WarmupRoutesDriftTests` (`PublicShapes` + `CallAsync`). The block/refresh/admin
  methods are 12c's: don't add them.

### Settings (`blog-api-config`, `k8s/configmap.yaml`, pinned in `K8sBaseTests`)
`Social__Enabled` (`"false"`), `Social__Mastodon__Server` (`""`), `Social__Mastodon__Account` (`""`),
`Social__Bluesky__Did` (`""`). Enabled with an empty server/DID disables that platform (logged once at
start, Information). No secrets in 12a (12d's credentials come later).

## Items
1. **Contracts, migration, link fields and validation.** Tests: URL validation table (good, wrong host,
   wrong account, query string, `http`, trailing slash, a DID URL, a handle URL resolved through a stub);
   resolution failure → 503 and nothing saved; clearing a link deletes its thread and replies; the
   migration guard passes (expand only); `PostDto` round-trips the URLs.
2. **Filtering and sanitising** (pure functions over the fixture shapes, no HTTP): one test per filter case
   in the spec list (deleted, blocked, `!no-unauthenticated`, hidden, each label, private/direct,
   `noindex`, sensitive), subtree removal, the HTML allow-list (script, style, `javascript:` href,
   `onclick`, nested `div`/`span`, an `img`), Bluesky facets with multi-byte characters, depth capping
   (a depth-5 chain renders as 3 levels with the rest flattened in order).
3. **The fetcher, schedule, lease and snapshot store.** Tests (stub handlers, `FakeTimeProvider`): the
   intervals at 1 h / 3 d / 40 d; two "replicas" ticking at once fetch each thread once (lease); an
   unchanged snapshot invalidates nothing (count notifier calls); a changed one replaces rows and
   invalidates `Discussion` only; a failure keeps the snapshot and records the error; 429 with
   `Retry-After` releases the platform's claims; Mastodon `X-RateLimit-Remaining: 0` likewise; an
   unpublished post is never fetched.
4. **Avatars and the SSRF guard.** Tests: a PNG and a JPEG become 48×48 WebP with a stable hash; same URL
   twice → one download; an oversized body, a non-image, a 10000×10000 header → `AvatarHash` null and the
   thread still stored; unreferenced avatars deleted after a refresh; the guard refuses `127.0.0.1`,
   `10.0.0.1`, `169.254.169.254`, `[::1]`, `fd00::1`, a hostname resolving to a private address (stub
   resolver), and `http://`; the avatar endpoint's headers and its 404s.
5. **The discussion read, caching and blocks.** Tests: anonymous read merges both platforms by time with
   nesting; 404 for draft/scheduled/unknown; `Social:Enabled=false` → no links, no replies; the 48-hour
   rule hides a stale thread **from a cached entry** (fake clock moves past 48 h without a write); staff
   read is never cached; a block removes the reply and its subtree from the next read on another
   replica (two factories sharing the database and the notifier, as the cache tests do); unblock makes
   the thread due; each endpoint's authorization; `CacheGroup.All` excludes `Discussion` and a post save
   doesn't evict a cached discussion; publish/unpublish invalidate it.
6. **Web client method, warm-up, docs and settings.** The client method and drift guard; `k8s/configmap.yaml`
   keys + `K8sBaseTests`; README: a "Social replies" section (what is fetched, the opt-outs honoured, the
   settings, that outbound HTTPS to the configured Mastodon server, `public.api.bsky.app`, `bsky.social`
   and avatar hosts is needed) and the suggested privacy-page text from the spec (Croft supplies the text;
   the owner edits the page).

Break-it-once (one per key test, name it in the commit): drop the `noindex` filter (its test fails);
remove the lease (the two-replica test fetches twice); put `Discussion` into `All` (the post-save test
fails); disable the SSRF guard for private ranges (the `10.0.0.1` test fails); apply the 48-hour rule
before the cache instead of after (the cached-stale test fails); use UTF-16 indices for facets (the
umlaut test fails).

## Out of scope
- Rendering, the avatar proxy, the reply form and continue page (12b); the editor fields, the
  Discussion panel, the blocks page (12c); announcing and credentials (12d).
- Media from replies, likes/boosts, notifications, Bridgy Fed (spec: later).
- Cross-pod Web output-cache eviction (owner: a minute of staleness is fine).
