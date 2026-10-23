# Site branding out of the code: design

Date: 2026-10-10. Status: approved by the owner (written spec reviewed 2026-10-10).

## Purpose

The blog becomes a framework: nothing in this repository identifies crawford.dev. A site supplies
its identity (name, words, colours, images) and its deployment settings (hostnames, image tags,
IdP settings, secret names) from its own folder at deployment time, the way secrets come from
tswap now. The owner expects no other users; the point is the right shape. Success:

- `git grep -i crawford -- src tests dev k8s` finds nothing (docs and history may mention it);
- a deployment with no branding at all looks finished, with neutral defaults;
- crawford.dev, deployed from its own site folder, looks exactly as it does on 0.1.3.

## Decisions (owner, 2026-10-10)

1. The site's files live outside this repository, in their own private git repository
   (`~/sites/crawford.dev`). This repository carries only an example (`branding.example/`).
2. The app's contract is a folder, not Kubernetes: `Blog:BrandingPath` (default `/app/branding`).
   Kubernetes (kustomize overlay, ConfigMap mount), Docker/Podman (bind mount) and a plain
   `dotnet` run are three documented ways to supply it.
3. A site defines every colour token for light and for dark explicitly; any token it leaves out
   takes the default's value. No derived colours.
4. `k8s/` becomes a generic kustomize base; everything site-specific moves to the site's overlay.
5. Everything about how one site is deployed belongs to the site repository, not the framework:
   the database operator (CloudNativePG), the ingress controller (Traefik routes and middleware),
   TLS (cert-manager), storage classes (Longhorn, local-path), secret management (tswap) and the
   cutover and deploy steps. The framework's base needs only plain Kubernetes.
6. The app's identity-provider settings get a neutral name: the `Authentik` configuration section
   becomes `Oidc` (`Oidc:Authority`, `Oidc:RoleGroups:Admin`, ...), in both apps, with Authentik
   as one documented example. Part of brief 07a.
7. The agent development setup (sandbox, sync, model allocation, the orchestration process) leaves
   this repository for its own (a Claude Code plugin; separate spec). This spec only removes the
   site facts from AGENTS.md (see "AGENTS.md").

## The branding folder

```
branding.json          all fields optional
logo.svg               sidebar/top-bar logo and the SVG favicon
favicon.ico
apple-touch-icon.png   180×180
fallback-banner.svg    or fallback-banner.png / fallback-banner.jpg (first found, in that order)
```

`branding.json`:

```json
{
  "name": "crawford.dev",
  "tagline": "Code, travel and the odd loaf",
  "description": "",
  "author": "",
  "palette": {
    "light": { "bg": "#FAF7F4", "surface": "#FFFFFF", "...": "..." },
    "dark":  { "bg": "#171314", "...": "..." }
  }
}
```

The palette tokens are today's CSS custom properties, renamed where they named the site:
`bg`, `surface`, `text`, `muted`, `sidebar`, `sidebar-text`, `primary`, `primary-strong`,
`primary-hover`, `primary-container`, `on-primary`, `secondary`, `secondary-container`,
`on-secondary-container`, `on-secondary`, `code-bg`, `code-text`, `fallback-wash` (was
`tartan-wash`). `on-primary` and `on-secondary` are new: they carry the contrast-text colours the
MudBlazor palette has today (`PrimaryContrastText`, `SecondaryContrastText`). Colours are `#RGB`,
`#RRGGBB`, `#RRGGBBAA` or `rgb()`/`rgba()`; anything else is invalid.

`description` is the home page's meta description (today the tagline is used); empty falls back to
the tagline. `author` is the JSON-LD publisher name; empty falls back to `name`.

## The app (Blog.Web)

- **`BrandingService`** (singleton, built at startup): reads `Blog:BrandingPath`, merges
  `branding.json` over the built-in defaults field by field and each palette token one by one, and
  exposes an immutable `Branding` (name, tagline, description, author, light and dark palettes,
  which image files the site supplied, and a content hash per file and for the palette).
  `BlogOptions.Name` and `BlogOptions.Tagline` are removed; `BlogOptions.BaseUrl` stays.
  Every current reader of the name and tagline (`BlogPageTitle`, `BrandBlock`,
  `FeedAlternateLink`, `SeoHead`, `Home`, `Category`, `Tag`, the feed and sitemap services) reads
  `Branding` instead.
- **`GET /branding/{file}`** for the five file names above only (anything else: 404): the site's
  file if present, otherwise the framework default from `wwwroot/branding-default/`. Correct
  content type, `Cache-Control: public, max-age=31536000, immutable`, and links carry `?v=<hash>`.
  `App.razor` (icon, favicon, apple-touch-icon) and `BrandBlock` (logo) link here.
- **`GET /branding/theme.css`**, generated once at startup from the palette: the `:root`,
  `:root[data-theme=dark]` and `@media (prefers-color-scheme: dark) :root:not([data-theme=light])`
  blocks that `app.css` holds today. Linked in `App.razor` before `app.css` with `?v=<hash>`;
  `app.css` keeps everything except colour values.
- **MudBlazor theme**: `BlogTheme` is built from `Branding` (light and dark), so the colours are
  defined once.
- **`GET /manifest.webmanifest`**, generated: name and short name, `theme_color` = light
  `primary`, `background_color` = light `bg`, the icons from `/branding/`. The static file goes.
- **`BannerOrTartan` becomes `BannerOrFallback`**: without a banner it shows
  `/branding/fallback-banner.*` under the `fallback-wash`, with the category icon as now. CSS
  classes and comments lose the tartan wording.
- **Errors**: malformed `branding.json`, an invalid colour or an unknown palette token stops Web at
  startup with a message naming the field (a deployment mistake, like a missing `Blog:BaseUrl`). A
  missing folder or file means defaults, with one information log line. Below WCAG AA (4.5:1) for
  `text`/`bg`, `text`/`surface`, `muted`/`bg`, `primary`/`surface`, `on-primary`/`primary`,
  `on-secondary-container`/`secondary-container` and `sidebar-text`/`sidebar`, in either mode, a
  warning names the pair and its ratio; Web still starts.

## Defaults (built into the framework)

Name "My Blog"; tagline, description and author empty. A neutral slate-and-blue palette for light
and dark that passes AA on every pair above (a unit test enforces it). A simple generic logo mark,
with matching `favicon.ico` and apple-touch icon. A quiet geometric pattern as
`fallback-banner.svg`, in the default slate tones. Fonts stay in the framework.

## `branding.example/`

A complete example folder in the repo root that differs visibly from the defaults (its own name
and tagline, a clearly different primary colour, its own logo and fallback banner). It documents
the format (a README beside `branding.json`), local dev and the E2E instances use it
(`start-local.sh` sets `Blog__BrandingPath`), and it proves overriding works.

## Kubernetes: generic base, site overlay

**This repository's `k8s/`** is a kustomize base:

- `kustomization.yaml` listing today's resources; `kubectl kustomize k8s` must build (a test
  checks it);
- only plain Kubernetes: the two Deployments and Services, the app config maps, the media PVC
  with no `storageClassName` (the cluster default applies), and a plain `networking.k8s.io/v1`
  Ingress with placeholder host `blog.example.com` and no class or TLS settings;
- placeholders instead of site values: example config map values (`Oidc__Authority`,
  `Blog__BaseUrl`, role-group names) and images `blog-api` / `blog-web` for overlays to retag;
- the database comes from a Secret, default name `blog-db`, with keys `host`, `port`, `dbname`,
  `username` and `password` (the key names CloudNativePG's generated app secret uses); the API
  builds its connection string from them. An overlay points the Deployments at another Secret
  name with a patch;
- Web mounts ConfigMap `blog-branding` at `/app/branding`, `optional: true`, so the base deploys
  alone with the defaults;
- leaving for the site repository: `cnpg-cluster.yaml`, the Traefik `IngressRoute`s and
  `Middleware`, the cert-manager `Certificate`, the Longhorn storage class, `secrets.tswap.tmpl`;
  `secrets.example.md` stays, rewritten to list the Secrets the base expects.

**The site repository** (`~/sites/crawford.dev`, private, created by the orchestrator, not by
agents):

```
kustomization.yaml     resources: the framework base; images: registry tags; patches; generator
branding/              today's identity, palette, logo, icons and the tartan as fallback-banner.svg
patches/               config map values, the database Secret name (blog-db-app), Longhorn
                       storage class on the media PVC, deleting the base's plain Ingress
cnpg-cluster.yaml      the CloudNativePG cluster (moved from the framework)
traefik.yaml           IngressRoutes and Middleware (moved), crawford.dev hosts
certificate.yaml       the cert-manager Certificate (moved)
secrets.tswap.tmpl     moved from the framework, unchanged
README.md              this site's cutover and deploy steps (tswap, CNPG, Authentik logout URI)
```

`configMapGenerator` builds `blog-branding` from `branding/`; a changed file gives the ConfigMap a
new name, which rolls Web. The base is referenced by a git URL pinned to a framework release tag
(`…//k8s?ref=v0.1.N`) once the repository is fetchable; until then by a local path (one line).
Deploying: `kubectl apply -f <(tswap apply secrets.tswap.tmpl)` and `kubectl apply -k .` from the
site folder.

**README**: the "Kubernetes" and "Cutover" sections (tswap, CNPG, crawford.dev) move to the site
repository. In their place, "Deploying a site": the Secrets and settings the app expects, the
branding folder format, and the three ways to supply it (kustomize overlay,
`-v ./branding:/app/branding:ro` with Docker/Podman and a compose snippet, `Blog__BrandingPath` for
a plain run).

## AGENTS.md

Two site facts go: "Production auth is Authentik" becomes "any OIDC provider (local dev uses
navikt/mock-oauth2-server)", and the role-group paragraph names the `Oidc:RoleGroups` settings
without production's group names. The forbidden-tools line keeps its meaning in generic words
("no cluster, deployment or secret-manager tools"). Everything else about the agent setup is the
harness spec's business.

**Releases** get git tags (`v0.1.N`) matching the image tags.

## Testing

- Unit: merge (per field, per token), each rejected input, contrast maths, the default palette
  passing AA, `theme.css` and the manifest generated from a known palette.
- Endpoint: `/branding/{file}` serves the site file or the default, 404s other names, sends the
  cache headers; `theme.css` and the manifest are generated.
- E2E: the normal suite runs on `branding.example/`; one test checks that name, a computed token
  colour, the logo and the fallback banner come from it. Screenshot tests run with the defaults and
  with the example.
- Tests that assert crawford.dev values today move to the example or neutral fixtures (rule 5:
  inputs change because this design changes the rules; assertions keep their meaning).

## Work split

- **Brief 07a, branding in the app**: everything under "The app", the defaults, `branding.example/`,
  the `Authentik` to `Oidc` rename (both apps, tests, dev config, `start-local.sh`), the two
  AGENTS.md edits, and removing every crawford reference from `src/` and `tests/`.
- **Brief 07b, generic k8s base**: the base (including the `blog-db` Secret contract and the
  `Oidc__*` config map keys), the build test and the README section. Runs in
  parallel with 07a; the shared contract is the `/app/branding` path and the `blog-branding`
  ConfigMap name.
- **Orchestrator, after both**: create `~/sites/crawford.dev` from today's values; render the site
  locally with that branding mounted and compare screenshots with 0.1.3's (the acceptance gate:
  no visible change); tag the release; give the owner the migration (`kubectl diff -k` first: only
  the branding ConfigMap, the volume mount, image tags, the `Oidc__*` key names and the database
  env wiring may change; the moved CNPG, Traefik and cert-manager resources must show no change;
  resource names stay, so nothing is recreated).
- Order (owner, 2026-10-10): the running briefs (03h, 03k, 03l) finish; then the agent harness
  moves to its own plugin (separate spec); then 07a and 07b run under it.

## Out of scope

Editing branding in the admin pages, per-category colours, more than one site per deployment,
custom fonts, a theme marketplace, publishing the repository.
