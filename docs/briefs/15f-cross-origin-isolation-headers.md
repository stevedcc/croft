# Brief 15f: Cross-Origin-Embedder-Policy and Cross-Origin-Resource-Policy (the last Observatory bonuses)

Read AGENTS.md first and follow it. One commit per item, test first. No pushes except your branch
`brief/15f` to `origin` and ONE draft PR. E2E instances 11..12 are yours
(`dev/run-e2e.sh --instances 2 --first-instance 11`); another implementer may be running suites on
9..10: check `pgrep -af "dotnet test|run-e2e"` before a suite run and never run two full suites
at once.

**Why:** MDN's HTTP Observatory scores crawford.dev 140 (2026-10-24): every test is at its maximum
except two that are "not implemented": Cross-Origin-Embedder-Policy (recommendation: `require-corp`
or `credentialless`) and Cross-Origin-Resource-Policy (defaults to `cross-origin`). Both are
bonus-only (0 today, no penalty). They complete the isolation set that COOP started.

**Owner-visible outcome:** the Observatory re-scan shows both tests passing with their bonus and
no other test regressed; the site, the feed, the admin and the editor behave exactly as before.

**CSP impact: none.** `CspSnapshots.txt` byte-identical.

## Facts
- Headers are assigned in `src/Croft.Web/Security/SecurityHeadersMiddleware.cs` (`Apply`, on
  `OnStarting`, values assigned not appended; HSTS outside Development). Tests:
  `tests/Croft.Web.Tests/SecurityHeadersTests.cs` (exact values on every response shape) and
  `ErrorResponseHeadersTests.cs`.
- Route-specific header handling already exists in `Program.cs` (e.g. the WebFinger endpoint sets
  `Access-Control-Allow-Origin: *` and removes the CSP headers in its own `OnStarting`, ~line 820;
  `/api/media/{id}` ~568, `/social/avatar/{hash}.webp` ~585, `/robots.txt`, `/sitemap.xml`,
  `/feed.xml`, `/branding/*`, `/manifest.webmanifest`, `/health/*`, the static assets).
- The reading pages' CSP is `default-src 'none'` with `img-src 'self'`, `font-src 'self'`,
  `connect-src 'self'`, `frame-src 'none'`: documents load no cross-origin subresource, so COEP
  cannot break them. The interactive documents (admin, editor) load MudBlazor's CSS/JS from
  `_content/MudBlazor` (same origin) and the Blazor WebSocket (not a subresource).

## Item 1: COEP on documents
`Cross-Origin-Embedder-Policy: require-corp` on every HTML document (static and interactive),
not on non-document responses. (`credentialless` is the fallback only if `require-corp` breaks
something the E2E suite shows: say which.) Decide "document" the way the CSP code does (find how
the CSP contributors tell documents from files; reuse it, don't invent a second rule).
Tests: `SecurityHeadersTests` gets the exact header on a reading page and an interactive page, and
its absence on `/robots.txt`, `/feed.xml`, `/api/media/{id}`, a static asset.
Break-it-once: drop the header → the document test fails.
Then E2E: the full suite once (admin and editor pages are the real check: MudBlazor assets, the
media proxy images in the editor, the social avatars, the draft backup script).

## Item 2: CORP per route class
- `Cross-Origin-Resource-Policy: same-origin` on documents, static assets (`_framework/*`, css,
  js, fonts via `MapStaticAssets`), `/social/avatar/*` and `/branding/theme.css` (only our documents
  load them).
- `cross-origin` (explicit) on what other sites and readers fetch from a browser:
  `/api/media/*` (post images shown by browser-based feed readers; Mastodon/Bluesky cards re-host,
  so they are unaffected either way), `/feed.xml`, `/sitemap.xml`, `/robots.txt`,
  `/.well-known/webfinger` and `/.well-known/security.txt`, `/manifest.webmanifest`,
  `/branding/*` files (logo, icons: referenced by the manifest and by other sites' cards).
- `/health/*`: same-origin.
Tests: one theory over the route classes above with the exact value each gets. HEAD answers carry
the same header as GET (W-101 middleware; add the two routes to `HeadRequestTests` if not covered).
Break-it-once: swap media to `same-origin` → the theory fails.

## Item 3: evidence and the score table
- Fetch MDN's scoring page (`https://developer.mozilla.org/en-US/observatory/docs/tests_and_scoring`)
  and record, in the commit message and the report, the exact modifiers for every COEP and CORP
  result row, and confirm no other test offers a bonus the site does not already have (the
  2026-10-24 report has CSP +10, COOP +10, HSTS +5, SRI +5, Referrer +5, XFO-via-CSP +5; Cookies,
  CORS, Redirection, X-Content-Type-Options at 0 max). Say what the expected new score is.
- Run the scanner locally against a started instance is not possible (it scans public hosts);
  instead run `npx -y @mdn/mdn-http-observatory crawford.dev` before your change (current state)
  and keep the output; the orchestrator re-scans after the deploy. If the CLI needs a different
  invocation, read its `--help` and say so.
- README (the security/headers section, wherever the CSP and COOP are described): the two new
  headers, the per-route CORP rule and why media stays `cross-origin`.

## Out of scope
The API host (api.crawford.dev) and its headers, HSTS `preload` directive (already preloaded via
the TLD), CSP changes of any kind, Permissions-Policy additions.
