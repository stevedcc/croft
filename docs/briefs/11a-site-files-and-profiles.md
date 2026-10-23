# Brief 11a: site files and profile links

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:**
- A site can publish small well-known files (security.txt, WebFinger, Apple's association file,
  and similar) from a mounted folder, without rebuilding Croft.
- Mastodon shows the owner's profile as verified (green tick) and credits shared posts to them.
- Searching `@steve@crawford.dev` in Mastodon finds the owner's real account.
- Search engines can tie the author to their profiles (`sameAs`).
- The footer shows small icons linking to the owner's profiles.

**CSP impact: none.** No directive changes. Site files are not documents (no HTML, SVG or
script, item 1 enforces it), the icons are inline SVG with presentation attributes only (no
`style`), and nothing new loads from another origin. `CspSnapshots.txt` must not change.

**Not in this brief:** the CRL at `pki.crawford.dev` (brief 11b); Bluesky's `atproto-did` file
(the owner verified the domain by DNS); any site's actual values (the site repository supplies
them).

## Item 1: a site files folder

**Do:**
- New option `Blog:SiteFilesPath` (default `/app/site-files`, like `Blog:BrandingPath`). A
  missing folder is fine: no site files.
- At startup, read every file in the folder, recursively. The path relative to the folder is the
  URL path: `.well-known/security.txt` → `/.well-known/security.txt`.
- **Allowed files only** (startup fails with a `BrandingException`-style message naming the
  file and the rule):
  - extension `.txt` → `text/plain; charset=utf-8`;
  - extension `.json` → `application/json`;
  - `.well-known/apple-app-site-association` (no extension) → `application/json`;
  - `.well-known/webfinger` (no extension) → item 3.
  - Anything else fails: `.html`, `.htm`, `.svg`, `.js`, `.css`, other extensions, other files
    without an extension. Reason: a site file must never be a document or a script on the
    site's origin.
  - A file over 64 KiB fails.
- **No shadowing:** a site file path that the app already serves (any endpoint route, a static
  asset, `/robots.txt`, `/sitemap.xml`, `/feed.xml`, `/manifest.webmanifest`, `/branding/*`,
  `/health/*`) fails startup, naming both. Files with an extension or under `.well-known/` can't
  collide with page slugs (`[a-z0-9-]+`), so no slug check is needed; say so in a comment.
- Serving: exact path match, GET and HEAD only, no redirects, `Cache-Control: public,
  max-age=3600`, a strong ETag from the content, `X-Content-Type-Options: nosniff`, **no
  cookies**, no CSP document header (not a document). Kubernetes ConfigMap symlinks
  (`..data/`) must work: test with a symlinked file.
- Files are read once at startup. A ConfigMap change rolls the pods (the site repository's
  configMapGenerator hash does that); document it.
- `k8s/`: an optional ConfigMap `blog-site-files` mounted read-only at `/app/site-files`, the
  same way as `blog-branding` (optional, so the base deploys alone). `k8s/README.md` documents
  it, including the 1 MiB ConfigMap limit.
- Dev and E2E: a `site-files.example/` folder (next to `branding.example/`) with a
  `security.txt`, a `.well-known/webfinger` and a `humans.txt`; the dev stack and the E2E stack
  use it.

**Tests:**
- Unit: each allowed type gets its content type; each forbidden one (`.html`, `.svg`, `.js`,
  `noext`) fails startup with the file named; 64 KiB + 1 byte fails; a shadowing file
  (`robots.txt`, `branding/x.txt`) fails, naming the route; a symlinked file is served.
- Integration (WebApplicationFactory): served bytes equal the file; HEAD has no body; the ETag
  round-trips to 304; no `Set-Cookie`; no `Content-Security-Policy` header; POST is 405.
- E2E (any engine): `/.well-known/security.txt` and `/humans.txt` return the example files.
- Break-it-once: allow `.svg`; the forbidden-type test must fail.

## Item 2: security.txt checks

RFC 9116. The file itself is a site file (item 1); Croft only checks it.
- When `.well-known/security.txt` exists, parse it at startup:
  - no `Contact:` or no `Expires:` → one Warning log line naming what's missing;
  - `Expires:` in the past → Warning "security.txt expired on <date>";
  - `Expires:` within 30 days → Warning at startup and once a day while it stays so, with the
    date (a hosted service using `TimeProvider`);
  - `Expires:` unparsable (not ISO 8601 / RFC 3339) → Warning.
- **Never** a startup failure and never a readiness failure for any of these: the site must not
  go down over an expiring text file.
- A metric `croft.site_files.security_txt.days_to_expiry` (gauge) if the telemetry setup makes
  that a few lines; otherwise skip it and say so.

**Tests (fake `TimeProvider`):** Expires in 31 days → no warning; in 29 days → warning; expired →
warning; missing Contact → warning; one more day passing at 29 days → one more warning (the
daily one); none of them fails startup or `/health/ready`. Break-it-once: change 30 to 0 days;
the 29-days test must fail.

## Item 3: WebFinger

RFC 7033. The site supplies `.well-known/webfinger` as a JSON Resource Descriptor (JRD) file.
- At startup it must parse as JSON with a string `subject`; `aliases` (array of strings) is
  optional. Otherwise startup fails, naming the file.
- `GET /.well-known/webfinger?resource=<r>`:
  - `resource` missing → 400;
  - `<r>` equals the `subject` or one of the `aliases` (compare case-insensitively for the
    `acct:` host part, exactly otherwise) → 200, the file's bytes, `Content-Type:
    application/jrd+json`, `Access-Control-Allow-Origin: *`;
  - anything else → 404.
  - A `rel` parameter may be ignored (RFC 7033 allows returning all links).
- Same caching, ETag and no-cookie rules as item 1.

**Tests:** subject → 200 with the exact bytes and both headers; an alias → 200; another account
on the same host → 404; no `resource` → 400; `ACCT:steve@CRAWFORD.DEV`-style host case → 200;
a file without `subject` fails startup. Break-it-once: answer every resource; the 404 test must
fail.

## Item 4: profiles in the branding, as head links and `sameAs`

**`branding.json` gains:**
```json
"profiles": [
  { "service": "mastodon", "url": "https://example.social/@someone" },
  { "service": "bluesky",  "url": "https://bsky.app/profile/example.com" },
  { "service": "github",   "url": "https://github.com/someone" },
  { "service": "linkedin", "url": "https://www.linkedin.com/in/someone" }
],
"fediverseCreator": "@someone@example.social"
```
- `service` is one of `mastodon`, `bluesky`, `github`, `linkedin`; anything else fails startup
  (`BrandingException`). Order is the owner's and is kept everywhere.
- `url` must be an absolute `https` URL (reuse the `poweredByUrl` validation from 10f).
- `fediverseCreator` must look like `@user@host`; otherwise startup fails.
- Both are optional; without them nothing below renders.

**Render, on every page (static and interactive):**
- `<link rel="me" href="<url>">` for each profile (Mastodon verifies its profile links by this;
  it's correct for all of them: they're the same person).
- `<meta name="fediverse:creator" content="<fediverseCreator>">` when set.
- In the post page's BlogPosting JSON-LD, the author Person gets `"sameAs": [urls…]` in profile
  order; omit when there are no profiles.
- `branding.example/branding.json` gets example profiles; README's branding section documents
  the fields, including the owner-side steps: add the site URL to a Mastodon profile field (for
  the green tick) and allow the domain under Mastodon's author attribution.

**Tests:** bUnit/SeoHead: one `rel="me"` link per profile, in order, exact hrefs; the meta tag
present/absent; `sameAs` exact array, absent without profiles; `</script>` safety unchanged.
Unit: unknown service, `http://` URL and `someone@host` without the leading `@` each fail.
E2E: a reading page and an admin page both carry the links. Break-it-once: drop the `rel="me"`
loop; the link test must fail.

## Item 5: profile icons in the footer

**Do:**
- `SiteFooter` renders the profiles as icon links, as the first group, before the footer pages,
  with the existing separator between groups: `[icons] · Privacy Policy · Impressum · Running on
  Croft v0.6.1`.
- Each link: `<a href="<url>" rel="me noopener" aria-label="<Service name>" title="<Service
  name>">` holding an inline `<svg aria-hidden="true" focusable="false">` with
  `fill="currentColor"`, sized to the footer text (about 1.25em), the footer's link colour.
- Icons: the services' official marks as SVG paths, from sources whose licence allows
  redistribution in an MIT project (for example Simple Icons, CC0, where it has the mark). If a
  mark isn't available under such a licence (check LinkedIn especially), use a neutral link icon
  for it and say so in the report. Record each icon's source and licence in
  `THIRD-PARTY-NOTICES.md` (create it if missing). No runtime fetch from anywhere.
- No `style` attributes, no script; the footer stays static markup (10f). Keyboard focus shows the
  09i focus ring; each icon has at least 3:1 contrast against the footer background in light and
  dark themes (non-text contrast) and a target of at least 24×24 CSS px (WCAG 2.2 target size).
- Without profiles, the footer is exactly as today (assert the markup is unchanged).

**Tests:** bUnit: one link per profile, in order, with `aria-label`, `rel="me noopener"` and an
`aria-hidden` SVG; no `style` attribute anywhere in the footer; no profiles → identical markup to
before. E2E (Chromium and Firefox, light and dark): each icon's measured contrast ≥ 3:1 and box
≥ 24×24 px; at 320 px wide the footer wraps without horizontal scroll; Tab reaches each icon and
the focus ring is visible. Break-it-once: drop `aria-label`; its test must fail.

## Out of scope
- The CRL and `pki.crawford.dev` (11b).
- Any change to CSP, Traefik or the site repository (the orchestrator does the site side).
- ActivityPub or a dynamic WebFinger.
