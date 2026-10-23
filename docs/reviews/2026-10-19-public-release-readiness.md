# Public-release readiness review (2026-10-19)

Scope: the MIT release of `stevedcc/croft` at commit `0e9e697` ("Croft 0.6.2"). Checked: licences of
everything vendored or bundled, personal or site-specific data, secret-looking strings, the README's
"What it does" claims against the code, and a stranger's walk through "Local development",
"Deploying a site" and `k8s/README.md`. Nothing but this file was changed.

Environment used: a fresh container with no .NET SDK and no container runtime. Installed SDK 10.0.402
(satisfies `global.json`'s 10.0.401 `latestFeature`). `dotnet build Croft.slnx`: **0 warnings, 0 errors**.
`dotnet test tests/Croft.Web.Tests --no-build`: **Passed 1057, Failed 31, Total 1088**; all 31 failures
are `Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'` from the Testcontainers
fixture (finding I-5). `tests/Croft.Api.Tests` (Testcontainers), `docker compose up -d`,
`./start-local.sh` and the E2E suite could not run here: everything about them below is from reading
the scripts and manifests and is marked **unverified** where it depends on running them.

Severity: **Critical** = must not go public like this; **Important** = a stranger gets stuck, a
licence obligation is unmet, or a README statement is wrong; **Minor** = polish or owner's call.

No Critical findings. No secret, real or otherwise, was found anywhere in the tree (see "Checked and
found sound").

---

## Important

### I-1. README omits the `croft-web-db` Secret the k8s base requires

- **Where:** `README.md:312-319` (the Secrets table lists `blog-db`, `blog-web-secrets`,
  `blog-api-secrets` only) and `README.md:269` ("`kubectl apply -k k8s` builds once the Secrets below
  exist"); versus `k8s/web-deployment.yaml:91-95` and `:121-125`:
  ```yaml
  - name: ConnectionStrings__WebDb
    valueFrom:
      secretKeyRef:
        name: croft-web-db
        key: connection-string
  ```
  and `k8s/secrets.example.md:30-41`, which documents it correctly ("The base requires this Secret").
- **What is wrong:** the root README's own list of what a deployment needs is incomplete. The Web
  `migrate` init container and the `web` container both read a Secret the README never names.
- **Why it matters:** a stranger following only the README creates three Secrets, applies the base,
  and both Web pods sit in `CreateContainerConfigError` ("secret "croft-web-db" not found"). The
  README also never says a *second* PostgreSQL database (`croft_web`) is needed, so even a reader who
  finds the Secret file has to go and create one.
- **Fix:** add `croft-web-db` (key `connection-string`, used by Web and its init container) to the
  README table, and a sentence that the base needs a second, small database, pointing at
  `k8s/README.md` "The Web database" and the single-instance overlay that removes the need.

### I-2. The README's manual "run it yourself" path cannot work: nothing reads `.env` except `start-local.sh`

- **Where:** `README.md:81-85`:
  ```bash
  dotnet run --project src/Croft.Api --urls "http://localhost:5000"
  dotnet run --project src/Croft.Web --urls "http://localhost:5002"
  ```
  and `README.md:99`: "Configure secrets via user-secrets or a `.env` file (see `.env.example`)."
- **Evidence:** `grep -rn "\.env\|DotNetEnv\|dotenv" src --include=*.cs --include=*.csproj` finds only a
  comment (`src/Croft.Api/Program.cs:194`). The `.env` file is parsed solely by `start-local.sh:45-52`
  and exported into that shell. Neither `Program.cs` adds a `.env` configuration source.
- **What happens:** `dotnet run --project src/Croft.Api` starts with no `ConnectionStrings:BlogDb`
  and no `Oidc:Authority`; the first request fails. `dotnet run --project src/Croft.Web` has no
  `ApiBaseUrl`, and `src/Croft.Web/Program.cs:195` does
  `client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"]!);`, which throws
  `ArgumentNullException` when the named client is first built (unverified by running; the code path
  is unconditional). No `Blog__BrandingPath` is set either, so the manual run also looks different
  from the scripted one (`start-local.sh:195` points at `branding.example`).
- **Why it matters:** the README offers the manual commands first and the script second. A .NET
  developer who prefers `dotnet run` gets an immediate failure with no hint that `.env` is a
  script-only convention.
- **Fix:** either say plainly that `.env` is read only by `./start-local.sh` and list the variables a
  manual run must export (or `dotnet user-secrets set` for each), or add a `.env` configuration
  source to both apps for Development only.

### I-3. Migrations: the README says they run automatically; the script skips them silently and the serving API never migrates

- **Where:** `README.md:101-102`: "`./start-local.sh` will apply EF Core migrations automatically
  using the local `dotnet-ef` tool." `README.md:274`: "The API runs the EF Core migrations at
  startup (in `k8s/` as the `migrate` init container, before the API serves)."
- **Evidence:** `start-local.sh:135-138`:
  ```bash
  if command -v dotnet-ef >/dev/null 2>&1 || dotnet ef --version >/dev/null 2>&1; then
      echo "Applying database migrations ..."
      dotnet ef database update -p src/Croft.Api -s src/Croft.Api --no-build
  fi
  ```
  No `else`, no message. `dotnet ef` only exists after `dotnet tool restore`
  (`dotnet-tools.json`), which the README mentions only under "To run migrations manually"
  (`README.md:105`). `src/Croft.Api/Program.cs:150-164` runs `MigrationRunner` only when the command
  line is `migrate`; the normal `app.Run()` path (`:237`) never calls `Migrate`.
- **Why it matters:** a stranger who clones, runs `docker compose up -d` and `./start-local.sh`
  without `dotnet tool restore` gets "Local dev stack is ready" against an empty database; every
  page is a 500 (`relation "Posts" does not exist`). The README's deployment sentence at `:274` is
  also wrong as written: outside the k8s init container nothing migrates at startup, and the
  README's own Docker/Podman and "plain run" examples (`:377-401`) never mention running
  `dotnet Croft.Api.dll migrate` (or `dotnet ef`) first. (Unverified by running: no Postgres here.)
- **Fix:** make `dotnet tool restore` step 0 of "Local development" (or have the script fail loudly
  when `dotnet ef` is missing instead of skipping), and reword `:274` to "the `migrate` command
  applies the migrations; `k8s/` runs it as an init container; a Docker/plain run must run it once
  before the API serves", with the command shown.

### I-4. The owner's real fediverse handle / e-mail is hard-coded in `src/` and `tests/`

- **Where:** `src/Croft.Web/SiteFiles/WebFingerDocument.cs:14` (`acct:steve@crawford.dev`),
  `:27-28` (`ACCT:steve@CRAWFORD.DEV` / `acct:steve@crawford.dev`);
  `tests/Croft.Web.Tests/WebFingerDocumentTests.cs` (19 occurrences, e.g. `:19`
  `"""{"subject":"acct:steve@crawford.dev"}"""`, `:31` `acct:steve@users.crawford.dev`);
  `tests/Croft.Web.Tests/WebFingerEndpointTests.cs` (15 occurrences, e.g. `:22-23`, `:55`, `:306`).
- **Why it matters:** AGENTS.md rule 3 says "Never hard-code users, emails, roles or credentials in
  `src/`", and the project's own release invariant (`docs/briefs/07a-branding-in-the-app.md:139`:
  "`git grep -il crawford -- src tests dev start-local.sh stop-local.sh` prints nothing";
  `docs/briefs/07c-croft-rename.md:57`) is now broken by three files. The handle is public on
  Mastodon, so this is not a leak, but it is a personal identifier baked into an engine that claims
  "nothing in this repository identifies a site" (`README.md:29`, `:266`). `docs/briefs/11a-site-files-and-profiles.md:10`
  states the same handle as the owner's "real account".
- **Fix:** replace with `example.com` fixtures (the sibling `site-files.example/.well-known/webfinger`
  already uses `acct:example-owner@example.com`), which keeps the case-sensitivity tests identical in
  meaning. Say so in the commit message (rule 5: input changed, assertions unchanged).

### I-5. `Croft.Web.Tests` is called a unit suite but 31 tests need a container runtime, and nothing tells a stranger

- **Where:** `AGENTS.md:130`: "`dotnet test tests/Croft.Web.Tests --no-build   # unit tests (~2s)`";
  `README.md` never mentions a container runtime for any test. The fixture is
  `tests/Croft.Web.Tests/Fixtures/SharedPostgresContainer.cs:1-14` ("The one Postgres Testcontainer
  shared by every test class in the assembly"), reached through `PostgresDatabaseFixture.cs`.
- **Evidence (run here):** `Failed: 31, Passed: 1057` with no Docker socket; the 31 are the
  `BackgroundJobLockTests`, `ConcurrentWebMigrationTests`, `DataProtectionKeyImportTests`,
  `HealthEndpointTests`, `SessionLifetimeTests`, `SessionPurgeTests`, `SessionRemovalTests`,
  `SharedDataProtectionKeysTests`, `SharedSessionRefreshTests` and `SharedSessionStoreTests`
  classes, every one failing with `Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'`.
  (The API suite's dependence on Testcontainers *is* documented, `AGENTS.md:129`.)
- **Why it matters:** a contributor on a laptop without Docker/Podman runs the "2-second unit suite"
  and sees a red run with Docker stack traces, with no README or AGENTS.md line to explain it; the
  natural conclusion is that the repo is broken.
- **Fix:** document the requirement beside the command (README "Running tests" section, which does
  not exist yet, and `AGENTS.md:130`), or put the database-backed classes behind a trait with a
  default filter like the API project's `Category=Container` mechanism
  (`tests/Croft.Api.Tests/Croft.Api.Tests.csproj:17`).

### I-6. `THIRD-PARTY-NOTICES.md` covers four icons and nothing else; one Apache-2.0 icon has no attribution anywhere

- **Where:** `THIRD-PARTY-NOTICES.md:3-16` lists only the `ProfileIcon.razor` paths (Simple Icons CC0,
  MDI `link` Apache-2.0). Not listed: highlight.js 11.11.1 (BSD-3-Clause,
  `src/Croft.Web/wwwroot/lib/highlightjs/LICENSE` present, header kept in `core.min.js:1-5`), Source
  Sans 3 / Source Serif 4 / JetBrains Mono (OFL-1.1, the three `OFL-*.txt` files present), the
  vendored Microsoft skills (MIT, `.claude/skills/MICROSOFT-SKILLS-LICENSE` present), and the
  Material Design `search` glyph inlined at `src/Croft.Web/Components/Pages/Search.razor:33-34`:
  ```html
  <path d="M0 0h24v24H0z" fill="none" />
  <path d="M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 ..." />
  ```
  That is Google's Material Icons `search` path (Apache License 2.0). No file in the repository
  attributes it.
- **Why it matters:** every licence here is MIT-compatible, so redistribution is fine, but
  Apache-2.0 §4(d) requires the attribution notice to travel with the work and BSD-3 §2 requires the
  copyright notice to be reproduced "in the documentation and/or other materials" of a binary
  distribution (the container images ship `core.min.js` with its header, which satisfies it; a
  notices file is the conventional place to point to). A reader of `THIRD-PARTY-NOTICES.md` is told,
  by omission, that nothing else is third-party.
- **Fix:** extend the notices file with a row each for highlight.js (BSD-3, path to its LICENSE), the
  three font families (OFL-1.1, path to each OFL file, "unmodified"), Material Icons `search`
  (Apache-2.0, link to the licence) and the Microsoft skills (MIT, `MICROSOFT-SKILLS.md`). Ten lines.

### I-7. Deploying from the README alone stalls twice before the first pod runs: no image build step, and the media PVC needs ReadWriteMany

- **Where:**
  - Images: `README.md:343-373` tells the overlay to retag `croft-api`/`croft-web` to
    `registry.example.com/...:0.1.0`, but neither `README.md` nor `k8s/README.md` has a build or push
    command; the only `docker build` instruction in the repository is in a historical brief
    (`docs/briefs/03-deployment.md:109`). Both Dockerfiles say "Build from the repo ROOT"
    (`src/Croft.Api/Dockerfile:2`, `src/Croft.Web/Dockerfile:2`) and need `--platform`/`TARGETARCH`;
    a stranger has to discover `docker build -f src/Croft.Api/Dockerfile .` by reading comments.
  - Storage: `k8s/pvc.yaml:7-10`:
    ```yaml
    accessModes:
      # ReadWriteMany: both API pods mount the uploads at once. The cluster's default
      # storage class must support RWX; a site overlay may patch the class.
      - ReadWriteMany
    ```
    The README's "What the app needs" (`:272-323`) lists a database and an OIDC provider, not an
    RWX-capable storage class.
- **Why it matters:** on the most common small clusters (k3s with local-path, kind, minikube
  default) the PVC stays `Pending`, so both API pods stay `Pending` for ever with no error in the
  app's own logs. The `k8s/README.md` single-instance overlay fixes it (`:152-228`), but the root
  README does not say when a reader needs it.
- **Fix:** a "Build the images" subsection with the two `docker build`/`podman build` lines (and
  `--platform linux/arm64` for the Pi case), and one sentence under "What the app needs": "an RWX
  storage class for the media volume, or the single-instance overlay in `k8s/README.md`".

---

## Minor

### M-1. "about 160 requests a second" has no evidence in the repository

- **Where:** `README.md:24-25`. Every measurement in the tree says less: `k8s/README.md:88-91`
  ("moved the limit to ~115–120 requests/s"), `docs/briefs/09d-public-reads-scale-out.md:14-19`
  (~55 → ~115–120), `docs/briefs/09b-fewer-queries.md:20-22` (~50). `grep -rn "160"` over `docs/`,
  `k8s/` and `README.md` finds the figure only in the README.
- **Why it matters:** it is the README's single quantitative claim; a reader who follows the links
  to the briefs finds a lower number and discounts the rest.
- **Fix:** cite the run (date, pods, replicas, tool) in a brief or `k8s/README.md`, or quote the
  documented 115–120.

### M-2. "Sign-in through any OIDC provider" — the scope list is hard-coded, including the non-standard `groups`

- **Where:** `README.md:21`; `src/Croft.Web/Program.cs:121-125`:
  ```csharp
  options.Scope.Add("openid"); options.Scope.Add("profile"); options.Scope.Add("email");
  options.Scope.Add("groups"); options.Scope.Add("offline_access");
  ```
  No `Oidc:Scopes` setting exists (`grep -rn Scope src/Croft.Web/Options` finds none).
- **Why it matters:** some providers reject unknown scopes outright (Microsoft Entra ID answers
  `invalid_scope` for `groups`), so sign-in fails before any Croft code runs. `groups` is also not
  an OIDC standard scope; Keycloak and Authentik need it configured. "Any provider that can issue a
  `groups` or `role` claim and accepts a `groups` scope" is the true statement.
- **Fix:** make the scope list configurable with the current list as default, or narrow the claim.

### M-3. `docs/runbooks/smoke-test.md` is stale and would mislead a stranger

- **Where:** `docs/runbooks/smoke-test.md:6` (`dotnet test` — fails in a fresh clone without a build
  and a container runtime, see I-5), `:14-16` (`src/Blog.Api`, `src/Blog.Web`: the pre-rename
  project names), `:17` ("Log in via Authentik": the mock IdP is the local provider).
- **Fix:** rewrite against the current names and `./start-local.sh`, or delete the file and point
  at AGENTS.md "Running things".

### M-4. The owner's infrastructure map is spread across the briefs and specs

Nothing secret, and the domain is already named in `README.md:4`, but the following details are
reconnaissance a public repository need not hand out (owner's call):

- `registry.crawford.dev` (private registry): `docs/briefs/03-deployment.md:9`, `07b:18`.
- `authentik.crawford.dev` (the IdP host, with a live CSP header dump): `docs/briefs/10c-strict-csp.md:14-24`.
- `pki.crawford.dev` and a CRL: `docs/briefs/11a-site-files-and-profiles.md:18`, `:163`.
- `api.crawford.dev` "used by the AI editor account" (an API client identity exists): `docs/briefs/03-deployment.md:14`.
- Cluster composition (K3s on Raspberry Pi, Longhorn, CloudNativePG, cert-manager, Traefik, Fedora
  CoreOS nodes, `hello-world` IngressRoute to delete): `docs/briefs/03-deployment.md:6-11`, `:101-104`;
  `docs/superpowers/specs/2026-10-11-high-availability-design.md:112-116`.
- The private site repository's layout and secrets tooling (`~/sites/crawford.dev`,
  `secrets.tswap.tmpl`, Secret name `blog-db-app`): `docs/superpowers/specs/2026-10-10-site-branding-design.md:140-158`.

**Fix:** either accept it (the README already says the briefs are "part of the repository",
`:31-39`) or redact hostnames to `<registry>`, `<idp>` style placeholders in those lines.

### M-5. `docs/superpowers/specs/2026-10-04-ui-redesign/` ships the real site's brand assets and mockups the engine deliberately removed from `src/`

- **Where:** `crawford-tartan.svg`, `logo.svg`, `favicon.ico`, `apple-touch-icon.png` in that folder,
  and `mockups/*.png` showing "crawford.dev — Code, travel and the odd loaf" with a photographic
  banner (`mockups/post-page-v2.png`, `visual-style.png`, `post-cards.png`). Brief 07a removed the
  same files from `wwwroot` precisely so that "nothing in this repository identifies a site"
  (`docs/briefs/07a-branding-in-the-app.md:18-21`, `:99`).
- **Provenance of the banner photograph: unverified.** The mockup HTML embeds only `data:` URIs
  (27 SVG, 3 PNG) and no source or licence note was found (`grep -i "unsplash|pexels|photo by|CC BY"`
  over `docs/superpowers` is empty). If it is not the owner's photograph, it has no licence to be in
  an MIT repository.
- **Fix:** move the folder to the private site repository (where the spec says the branding lives),
  or confirm the photo's provenance in a one-line note.

### M-6. FluentAssertions 8.x is not an open-source licence

- **Where:** `tests/Croft.Api.Tests/Croft.Api.Tests.csproj:22` and
  `tests/Croft.Web.Tests/Croft.Web.Tests.csproj:13`: `FluentAssertions` `8.11.0`.
- **Why it matters:** from 8.0 FluentAssertions is under the Xceed Community licence: free for
  non-commercial and open-source use, paid for commercial use. The MIT project itself is fine, but
  a company that forks Croft and runs its tests needs a licence, which is an odd thing for an MIT
  repository to impose. (Not vendored, so no notice obligation; a compatibility note only.)
- **Fix:** pin to 7.x (Apache-2.0) or switch to the Apache-2.0 fork `AwesomeAssertions`; or state
  the situation in `THIRD-PARTY-NOTICES.md`.

### M-7. README E2E instructions diverge from the project's own E2E path

- **Where:** `README.md:154-167`: install Chromium, "Start the database, OAuth2 server, API, and web
  app (see steps 1–3 above)" (that is the dev stack), then `dotnet test tests/Croft.Web.E2E`.
- **Evidence:** the dev stack runs the Web app without its database (`start-local.sh:190-192`,
  `INSTANCE_WEB_CONN` is empty in dev mode), while the suite's session tests expect the instance's
  `UserSessions` table (`dev/instance-params.sh:9-11`); AGENTS.md says the suite runs against
  `./start-local.sh --e2e` instances through `dev/run-e2e.sh` (`AGENTS.md:104-121`) and that
  `SidebarFooterAlignmentTests` needs Firefox installed too (`AGENTS.md:174-176`). The README names
  neither. (Unverified by running.)
- **Fix:** replace the three steps with `dev/run-e2e.sh` and the Chromium + Firefox install line.

### M-8. Git identity, tracked-but-ignored files, and two stale names

- The single commit (`0e9e697`) is authored and committed with a personal address (redacted
  2026-10-20; the snapshot was rebuilt as `steve@crawford.dev`).
  That address becomes public with the repository. Owner's call; before the first public push is the
  only cheap moment to re-author it (a GitHub `noreply` address) since there is one commit.
- `.gitignore:31-32` ignores `appsettings.Development.json` and `appsettings.Production.json`, yet
  `src/Croft.Api/appsettings.Development.json` and `src/Croft.Web/appsettings.Development.json` are
  tracked (`git ls-files`). Harmless (they hold only log levels) but a contributor's local edits to
  them are tracked while the ignore rule suggests otherwise.
- `AGENTS.md:51` says "this repo's `docker-compose.yml`"; the file is `compose.yaml`.
- `README.md:345` pins the overlay example to `.../k8s?ref=v0.1.N` and `newTag: 0.1.0` (`:349`, `:352`)
  while `Directory.Build.props` is at `0.6.2`; a reader copies a tag that may not exist.
- `README.md:37` links `https://github.com/stevedcc/chargehand`; from this sandbox the URL answered
  **403** through the proxy, so whether it is public is **unverified**. If it is private, the README's
  "How it was built" paragraph points at a dead link.

---

## The walk, step by step (where a stranger stops)

**Build.** Stop 1: no SDK version stated in the README; `global.json` pins 10.0.401 with
`latestFeature`, so a plain "install .NET 10" works only if it is a 10.0.4xx SDK (10.0.1xx refuses,
as AGENTS.md notes for Fedora). After that, `dotnet build Croft.slnx` is clean.

**Run locally.** Stop 2: `docker compose up -d` needs Docker or Podman with compose; fine. Stop 3:
the manual `dotnet run` path fails (I-2). Stop 4: the script path silently skips migrations without
`dotnet tool restore` (I-3). Stop 5: `dotnet test tests/Croft.Web.Tests` is red without a container
runtime (I-5). The E2E section leads to the wrong stack (M-7). Not reached here: everything past
`docker compose` is unverified for lack of a daemon.

**Deploy.** Stop 6: no image build/push instructions (I-7). Stop 7: `croft-web-db` Secret and the
second database are missing from the README (I-1). Stop 8: the RWX PVC (I-7). Stop 9: the
Docker/Podman and "plain run" examples never run `migrate` (I-3). `k8s/README.md` itself is
accurate and thorough on the HA shape, pools, circuits, probes, site files, telemetry and export,
and `k8s/secrets.example.md` lists all four Secrets correctly.

---

## Checked and found sound

- **Fonts (OFL-1.1).** The seven `.woff2` files are unmodified upstream builds: name tables intact
  (`Source Sans 3` 3.052, `Source Serif 4` 4.005, `JetBrains Mono` 2.304 with ttfautohint), full glyph
  counts (2478 / 1464 / 1743), Adobe's Reserved Font Name "Source" untouched, and each file's
  embedded licence string points at `scripts.sil.org/OFL`. The three `OFL-*.txt` files carry the
  right copyright lines. OFL permits bundling with MIT software.
- **highlight.js 11.11.1** (BSD-3-Clause): `LICENSE` beside the files, header kept in `core.min.js`;
  `css/highlight-theme.css` is original (built from the palette tokens), not a copied theme.
- **Vendored Microsoft skills**: MIT (`MICROSOFT-SKILLS-LICENSE`), source commit recorded in
  `MICROSOFT-SKILLS.md`, the `blazor-` rename disclosed. MIT permits it.
- **Profile icons**: Simple Icons CC0 and MDI `link` Apache-2.0 attributed in
  `THIRD-PARTY-NOTICES.md`, including the honest LinkedIn note. The contents-panel chevron
  (`ContentsPanel.razor:23`) is a three-point line, not a copyrightable work.
- **Default branding assets**: `branding-default/logo.svg` is three rounded rectangles (in-house);
  `fallback-banner.svg` is the Black Watch tartan, generated by `dev/black-watch-banner.py` with a
  provenance comment (Register of Tartans ref. 277, 1739, no copyright). `favicon.ico` and
  `apple-touch-icon.png` are presumably renders of the logo (unverified).
- **NuGet dependencies** (not vendored, so no notice needed): MudBlazor MIT, Markdig BSD-2, Npgsql
  PostgreSQL licence, OpenTelemetry Apache-2.0, YamlDotNet MIT, bUnit MIT, xUnit Apache-2.0, NUnit
  MIT, Playwright Apache-2.0, Testcontainers MIT, Deque.AxeCore.Playwright MPL-2.0 (fine as a
  dependency). Only FluentAssertions stands out (M-6).
- **Secrets.** Scans for private keys, GitHub/AWS/Slack/OpenAI token shapes, JWTs, long base64/hex
  blobs and `password=`/`secret=` assignments found nothing real. `.env.example` holds
  `Oidc__ClientSecret=secret`, `Password=blog`, `dev-only-ingest-key`, `dev-only-hash-key`, all
  labelled dev-only and all meaningless outside the mock IdP and the compose Postgres; `.env`,
  `appsettings.Production.json` are gitignored and absent; `k8s/` holds placeholders only
  (`idp.example.com`, `your-client-id`, `blog.example.com`) and `K8sBaseTests` enforces it; the
  base64 blobs in `dev/seed-*.sh` are tiny generated PNGs. Only `dev/login.html` holds dev
  identities (`admin@example.com` → `Admin`), as AGENTS.md rule 3 requires.
- **Personal data in `src/`, `tests/`, `dev/`, `k8s/`:** apart from I-4, none. `site-files.example/`
  and `branding.example/` use `example.com` / `example.social` throughout.
- **README "What it does", claim by claim:**
  - Reading pages without a circuit: the seven pages carry `[ExcludeFromInteractiveRouting]`
    (`Components/Pages/{Home,Post,Category,Tag,Categories,StaticPage,Search}.razor`); no cookie on
    them (W-092, `Security/StaticDocumentAntiforgery.cs`); MudBlazor loaded only on interactive
    documents (`App.razor`). **Accurate.**
  - Strict CSP: `default-src 'none'`, SRI on every `<script src>` (`App.razor:64`,
    `Services/StaticAssetIntegrity`), no nonces (`ScriptSourcesCspContributor.cs:27`), Trusted
    Types report-only (`TrustedTypesCspContributor.cs`), snapshot pinned
    (`CspPolicySnapshotTests` + `CspSnapshots.txt`, passing here). **Accurate.**
  - Writing and administration: scheduled publishing (`ScheduledPublisherHostedService`), drafts
    that survive a lost session (`wwwroot/js/draft-backup.js`), media (`MediaService`,
    `LocalFileStorage`), sidebar/footer placement (`NavigationPlacement`). **Accurate.**
  - OIDC: tokens server-side (`SaveTokens = false`, `Program.cs:136`; `ITokenStore`), back-channel
    logout (`/backchannel-logout`, `Program.cs:851`). **Accurate**, with the "any provider" caveat (M-2).
  - Scale-out: read cache + `Database:ReadHost` routing (`BlogDbConnectionString`), LISTEN/NOTIFY
    (`CacheListenerService.cs:53`, `CacheNotifier.cs:36`), ReadyToRun (`-p:PublishReadyToRun=true`
    in both Dockerfiles), load shedding (API concurrency limiter `src/Croft.Api/Program.cs:111-129`, Web rate
    limiter and `MaxActive` 503). **Accurate** except the 160 rps figure (M-1).
  - SEO: canonical (`SeoHead.razor:29`), 12 `og:` tags, JSON-LD `BlogPosting` and `BreadcrumbList`
    (`SeoHead.razor:2`, `:69`), sitemap (`SitemapService`), RSS (`/feed.xml`, `Program.cs:599`).
    **Accurate.**
  - Per-site branding from a mounted folder: `BrandingService`, `Blog__BrandingPath`,
    `branding.example/`. **Accurate.**
  - Stack line "Container images for arm64 and amd64": both Dockerfiles cross-publish for
    `$TARGETARCH`. **Accurate.**
- **README security-headers and CSP section**: consistent with `SecurityHeadersMiddleware`, the
  CSP contributors and the snapshot the passing tests pin.
- **`k8s/` manifests**: no site values, no provider names (`K8sBaseTests` enforces both), Secrets
  referenced by name only, runAsNonRoot with numeric UID matching the Dockerfiles' `USER $APP_UID`.
