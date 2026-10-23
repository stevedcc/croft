# Brief 07a: the site's identity comes from a branding folder, and auth settings are `Oidc`

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice in a row at the end (through `dev/run-e2e.sh`), then the final whole-branch review.

The design is `docs/superpowers/specs/2026-10-10-site-branding-design.md` (approved); read it
whole. This brief fixes its open values. Brief 07b runs at the same time on `k8s/` and
`README.md`; **you own** `src/`, `tests/` (except `tests/Blog.Web.Tests/K8sBaseTests.cs` and
`tests/Blog.Web.Tests/Blog.Web.Tests.csproj`, which are 07b's: if you need a package, stop and
report), `branding.example/`, `start-local.sh`, `dev/` and `AGENTS.md`. Never edit `k8s/` or
`README.md`.

**The contract with 07b:** branding folder `/app/branding` (the default of `Blog:BrandingPath`),
ConfigMap name `blog-branding`, configuration keys `Oidc:Authority`, `Oidc:Audience` (API),
`Oidc:ClientId`, `Oidc:ClientSecret` (Web), `Oidc:RoleGroups:Admin`, `Oidc:RoleGroups:Editor`
(both), `Blog:BaseUrl`, `ConnectionStrings:BlogDb`.

Today (evidence): `git grep -il crawford -- src tests dev` lists 16 files; the name and tagline
come from `BlogOptions` (`appsettings.json` has `"crawford.dev"`); colours are written three times
in `wwwroot/app.css` and once more in `Theme/BlogTheme.cs`; `wwwroot/icon.svg`, `favicon.ico`,
`apple-touch-icon.png`, `manifest.webmanifest` and `img/crawford-tartan.svg` are the site's files.

## Item 1: `BrandingService` and the defaults

- `Blog.Web/Branding/`: an immutable `Branding` record (Name, Tagline, Description, Author,
  `Palette Light`, `Palette Dark`, the set of image files the site supplied, a content hash per
  served file and one for the palette) and a singleton `BrandingService` built once at startup.
  It reads `Blog:BrandingPath` (default `/app/branding`), merges `branding.json` over the defaults
  field by field and token by token. `BlogOptions.Name` and `Tagline` are removed (`BaseUrl` stays).
- Tokens, exactly these 18: `bg`, `surface`, `text`, `muted`, `sidebar`, `sidebar-text`,
  `primary`, `primary-strong`, `primary-hover`, `primary-container`, `on-primary`, `secondary`,
  `secondary-container`, `on-secondary-container`, `on-secondary`, `code-bg`, `code-text`,
  `fallback-wash`. Valid colours: `#RGB`, `#RRGGBB`, `#RRGGBBAA`, `rgb(r,g,b)`, `rgba(r,g,b,a)`.
- Startup fails (exception naming the field, e.g. `branding.json: palette.dark.primary "red" is
  not a colour`) on malformed JSON, an invalid colour or an unknown token. A missing folder or file
  logs one Information line and uses the defaults. Contrast below 4.5:1 logs one Warning per pair,
  naming mode, pair and ratio, for: `text`/`bg`, `text`/`surface`, `muted`/`bg`,
  `primary`/`surface`, `on-primary`/`primary`, `on-secondary-container`/`secondary-container`,
  `sidebar-text`/`sidebar` (alpha colours: skip). Use the WCAG 2 relative-luminance formula.
- **Default values** (name "My Blog"; tagline, description, author empty). These were checked: every
  listed pair is ≥ 5.4:1 in both modes.

  | token | light | dark |
  |---|---|---|
  | bg | #F8FAFC | #0F172A |
  | surface | #FFFFFF | #1E293B |
  | text | #0F172A | #E2E8F0 |
  | muted | #475569 | #94A3B8 |
  | sidebar | #F1F5F9 | #131C2E |
  | sidebar-text | #1E293B | #CBD5E1 |
  | primary | #1D4ED8 | #93C5FD |
  | primary-strong | #1D4ED8 | #1E3A8A |
  | primary-hover | #1E40AF | #1E40AF |
  | primary-container | #DBEAFE | #1E2A4A |
  | on-primary | #FFFFFF | #0F172A |
  | secondary | #0F766E | #5EEAD4 |
  | secondary-container | #CCFBF1 | #134E4A |
  | on-secondary-container | #134E4A | #99F6E4 |
  | on-secondary | #FFFFFF | #0F172A |
  | code-bg | #1E293B | #0B1220 |
  | code-text | #E2E8F0 | #E2E8F0 |
  | fallback-wash | rgba(15,23,42,.38) | rgba(2,6,23,.45) |

- Tests (unit): merge per field and per token (a partial `branding.json` keeps the other
  defaults); each rejected input names its field; contrast maths against known pairs (black/white =
  21.00, `#777777`/white = 4.48); **the default palette passes every listed pair in both modes**.
  Break-it-once: set default light `muted` to `#94A3B8`; the defaults test must fail.

## Item 2: generated `theme.css`, the MudBlazor theme from the same palette, the manifest

- `GET /branding/theme.css`, generated once at startup: the `:root`, `:root[data-theme=dark]` and
  `@media (prefers-color-scheme: dark) { :root:not([data-theme=light]) }` blocks `app.css` holds
  today, with `--fallback-wash` instead of `--tartan-wash` (rename every use). `App.razor` links it
  **before** `app.css` with `?v=<palette hash>`; `app.css` keeps no colour values (fonts, spacing
  and the rest stay).
- `BlogTheme` is built from `Branding`: Background=`bg`, Surface=`surface`,
  BackgroundGray=`sidebar`, TextPrimary=`text`, TextSecondary=`muted`, Drawer/Appbar
  Background=`sidebar`, Drawer/Appbar Text and DrawerIcon=`sidebar-text`, Primary=`primary`,
  PrimaryContrastText=`on-primary`, PrimaryDarken=`primary-hover`,
  PrimaryLighten=`primary-container`, Secondary=`secondary`, SecondaryContrastText=`on-secondary`,
  SecondaryLighten=`secondary-container`, for each mode. (This is today's mapping; with the
  site's values it reproduces today's theme exactly.)
- `GET /manifest.webmanifest`, generated: `name` and `short_name` = Name, `theme_color` = light
  `primary`, `background_color` = light `bg`, `display: "standalone"`, icons from `/branding/`.
  Delete `wwwroot/manifest.webmanifest`.
- Tests: from a known palette, `theme.css` contains each token once per block with its value;
  `BlogTheme` maps each field as above (both modes); the manifest's fields. E2E: on the example
  site, the computed `--primary` on `:root` equals the example's value. Break-it-once: map
  PrimaryContrastText to `on-secondary`; a test must fail.

## Item 3: `/branding/{file}`, default images, `BannerOrFallback`

- `GET /branding/{file}` for exactly `logo.svg`, `favicon.ico`, `apple-touch-icon.png`,
  `fallback-banner.svg|png|jpg`; anything else 404 (including `../`, encoded or not). The site's
  file if present, else `wwwroot/branding-default/<file>`. Correct content type;
  `Cache-Control: public, max-age=31536000, immutable`; every link to it carries `?v=<hash>`.
  For the fallback banner, the first of svg, png, jpg the site supplies, else the default svg.
- `App.razor` (icon, favicon, apple-touch-icon, manifest) and `BrandBlock` (logo) use these URLs.
  Delete `wwwroot/icon.svg`, `favicon.ico`, `apple-touch-icon.png`, `img/crawford-tartan.svg`.
- Default `logo.svg` (use exactly this):
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="14" fill="#1D4ED8"/><rect x="16" y="18" width="32" height="5" rx="2.5" fill="#fff"/><rect x="16" y="30" width="32" height="5" rx="2.5" fill="#fff"/><rect x="16" y="42" width="20" height="5" rx="2.5" fill="#fff"/></svg>`
  `favicon.ico` (16, 32, 48) and `apple-touch-icon.png` (180×180) from it:
  `magick -background none logo.svg -define icon:auto-resize=16,32,48 favicon.ico` and
  `magick -background none -density 576 logo.svg -resize 180x180 apple-touch-icon.png`.
- Default `fallback-banner.svg` (use exactly this):
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice"><defs><pattern id="p" width="80" height="80" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="80" height="80" fill="#334155"/><rect width="40" height="80" fill="#3B4A63"/></pattern></defs><rect width="1600" height="900" fill="url(#p)"/></svg>`
- `BannerOrTartan` becomes `BannerOrFallback` (file, CSS, tests, comments, CSS classes: no
  "tartan" anywhere in `src/` or `tests/`), showing `/branding/fallback-banner.*` under
  `--fallback-wash` with the category icon as now.
- Tests: endpoint (site file served; default when absent; 404 for an unknown name and for
  `..%2Fappsettings.json`; headers); bUnit for `BannerOrFallback`. E2E: on the example site the
  logo request returns the example's bytes and a post without a banner shows the example's
  fallback banner (assert the loaded image's natural width and the URL). Break-it-once: always
  serve the default; the E2E logo test must fail.

## Item 4: every reader uses `Branding`; `branding.example/`; no "crawford" in code or tests

- Readers: `BlogPageTitle`, `BrandBlock`, `FeedAlternateLink`, `SeoHead` (og:site_name, JSON-LD
  publisher = Author, else Name), `Home` (description = Description, else Tagline), `Category`,
  `Tag`, `FeedService`, `SitemapService`, robots, wherever `BlogOptions.Name/Tagline` was read.
  Remove `Blog:Name`/`Blog:Tagline` from `appsettings.json`.
- `branding.example/` in the repo root: `branding.json` = name `"Field Notes"`, tagline
  `"An example site for the blog framework"`, description `"Notes from the field: an example of a
  site's own branding."`, author `"Example Author"`, palette overriding only `primary` and
  `primary-strong` (light `#9A3412`, dark `#FDBA74` / `#7C2D12`), `primary-hover` (light
  `#7C2D12`, dark `#9A3412`), `primary-container` (light `#FFEDD5`, dark `#431407`) and
  `on-primary` (light `#FFFFFF`, dark `#0F172A`) — everything else inherits the defaults (that is
  what the example proves). Its `logo.svg` is the default logo with `fill="#9A3412"`; its
  `fallback-banner.svg` is: `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice"><defs><pattern id="d" width="48" height="48" patternUnits="userSpaceOnUse"><rect width="48" height="48" fill="#7C2D12"/><circle cx="24" cy="24" r="6" fill="#9A3412"/></pattern></defs><rect width="1600" height="900" fill="url(#d)"/></svg>`
  (favicon and apple-touch icon generated from its logo as in item 3). A `README.md` beside it
  documents the folder format, every token, the colour syntax, the fallback order and the three
  ways to supply the folder (kustomize ConfigMap mounted at `/app/branding`,
  `-v ./branding:/app/branding:ro` with Docker/Podman, `Blog__BrandingPath` for a plain run).
- `start-local.sh` sets `Blog__BrandingPath` to the repo's `branding.example` (dev and E2E
  instances) unless `BLOG_BRANDING_PATH` is set; `BLOG_BRANDING_PATH=` (empty) means defaults.
- Tests that assert crawford.dev values move to the example's values or neutral fixtures
  (`https://example.com` for base URLs): the input changes because the design changed the rules,
  the assertions keep their meaning (rule 5; say so in the commit).
- Check: `git grep -il crawford -- src tests dev start-local.sh stop-local.sh` prints nothing.
- Screenshots: the normal screenshot tests run on the example. Run them once more on an instance
  started with `BLOG_BRANDING_PATH=` and keep both sets (report the folders).

## Item 5: `Authentik` → `Oidc`

- Both apps read `Oidc:*` (keys in the contract above); `RoleGroupSettings.SectionName` =
  `"Oidc:RoleGroups"`; error messages and comments say "the OIDC provider" (Authentik may appear
  once, in a comment, as an example). `start-local.sh` exports `Oidc__*`. Tests renamed with it.
- AGENTS.md, two edits only: "Production auth is Authentik (OIDC)" → "Auth is any OIDC provider";
  in "Local identities", the setting names become `Oidc:RoleGroups:Admin` /
  `Oidc:RoleGroups:Editor`, and drop "production sets "Blog Admins" / "Blog Editors"".
- Tests: a startup test that `Oidc:Audience` missing outside Development fails with the new
  message; role mapping reads `Oidc:RoleGroups`. Break-it-once: read the old section name; a role
  test must fail.
- Check: `git grep -n Authentik -- src tests dev start-local.sh` prints at most one comment line.

## Out of scope

`k8s/`, `README.md` (07b), the crawford.dev site repository and its branding (the orchestrator),
admin editing of branding, per-category colours, fonts, any visual change on a site that supplies
today's values.
