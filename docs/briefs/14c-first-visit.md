# Brief 14c: a first visit downloads a third of what it does today

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Builds on 14b (output cache):** `ReadingPageCache`, `ReadingPageCachePolicy`, `FeedCachePolicy`,
the `content` tag and the `Croft:Web:OutputCache:*` settings. Read `src/Croft.Web/Caching/` first.

**Why (measured on crawford.dev, 2026-10-22, Croft 0.6.6):**
- A first-time visitor costs ~19 requests and ~650 KB. In the load test, 30 new visitors/s put
  every Pi at 99 % CPU; the cost is per byte and per request (TLS, Traefik), not Croft's code.
- **Fonts: 628 KB, all seven files on every page.** `App.razor:17-23` preloads all seven faces, so
  the browser downloads Bold, Italic and JetBrains Mono even on pages that never use them.
- **Fonts are not fingerprinted:** `/fonts/SourceSerif4-Regular.woff2` is served with
  `Cache-Control: max-age=3600, must-revalidate`, so a returning reader revalidates them every
  hour. MapStaticAssets already serves the fingerprinted route
  (`fonts/SourceSerif4-Regular.pfi95wxypu.woff2`, `max-age=31536000, immutable`); nothing uses it.
- **The HTML is not compressed:** `/` sends 21,190 bytes and a post 33,679 bytes, no
  `Content-Encoding`, with `Accept-Encoding: br, gzip` (CSS and JS are, via MapStaticAssets).
- **Every image request goes to the API:** `GET /api/media/{id}` in `Program.cs:562` proxies to
  the API each time (a DB lookup plus a file read). The API answers `public, max-age=86400` for a
  published or global image and `no-store` for a draft's.

**Already prepared (commit `f498767`, by the orchestrator):** Latin subsets of the seven fonts in
`src/Croft.Web/wwwroot/fonts/` (`CroftSerif-{Regular,Italic,SemiBold,Bold}.woff2`,
`CroftSans-{Regular,SemiBold}.woff2`, `JetBrainsMono-Latin.woff2`; 217 KB together), built by
`dev/fonts/subset-fonts.py` from the unmodified sources in `dev/fonts/source/`. Nothing references
them yet. Don't run or change the script; use its output as it is. Source Serif 4 and Source
Sans 3 are renamed **Croft Serif** and **Croft Sans** because a subset is an OFL Modified Version
and may not use the Reserved Font Name "Source" (read the script's docstring).

**Owner-visible outcome:**
- A first visit to the home page or a post downloads only the font faces that page uses, as Latin
  subsets, and the page HTML compressed.
- A returning reader's browser fetches no font again for a year (until a font file changes).
- Images on published posts come from the Web pod's memory after the first request.
- Pages look the same: same faces, same measure, same line breaks (the subsets keep the metrics).

**CSP impact: none.** Fonts stay same-origin (`font-src 'self'`), preload links are not scripts,
compression changes no header CSP reads. `CspSnapshots.txt` byte-identical. Any change that seems
to need a CSP change: stop and report.

## Item 1: measure a first visit (E2E), before changing anything
A Playwright test that loads a page in a fresh browser context (empty cache) and records, from the
network events: the number of requests, the bytes transferred (encoded body sizes) in total and
per kind (document, font, style, script, image), and the font URLs fetched.
- Pages: `/` and one post with at least two images, at 1440 × 900 and at 390 × 844. Create the
  post in the test's own setup (the existing E2E seeding helpers); don't depend on database state.
- Wait for `document.fonts.ready` and network idle before reading the numbers.
- This item asserts nothing about sizes yet (item 7 adds the budget). It writes the numbers to the
  test output. **Put the numbers for all four runs in the commit message**: they are the baseline.
- Break-it-once: none (it measures).

## Item 2: the pages use the subset fonts
- `app.css`: the `@font-face` rules point at the subset files. The family names become
  `"Croft Serif"` and `"Croft Sans"` everywhere they are used: `app.css` (including
  `--font-*` variables), the scoped `.razor.css` files (`PageSurface`, `Post`,
  `DraftReviewPage`), and any MudBlazor typography setting. Keep each stack's fallbacks.
  JetBrains Mono keeps its name and uses `JetBrainsMono-Latin.woff2`.
- Delete the seven old `.woff2` files from `wwwroot/fonts/` (the sources stay in
  `dev/fonts/source/`). The `OFL-*.txt` files stay.
- `THIRD-PARTY-NOTICES.md`: the Source Sans 3 and Source Serif 4 rows say they are Latin subsets,
  renamed Croft Sans and Croft Serif under the OFL's Reserved Font Name rule, built by
  `dev/fonts/subset-fonts.py` from the unmodified upstream files in `dev/fonts/source/`; the
  JetBrains Mono row says Latin subset. Remove "Unmodified WOFF2 builds".
- Update the tests that name the old files or families (`ReadingMeasureTests`,
  `SiteFilesEndpointTests`, `SiteFilesServiceTests`, `PageRequestRateLimiterTests`, and any other
  `grep -rn "SourceSerif4\|SourceSans3\|Source Serif\|Source Sans"` finds). A test whose point was
  "a real static file" uses one of the new files; don't weaken what it checks.
- Tests:
  - Every `url()` in `app.css` and every font `<link rel=preload>` in the rendered page answers
    200 from the test server.
  - No served CSS (app.css, the scoped bundle) contains `Source Serif` or `Source Sans`.
  - E2E: on a post at 1440 × 900, the computed `font-family` of the body text, a heading and an
    inline code span resolves to a face `document.fonts` reports as `loaded` (Croft Serif, Croft
    Serif/Sans as the CSS says, JetBrains Mono).
  - `ReadingMeasureTests` passes unchanged in what it asserts (the subsets keep the metrics; if
    a measure changes, stop and report the numbers instead of adjusting the test).
- Break-it-once: point one `@font-face` at a deleted old file; the 200 test fails.

## Item 3: preload only the faces every reading page uses
- Find out, in E2E, which faces `document.fonts` reports as `loaded` after a cold load of `/`, a
  post (with italic, bold and inline code in its Markdown), a category page and a tag page, at
  both viewports.
- `App.razor` preloads **exactly the faces loaded on every one of those pages** (expected: the body
  serif Regular and the UI sans Regular, perhaps SemiBold; your measurement decides). The other
  faces load when a page uses them (`font-display: swap` stays).
- Tests (E2E): on each of the four pages, every preloaded face is `loaded` (no wasted preload),
  and Chromium logs no "preloaded but not used" warning. A unit/bUnit test pins the preload list
  so a new preload needs a deliberate change.
- Break-it-once: add a preload for `CroftSerif-Bold`; the home page's no-wasted-preload test fails.
- Commit message: the measured list per page and viewport.

## Item 4: fonts are fingerprinted and cached for a year
- The preload `href`s come from `@Assets["fonts/…"]`, so they carry the fingerprint.
- `app.css` is not processed by the build, so its `url()`s don't get fingerprints on their own.
  First check whether the .NET 10 static-assets pipeline can rewrite `url()` in a CSS file (if it
  can, use it and say how in the commit). If not: write the fingerprinted file names into
  `app.css` (`fonts/CroftSerif-Regular.<fingerprint>.woff2`), with a comment above the rules that
  says where the fingerprints come from and that the guard test below fails when a font changes.
- The preload URL and the CSS URL of a face **must be the same URL**, or the browser downloads it
  twice (a preload is only reused for the identical URL with the same CORS mode: keep
  `crossorigin` on the preloads).
- Tests:
  - Guard: every font `url()` in `app.css` answers 200 with `Cache-Control` containing
    `immutable` from the test server. (A changed font gets a new fingerprint, the old URL 404s,
    and this test names the rule to update.)
  - E2E, cold load of a post: each font URL is requested exactly once, and every font response
    has `immutable`.
- Break-it-once: change one fingerprint in `app.css` by a character; the guard fails.

## Item 5: compressed reading pages, feed and sitemap
- Exactly the responses the output cache may store (`ReadingPageCachePolicy.IsCacheable` and
  `FeedCachePolicy.IsCacheable`: anonymous, static reading pages, the feed, the sitemap) are
  compressed: Brotli if the request accepts `br`, else gzip if it accepts `gzip`, else none.
  Nothing else: no admin or staff page, nothing authenticated, no error page, no `/api/media`
  (images are already compressed), no static asset (MapStaticAssets serves its own precompressed
  files).
- **Why only those:** they carry no secret (no antiforgery token, no user data), so compressing
  them opens no BREACH-style leak. Write that reason in a comment where the predicate is used.
- **Compressed once, served many times:** compression runs inside the output cache, so a cached
  entry holds the compressed bytes and a hit compresses nothing. The cache key varies on the
  **normalised** encoding (`br`, `gzip` or `identity`, from the request's `Accept-Encoding`),
  never on the raw header, so at most three entries per page exist. Use ASP.NET Core's
  `ResponseCompression` (`EnableForHttps = true`, providers limited to the predicate) or a small
  middleware of your own; say which and why in the commit. Quality: `CompressionLevel.Optimal`
  (it runs once per entry per `Duration`).
- Responses carry `Vary: Accept-Encoding`. The feed and sitemap keep their
  `Cache-Control: public, max-age=…`.
- Check enhanced navigation (`blazor-enhanced-nav`, W-098) still works on a compressed cached
  page: the existing enhanced-navigation E2E passes, and a cache hit still returns the header as
  `allow`.
- Tests:
  - `/` with `Accept-Encoding: br` → `Content-Encoding: br`; the decompressed body equals the
    uncompressed response byte for byte; compressed size ≤ 35 % of uncompressed. Same for gzip,
    the feed and the sitemap.
  - `Accept-Encoding: identity` (or none) after a `br` request filled the cache → plain HTML, not
    Brotli bytes (the vary works).
  - A signed-in request to `/` with `br` → no `Content-Encoding`.
  - An admin page, `/not-found` and `/api/media/{id}` with `br` → no `Content-Encoding` from this
    code.
  - Two `br` requests to `/` → the compressor ran once (count it through a seam, e.g. a wrapping
    provider registered in the test host).
- Break-it-once: drop the anonymous condition from the compression predicate; the signed-in test
  fails.

## Item 6: published images are cached in the Web pod
- `GET /api/media/{id}` in the Web is output-cached when: the request is anonymous, the API
  answered 200, and the API's `Cache-Control` contains `public`. Never cached: a signed-in request
  (staff see drafts), a `no-store` answer (a draft's image), any non-200, a 503 from the proxy.
- Key: the `id` only (ignore the query string). Tag: `content` (14b's staff writes evict it, media
  deletes included). Lifetime: `Croft:Web:OutputCache:Duration`, the same minute as the pages, so
  an unpublished post's images disappear within that minute on every pod.
- New setting `Croft:Web:OutputCache:MediaMaximumBodyKilobytes`, default `512`: a larger image is
  streamed uncached, whole. Add it to `k8s/configmap.yaml` and the `k8s/README.md` table beside
  the other `OutputCache` rows.
- The cached response replays `Content-Type`, `Cache-Control` and `X-Content-Type-Options`.
- Tests (with a fake API that counts calls):
  - Two anonymous requests for a published image → one API call; identical bytes and headers.
  - Two anonymous requests for a draft's image (`no-store`) → two API calls.
  - Two signed-in requests → two API calls.
  - An image over 512 KB → served whole both times, two API calls.
  - A staff media delete through the Web → the next request calls the API.
  - `?v=1` and `?v=2` on the same id → one API call.
- Break-it-once: drop the `public` check; the draft test fails.

## Item 7: the first-visit budget
- Run item 1's measurement again and put before/after for all four runs in the commit message.
- Turn it into assertions, at the after-numbers plus 10 % headroom (rounded up to the next 5 KB),
  per page and viewport: total bytes transferred, font bytes, and the number of font requests.
- Break-it-once: preload `CroftSerif-Bold` again; the home page's font budget fails.

## Out of scope
- Removing MudBlazor's CSS from public pages (its own 0.7.0 brief), variable fonts, splitting fonts
  by `unicode-range`, image resizing or `srcset`.
- Browser caching of pages or images (`Cache-Control` changes on either): unchanged.
- Compression in Traefik or HAProxy.
- Changing `dev/fonts/subset-fonts.py`, the character range or the subset files.
