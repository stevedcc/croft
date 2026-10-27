# Brief 18d: an optional site.css in the branding folder (issue #16)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once where named, E2E twice
at the end. No pushes except your branch `brief/18d` to `origin` and ONE draft PR. Every suite goes
through `dev/with-suite-lock.sh`. Your E2E instances are **7..9**
(`dev/run-e2e.sh --instances 3 --first-instance 7`; single classes: `--instances 1 --first-instance 7`).

Other briefs running at the same time: 18a (tests, `dev/with-suite-lock.sh`; it may touch
`src/Croft.Web/Program.cs` away from the branding endpoints), 18b (workflows,
`Directory.Build.props`, `packages.lock.json`), 18c (a migration, AGENTS.md "Pending contracts").
Don't edit their files. Keep your `Program.cs` change next to the existing `/branding` endpoints.

## What the owner wants

Per-site styling without rebuilding the image: a site drops `site.css` into its branding folder
(the same folder as `branding.json` and the logo, `Blog__BrandingPath`), and every page, reading
and staff, loads it **after** the framework's stylesheets, so its rules win the cascade.
Without the file, nothing changes: no link, no request, `/branding/site.css` returns 404.

**CSP impact: none.** The stylesheet is same-origin and linked, not inline. `style-src` and
`style-src-elem` are `'self'` (`src/Croft.Web/Security/Csp/CspMiddleware.cs:312-313`), which
already allows it. No directive changes. A test proves the page's CSP header is byte-identical
with and without the file (item 2). `@import` of another origin or a `url()` to another origin
inside `site.css` stays blocked by the CSP, and the README says so.

## Item 1: load, hash and serve `site.css`

How branding works today: `BrandingService.Load` (`src/Croft.Web/Branding/BrandingService.cs:42`)
reads the folder once at startup. `ResolveImageFiles` (:340-394) records which files the site
supplied (`Branding.SuppliedFiles`) and a 16-hex-char SHA-256 cache-buster per served file
(`Branding.FileHashes`). `Branding.FileUrl(name)` builds `/branding/{name}?v={hash}`.
`ResolveFile` (:53-63) is the allow-list behind `GET /branding/{file}` (`src/Croft.Web/Program.cs:755-765`),
which serves the file with `Cache-Control: public, max-age=31536000, immutable` and a content type
from `BrandingContentType` (:767-774).

Do the same for `site.css`:
- If `site.css` exists in the branding folder, it's supplied: its served name is `site.css`, it
  gets a path and a hash exactly like the images, and there is **no framework default** (no
  folder, or no file, means not served).
- `ResolveFile("site.css")` returns its path when supplied, null otherwise (404).
- `BrandingContentType`: `.css` → `text/css; charset=utf-8`.
- The file is read as it is. No parsing or rewriting, and no size limit (the site owner controls
  the file, and it lives in a ConfigMap, which has its own 1 MiB limit).
- Keep the doc comments in step: `ResolveFile`'s summary, the comment above the endpoint ("The
  five branding images" becomes the images and the optional stylesheet), and `Branding.SuppliedFiles`'
  summary ("image file names" becomes "file names").

**Tests** (`tests/Croft.Web.Tests/BrandingServiceTests.cs` and `BrandingEndpointTests.cs`, same
fixtures as the image tests): with a temp branding folder holding `site.css`, `GET /branding/site.css`
returns 200, `text/css; charset=utf-8`, the immutable cache header and a body byte-identical to the
file, and `FileHashes["site.css"]` is the first 16 hex chars of its SHA-256 (computed in the test
from the bytes). Without the file, and without a folder: 404. Break-it-once: drop `site.css` from
`ResolveFile`'s switch, and the 200 test must fail.

## Item 2: link it last, on every page

In `src/Croft.Web/Components/App.razor` (:27-31), after the `Croft.Web.styles.css` link:

```razor
@if (Branding.SuppliedFiles.Contains("site.css"))
{
    <link rel="stylesheet" href="@(Branding.FileUrl("site.css"))" />
}
```

with a one-line Razor comment saying why it comes last (the site's rules win over the framework's).
This is the shared shell, so reading pages (static SSR) and interactive pages get it alike. No
MudBlazor or render-mode condition.

**Tests** (Web unit tests, rendering `/` and one staff page such as `/admin/pages` (signed in as Admin, the way the
existing staff-page tests do it) through the existing factory with a temp branding folder): (a) with the file, the document's `<head>` has exactly one
`link[rel=stylesheet]` whose `href` is `/branding/site.css?v=<hash>`, and it's the **last**
stylesheet link in the head; (b) without it, no `site.css` link at all; (c) the
`Content-Security-Policy` header of `/` is identical with and without the file. Break-it-once: move
the link above `app.css`, and (a) must fail on the order.

## Item 3: proven in a real browser (E2E)

`branding.example/` is the folder the E2E instances run with (`start-local.sh`, `Blog__BrandingPath`),
and the `DefaultBranding` pass runs with no folder (AGENTS.md). Add `branding.example/site.css`
containing a comment that explains the file (what it's for, that it loads last, that the CSP
allows same-origin only), and **one** rule the suite can measure without changing how the example
looks: `:root { --site-css: "branding.example"; }`. The comment says that rule is the marker the
E2E test reads, and that a real site can delete it.

New E2E test class (e.g. `tests/Croft.Web.E2E/SiteCssTests.cs`), on the home page and one post
page the test publishes itself:
- the response for `/branding/site.css?v=…` is 200 with `text/css` (wait on the actual `Response`
  of that request, not on the element);
- `getComputedStyle(document.documentElement).getPropertyValue('--site-css').trim()` is
  `"branding.example"` (with the quotes, as CSS returns it). That proves the browser applied the
  file, not merely that a link exists (rule 7);
- no CSP violation was reported while loading (use the suite's existing CSP-violation collection,
  see how `CspTests` or `CspPublicPagesTests` listen; don't write a new mechanism).
In the `DefaultBranding` category (runs with no branding folder, see how existing
`[Category("DefaultBranding")]` tests skip themselves elsewhere): no `site.css` link in the document,
and `GET /branding/site.css` is 404.

Break-it-once: remove the `<link>` from `App.razor`, and the computed-property assertion must fail.
Restore it.

## Item 4: documentation

- `branding.example/README.md`: a `site.css` section: optional, loads last on every page, cached
  forever with a content-hash `?v=` (a new file is a new URL after a restart), same-origin only under
  the CSP (no `@import` or `url()` to other origins; `img-src` is `'self'` only
  (`CspMiddleware.cs:315`), so `data:` images are blocked too, and the `?v=` of the branding images
  changes with their content, so `site.css` shouldn't reference them: say images aren't supported
  in `site.css` yet), and that
  the theme's CSS custom properties (`theme.css`) are the stable thing to override. List two or
  three of the most useful token names, taken from the generated `theme.css`.
- `README.md` "The branding folder" (:687-695): add `site.css` to the list of what the folder
  holds, and add the commented `# - branding/site.css` line to the `configMapGenerator` example
  (:716-724), next to the masthead line.

Evidence: the README renders (headings grep), and the token names you list exist in
`/branding/theme.css` on a running instance (curl it on your instance 7 and grep).

## Finally

All three suites once after the last change (E2E twice on instances 7..9, both runs' summary
lines), then `requesting-code-review`.

## Out of scope

- A `site.js` or any script: CSP-relevant, needs an owner decision.
- Hot reload of `site.css` without a restart (the branding folder is read once at startup, and a
  changed ConfigMap rolls the Web pods).
- #22 branding follow-ups.
