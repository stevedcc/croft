# Brief 10c: the strictest Content Security Policy the reading pages can carry

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item,
TDD, break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner's position (2026-10-16):** "I want that CSP as strict as it can be". CSP strictness is
first-class for this project. **Never loosen a directive** without stopping and reporting: that
is the owner's decision.

**Owner-visible outcome:**
- crawford.dev passes every test in the MDN HTTP Observatory (today: A+, 125/100, 11 of 12;
  the failing one is "deny by default").
- A reading page runs only the scripts it needs, each pinned by content, and sets no cookie.

**Evidence: the live header on `https://crawford.dev/` today, static document:**
```
default-src 'self'; script-src 'self' 'sha256-CbWm…'; style-src 'self';
style-src-elem 'self' 'sha256-XzQs…' 'sha256-6bvr…' 'sha256-+Jl3…'; style-src-attr 'none';
img-src 'self'; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self';
form-action 'self' https://authentik.crawford.dev; frame-ancestors 'none';
report-uri /csp-report; report-to csp
```
Scripts on that page:
- the inline theme bootstrap (hashed);
- `ReconnectModal.razor.js` (module);
- `blazor.web.js`;
- `js/editor.js`;
- `js/draft-backup.js`;
- `js/analytics.js`;
- `js/static-glue.js` (module, which imports `shell.js`, `post.js` and highlight).

The page also sets `.AspNetCore.Antiforgery.*` without `Secure`, and `croft_affinity`, which is
the site's Traefik setting and not yours.

## Item 0: a browser compatibility spike (results in the commit message, no product code)

Before changing the policy, measure what Playwright's three engines (Chromium, Firefox, WebKit)
do with the candidate policies. Use a throwaway test or page under `/tmp` or a test-only
endpoint. Record a table:

1. **External scripts allowed by a hash.** `script-src 'sha256-<file hash>' 'strict-dynamic'`
   with no `'self'`, and the `<script src>` carrying a matching `integrity`. Does each engine
   run it? Does it run the module scripts and the modules they `import`?
2. **Path-scoped sources:** `script-src https://host/_framework/ https://host/js/`, without
   `'self'`. Do the engines enforce the path?
3. **`require-trusted-types-for 'script'`:** which engines enforce it, and does `blazor.web.js`
   (enhanced navigation) or any of our scripts violate it on a reading page? Check in
   `Content-Security-Policy-Report-Only`.
4. **`default-src 'none'`** with the explicit directives listed below: anything blocked
   (manifest, fonts, favicon, beacon, enhanced-navigation fetch)?

## Item 1: reading pages load only what they use

**Do:**
- **Interactive documents only** (`PageRenderMode is not null`, the same switch 09i used for
  MudBlazor):
  - `js/editor.js` and `js/draft-backup.js`;
  - `ReconnectModal` with its module script. Reading pages have no circuit since 09f; confirm
    nothing on a static page uses it.
- **Static documents:** `MudThemeStyleCspContributor`'s `style-src-elem` hashes go. 09i removed
  MudBlazor from them, so the hashes allow styles that aren't on the page. Keep only the
  hashes a static document's own inline styles need. If there are none, keep none.
- **Moving from a reading page into admin or the editor** must still load these scripts:
  confirm the links into interactive areas cause a full document load, or make them do so.

**Tests:**
- E2E: a reading page's HTML has no `editor.js`, `draft-backup.js`, `ReconnectModal`
  or `MudBlazor` reference. The editor reached by clicking the sidebar's "New post" from a
  reading page has `editor.js` and `draft-backup.js`, and the draft backup works there.
- Break-it-once: load `editor.js` everywhere again; the first test must fail.

## Item 2: deny by default, every directive explicit

**Target policy, static documents** (adjust only with the spike's evidence, never by loosening):
```
default-src 'none';
script-src <item 3's choice>;
style-src 'self'; style-src-elem 'self' <only needed hashes>; style-src-attr 'none';
img-src 'self'; font-src 'self'; connect-src 'self'; manifest-src 'self';
object-src 'none'; base-uri 'none'; form-action 'self' <OIDC origin only where W-047 needs it>;
frame-ancestors 'none'; frame-src 'none'; worker-src 'none';
report-uri /csp-report; report-to csp
```

**Interactive documents:** the same `default-src 'none'` base, plus what they need, all from
the existing contributors (MudBlazor's style rules for admin under W-007, and so on).
`connect-src` keeps `'self'` for the circuit; check that the WebSocket works with `'self'` in
all three engines.

- `base-uri 'none'`: check nothing uses a `<base>` element. Blazor uses `<base href="/">`. If
  so, `base-uri 'self'` stays, and the commit says why.
- `form-action`: the OIDC origin (W-047) only on documents that render the sign-out form. If
  that's every document for a signed-in user, keep it everywhere, and say so.

**Policy snapshot tests:** one per document kind, comparing the full header string:
- a static reading page;
- the not-found page;
- an admin page;
- the editor;
- the login redirect.

A future change to the policy must then update a snapshot on purpose. The snapshot file says
"changing this is an owner decision if it loosens anything".

**Tests:**
- E2E in **Chromium, Firefox and WebKit**, if the project's Playwright setup supports running
  them. If it doesn't, add a project setting or category that does, and run the CSP tests in all
  three. Every document kind loads with **zero CSP violations** (the existing violation probe /
  `CspViolationProbe`), including enhanced navigation between reading pages, the beacon, the
  manifest, fonts and the favicon.
- Break-it-once: drop `manifest-src` with `default-src 'none'`; the manifest test must fail.

## Item 3: scripts pinned by content

Choose by the spike's results, in this order of preference:

1. **If all three engines run hash-allowed external and module scripts:**
   - `script-src 'sha256-<each root script>' 'strict-dynamic'`, with `integrity` on each
     `<script src>`; no `'self'`.
   - Imports by a hashed module are then trusted through `'strict-dynamic'`.
2. **If not:**
   - `script-src` lists the **path-scoped** sources the reading pages need, absolute with
     `Blog:BaseUrl`'s origin: `/_framework/`, `/js/`, and the hashed inline bootstrap.
   - Add `integrity` (SRI) on every `<script src>` and module preload anyway.
   - Commit: which engine lacked what, citing the spike.
3. Either way, `MapStaticAssets` already computes integrity hashes; use the framework's
   integrity support (`@Assets` / the resource collection) rather than hashing files at runtime.

**Tests:**
- E2E (three engines): each reading page's scripts carry `integrity`.
- A script served from `/api/media/…` would be blocked. Simulate it with a same-origin script
  outside the allowed set: a test-only endpoint or a static file under a different path,
  injected via DevTools/Playwright `addScriptTag`. It must be refused, with a violation report.
- Break-it-once: put `'self'` back into `script-src`; the refusal test must fail.

## Item 4: Trusted Types, as far as the spike allows

- **If no engine reports a violation on reading pages:** add `require-trusted-types-for 'script'`
  and `trusted-types 'none'` to static documents.
- **If blazor.web.js or our scripts violate it:** for our own scripts, change the code so it
  doesn't need a sink (`textContent`, DOM APIs). For blazor.web.js, ship the directives in
  `Content-Security-Policy-Report-Only` only, so the reports show what remains, and write the
  finding into the workaround register (next free ID) with the upgrade check "remove when
  blazor.web.js is Trusted-Types clean".
- **Never** add a pass-through `default` policy. That defeats the purpose; stop and report
  instead.

## Item 5: no cookie on reading pages; Secure everywhere

- **Antiforgery:**
  - `Cookie.SecurePolicy = CookieSecurePolicy.Always` outside Development (TLS ends at Traefik,
    so `SameAsRequest` gave no `Secure`).
  - **Static reading pages issue no antiforgery cookie at all:** Razor component endpoints
    generate a token on every render today.
  - Find the supported way to skip it for static documents that render no form (e.g.
    `[RequireAntiforgeryToken(false)]`, endpoint metadata, or not rendering `AntiforgeryToken`).
    Admin, the editor and login keep antiforgery.
  - The Search page's GET form needs no token.
- Check the authentication cookies are `Secure` too.

**Tests:**
- E2E or integration: `/`, a post, a category, a tag, search, a static page and not-found
  respond with **no `Set-Cookie`** for an anonymous visitor (the site's `croft_affinity` comes
  from Traefik and isn't present in the test stack).
- Admin still sets the antiforgery cookie, with `Secure` when not in Development.
- An admin form POST without a token is still rejected.
- Break-it-once: render the token on reading pages again; the no-cookie test must fail.

## Item 6: report endpoints and documentation

- `Reporting-Endpoints` and `report-to` already exist. Check that the Report-Only header (item 4),
  if any, reports to the same endpoint, and that the rate limiter covers it.
- Update the docs that describe the CSP: `README` / `docs/` / `branding.example`, wherever the
  policy is explained, and the workaround register entries the changes touch (W-007, W-047,
  and any that are removed or added).

## Out of scope

- The site's `croft_affinity` cookie (Traefik configuration in the site repository; the
  orchestrator handles it).
- Output caching (a later brief). Note in the report whether item 3's choice is
  cache-friendly: hashes are, per-request nonces would not be. **Don't use nonces.**
- Any loosening (see the top).
