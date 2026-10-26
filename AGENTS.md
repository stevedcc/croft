# Agent guide

Croft, a self-hosted blog engine: Blazor front end (static SSR reading pages, Blazor Server + MudBlazor for admin) (`src/Croft.Web`) calling an ASP.NET Core Web API
(`src/Croft.Api`) backed by PostgreSQL via EF Core. Shared request/response records live in
`src/Croft.Shared`. Auth is any OIDC provider; local dev uses navikt/mock-oauth2-server.
.NET 11 (RC 2), PostgreSQL 18.

This file is read by everyone and every agent working here. The rules apply to whoever is writing
code.

## Rules (non-negotiable)

1. **TDD.** Use the superpowers `test-driven-development` skill (`superpowers:test-driven-development`
   in Claude Code) for every behaviour change and bug fix: write the failing test, watch it fail
   for the right reason, then make it pass. For changes that can't be unit tested (config, compose,
   scripts), show before/after evidence from running the thing instead.
2. **Verify before claiming done.** Use the superpowers `verification-before-completion` skill. "Done"
   means the relevant test suites were run after the last change and their output is shown.
3. **Never hard-code users, emails, roles or credentials in `src/`.** Dev-only identity setup belongs
   in `dev/` (see `dev/login.html`).
4. **Never disable a security check unconditionally** (audience/issuer validation, HTTPS metadata,
   detailed errors, authorization attributes). Dev-only relaxations are gated on `IsDevelopment()`.
5. **Never change an existing test's assertion to make it pass.** If a test looks wrong, stop and say
   why. Changing a test input because the brief changed the rules (e.g. an allow-list) is fine, but
   say so in the commit message.
6. **No `Task.Delay`, sleeps, or blanket `try/catch` in tests** to hide timing or failures. Wait on a
   concrete condition (an element, a URL, a response).
7. **Tests assert outcomes, not presence.** An E2E test must check the result the user cares about
   (HTTP status, an image actually loaded, data persisted and shown again), not merely that an
   element exists or is visible; a broken `<img>` is still "visible". For each key test, break the
   feature once (e.g. make the endpoint return 404), confirm the test fails, then revert. Report
   that check in the commit message.
8. **Stop when blocked.** If a tool, library or IdP can't do what's needed, report it with the
   evidence. Silent workarounds are how a hard-coded admin backdoor got in here once. A command
   denied by the session's permissions is a block too: stop and ask, and never get the same effect
   another way (git plumbing, a script, another tool), even when the brief seems to require it.
9. **One commit per task item**, with a message that says what was done and anything left undone.
   Write multi-line messages with `git commit -F <file>` or several `-m` flags, never literal `\n`.
   **Another agent may be working in the same clone at the same time.** Always commit with an
   explicit path list (`git commit -F msg -- <path>...`), never a bare `git commit` that takes
   whatever is staged. Check `git diff --cached --stat` first, and never stage files you didn't change.
   Commit only when the brief or the user asks for it.
   **Merging** (only when told to, never rebase): `git rerere` is on, so Git may print
   `Resolved '<file>' using previous resolution`. Check each such file's diff before committing the
   merge, then run the suites. Never set `rerere.autoUpdate`.
10. **Clean up.** Remove temporary files.

## Containers

Tests reach containers through the current user's rootless Podman socket (Testcontainers). Besides
that, only `docker compose up -d` and `docker compose down` with this repo's `compose.yaml`
(never `down -v` or `--volumes`: that deletes the dev database), and `docker run --rm …` /
`podman run --rm …` of images built from this repo, to check them (no volume or bind mounts, no
`--privileged`, `--cap-add`, `--device`, `--pid`, `--userns` or `--security-opt`). Never stop or
remove containers that aren't Testcontainers instances from your own test run.

## Superpowers skills

Skill names are shown without the `superpowers:` prefix some tools use. Where a skill's instructions conflict with this file, this file wins.

| Skill | How to use it in this project |
|---|---|
| `test-driven-development` | Always, for behaviour changes and bug fixes (rule 1). |
| `systematic-debugging` | For ANY test failure, crash or unexpected behaviour: find the root cause before changing code. Never make a failure go away by patching the symptom (loosening a locator, adding a `catch`, a wait, or a fallback value). |
| `verification-before-completion` | Before claiming anything works (rule 2). |
| `requesting-code-review` | Before calling a change finished: a separate reviewer checks the diff against the task and this file. |
| `receiving-code-review` | When handling review findings: check each finding against the code, fix the valid ones, push back with evidence on wrong ones, and say which. Never silently skip a finding. |
| `using-superpowers`, `diagnosing-superpowers`, `writing-skills` | No project-specific guidance. |

## .NET skills in this repo (`.claude/skills/`)

| Skill | When |
|---|---|
| `writing-dotnet10-code` | Before writing or changing any C#, Razor or EF code (Conventions, "C# style"). |
| `writing-dotnet11-code` | Now applies (the projects target `net11.0` since brief 16a): in addition to `writing-dotnet10-code`, before writing or changing any C#, Razor or EF code. |
| `blazor-*` | Before writing or changing Razor components and pages; `blazor-support-prerendering` for anything a page loads (see "Interactivity"). |
| `optimizing-ef-core-queries` | When writing or changing EF Core queries, and when reviewing them. |
| `dotnet-webapi` | When adding or changing API endpoints. |
| `csharp-refactoring` | For refactoring items (behaviour must not change). |
| `analyzing-dotnet-performance` | For performance items and reviews. |
| `test-anti-patterns`, `assertion-quality` | **Task reviewers and final reviewers:** check the new and changed tests with both, on top of rule 7. |
| `writing-dotnet11-code` (review) | **Task reviewers and final reviewers** judge new or changed C# against `writing-dotnet11-code`'s defaults table: an old-style construct in new code where the table names the .NET 11 form is a finding. |
| `migrate-dotnet10-to-dotnet11` | For the .NET 11 upgrade; also walk `docs/workarounds.md` (the register, see "Workarounds" below). |

The Microsoft ones are vendored (source, commit and precedence in `.claude/skills/MICROSOFT-SKILLS.md`);
where they disagree with this file, this file wins.

## Running things

**Prerequisites.** SDK 11.0.100 (RC 2, `11.0.100-rc.2.26504.105`) is pinned in `global.json`; on this
machine it lives in `~/dotnet` (Fedora's `/usr/bin/dotnet` is 10.0.1xx and will refuse to build). If
`dotnet --version` isn't 11.0.1xx, run `export DOTNET_ROOT=$HOME/dotnet PATH=$HOME/dotnet:$PATH` first. Containers run on rootless Podman
via the **current user's** socket, `$XDG_RUNTIME_DIR/podman/podman.sock` (enabled with
`systemctl --user enable --now podman.socket`). Inside the agent sandbox `DOCKER_HOST` is already set
correctly for the sandbox user; never override it or hard-code a UID. Test fixtures must not start or kill container
runtimes themselves.

```bash
docker compose up -d          # Postgres :5432 + mock OIDC :5556 (issuer http://localhost:5556/default)
dotnet tool restore           # local dotnet-ef
./start-local.sh --e2e        # builds once, drops/recreates + migrates blog_e2e_1 and the Web database blog_e2e_1_web (sessions), starts API :5000 and Web :5002 (logs in /tmp/blog-e2e-1-*.log)
./stop-local.sh --e2e
```

Agents run the stack and the E2E suite only against `./start-local.sh --e2e` (every start drops and
recreates `blog_e2e_<N>` and gives the API a fresh `/tmp` uploads folder). The shared dev database
`blog` (`./start-local.sh` with no flags) is for the owner's hand testing. E2E instance PID and log
files differ from the dev stack's (`/tmp/blog-e2e-<N>-*.pid` vs `/tmp/blog-api.pid`), so plain
`./stop-local.sh` stops only the dev stack; `./stop-local.sh --e2e` stops an E2E instance and
`./stop-local.sh --all` stops everything. The E2E suite goes through `dev/run-e2e.sh` (`--instances N`,
`--first-instance K`, default instances 1..3 per the 2026-10-10 measurement: 1→229 s, 2→152 s,
3→130 s, 4→135 s suite wall): it starts instances K..K+N-1, runs the suite with one NUnit worker per
instance and stops exactly the instances it started, also on failure. Running the suite directly
against one manually started instance (`./start-local.sh --e2e --instance N`, then
`dev/with-suite-lock.sh dotnet test tests/Croft.Web.E2E --no-build` (through the suite lock, like every suite) with `E2E_WEB_URL`/`E2E_API_URL` set to that instance's
ports, dev/instance-params.sh) still works, but is the exception, not the normal path.
A `dev/run-e2e.sh` run with no mode flag (`--csp-enforced`, `--default-branding`, `--output-cache`) and no `--`
arguments (`--instances` and `--first-instance` do not matter) ends with a second pass of the
`DefaultBranding` category on one instance with no branding folder (`BLOG_BRANDING_PATH=`), which
the main suite's `branding.example` instances cannot show; `dev/run-e2e.sh --default-branding`
runs that pass alone. A third pass runs the `OutputCache` category (`--output-cache` runs it alone):
every other run starts its instances with the Web app's output cache off
(`Croft__Web__OutputCache__Duration` and `__FeedDuration` at `00:00:00`, unless you export other values), because the suite
publishes through the API behind the Web app's back and reads the page at once, which a cache up to a
minute stale would turn into a lottery; the `OutputCache` tests need it on and skip themselves
elsewhere.

**After changing code in `src/`, restart any running stack** (`./stop-local.sh --e2e &&
./start-local.sh --e2e`) before probing it or running E2E against it by hand; a `dev/run-e2e.sh` run
always starts fresh instances. The running processes don't pick up rebuilds.

Tests (build once with `dotnet build Croft.slnx`, then always `--no-build`):

```bash
dev/with-suite-lock.sh dotnet test tests/Croft.Api.Tests --no-build   # integration tests; Testcontainers Postgres (~20s)
dev/with-suite-lock.sh dotnet test tests/Croft.Web.Tests --no-build   # unit tests plus 31 container-backed tests (~1 min)
dev/run-e2e.sh                                # Playwright E2E; starts instances K..K+N-1 (default 1..3), runs the suite, stops them
```

Two suites running at once have twice taken this machine down (swap on 2026-10-23, OOM kills on
2026-10-25), so suites go through `dev/with-suite-lock.sh`: it takes a per-machine lock
(`~/.cache/croft/suite.lock`), prints who holds it while it waits, and passes the command's exit
code through (`--try` exits 75 instead of waiting). `dev/run-e2e.sh`, `dev/run-ha-e2e.sh` and
`dev/run-large-data-e2e.sh` take the lock themselves. Those three scripts also carry the same
guard (`dev/lib/test-summary.sh`): when `dotnet test` exits 0 they exit 70 (no NUnit summary line in
the log), 71 (more than one) or 72 (the summary reports failures); a non-zero `dotnet test` code is
kept as it is.

**Test cadence** (keeps runs fast without weakening verification):
- During red/green and break-it-once loops: run only the affected test class or test
  (`--filter "FullyQualifiedName~ClassName"`).
- Implementer: run all three suites ONCE per item, after the last change and before committing,
  through the `test-runner` subagent when one is available, and record its verbatim summary lines in the report.
- Run E2E for an item only if it touched `src/Croft.Web`, routes, auth or anything the browser sees.
  Otherwise API + unit suites are enough until the final review.
- Task reviewers: run the item's new/changed tests (plus any you suspect) through `test-runner` (when available),
  and rely on the implementer's recorded full-suite output. Re-run everything only if that output
  is missing or doesn't match the commit.
- Final whole-branch review: one full run of all three suites from a clean build (E2E via
  `dev/run-e2e.sh`), through `test-runner` when available.
- Never run suites on an instance another agent is using; parallel briefs each get their own. The
  suite lock serialises suites on the machine; `pgrep` is no longer needed.

**Without a container runtime.** Every API test class and the Web test classes labelled
`[Trait("Category", "Container")]` (those using `PostgresDatabaseFixture`; a test guards the
label) need Docker or Podman. A machine without one runs
`dev/with-suite-lock.sh dotnet test tests/Croft.Web.Tests --no-build --filter "Category!=Container"`; the API suite has
no such subset. The Web project has no default filter, so its plain run includes the labelled
classes. (In the API project the same label name means the opt-in image-building tests, below.)

**Opt-in tests (`Croft.Api.Tests`).** Two categories are excluded from a plain `dotnet test`
(a default `VSTestTestCaseFilter` in the project file, the same mechanism as the E2E project's
`LargeData`/`HaTests`) because they are slow and need container access or a long publish:
`Category=Container` (builds both Dockerfiles and runs `migrate` in the images through Podman,
about 1 minute) and `Category=ReadyToRun` (publishes both apps for linux-arm64 with the
Dockerfiles' own flags and checks for ReadyToRun code, about 40 s). Run them explicitly, after a
normal build:

```bash
dev/with-suite-lock.sh dotnet test tests/Croft.Api.Tests --no-build --filter "Category=Container"
dev/with-suite-lock.sh dotnet test tests/Croft.Api.Tests --no-build --filter "Category=ReadyToRun"
```

They **must be run** after any change to a Dockerfile, to `Croft.Shared/MigrationRetry.cs`, to the
`migrate` entries in the two `Program.cs` files, or to `Database:MigrateTimeout` handling, and in
the final whole-branch review of a brief that touched any of those. A `--filter` replaces the
default, so any filter you pass includes these categories if it matches them.

CSP measurements also run in Firefox and WebKit (`dev/run-e2e.sh --browser firefox --csp-enforced`, or
`BROWSER=firefox|webkit` for a manual run): install them once with
`node tests/Croft.Web.E2E/bin/Debug/net11.0/.playwright/package/cli.js install firefox webkit`. WebKit
cannot sign in over plain `http://localhost` (the OIDC correlation cookie is `Secure`), so staff
pages are measured in Chromium and Firefox only; `CspPublicPagesTests` covers the anonymous
documents in all three engines.

E2E browsers, once: `dotnet build tests/Croft.Web.E2E && node tests/Croft.Web.E2E/bin/Debug/net11.0/.playwright/package/cli.js install chromium`
Firefox too, for `SidebarFooterAlignmentTests` (Firefox 157.0, Playwright build firefox-1555; the
test fails with Playwright's "Executable doesn't exist" error when it is missing, it is never
skipped): `node tests/Croft.Web.E2E/bin/Debug/net11.0/.playwright/package/cli.js install firefox`

Releases are tags, not version bumps, and Renovate's dependency-only merges tag themselves as
the next patch: see README "Releases" before touching `Directory.Build.props`' `<Version>`,
`release.yml` or `auto-release.yml`.

Migrations: `dotnet tool run dotnet-ef migrations add <Name> -p src/Croft.Api -s src/Croft.Api`.
Where instances may migrate concurrently (the k8s init containers), use the API's `migrate`
entry (`dotnet Croft.Api.dll migrate`): it serialises runs on an advisory lock — EF's own
migration lock covers a single migration only, so two unguarded runs over a migration chain
collide.

### Local identities

The mock IdP's login page (`dev/login.html`) maps usernames to claims:

| Username | `sub` | `groups` |
|---|---|---|
| `admin@example.com` | `admin-id` | `Admin` |
| `editor@example.com` | `editor-id` | `Editor` |

Any other username logs in with no groups (no role). Roles come only from the `groups` (or `role`)
claim, mapped to `ClaimTypes.Role` in both apps by the shared `Croft.Shared.RoleGroupMapper`: a
`groups` claim value grants a role only when it equals the group name configured in
`Oidc:RoleGroups:Admin` / `Oidc:RoleGroups:Editor` (defaults `Admin` / `Editor`, so the
mock IdP above works unchanged), and a `role`
claim value passes through only when it is literally `Admin` or `Editor`. Other values grant
nothing; each role is added at most once.

## Conventions

- **API:** thin controllers; logic in `Services/` behind interfaces; EF entity configuration in
  `Data/Configurations/`; services return `Croft.Shared` DTOs, never entities.
- **Identity in the API:** read the user via `Croft.Api.Identity.UserExtensions` (`GetUserId()` →
  `sub`, `GetUserName()` → `name`). `MapInboundClaims` is off, so don't use `ClaimTypes.NameIdentifier`.
- **API tests:** `WebApplicationFactory` + `TestAuthHandler`; set the role with
  `client.WithRole("Admin")`. One Postgres Testcontainer per run; one database per test class,
  created from the migrations at class start and dropped at class end; classes run in parallel,
  and tests inside a class run serially on shared rows — scope assertions to data the test created.
- **Auth tokens in the Web app** live server-side. At login, `TokenSessionEvents.OnTokenValidated`
  saves the token endpoint response (access token, refresh token, expiry) in the singleton
  `ITokenStore` under a random `blog_sid` claim; nothing token-shaped is kept in the auth cookie.
  `IUserAccessTokenService.GetAccessTokenAsync(user, ct)` returns a valid access token per request
  and refreshes near expiry (60 s window, refresh-token rotation, one refresh per session).
  A `blog_sid` with no store entry (e.g. after a pod restart) is rejected and signed out in the
  cookie handler's `OnValidatePrincipal`; `/logout` removes the entry before signing out.
- **Web → API calls from components:** go through `IBlogApiClient`, which uses the `"BlogApi"` named
  client and attaches the token per request: the user comes from `AuthenticationStateProvider`,
  the token from `IUserAccessTokenService`. Never resolve
  `AuthenticationStateProvider` or `HttpContext` inside an `HttpClient` handler; handlers run in a
  different DI scope.
- **Web → API calls from plain HTTP endpoints** (minimal APIs such as the `/api/media/{id}` proxy):
  call `IUserAccessTokenService.GetAccessTokenAsync(HttpContext.User, ct)`;
  `AuthenticationStateProvider` is not available there.
- **Files never go in the database.** Uploaded binaries are written through the file-storage
  service (`LocalFileStorage`: a folder locally, a PVC in Kubernetes); the database holds only
  the reference and metadata (`MediaAsset`: file name, path, content type, alt text). No `byte[]`,
  `bytea` or base64 columns for file content.
- **Media URLs** are relative (`/api/media/{id}`). The Web app serves that path as a narrow proxy to
  the API, so images work on the Web host for both public and (logged-in) draft content.
- **Blazor:** pages render `InteractiveServer`, except the seven static reading pages and the not-found and error pages (see
  "Interactivity" below). Anything with event handlers needs interactivity.
  See "Interactivity" below and the `blazor-*` skills.
- **Blazor components:** pages (`@page`) fetch data and compose; components present. Components take
  data via `[Parameter]` and report actions via `EventCallback`, and don't inject `IBlogApiClient`
  unless a spec explicitly marks them as service components. One component per file, styles in its
  own `.razor.css`. The folder map (UI redesign): `Components/Layout/` (the shell), `Components/Blog/`
  (post surfaces: hero, body, cards, TOC, rail), `Components/Admin/` (admin/editor shared: forms,
  grids, pickers), `Components/Pages/` with `Admin/` and `Editor/` below it (routable pages). Never
  duplicate markup between pages: extract a component. When a spec names components, build those
  (names, folders, parameters), not your own structure.
- **Dates:** store UTC. Normalise incoming `DateTime` values to UTC before they reach Npgsql
  (`timestamp with time zone` rejects `Kind=Unspecified/Local`).
- **Migrations (expand/contract):** a migration may add tables, nullable columns, columns with
  defaults and indexes; it may not drop, rename, or make NOT NULL anything the previous release
  reads. Removals ship one release later. A migration that contracts anyway carries
  `[ContractMigration("why it is safe now")]`; the migration guard test rejects contracting
  operations without it.
  **Pending contracts** (columns kept only for older releases; drop each in a later contract
  migration once no pod older than the named release runs, then delete its line here):
  - `Posts.ShowInNavigation` (replaced by `NavigationPlacement` in 0.6.1, kept as a shadow
    property in `PostConfiguration`): drop once no pod older than 0.6.1 runs.
  - `Posts.ViewCount` (replaced by the narrow `PostViewCounts` table in the release after 0.6.3, brief 13c; the model maps it as
    `Post.LegacyViewCount` and nothing of the new release writes or reads it): a pod of the release before brief 13c (0.6.3 and older) still adds
    its views to it during a rolling deploy, and those views are not in `PostViewCounts` (the migration copied the
    totals once, a small accepted loss). Drop the column once no pod older than the release of brief 13c (the release after 0.6.3) runs.
  - `PageViews.Referrer` and `PageViews.UserAgent` are unbounded `text` (review 2026-10-19,
    finding 3): the API truncates them to 2048 and 512 characters on write since brief 13a (the release after 0.6.2). A
    pod older than that may still write longer values during a rolling deploy, so the
    `HasMaxLength(2048)` / `HasMaxLength(512)` migration (and a one-off trim of existing rows) ships one
    release later, once no pod older than the truncation runs.
- **Rules for data migrations (expand/contract, continued):** the migration guard reads raw SQL for
  `DROP`, `RENAME`, `SET NOT NULL` and `ALTER COLUMN ... TYPE`, and flags a `NOT NULL` column with no
  default; moving data is the other half of the rule.
  - **Dual-write during the overlap.** While a pod of the previous release can still run, the new
    release writes both the old and the new column (or the old one is a shadow property with a default
    that nothing of the new release needs to set); a one-off backfill alone loses an edit made through
    an old pod, or hides one made through a new pod from the old pods. The contract migration drops the
    old column one release later.
  - **A backfill that would degrade public output renders the old value until it is done.** Do not null
    or empty a value the public pages show and refill it afterwards: add the new column nullable, let
    readers fall back to the old value while it is null, and fill it in a backfill (or compute it in the
    migration itself) that **commits per batch**, so no transaction holds a row lock on every post and a
    half-finished run leaves a consistent site.
- **C# style:** load the `writing-dotnet10-code` skill (`.claude/skills/`) before writing C#, Razor or
  EF code. New and changed code follows its .NET 10 / C# 14 defaults even where nearby code is
  older-style; don't rewrite code you aren't otherwise touching. Nullable reference types are on;
  keep builds warning-free.

## Workarounds

Code that exists, or has its shape, only because a framework, library, browser or platform
behaves a certain way gets an entry in `docs/workarounds.md` and a marker in the code, in the same
commit. A block is a `WORKAROUND(W-NNN, <library>): <behaviour>` comment above the code and an
`END WORKAROUND(W-NNN)` comment below it; a file that is only the workaround carries
`WORKAROUND-FILE(W-NNN, <library>): <behaviour>` in its first five lines. Mark only the
compensating code, never the check: the test that fails without the workaround is named in the
entry, not marked. `WorkaroundRegisterTests` fails when markers and entries drift apart. Retired
IDs are listed under "Retired" in the register and are never reused. Upgrade briefs walk the
register (see the `migrate-dotnet10-to-dotnet11` row above).

## Interactivity

| Setting | Value |
|---|---|
| **Interactivity Mode** | Server |
| **Interactivity Scope** | Per page: global `InteractiveServer` with prerendering, except the seven reading pages and the not-found and error pages, which are static SSR (`<Routes @rendermode="PageRenderMode" />` in `Components/App.razor`; `PageRenderMode` is `null` for pages marked `[ExcludeFromInteractiveRouting]`) |

The `blazor-*` skills in `.claude/skills/` (Microsoft's, vendored; see `.claude/skills/MICROSOFT-SKILLS.md`) read this
table. What it means here:

- **Two kinds of page.** The seven reading pages (Home, Post, Category, Tag, Categories,
  StaticPage, Search) carry `@attribute [ExcludeFromInteractiveRouting]` and render as **static
  SSR**: no Blazor circuit, no WebSocket, no `[PersistentState]` (there is no handoff to restore),
  no `OnAfterRenderAsync`, no event handlers. They load their data once in
  `OnParametersSetAsync`. Links between them use Blazor's enhanced navigation. Their JavaScript
  (shell, theme switch, scroll spy, contents panel, highlighting, the page-view beacon) is started
  by `wwwroot/js/static-glue.js` on load and after every `enhancedload`; the search is a plain GET
  form. A new reading page follows the same rules; a page that needs an event handler or component
  state is interactive, not a reading page.
- **The not-found and error pages are static too** (`[ExcludeFromInteractiveRouting]`, plain markup): an anonymous visitor never opens a circuit, and `CircuitAdmissionMiddleware` refuses a circuit start from a request that is not signed in.
- **Everything else is interactive** (admin, editor, login/logout): every such page
  prerenders on the server, then runs again when the circuit connects: `OnInitializedAsync` runs
  **twice** on a full page load. Data a page loads there must survive the handoff with
  `[PersistentState]` and the `??=` pattern (skill `blazor-support-prerendering`), or it is fetched
  from the API twice. `MainLayout` renders in both kinds of page and keeps its own
  `[PersistentState]` for the interactive case.
- Data access is **not** direct: Web never touches the API's database. Pages load data through
  `IBlogApiClient` (Conventions above); components receive it as parameters.
- `HttpContext` exists only during the static render. Pages that need it (cookies, status codes,
  sign-in/out endpoints) are static SSR via `[ExcludeFromInteractiveRouting]`.
- The reading pages load no MudBlazor: `App.razor` links its stylesheet and script, and
  `MainLayout` renders its providers, only on interactive documents. A reading page uses no
  MudBlazor component (the static not-found copy is plain markup, see `NotFoundContent`).
- Circuits are for admin and the editor only (a circuit's memory is a real cost; readers must not
  hold one).
- Don't add `@rendermode` to pages: the render mode is chosen once, in `App.razor`, from the
  `[ExcludeFromInteractiveRouting]` attribute. Use `RendererInfo.IsInteractive` to skip work that
  only makes sense in one phase of an interactive page.
