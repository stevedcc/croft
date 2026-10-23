# Brief 07f: security headers and a strict, composable Content Security Policy

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review. Brief 07e (long posts) runs at the same
time and also edits `Croft.Web/Program.cs`. When the orchestrator tells you 07e has been accepted,
`git fetch origin` and `git merge --no-edit origin/blog-framework` into your branch (never rebase),
resolve any conflict keeping both changes, re-run the suites, and say so in the report. Pick E2E
instances that `ss -ltn` shows free: 07e uses some of the same range.

**Goal, owner-visible:** internet.nl and Mozilla Observatory rate crawford.dev's headers "good";
the public pages run under a CSP with **no `unsafe-inline` of any kind**; the admin and editor
pages allow inline `style` *attributes* only (MudBlazor needs them, see below) and nothing else
inline. Nothing on any page looks different (the screenshot gate stays pixel-identical).

**Facts this brief relies on (checked 2026-10-11, confirm what your items touch):**
- Croft.Web sends no CSP today; `Program.cs:172` `UseHsts()` gives `max-age=2592000`; Kestrel
  sends `Server: Kestrel`.
- Inline script: one, the theme/sidebar bootstrap at the top of `Components/App.razor`'s `<head>`.
  JSON-LD blocks (`SeoHead.razor`, `type="application/ld+json"`) are data, not executed.
  Every other script is same-origin (`blazor.web.js`, `MudBlazor.min.js`, `js/*.js`,
  `ReconnectModal.razor.js`, highlight.js via `post.js`). No inline event handlers.
- Inline `<style>` elements, all from MudBlazor 9.11.0 (`MudThemeProvider` in `MainLayout.razor`):
  the theme variables (`<style class='mud-theme-provider'>`, built by `BuildTheme()` /
  `GenerateTheme()` from the `MudTheme` and `IsDarkMode`, so deterministic per theme and mode),
  the scrollbar block (`BuildMudBlazorScrollbar()`, omitted when `DefaultScrollbar="true"`), and
  a `.mud-chart-serie:hover` block (find its source).
- MudBlazor has **no nonce support** and many components render `style="..."` attributes at
  runtime (MudBlazor issue #4529, open since 2022; no fix in 9.x). Croft's own markup has two
  style attributes: `BannerOrFallback.razor:12` (`background-image: url(<fallback banner>)`) and
  `DraftReviewPage.razor:46` (`top/left` of the "Add feedback" button).
- Markdown is rendered with `DisableHtml()`: post bodies carry no raw HTML.
- `Routes` is `InteractiveServer` everywhere with enhanced navigation. **A document's CSP is
  fixed when it loads**: enhanced navigation keeps the first page's policy.

## Item 1: the plain security headers (Web and API)

Web, every response: `Strict-Transport-Security: max-age=31536000; includeSubDomains` (outside
Development); `X-Content-Type-Options: nosniff`; `Referrer-Policy:
strict-origin-when-cross-origin`; `Permissions-Policy: camera=(), microphone=(), geolocation=()`;
`Cross-Origin-Opener-Policy: same-origin`; no `Server` header
(`AddServerHeader = false`). API, every response: the same headers, plus `Content-Security-Policy:
default-src 'none'; frame-ancestors 'none'` on its JSON
responses; check whether the API serves any HTML (`MapOpenApi` serves JSON; report anything else).

Tests (integration, `WebApplicationFactory`): each header with its exact value on a page, a static
file, a 404 and a redirect; `Server` absent. Break-it-once: drop `nosniff`; its test fails.

## Item 2: the policy model: a base policy plus contributors, merged per directive

`Croft.Web/Security/Csp/`:
- `CspPolicy`: directives → ordered set of sources; `Merge` unions per directive without
  duplicates; `ToString()` is the header value (directives in a fixed order, so it is stable for
  tests and caches).
- `ICspContributor` (DI, many): `void Contribute(HttpContext context, CspPolicy policy)`; runs
  per request, so a contributor may add only for some paths.
- Per-request additions: `context.Features.Get<ICspFeature>()!.Policy.Add("frame-src", "...")`
  from any component or endpoint before the response starts (the hook a post embedding a video
  would use; no such post exists yet, so this is tested, not used).
- Site additions from configuration: `Csp:Add:<directive>` = array of sources (the site repo's
  `config.yaml` can add, never remove).
- One middleware writes **one** header: `Content-Security-Policy`, or
  `Content-Security-Policy-Report-Only` when `Csp:ReportOnly` is true. Set
  `AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = null)` so Blazor
  does not add a second CSP header; `frame-ancestors 'none'` is in ours.
- Base policy:
  `default-src 'self'; script-src 'self'; style-src 'self'; style-src-elem 'self';
  style-src-attr 'none'; img-src 'self' data:; font-src 'self'; connect-src 'self';
  object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none';
  report-uri /csp-report; report-to csp` with a `Reporting-Endpoints: csp="/csp-report"` header.
  (Confirm `data:` images are needed, e.g. MudBlazor icons; drop it if nothing uses it.)
- Built-in contributors: OIDC adds the authority's origin to `form-action` (sign-out posts then
  redirect there; Chrome checks redirects against `form-action`); analytics adds nothing
  (the beacon is same-origin), say so in a comment.
- `POST /csp-report` accepts `application/csp-report` and `application/reports+json`, at most
  16 KB, rate-limited per client IP, logs one structured warning per violation (directive, blocked
  URI, document path; no query strings), returns 204.

Tests (unit): merge unions without duplicates and keeps order; config additions merge; a
per-request addition appears only on that response; report-only switches the header name; exactly
one CSP header on a page response; the report endpoint rejects 17 KB and logs a valid report.
Break-it-once: make `Merge` replace instead of union; the merge test fails.

## Item 3: the measurement, an E2E that fails on any violation

`CspTests` (E2E): an init script registers a `securitypolicyviolation` listener that records every
event (directive, blocked URI, sample); Playwright also records console CSP errors. Run against the
**enforced** policy (set `Csp:ReportOnly=false` for E2E). Pages: home, a post with banner, a post with
the fallback banner, category, tag, search (type a query), 404; logged in: admin posts,
tags, media, the editor (new and existing post, open the media picker dialog), the draft review
page (select text so "Add feedback" appears), the analytics chart page. Each in light and dark,
and toggle the theme switch once on home and on admin posts. Assert zero violations, and print
the full list on failure.

Commit this test **red**, with the list of violations it found in the commit message: that list is
the work for items 4–6. Break-it-once is its own red state.

## Item 4: the theme bootstrap script is allowed by its hash

Move the script's text to one place (e.g. a `ThemeBootstrapScript.Text` constant rendered with
`(MarkupString)`), so the markup and the hash share one source. A contributor adds
`'sha256-<base64>'` of exactly the rendered text to `script-src`, computed once at startup.
Test (unit): the hash equals SHA-256 of the script element's text in the rendered `App` markup.
E2E: no `script-src` violations. Break-it-once: add a space to the script text but not the hash
source (render a copy); the E2E fails.

## Item 5: MudBlazor's `<style>` elements allowed by hash, or moved to files

- Scrollbar: `DefaultScrollbar="true"` and the same rules copied into `app.css` (so the look is
  unchanged; screenshot gate).
- The chart block: the same treatment if it can be turned off; otherwise hash it.
- Theme variables: at startup, render `MudThemeProvider` with Croft's `MudTheme` for
  `IsDarkMode=false` and `true` with Blazor's `HtmlRenderer`, take each `<style>` element's text
  exactly as the browser will see it, and add both `'sha256-...'` to `style-src-elem`. Interactive
  theme switches replace the text with the other mode's; both hashes cover that (the E2E toggles).
  Document in code that a `MudTheme` change at runtime would need new hashes (Croft builds it
  once from branding at startup).
Tests: unit, the hashes match a fresh render for each mode; E2E, no `style-src-elem` violations
in both modes and after toggling. Break-it-once: drop the dark hash; the dark E2E fails.

## Item 6: no style attributes on the public pages; the admin area allows them

- `BannerOrFallback`: the fallback banner URL becomes a token in the generated `theme.css`
  (`--fallback-banner: url(...)`) and the class uses it; no `style` attribute.
- `DraftReviewPage`'s "Add feedback" button: position it through the CSSOM from JS (like
  `editor.js:94` does), no `style` attribute.
- After these, the public pages must pass item 3's E2E with `style-src-attr 'none'`. If a
  MudBlazor component on a public page still writes a style attribute, report which and stop
  for a ruling (don't widen the public policy).
- An `AdminAreaCspContributor` adds `style-src-attr 'unsafe-inline'` only for the pages under
  `/admin` and `/editor` (list the exact routes from the route table in the commit message).
- Because the policy is fixed per document, every link and navigation **between** the public area
  and the admin area is a full page load (`data-enhance-nav="false"` on the links, and
  `NavigationManager.NavigateTo(..., forceLoad: true)` in code). E2E: from home, follow the admin
  link; the admin page's `document` was reloaded (a marker set on `window` before the click is
  gone) and its response carried the admin policy; back to a post via its link, the same.
Break-it-once: remove the contributor; the admin E2E fails. Put `style="..."` back on the fallback
banner; the public E2E fails.

## Item 7: enforce, and check nothing moved

`Csp:ReportOnly` defaults to `false`. `README` (framework): the headers, the policy, how a site
adds sources (`Csp:Add`), how a component adds per request, and how to roll out with
`Csp:ReportOnly=true` first. Run the full E2E twice and the screenshot comparison against
`blog-framework` before this brief (public and admin, both themes): pixel-identical; attach the
diff counts to the completion report.

## Out of scope

Removing MudBlazor, Trusted Types, SRI on same-origin scripts (no third-party scripts exist; if one
is ever added it gets SRI), the site repo's settings (the orchestrator adds them), .NET 11's
`CSP-compliant `Virtualize`.
