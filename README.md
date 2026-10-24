# Croft

[![ci](https://github.com/stevedcc/croft/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/stevedcc/croft/actions/workflows/ci.yml)
[![codeql](https://github.com/stevedcc/croft/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/stevedcc/croft/actions/workflows/codeql.yml)

A small, self-hosted blog engine for .NET: Blazor + ASP.NET Core API + PostgreSQL, with each
site's branding supplied at deploy time. It runs [crawford.dev](https://crawford.dev) on four
Raspberry Pis.

Croft is an engine, not a framework: there are no plugins. A site is the engine plus a folder of
branding and a handful of settings, so the same images serve any number of sites.

## What it does

- **Reading pages without a circuit.** Home, posts, categories, tags, pages and search render as
  static server-side HTML with a little JavaScript: no WebSocket per reader, no MudBlazor on the
  reading side, no cookies for readers.
- **A strict Content Security Policy.** `default-src 'none'`, every directive explicit, scripts
  pinned by path and Subresource Integrity, no `'unsafe-inline'` for scripts, no nonces (so pages
  stay cacheable), Trusted Types reported. The policy per page type is snapshotted in the tests.
- **Writing and administration** as an interactive Blazor Server app: a Markdown editor with live
  preview, drafts that survive a lost session, scheduled publishing, categories, tags, media,
  pages placed in the sidebar or footer.
- **Sign-in through an OIDC provider** (Authentik in production, a mock provider locally): any
  provider that can send a `groups` (or `role`) claim, with the requested scopes configurable
  (`Oidc__Scopes`); tokens kept server-side, back-channel logout.
- **Built to scale out on small hardware:** public reads from a cache and the database's read
  replicas, invalidated over PostgreSQL LISTEN/NOTIFY; an output cache; ReadyToRun images; load
  shedding. On four Raspberry Pi 4 it serves about 600 page requests a second (800 compressed)
  without errors, limited by TLS on the Pis
  ([the measurement](docs/measurements/2026-10-23-load-test.md), with its caveats).
- **SEO and sharing:** canonical URLs, Open Graph, JSON-LD (`BlogPosting`, `BreadcrumbList`),
  sitemap, RSS.
- **Per-site branding at deploy time:** name, palette (light and dark), logo, icons, fallback
  banner and masthead come from a mounted folder; nothing in this repository identifies a site.

## How it was built

Croft was written by AI coding agents working from written briefs, with every change verified
in a sandbox before it was merged. The briefs, specs and plans are part of the repository:
[`docs/briefs/`](docs/briefs/) (one per piece of work, in order) and
[`docs/superpowers/`](docs/superpowers/). The harness that runs them is
[Chargehand](https://github.com/stevedcc/chargehand). [`docs/workarounds.md`](docs/workarounds.md)
lists every place the code works around a framework or browser behaviour, and why.

## Stack

- .NET 10, C# 14
- Blazor (static SSR for readers, Blazor Server for admin) + MudBlazor (admin only)
- ASP.NET Core Web API
- EF Core + Npgsql, PostgreSQL 18
- OIDC (any provider) / mock-oauth2-server for local development
- Container images for arm64 and amd64 (multi-arch Dockerfiles); Kubernetes manifests (`k8s/`, a generic kustomize base)

## Licence

[MIT](LICENSE).

## Local development

**Prerequisites:** the .NET SDK pinned in `global.json` (10.0.401 or a later 10.0.4xx; a 10.0.1xx
SDK refuses to build), Docker or Podman with compose, and `curl`.

1. Start PostgreSQL and the local OIDC provider:

   ```bash
   docker compose up -d
   ```

   This starts a [navikt/mock-oauth2-server](https://github.com/navikt/mock-oauth2-server)
   container at `http://localhost:5556/default` with a custom dev login page.
   Enter one of the built-in usernames to receive the matching `groups` claim:

   - `admin@example.com` → role **Admin**
   - `editor@example.com` → role **Editor**

   (Password is ignored by the mock server.)

2. Start the apps with the helper script:

   ```bash
   ./start-local.sh
   ```

   On first run it creates `.env` from `.env.example` (the dev-only connection string, OIDC
   client and keys), builds the solution, restores the local `dotnet-ef` tool, applies the EF Core
   migrations, starts the API (`http://localhost:5000`) and the Web app
   (`http://localhost:5002`) in the background with the example branding in `branding.example/`,
   and waits for their health checks. It stops with an error if the migrations cannot be applied.
   Open <http://localhost:5002/> and sign in at `/admin`. Stop the background services with:

   ```bash
   ./stop-local.sh
   ```

**Why the script and not `dotnet run`.** Neither app reads `.env`: the script reads it and
passes each app the settings it needs (connection string, OIDC client, the API's address, the
branding folder, uploads folder, keys). Running `dotnet run --project src/Croft.Api` by hand would
need all of those exported first, and the apps do not migrate the database when they serve. The
script is the supported local path.

**Migrations.** `./start-local.sh` applies them (`dotnet ef database update`, after
`dotnet tool restore`). To apply them without starting the apps:

```bash
dotnet tool restore
dotnet tool run dotnet-ef database update -p src/Croft.Api -s src/Croft.Api
```

In Kubernetes the `migrate` init containers apply them (see "Deploying a site").

### Isolated E2E stack

`./start-local.sh --e2e [--instance N]` starts an isolated stack instance for the E2E
suite: it drops and recreates the database `blog_e2e_N`, applies the migrations, gives the
API a fresh empty uploads folder under `/tmp`, and starts API and Web on that instance's
ports. `./stop-local.sh --e2e --instance N` stops one instance; `./stop-local.sh --all`
stops the dev stack and every E2E instance.

Instance parameters (database, uploads folder, ports, PID and log files) live in
`dev/instance-params.sh`. Instance 1 and the dev stack share ports 5000/5002 (API 5000 /
Web 5002), so they can't run at once. Instance N > 1 uses API `5000 + 10*(N-1)`, Web
`5002 + 10*(N-1)` (instance 2 is API 5010 / Web 5012). The suite reads `E2E_WEB_URL` /
`E2E_API_URL` and defaults to instance 1.

### Resetting and seeding the dev data

`dev/reset-dev-data.sh` resets the dev environment: it drops and recreates the dev
database (`blog`), applies the migrations and empties `./uploads`. It is destructive and
asks for confirmation (`--yes` skips the prompt); run it with the dev stack stopped, since
open database connections make the drop fail.

```bash
./stop-local.sh
dev/reset-dev-data.sh          # or: dev/reset-dev-data.sh --yes
```

The script loads no data itself. Afterwards `dev/seed-dev-data.sh` can optionally seed a
small, realistic sample for working on the site by hand — 3 categories with icons, 6
tags, 8 published posts (one with a banner image and code blocks, one with a table), 1
draft and 1 scheduled post. It creates everything through the API as the mock admin (the
same validation as real use) and is idempotent: running it twice does not duplicate. It
needs a running stack and seeds its API (`API_BASE_URL`, default `http://localhost:5000`):

```bash
./start-local.sh
dev/seed-dev-data.sh
```

The E2E suite needs no seed at all: `./start-local.sh --e2e` calls `dev/seed-e2e.sh`,
which stays empty because every test creates the data it needs.

### Running end-to-end tests

The E2E suite uses [Playwright](https://playwright.dev/dotnet/) and the local mock OAuth2 server
(`docker compose up -d` first). Install the browsers once (Chromium for the suite, Firefox for
`SidebarFooterAlignmentTests`):

```bash
dotnet build tests/Croft.Web.E2E
node tests/Croft.Web.E2E/bin/Debug/net10.0/.playwright/package/cli.js install chromium firefox
```

Then run the suite with the script that owns the whole cycle:

```bash
dev/run-e2e.sh
```

It starts isolated stack instances (their own databases, uploads folders and ports; three by
default, `--instances N`), runs the suite with one worker per instance and stops exactly the
instances it started, also on failure. It does not touch the dev stack's database. Running
the suite by hand against the dev stack is not supported: the dev stack has no Web database, and
the session tests need one. AGENTS.md, "Running things", has the details.

### Running the other tests

```bash
dotnet build Croft.slnx
dotnet test tests/Croft.Api.Tests --no-build
dotnet test tests/Croft.Web.Tests --no-build
```

Both suites need a **container runtime** (Docker or Podman; Testcontainers starts a PostgreSQL
container): every API test class, and 31 Web tests in the classes labelled `Category=Container`.
Without one, run only the Web tests that need none:

```bash
dotnet test tests/Croft.Web.Tests --no-build --filter "Category!=Container"
```

The API suite has no such subset. With the default filter it also leaves out the opt-in
`Category=Container` and `Category=ReadyToRun` tests (they build the images); AGENTS.md says when
to run those.

### Continuous integration

GitHub Actions runs the same suites on every pull request to `main`, every push to `main`, and by
hand (`workflow_dispatch`):

| Workflow | When | What |
|---|---|---|
| `ci` | pull requests, pushes to `main` | `build` (Release, `-warnaserror`), `api-tests`, `web-tests` (Testcontainers on the runner's Docker), `e2e` (`dev/run-e2e.sh --instances 2` against the compose stack, Chromium and Firefox) |
| `codeql` | pull requests, pushes to `main`, weekly | CodeQL for C# and for the workflows |
| `scorecard` | pushes to `main`, weekly | OpenSSF Scorecard, results published |
| `nightly` | 03:00 UTC | the `HaTests` E2E category; skipped for now with a notice, because it needs the `dev/ha-local.sh` stack (a rootless Podman socket), not the compose stack |

To run the same locally (AGENTS.md, "Running things", has the details):

```bash
docker compose up -d
dotnet tool restore
dotnet build Croft.slnx -c Release -warnaserror   # the build job
dotnet build Croft.slnx
dotnet test tests/Croft.Api.Tests --no-build
dotnet test tests/Croft.Web.Tests --no-build
dev/run-e2e.sh --instances 2
dev/run-ha-e2e.sh                                  # the HaTests category
```

## Security headers and Content Security Policy

Every response from both apps carries the same plain security headers, written first in the
pipeline so error paths keep them too:

| Header | Value |
|---|---|
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` — sent outside Development on every response whatever the scheme (TLS terminates at the ingress, so the app does not judge by the request it sees) |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` |
| `Cross-Origin-Opener-Policy` | `same-origin` |

The Web app completes the cross-origin isolation set that COOP starts with two more headers,
written by the same middleware (`src/Croft.Web/Security/SecurityHeadersMiddleware.cs`):

- **`Cross-Origin-Embedder-Policy: require-corp`** on every HTML document (the reading pages,
  admin and the editor, the error page), and on nothing else. "Document" is the
  rule the CSP uses (`CspDocument.KindOf`: a Razor component page). The documents load no
  cross-origin subresource (the CSP allows `'self'` only for images, fonts, styles and
  connections), so the header costs nothing and stops a future change from embedding a
  cross-origin resource that has not opted in.
- **`Cross-Origin-Resource-Policy`** on every response, per route class. `cross-origin` on what
  other sites and readers fetch from a browser: `/api/media/{id}`, `/feed.xml`, `/sitemap.xml`,
  `/robots.txt`, `/.well-known/webfinger` and the other site files (`security.txt` and the rest),
  `/manifest.webmanifest` and the `/branding/{file}` images. `same-origin` on everything else,
  which only the site's own documents load: the documents, the static assets (`_framework/`,
  CSS, scripts, fonts), `/social/avatar/*`, `/branding/theme.css` and `/health/*`. An endpoint
  opts into `cross-origin` with the `CrossOriginResource` endpoint metadata.

Media stays `cross-origin` because browser-based feed readers show a post's images straight from
`/api/media/{id}` on their own origin; `same-origin` would break every image in those readers.
Mastodon and Bluesky link cards copy the image to their own servers, so they would work either way.

Each app also sends exactly one `Content-Security-Policy` header of its own (Blazor's built-in
`frame-ancestors` header is disabled so the two never mix). The API serves JSON and media file
downloads, never HTML, so its policy is minimal: `default-src 'none'; frame-ancestors 'none'`.
The Web app's policy is composed per response from a base policy, the site's configured
additions and per-request contributions (`src/Croft.Web/Security/Csp/`). It denies by default
(`default-src 'none'`) and writes out every directive a page needs. The base, on every response:

```text
default-src 'none'; script-src 'none'; style-src 'self'; style-src-elem 'self';
style-src-attr 'none'; img-src 'self'; font-src 'self'; connect-src 'self';
manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self';
frame-ancestors 'none'; frame-src 'none'; worker-src 'none';
report-uri /csp-report; report-to csp
```

(`Reporting-Endpoints: csp="/csp-report"` accompanies it.) `base-uri` stays `'self'` because
Blazor resolves its URLs from the `<base href="/">` in `App.razor`; `base-uri 'none'` makes the
browser refuse that element.

The rest depends on the kind of document, decided from the endpoint when the response starts
(`CspDocumentKind`; the policy is fixed per document, so links that cross between the public and
the staff area load a new document). Scripts are pinned by place and by content, with no
`'self'`, no `'strict-dynamic'` and no nonces (the policy of a document kind is the same on every
request, so a cached response stays valid):

| Document | `script-src` |
|---|---|
| A static page (the reading pages: home, post, category, tag, categories, static page, search; the not-found and error pages) | `'sha256-<theme bootstrap>'` and the site's origin (`Blog:BaseUrl`) plus `/_framework/`, `/js/`, `/lib/` |
| An interactive document (admin and the editor; the login redirect is not a document) | the same, plus `/_content/MudBlazor/` and `/Components/Layout/` |
| The sign-out interstitial | exactly `/js/logout.js` |
| Anything else (redirects, files, feeds) | `'none'` |

Every `<script src>` also carries an `integrity` digest, so a file in an allowed directory must
still be the file the build produced (`Services/StaticAssetIntegrity`, W-090). MudBlazor's theme
style-block hashes go on `style-src-elem` of interactive documents only (a reading page has no
inline style), `form-action` is `'self'` only (no form of ours posts to the IdP: sign-in is a
redirect, sign-out a link chain), and `style-src-attr 'unsafe-inline'` is on the staff area only (`/admin`, `/editor`, W-007). The
static reading pages also send `Content-Security-Policy-Report-Only: require-trusted-types-for
'script'; trusted-types 'none'; report-uri /csp-report; report-to csp`: `blazor.web.js`'s
enhanced navigation violates it (W-091), so the violations are reported to the same endpoint and
nothing is enforced yet. A reading page also sets no cookie (W-092); outside Development every
cookie the site sets is `Secure`. `CspPolicySnapshotTests` pins the complete header of each kind
of document, so a change to the policy is a visible change to a snapshot; loosening anything is the
owner's decision.

**A site adds sources in configuration.** `Csp:Add:<directive>` is an array of sources that
merges into the policy beside the built-in ones — additions never remove anything:

```json
{
  "Csp": {
    "Add": {
      "img-src": ["https://cdn.example.com", "https://images.example.com"]
    }
  }
}
```

Environment variables use the same shape: `Csp__Add__img-src__0=https://cdn.example.com`.

**A component or endpoint adds sources for one request** through the `ICspFeature` hook — any
time before the response starts (from an `OnStarting` callback when the value is known late);
the header follows the policy until then, and the addition belongs to that request alone:

```csharp
context.Features.Get<ICspFeature>()!.Policy.Add("frame-src", "https://video.example.com");
```

For sources computed from the request itself (a path, the OIDC authority, …), register an
`ICspContributor` instead; it runs once per request before the response starts.

**Rolling out a site's additions.** Start with `Csp:ReportOnly=true`. The policy then goes out
as `Content-Security-Policy-Report-Only`: browsers block nothing and POST every violation to
`/csp-report` (size-bounded and rate-limited per pod: `Csp:ReportRateLimitPerMinute` reports per
client per minute, default 60, where a client is an IPv4 address or an IPv6 /64; at most
`Csp:ReportMaxClients` clients tracked at once, default 10 000, new ones refused until the minute's
sweep when the table is full; `Csp:ReportGlobalPerMinute` reports from all clients together, default
600; a refused report is answered 429 before its body is read; the endpoint logs at most 25 violations per report plus one summary
line counting the rest, with query strings and control characters stripped from every logged
field). Add the missing sources with `Csp:Add` or a contributor, watch the reports go quiet,
then drop the setting — the default (`Csp:ReportOnly=false`) is enforced. The reports do go
quiet, including on a dropped circuit: the reconnect overlay is the site's own markup styled by
its own stylesheet (`Components/Layout/ReconnectModal`), so Blazor never inserts an
inline style element and a circuit drop produces no violations.

## Deploying a site

Croft is an engine: nothing in this repository identifies a site. A deployment supplies its
identity (the branding folder), its settings (the configuration keys and Secrets below), a
PostgreSQL 18 database and an OIDC provider. `k8s/` is a generic kustomize base of plain
Kubernetes with placeholder values (`kubectl apply -k k8s` builds once the Secrets below
exist); a site overlays it with its hosts, image tags and values.

In order: [build and push the two images](#building-the-images), create the databases and the
[Secrets](#what-the-app-needs), supply the [branding](#the-branding-folder), then
`kubectl apply -k` your overlay.

### Releases

A release is a tag. There is no version-bump commit: the images are built as the tag's version
(the Dockerfiles take a `VERSION` build argument), so the footer of a `v0.7.0` image says
"Running on Croft v0.7.0". The whole manual release is

```bash
git tag -a v0.7.0 -m "Croft 0.7.0" && git push origin v0.7.0
```

on a commit of `main` whose `ci` passed. `<Version>` in `Directory.Build.props` is only what local
builds and tests carry.

Versions are three-part semver. **A feature release bumps the minor** (0.7.0); **a dependency or
fix release bumps the patch** (0.6.10, 0.6.11, …). Prereleases (`vX.Y.Z-rc.1`) are possible; a
Flux `ImagePolicy` semver range skips them, so they deploy nowhere unless pinned.

**Dependency merges release themselves.** When `ci` passes on a push to `main`,
`.github/workflows/auto-release.yml` looks at the merged commit. If Renovate wrote it (author or
committer `renovate[bot]`) and every file it changed is on the allow-list (`**/*.csproj`,
`Directory.Build.props`, `Directory.Packages.props`, `global.json`, `dotnet-tools.json`,
`src/*/Dockerfile`, `compose.yaml`, `.github/compose.ci.yaml`, `.github/workflows/*.yml`,
`renovate.json5`), it tags the commit as the next patch (the highest stable `vX.Y.Z` plus one)
and dispatches `release.yml` on that tag; from there Flux's image automation rolls the site out.
Any other merge waits for a hand-made tag. Merges are tagged one at a time, in order; a commit an
existing stable tag already contains is not tagged again (of several merges in quick succession,
one may be skipped, and its changes ship in the next tag). Running `auto-release.yml` by hand
(inputs `sha`, the full commit SHA, and `dry_run`, which must stay `true`) prints the decision
and the tag it would create, and tags nothing.

To hold a dependency release back: close (or don't merge) the Renovate PR; Renovate automerges
only patch, digest and pin updates (and dev-only compose image minors) once `ci` is green. To
stop new releases reaching the cluster whatever is tagged, suspend the site's Flux image update
automation (`flux suspend image update <name>`, `flux resume image update <name>` afterwards).

Each `v*` tag, pushed or dispatched, runs `.github/workflows/release.yml`, which publishes both
images to the GitHub Container Registry and creates a GitHub Release for the tag:

| Image | Tags |
|---|---|
| `ghcr.io/stevedcc/croft-api` | `X.Y.Z`, plus `X.Y` and `latest` unless the tag is a prerelease (`vX.Y.Z-rc.1` gets `X.Y.Z-rc.1` only) |
| `ghcr.io/stevedcc/croft-web` | the same |

Each tag is one OCI index with `linux/amd64` and `linux/arm64`, signed with cosign (keyless,
through the workflow's GitHub OIDC identity), with a SLSA build provenance attestation. Before
anything is released, each image is started on both architectures (arm64 under QEMU) and must
answer `/health/live` with the version it was built as (the `X-Croft-Version` header). The
Release notes list both digests. Verify an image before you deploy it:

```bash
cosign verify ghcr.io/stevedcc/croft-api:X.Y.Z \
  --certificate-identity-regexp '^https://github\.com/stevedcc/croft/\.github/workflows/release\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
gh attestation verify oci://ghcr.io/stevedcc/croft-api:X.Y.Z --repo stevedcc/croft
```

Running the workflow by hand on a branch (`workflow_dispatch`, input `test_tag`, default
`ci-test`) is a test build: it pushes only `ci-test-<short sha>` (built as version
`0.0.0-ci-test.<short sha>`), signs it, and creates no Release and no `X.Y` or `latest` tag. Run
on a `v*` tag, it is that tag's release; any other tag is refused. Pin a deployment to a version
or a digest, not to `latest`.

crawford.dev runs these images (`ghcr.io/stevedcc/croft-*`, since 0.6.9 on 2026-10-24): Flux image
automation (an `ImagePolicy` of `>=0.6.9`, stable tags only) deploys a new tag within about seven
minutes of its release.

### Building the images

Both Dockerfiles build from the repository **root** (the apps need `Croft.Shared` and
`global.json`). Build them for the architecture of your nodes, tag them for your registry and
push:

```bash
podman build -f src/Croft.Api/Dockerfile --platform linux/arm64 -t registry.example.com/croft-api:0.6.3 .
podman build -f src/Croft.Web/Dockerfile --platform linux/arm64 -t registry.example.com/croft-web:0.6.3 .
podman push registry.example.com/croft-api:0.6.3
podman push registry.example.com/croft-web:0.6.3
```

(`docker build` takes the same arguments.) Add `--build-arg VERSION=0.6.3` to stamp the version
the footer shows; without it the image carries the `<Version>` in `Directory.Build.props`. The
SDK stage runs on your build machine and cross-publishes for the target, so `--platform
linux/arm64` (Raspberry Pi and other Arm nodes) and `--platform linux/amd64` both build without
emulation; for a mixed cluster build both and publish a manifest list (`podman manifest`, or
`docker buildx build --platform linux/amd64,linux/arm64 --push`). The base's Deployments name the images `croft-api` and
`croft-web`; your overlay points them at your registry with `images:` (see
[Supplying the branding](#supplying-the-branding)) and the tag you pushed.

### What the app needs

**Two PostgreSQL 18 databases** on a server the cluster can reach (the base does not run one;
CloudNativePG or any managed Postgres will do):

- The **blog database** (`blog`) holds posts, pages, media metadata and page-view counts. The API
  builds its connection string from the `blog-db` Secret: keys `host`, `port`, `dbname`,
  `username`, `password` become
  `Host=$(DB_HOST);Port=$(DB_PORT);Database=$(DB_NAME);Username=$(DB_USER);Password=$(DB_PASSWORD)`.
  Those key names match the app secret PostgreSQL operators generate, so pointing the base at one
  takes only a patch.
- The **Web database** (conventionally `croft_web`) is a second, small database, separate because
  the two apps keep separate migration histories. It holds what several Web pods must share:
  signed-in sessions and Data Protection keys, which is why any Web pod can serve any user. The
  Web app reads its connection string from the `croft-web-db` Secret (key `connection-string`),
  both in the `web` container and in the `migrate` init container. Create the empty database
  yourself (for example `CREATE DATABASE croft_web OWNER <user>;`); the init container creates
  the tables. A site that runs one Web pod can leave the Web database out with the
  single-instance overlay in [`k8s/README.md`](k8s/README.md#running-a-single-instance), which
  needs no `croft-web-db` Secret.

**Who runs the migrations.** In Kubernetes the `migrate` init containers (`dotnet Croft.Api.dll
migrate`, `dotnet Croft.Web.dll migrate`) apply the schema before each app starts, serialised
under a PostgreSQL advisory lock so pods starting together migrate once. The serving apps never
migrate. Outside Kubernetes (plain Docker, `dotnet`) run the same command once before the apps
serve, with the connection string in the environment:

```bash
ConnectionStrings__BlogDb='Host=...;Database=blog;Username=...;Password=...' dotnet Croft.Api.dll migrate
ConnectionStrings__WebDb='Host=...;Database=croft_web;Username=...;Password=...' dotnet Croft.Web.dll migrate
```

(In an image: `podman run --rm -e ConnectionStrings__BlogDb=... croft-api migrate`.) Locally,
`./start-local.sh` does it for you; see [Local development](#local-development).

**Storage for uploads: ReadWriteMany.** The API stores uploaded media in the `blog-media-pvc`
claim (`k8s/pvc.yaml`, 5Gi), which the base asks for as `ReadWriteMany` because both API replicas
(the base runs two) read and write the same files at once; only the API mounts it. The cluster's default storage class
must offer RWX (NFS, CephFS, Longhorn RWX and similar). A cluster without one (k3s with
`local-path`, kind and minikube defaults) leaves the claim `Pending` and every API pod `Pending`
with it, with nothing in the app's own logs. Either use an RWX storage class (patch
`storageClassName` onto the claim in your overlay) or run the single-instance shape in
[`k8s/README.md`](k8s/README.md#running-a-single-instance): one replica of each app and
`ReadWriteOnce` volumes.

**An OIDC provider** for sign-in (Authentik is one example). Register a confidential client and
give it the three URIs the app uses:

| Provider setting | Value |
|---|---|
| Redirect URI | `https://<your-host>/signin-oidc` |
| Post-logout redirect URI | `https://<your-host>/signout-callback-oidc` |
| Back-channel logout URI | `https://<your-host>/backchannel-logout` |

By default the app requests the scopes `openid profile email groups offline_access`. Roles come
from the `groups` claim (or a `role` claim): a group name equal to `Oidc__RoleGroups__Admin` /
`Oidc__RoleGroups__Editor` (defaults `Admin` and `Editor`) grants that role, anything else grants
nothing (see the keys below). `groups` is not an OpenID Connect standard scope: Keycloak and
Authentik need a client scope or mapper that puts it in the token, and some providers (Microsoft
Entra ID answers `invalid_scope`) reject the scope outright. For such a provider set
`Oidc__Scopes__0`, `Oidc__Scopes__1`, ... to the list it accepts (the setting replaces the
default list; `openid` is always requested), configure the provider to send the user's groups or
roles in a claim named `groups` or `role` (Croft reads only those two claim names), and set
`Oidc__RoleGroups__*` to the group values the provider sends. If the provider supports OIDC Back-Channel
Logout (Authentik 2025.8+ does), set its **Logout URI** to
`https://<your-host>/backchannel-logout` and its **Logout Method** to **Back-channel**: the
provider then POSTs a signed `logout_token` whenever the session ends at the provider (the user
logs out, or an admin ends the session), and the blog's `/backchannel-logout` endpoint ends the
matching blog session(s).

**Configuration keys** (non-secret). `k8s/configmap.yaml` ships the `Oidc__*` and
`Blog__BaseUrl` keys with placeholders; `ConnectionStrings__BlogDb` is assembled in
`k8s/api-deployment.yaml` from the `blog-db` Secret keys:

| Key | App | Meaning |
|---|---|---|
| `Oidc__Authority` | both | The provider's issuer URL for the client, e.g. `https://idp.example.com/application/o/blog/` |
| `Oidc__Audience` | API | The client ID — the same value as Web's `Oidc__ClientId` (the API validates the token's `aud` claim against it, and refuses a token without a `sub` claim with 401: authorship and ownership go by `sub`) |
| `Oidc__ClientId` | Web | The client ID |
| `Oidc__Scopes__0`, `Oidc__Scopes__1`, ... | Web | The scopes requested at sign-in; replaces the default `openid profile email groups offline_access` (`openid` is always included); indices must run 0, 1, 2 ... without gaps or the Web app refuses to start. Set it for a provider that rejects `groups` |
| `Oidc__RoleGroups__Admin`, `Oidc__RoleGroups__Editor` | both | Which external group names grant the blog's Admin/Editor roles |
| `Blog__BaseUrl` | both | The public origin every absolute URL is built from (canonical, og:image, JSON-LD); the API puts it in the User-Agent of its requests to Mastodon and Bluesky (`Croft/<version> (+<origin>)`) |
| `Social__Enabled`, `Social__Mastodon__Server`, `Social__Mastodon__Account`, `Social__Bluesky__Did` | API | Social replies below posts: off by default; see [Social replies](#social-replies) |
| `Social__AnnouncementLanguage`, `Social__Bluesky__Service` | API | Announcing on publish: the language an announcement is sent in (default `en`, blank sends none) and the Bluesky service the API posts to (default `https://bsky.social`); see [Announcing on publish](#announcing-on-publish) |
| `Csp__ReportOnly`, `Csp__Add__*`, `Csp__ReportRateLimitPerMinute`, `Csp__ReportMaxClients`, `Csp__ReportGlobalPerMinute` | Web | The CSP rollout mode, the site's added sources and the report limits (per client, tracked clients, per pod) (see [Security headers and Content Security Policy](#security-headers-and-content-security-policy)) |
| `ConnectionStrings__BlogDb` | API | Built from the `blog-db` Secret keys (above) |
| `Croft__Web__MaxConcurrentRequests`, `Croft__Web__RequestQueueLimit` | Web | Page requests in progress (default 64) and waiting (64) across all visitors; beyond that a fast `503` with `Retry-After`. `Croft__Api__*` is the same pair for the API |
| `Croft__Web__MaxConcurrentRequestsPerClient`, `Croft__Web__MaxConcurrentMediaRequestsPerClient`, `Croft__Web__ClientQueueLimit` | both (`Croft__Api__...`) | Requests in progress per client address (Web default 16 for pages and 16 for media, counted separately; the API's defaults are 32 and 32 and count the visitor address Web forwards with the ingest key). Beyond its permits a client queues, oldest first, up to the queue limit (default 32 per partition), then is refused at once; one client never blocks another |
| `Croft__Web__Api__AttemptTimeoutSeconds`, `Croft__Web__Api__TotalTimeoutSeconds` | Web | How long one call to the API may take (default 5 s) and all attempts of one call together (default 10 s). A call that outlives them fails at once and the page answers the error page with `503`; for the image proxy the limits cover getting the response headers, not streaming the body |
| `Croft__Web__Api__RetryDelayMilliseconds` | Web | The first backoff before a retry (default 250 ms, exponential with jitter). Only `GET` and `HEAD` calls are retried, at most twice, on a connection error, an attempt timeout, `408`, `429` (its `Retry-After` honoured) and `5xx` except `501`; a write is never retried |
| `Croft__Web__Api__CircuitBreakerMinimumCalls`, `Croft__Web__Api__CircuitBreakerBreakSeconds` | Web | The circuit breaker opens when at least half of the calls in 30 s fail and there were at least this many calls (default 20); it then refuses calls at once for this long (default 15 s) before letting one through |
| `Croft__Api__Cache__PrimaryAfterInvalidation` | API | After an edit invalidates a cache group, the next refills of that group read the primary database, not a read replica that may not have replayed the write yet, for this long (a `TimeSpan`, default `00:01:00`). Only matters with `Database__ReadHost` set |
| `Croft__Web__MaxConcurrentMediaRequests` | both (`Croft__Api__...`) | Image requests in progress (default 32), a partition separate from the pages, so slow image downloads never starve pages |
| `Croft__Web__MinResponseDataRate`, `Croft__Web__MinRequestBodyDataRate`, `Croft__Web__MinDataRateGracePeriodSeconds` | both | Kestrel aborts a connection reading or sending slower than this many bytes per second (default 2048) after the grace period (default 10 s, must exceed 1 s) |
| `Croft__Web__MaxConcurrentConnections` | both | Open connections per pod (default 512). A pod-wide ceiling only: limit connections per client address at the ingress (Traefik), not here. Every request held by a slow reader, and every request waiting in a client's queue, keeps its socket open, so the connection ceiling and the per-client permits and queue limits must be sized together |

**Secrets** (never in this repository; `k8s/secrets.example.md` has the `kubectl create secret`
commands):

| Secret | Keys | Used by |
|---|---|---|
| `blog-db` | `host`, `port`, `dbname`, `username`, `password` | API (and its `migrate` init container) |
| `croft-web-db` | `connection-string` (an Npgsql connection string to the Web database) | Web (and its `migrate` init container); not needed by the single-instance overlay |
| `blog-web-secrets` | `Oidc__ClientSecret`, `Analytics__IngestKey` | Web |
| `blog-api-secrets` | `Analytics__HashKey`, `Analytics__IngestKey`, and optionally `Social__Mastodon__AccessToken`, `Social__Bluesky__AppPassword` | API |

Generate the analytics ingest key once and use the same value in both analytics secrets: the Web
app sends it with every page-view beacon and the API only counts beacons that carry it.

### Social replies

Readers can answer a post where they already are, on Mastodon or Bluesky, and those public replies
are collected by the API (the page that shows them comes in a later release). Croft runs no comment
system: no reader accounts, no logins, nothing to moderate from scratch. The platforms moderate
first; the owner can hide any reply or account.

**Linking a post.** A post carries two optional fields, `MastodonUrl` (`https://<server>/@<account>/<id>`)
and `BlueskyUrl` (`https://bsky.app/profile/<handle or DID>/post/<id>`), set through the post API.
Only the owner's own posts can carry a discussion: the Mastodon URL must be on `Social__Mastodon__Server`
and from `Social__Mastodon__Account`; the Bluesky URL's author must be `Social__Bluesky__Did` (a handle in
the URL is resolved once, when the post is saved, with `com.atproto.identity.resolveHandle`; if Bluesky does
not answer the save fails with a 503 instead of saving a link nobody checked). The fields save and validate
with social replies switched off, so posts can be prepared.
In an update, leaving a field out (or `null`) keeps that link, its thread and its stored replies; an empty or
whitespace-only string clears the link and deletes that platform's thread and replies; any other value is validated and set.

**What is fetched.** With `Social__Enabled` set, each API instance checks once a minute which linked,
published posts are due and fetches the replies from the owner's Mastodon server
(`/api/v1/statuses/<id>/context`) and from Bluesky's public service (`app.bsky.feed.getPostThread`): every
10 minutes for the first 2 days after publishing (or after the link was added), hourly until day 30, daily
after that. One request at a time per platform; `429` and Mastodon's rate-limit headers are honoured. Only
replies that are visible without an account are kept:

- Mastodon: `public` and `unlisted` statuses only; accounts that set `noindex` are left out; a content
  warning or the `sensitive` flag marks the reply (shown collapsed); `indexable: false` is a search opt-in,
  not a display opt-out, so those replies are shown.
- Bluesky: deleted and blocked posts are left out; authors who carry the label `!no-unauthenticated` ("do not
  show my account to logged-out users", which Bluesky asks third-party apps to respect); replies the owner hid
  in Bluesky (the thread gate); posts labelled `porn`, `sexual`, `nudity` or `graphic-media`.
- Everything on the owner's blocklist, and whatever hangs below a reply that is left out.

What is stored per reply is the minimum: the author's display name, handle, account link and a 48 x 48 avatar,
the time, a link to the original, the sanitised text (only paragraphs, line breaks and links), the content-warning
text and whether it had media. No counts and no other metadata. Each successful fetch replaces what was stored:
what was deleted at the source disappears here at the next fetch, its avatar with it. If a thread cannot be
fetched for 48 hours its replies are no longer shown (what was deleted meanwhile cannot be known).

**Hiding.** `POST /api/discussion/blocks` hides one reply or one account (all of its replies, on every post)
at once; `DELETE /api/discussion/blocks/<id>` brings them back with the next fetch. A block carries who made it,
when, a note, and the label the admin saw, so the list stays readable later; it survives refreshes and the post.

**Settings** (the API; `k8s/configmap.yaml`): `Social__Enabled` (`false`), `Social__Mastodon__Server`
(for example `mastodon.example`), `Social__Mastodon__Account` (without the `@`), `Social__Bluesky__Did`
(`did:plc:...`). Enabled with an empty server or DID leaves that platform out (logged once at start). No secrets:
reading public replies needs none.

**Network.** The API makes outbound HTTPS requests (port 443 only, to public addresses only: it resolves the
host itself and refuses loopback, private, link-local and similar addresses, follows no redirects, and uses no
proxy) to the configured Mastodon server, `public.api.bsky.app`, `bsky.social` (handle resolution) and the hosts
the avatars come from (the Mastodon server's media host and `cdn.bsky.app`). The base manifests set no egress
rules; a site that restricts egress has to allow those. Avatars are downloaded by the API, never by the
reader's browser (at most 1 MB, PNG, JPEG, WebP or GIF, re-encoded to a small WebP). Neither the logs nor the
traces carry a handle, a name or a reply text; the span of an avatar download exports its host only.

**Suggested text for the privacy page** (Croft supplies the text, the owner edits the page; this is not legal
advice):

> **Replies from Mastodon and Bluesky.** Under some posts this site shows replies that were written in
> response to the post's announcement on Mastodon or Bluesky. Only replies that are publicly visible without an
> account are shown, and not those of people who asked not to be shown to logged-out visitors. For each reply the
> site stores the display name, the user name, the profile picture (as a small copy), the time, the text and a link
> to the original, which it fetches from the platform. The legal basis is our legitimate interest in showing the
> discussion of our articles in one place (Art. 6(1)(f) GDPR). Replies are kept until they are deleted at the
> source (the copy here disappears with the next update, within a few minutes to a day) or hidden at your request.
> Nothing is stored about readers who only read. To have a reply or an account removed from this site, write to
> the contact address in the Impressum; replies you delete on Mastodon or Bluesky vanish here by themselves.

#### Announcing on publish

A post announces itself when it is published, on Mastodon and on Bluesky, once. Publishing (the Publish
button, or the scheduler at the scheduled time) queues an announcement per network in an outbox; the API posts
it within about a minute and then fills in the post's `MastodonUrl` and `BlueskyUrl` itself, so the replies
start showing without anyone pasting a link.

- **What is queued.** Only on the change to published (publishing a published post queues nothing), only for
  posts (pages are never announced), per network only when the post's flag for it is on (new posts: on), the
  post has no link for that network yet (a link entered by hand means it was announced elsewhere), and the
  network is set up. An unpublished post's pending announcements are cancelled. A posted announcement is never
  sent again; a failed one waits for **Retry** on the post's Discussion tab (Admin), which also has
  **Announce now** for a post that was published without announcing.
- **The text** is the post's title and, after a blank line, its subtitle, unless the editor's "Announcement text"
  says otherwise; the post's address (`Blog__BaseUrl` and `/post/<slug>`) is added at the end. The post editor shows one
  preview per network: the exact text that will go out, fitted to the limit, with its count as that network counts it. It is composed
  when it is sent, so an edit while pending is what goes out. Limits: Mastodon 500 and Bluesky 300 characters
  (grapheme clusters; Mastodon counts any address as 23, Bluesky as written). A default that is too long is
  shortened with "…" (see "Fitting a long default" below for the order); a custom text that is too long is refused on save. Hashtags
  (`#homelab`) are linked on both networks: Mastodon finds them itself, Croft marks them in the Bluesky post.
- **Hashtags of categories and tags.** Link-only posts from a small account get little reach, and hashtags are how
  people find posts on both networks, so on **Tags & categories** (Admin) each category and tag can carry up to 10
  hashtags of the owner's choosing (established ones, such as `#SelfHosted`, not ones made up from the name), typed in
  CamelCase because screen readers read it word by word and both networks match without regard to case. Each applies
  to both networks unless marked **Mastodon only** or **Bluesky only**; a name is letters, digits and underscores
  (1 to 64, at least one letter, not ending in an underscore), and one that differs from another only in letter case
  is a duplicate. The default text appends **at most 3** of them, after the subtitle and a blank line, separated by
  spaces: the category's first, then the tags' in the order the post lists its tags (by name), each network getting
  the ones that apply to it, without duplicates: `Title`, blank line, `Subtitle`, blank line, `#Homelab #SelfHosted
  #DotNet`, blank line, the address. A text the owner typed is sent as typed: no hashtags are added to it. The editor's
  two previews show exactly what each network will get, with the count.
- **Fitting a long default.** When the default is over a network's limit, it is shortened in this order until it fits:
  the subtitle is cut (ending in "…") and the hashtags stay; then the subtitle goes and all hashtags stay; then the
  hashtags are dropped from the end, one at a time; last, the title is cut. The hashtags go after the subtitle, never
  before it. Content exports carry the hashtags (`hashtags` in `categories.json` and `tags.json`, optional: an older
  archive imports with none).
- **Bluesky** gets a link card (title, subtitle, and the banner re-encoded as a JPEG of at most 950,000 bytes,
  long side at most 1200 pixels); **Mastodon** builds its own card.
- **Exactly once.** One outbox row per post and network; its id is Mastodon's `Idempotency-Key`. Every attempt after
  the first looks through the account's latest posts for the post's address and records what it finds
  instead of posting again (after a timeout, or a crash between the post and the database write). A failed attempt
  is retried after 1, 2, 4, 8, 16 and 32 minutes, then hourly (or at the network's rate-limit reset, if later), for
  6 hours; then the row is failed. Errors that cannot heal (HTTP 400, 401, 403, 404, 422, a custom text over the
  limit) fail at once. The check for an earlier Mastodon send needs `Social__Mastodon__Account`; without it
  only the `Idempotency-Key` protects a resend, and only within Mastodon's window for it.

**Settings** (the API): `Social__Enabled` must be `true` and the network's `Social__Mastodon__Server` /
`Social__Bluesky__Did` set (as for replies); `Blog__BaseUrl` must be set; plus the credential:

| Setting | Meaning |
|---|---|
| `Social__Mastodon__AccessToken` (secret) | An access token of the owner's account with the scope `write:statuses` only (Preferences, Development, New application) |
| `Social__Bluesky__AppPassword` (secret) | An app password **without** access to direct messages (Settings, Privacy and security, App passwords) |
| `Social__Bluesky__Service` | The Bluesky service the API signs in to and posts through (default `https://bsky.social`; the account's own PDS if it is not on Bluesky's) |
| `Social__AnnouncementLanguage` | The language tag sent with an announcement (Mastodon `language`, Bluesky `langs`); default `en`, blank sends none |

The two secrets go in `blog-api-secrets` (`k8s/secrets.example.md`), never in the ConfigMap. A network is ready
when social is enabled and its account setting, its credential and `Blog__BaseUrl` are all set; for a network that is not ready nothing
is queued or sent, and the Discussion tab says "Not set up on this site". The credentials never reach a log line, a stored error or an
API answer.

**Network.** In addition to the hosts of [Social replies](#social-replies) the API posts (HTTPS, port 443, public
addresses only, no redirects) to the configured Mastodon server, to `bsky.social` (or the host of
`Social__Bluesky__Service`) and reads `public.api.bsky.app` to check for an earlier send.

### The branding folder

The Web app reads its identity — name, tagline, palette, logo, icons, fallback banner, and optionally the home page masthead's own banner — from a
folder (`Blog__BrandingPath`, default `/app/branding`). Every file is optional: with no folder the
site runs on neutral built-in defaults. The format is documented in
[`branding.example/README.md`](branding.example/README.md), and `branding.example/` is a complete
working example. The owner's profile links (`profiles`) and fediverse handle
(`fediverseCreator`) live in `branding.json` too: they render as `rel="me"` head links on every
page, as the post pages' structured-data `sameAs`, and as footer icons — with the Mastodon-side
steps for the green verification tick and author attribution documented in the same place.

### Supplying the branding

**Kubernetes — a kustomize overlay.** The base mounts a `blog-branding` ConfigMap at
`/app/branding` (read-only, optional). Generate it from your branding folder in your site's
overlay:

```yaml
# your site folder's kustomization.yaml
resources:
  - ./croft/k8s            # this repository, cloned into your site folder
                           # (or its pinned git URL: .../k8s?ref=<a release tag>)
images:
  - name: croft-api
    newName: registry.example.com/croft-api
    newTag: 0.6.3
  - name: croft-web
    newName: registry.example.com/croft-web
    newTag: 0.6.3
configMapGenerator:
  - name: blog-branding
    files:
      - branding/branding.json
      - branding/logo.svg
      - branding/favicon.ico
      - branding/apple-touch-icon.png
      - branding/fallback-banner.svg
      # - branding/masthead-banner.png   # optional: the home page masthead's own picture
patches:
  - target:
      group: networking.k8s.io
      version: v1
      kind: Ingress
    patch: |-
      - op: replace
        path: /spec/rules/0/host
        value: blog.yoursite.example
```

Deploy with `kubectl apply -k .` from the site folder. A changed branding file gives the ConfigMap
a new name, which rolls the Web Deployment; the same `patches:` mechanism carries the site's
configuration values and anything else the cluster needs.

**Docker/Podman.** Bind-mount the folder read-only:

```bash
podman run --rm -p 5002:8080 \
  -v ./branding:/app/branding:ro \
  croft-web
```

(`docker run` works the same.) With compose:

```yaml
services:
  web:
    image: croft-web
    ports:
      - "5002:8080"
    volumes:
      - ./branding:/app/branding:ro
```

**A plain run.** Point `Blog__BrandingPath` at the folder:

```bash
Blog__BrandingPath=/path/to/branding dotnet run --project src/Croft.Web --urls "http://localhost:5002"
```
