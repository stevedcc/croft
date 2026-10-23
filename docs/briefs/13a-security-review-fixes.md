# Brief 13a: fix the five Important findings of the security review

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-security.md` (an independent review of 0.6.2, findings 1–5).
Read each finding completely before its item: it has the file:line references, the evidence and
the reasoning. This brief decides how to fix them; where it and the review differ, the brief wins.

**Owner-visible outcome:** nobody can take the site down with slow downloads or idle 404 tabs,
fill the database through the page-view beacon, bounce a staff member from the sign-in to another
site, or put attributes or `javascript:` links into a page through Markdown.

**CSP impact:** tightening only. The not-found and error pages move from the interactive policy to
the static one (item 2); update `CspSnapshots.txt` and say so in the commit. No directive is
loosened anywhere; the snapshot test must show that.

## Item 1: slow readers can't exhaust the request permits (review finding 1)

**Do:**
- Web and API: the global concurrency limiter stays, but is **chained with a per-client limiter**
  (`PartitionedRateLimiter.CreateChained`): per client address (after forwarded headers), at most
  `Croft:Web:MaxConcurrentRequestsPerClient` (default 8) / the API equivalent (default 16; its
  client is the Web pod, so the API keys on the forwarded visitor address when the request
  carries one, else the remote address — verify how Web forwards it and say what you used).
- `/api/media/` gets **its own** concurrency partition (`Croft:Web:MaxConcurrentMediaRequests`,
  default 32), separate from pages, so images can never starve pages and vice versa. Same split
  in the API for its media endpoint.
- Kestrel limits in both apps: `MinResponseDataRate` = 2 KiB/s with a 10 s grace (configurable),
  `MinRequestBodyDataRate` likewise, and a `MaxConcurrentConnections` (configurable, default 512).
- Document the settings in README (configuration table).

**Tests:** integration (WebApplicationFactory or a real Kestrel on a random port where the test
needs real sockets): one client holding 8 slow requests gets its 9th refused while another client
is still served; a slow media download does not block a page request even with the media
partition full; a response read slower than the minimum rate is aborted (fake or short grace in
test configuration). Break-it-once: remove the chained per-client limiter; the "another client is
still served" test must fail.

## Item 2: anonymous visitors never open a circuit (review finding 2)

**Do:**
- `NotFound.razor` and `Error.razor` become static SSR (`[ExcludeFromInteractiveRouting]`): plain
  markup, no MudBlazor component, consistent with the other reading pages (AGENTS.md
  "Interactivity"). Check every caller of `NavigationManager.NotFound()` and the status codes
  (404, 500) still come out right on both kinds of page.
- `CircuitAdmissionMiddleware`: a circuit start from an **unauthenticated** request is refused
  outright (the status and body the middleware already uses for refusals), whatever the count.
  Interactive pages are staff-only; if you find an interactive page an anonymous visitor
  legitimately needs, stop and report it instead of exempting it.
- W-007's "known edge" sentence about `/not-found` becomes obsolete: update the register.
- `CookieTests.NotFoundPage_AnInteractiveDocument_*` and the CSP snapshot change with the rule;
  say so in the commit (rule 5).

**Tests:** a GET to an unknown path renders the not-found page with 404, the static CSP and no
`blazor.web.js` (E2E, any engine); an anonymous SignalR negotiate/connect to `/_blazor` is refused
(integration); a signed-in staff member still opens `/admin` and the editor works (existing E2E).
Break-it-once: put the interactive render mode back on NotFound; the first test must fail.

## Item 3: the beacon can't fill the database (review finding 3)

**Do:**
- Web `/api/analytics/pageview`: `MaxRequestBodySize` 4096 bytes for that endpoint (413 above).
- API: `Referrer` truncated to 2048 characters and `UserAgent` to 512 before storing (truncate,
  don't reject: a long legitimate referrer still counts the view). The per-client limiter of item
  1 also covers the beacon.
- **No column-type change in this release** (expand/contract: a 0.6.2 pod still writing during the
  rolling deploy must never hit a constraint). Add the `HasMaxLength(2048/512)` + migration as a
  **pending contract** line in AGENTS.md for the next release, with the reason.

**Tests:** a 5 KB beacon body → 413 and nothing stored; a 10 KB referrer is stored as exactly its
first 2048 characters; a 2 KB User-Agent stored as 512. Break-it-once: remove the truncation; the
referrer test must fail.

## Item 4: no open redirect after sign-in (review finding 4)

**Do:** `LocalUrl.IsLocal` follows `UrlHelperBase.CheckIsLocalUrl`'s rules: reject any control
character or whitespace anywhere, `//` and `/\` after the first character too (e.g. `/\t/`, `/ /`,
`%09` is already decoded by the time it reaches us — check where it is decoded and test the form
that actually arrives), and keep `~/` handling. Also check every other place a redirect target
comes from the request (sign-out, `returnUrl` on other endpoints) and route them all through it.

**Tests:** extend `LocalUrlTests.IsLocal_ExternalOrMaliciousUrls_ReturnsFalse` with `/\t/evil.example`,
`/\n//evil.example`, `/\r/evil.example`, `/ /evil.example`, `/\u000B/evil.example`; keep the valid
cases passing; an integration test that `/login?returnUrl=%2F%09%2Fevil.example` ends with a local
redirect target (or none), never a `Location` that a browser resolves off-site. Break-it-once:
remove the control-character check; the tab case must fail.

## Item 5: Markdown can't emit attributes or dangerous links (review finding 5)

**Do:**
- `MarkdownRenderer` (Web) builds its pipeline from an explicit extension list equal to what
  `UseAdvancedExtensions()` gives today **minus GenericAttributes**; keep `DisableHtml()`. List the
  extensions in a comment. The API's `PostSummaryExtractor` pipeline gets the same list (it renders
  plain text, but keep the two consistent; a shared helper in Croft.Shared is fine).
- Links and images: allow only `http`, `https`, `mailto`, relative URLs and `#fragments`. Any other
  scheme (`javascript:`, `data:`, `vbscript:`, `file:`…, case- and whitespace-insensitive) renders
  as plain text (the link text, no `<a>`; the image's alt text, no `<img>`).
- The live posts use no `{…}` attributes (checked by the orchestrator), so nothing visible changes;
  confirm with the seed data and the E2E post pages.

**Tests:** `MarkdownRendererTests`: `{onerror=alert(1)}` on an image, `{style=…}` on a paragraph and
`{.class #id}` emit no attributes (the braces render as text or vanish — pin what Markdig does);
`[x](javascript:alert(1))`, `[x](JaVaScRiPt:…)`, `[x]( javascript:…)`, `![x](data:image/svg+xml,…)`
render without `href`/`src` to them; `http`, `https`, `mailto`, `/relative` and `#frag` still
render as links; tables, task lists, footnotes, emphasis extras and the other advanced
extensions the site uses still work (one case each). Break-it-once: add `UseGenericAttributes()`
back; the `onerror` test must fail.

## Out of scope
- Findings 6–12 (Minor): brief 13b.
- Serving media without the Web proxy (an ingress route or object store): a later decision.
- The database column constraints (pending contract, item 3).
