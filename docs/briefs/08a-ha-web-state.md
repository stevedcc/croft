# Brief 08a: high availability, part 2: any Web instance serves any signed-in user, and no words are lost

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end (plus the HA suite twice), the final whole-branch review. The design is
`docs/superpowers/specs/2026-10-11-high-availability-design.md`, decisions D1, D2, D3, D7 and
the "Decided" section (read it). Brief 08b built the API side and the local HA stack
(`dev/ha-local.sh`, `dev/ha-kill.sh`, `dev/run-ha-e2e.sh`, `HaTests`) and the migration lock;
reuse them, don't rebuild them. Brief 07i (contents panel polish, `Croft.Api` tag order) may run
at the same time; it doesn't touch anything below. When the orchestrator says another brief was
accepted, `git fetch origin` and `git merge --no-edit origin/blog-framework` (never rebase).

**Owner-visible outcome:** with two Web and two API instances behind the load balancer, a signed-in
admin is never signed out when an instance stops or restarts, two instances never break a
session by refreshing its token at the same time, and text typed in the editor survives the loss
of the instance serving it. A single-instance site still runs with no Web database at all.

**Facts (checked 2026-10-11, `blog-framework` b660eb3 plus 08b):**
- `ITokenStore` (`Croft.Web/Services/ITokenStore.cs`) is synchronous: `Get`, `Set`, `Remove`,
  `RemoveBySid`, `RemoveBySub`; `InMemoryTokenStore` is a singleton (`Program.cs:124`); the key is
  the `blog_sid` claim.
- `UserAccessTokenService` refreshes 60 s before expiry under an in-process
  `SemaphoreSlim` per session (`UserAccessTokenService.cs:19-67`); the refresh token rotates.
- Data Protection keys: `PersistKeysToFileSystem(DataProtection:KeysPath)` (`Program.cs:182-188`),
  a RWO volume in k8s.
- `ReconnectModal` is rendered (`App.razor:43`, styled under CSP by 07f).
- The Web app has no database connection. E2E signs in through the mock OAuth2 server
  (`dev/mock-oauth2-config.json`, `WebTest.LoginAsync`).
- The editor: `Components/Admin/PostEditorForm.razor`, `MarkdownEditor.razor`,
  `wwwroot/js/editor.js`; routes `/admin/posts/new`, `/admin/posts/{id}/edit`, and 07g's page
  routes.
- The `test-runner` subagent's pinned model is out of quota tonight: run builds and suites
  yourself, output to a file, read the summary lines. Do not use `pattern-applier` in this brief.

## Item 1: the Web database, optional (D1)

- `CroftWebDbContext` (Npgsql, EF Core), used only when `ConnectionStrings:WebDb` is set. Without
  it, everything stays as today (in-memory token store, keys on the file system): the
  single-instance default must not need a database (spec goal 4).
- Its migrations run the way 08b made the API's run: one at a time under a Postgres advisory lock,
  through a `migrate` entry of the Web app (same pattern and lock helper; a different lock id),
  in a Web init container in `k8s/`. 08b's expand/contract guard covers the Web migrations too.
- **Fix in 08b's lock helper (`BackgroundJobLock.RunAsync`):** its `SELECT pg_advisory_lock(...)`
  runs under the default 30 s command timeout, so a migrator that waits longer than that (the
  other pod's migrations take > 30 s) fails its init container and Kubernetes restarts it with
  back-off. Make the lock wait (only the wait) unbounded, for the API and the Web entry alike.
- Tests: with `WebDb` unset the app starts and has no `CroftWebDbContext` registered; with it set,
  two concurrent `migrate` runs against one empty database both exit 0 (the 08b test, for Web);
  a `RunAsync` waiter whose lock is held for 35 s by another session (a test holding
  `pg_advisory_lock` on the same id) still acquires it and completes. Break-it-once: restore the
  default timeout; that test fails.

## Item 2: sessions shared by every Web instance, refreshed once (D1)

- `ITokenStore` becomes async (`GetAsync`, `SetAsync`, `RemoveAsync`, `RemoveBySidAsync`,
  `RemoveBySubAsync`, all with `CancellationToken`); every caller is updated; `InMemoryTokenStore`
  keeps its behaviour.
- `PostgresTokenStore` (table of sessions keyed by session id, with sid, sub, expiry; tokens
  stored as today's entry holds them) when `WebDb` is set.
- **Refresh under a row lock:** the refresh path opens a transaction, `SELECT ... FOR UPDATE` on
  the session row, re-reads it, and refreshes only if it is still within the refresh window; the
  loser of a race gets the winner's new token. The in-process semaphore may stay as a first gate.
- Expired sessions are purged by a background job under an advisory lock (08b's job pattern).
- Back-channel logout removes the session from the shared store, so it ends on every instance.
- Tests (Web, Testcontainers Postgres, two app instances = two `WebApplicationFactory` hosts on
  one database): a session written by instance A is read by B; back-channel logout on A ends it
  on B; **two instances refreshing one session at once against a token endpoint that revokes a
  refresh token on second use: exactly one refresh request reaches the endpoint, both callers
  get the new access token, the session survives**; an expired session is purged once with two
  purgers running. Break-it-once: drop `FOR UPDATE`; the concurrent-refresh test fails (two
  requests, a revoked token).

## Item 3: Data Protection keys in the database, and the import (D1)

- With `WebDb` set: `PersistKeysToDbContext<CroftWebDbContext>()`; without it, the file system
  as today.
- `croft-web keys import <directory>`: a command of the Web app that copies every key XML from
  the old key directory into the database once (idempotent: a key already present by id is
  skipped; it prints what it imported and skipped). This is what lets the cluster switch without
  signing anyone out.
- Tests: an auth cookie protected by instance A is unprotected by instance B; a cookie protected
  with a file-system key ring is still readable after `keys import` of that directory into an
  empty database; running the import twice imports nothing the second time. Break-it-once: point
  B at its own file key ring; the A→B cookie test fails.

## Item 4: Web health and the Kubernetes base (D7)

- `GET /health/live` (the process is up; checks nothing else, so an API outage never restarts
  Web pods) and `GET /health/ready` (the Web database when `WebDb` is set, and the API's
  `/health`); anonymous, `noindex`, not in the sitemap; add both to `ReservedSlugs`.
- `k8s/`: Web liveness on `/health/live`, readiness on `/health/ready` (replace 08b's commented
  block); the Web Deployment reads `ConnectionStrings__WebDb` from a Secret `croft-web-db`, key
  `connection-string`; the Web init container runs the Web `migrate`; the Data Protection keys
  volume is removed from the base, and `k8s/README` shows the single-instance overlay that puts
  it back (no `WebDb`, `replicas: 1`, RWO, `Recreate`, file keys). `K8sBaseTests` gain an
  assertion for each change.
- Tests: `/health/ready` is 503 when the database is unreachable or the API's health fails, 200
  otherwise; `/health/live` is 200 in both failure cases. Break-it-once: make live check the API;
  its test fails.

## Item 5: the editor's local draft backup and the restore prompt (D3, owner's decision 2)

- A same-origin JS module (CSP-clean: no inline script or style attributes) keeps a backup in
  `localStorage` under `croft-draft:<post id>` (`croft-draft:new` for an unsaved new post, moved
  to the id on first save): title, body, the server's saved version stamp the editor loaded, and
  the time. Written on every input, debounced 500 ms. Cleared on a successful save.
- On loading the editor, when a backup exists, is newer than the saved version and differs from
  it: a prompt, never a silent replace: "You have unsaved changes from <local date and time>.
  Restore them?" with **Restore** and **Discard**. Restore puts the backup's title and body in the
  editor (unsaved, so the user saves); Discard deletes the backup. A backup identical to the saved
  text is deleted silently.
- `/logout` clears every `croft-draft:*` key (a shared computer keeps no drafts after sign-out).
- Tests (E2E): type, reload without saving: the prompt shows with the time; Restore: the editor
  holds exactly the typed text; Discard: the server's text and no backup key left; save clears the
  key; an unchanged backup gives no prompt; a 200,000-character body (the editor's limit)
  round-trips through the backup; after logout no `croft-draft:` key remains. Break-it-once:
  remove the restore check on load; the reload test fails.

## Item 6: the circuit-persistence spike, adopt or drop (D3)

.NET 10 can persist circuit state for a reconnect to another instance (`[PersistentState]` on the
editor's model, stored through `HybridCache` over an `IDistributedCache` on `croft_web`).
Microsoft documents it as best effort. Measure it on the HA stack: open the editor through
Traefik, type, kill (SIGKILL) the Web instance serving it, ten times. **Adopt** it if the editor
resumes on the other instance without a reload, with every typed character, in at least 9 of 10;
otherwise **drop** it: remove its code, keep item 5 as the answer, and give the measurements
(and why) in the completion report. Either way, say which.

## Item 7: the signed-in HA tests (08b's stack, `HaTests`)

Extend `dev/ha-local.sh` with a fresh `croft_web_ha` database (migrated once) and `WebDb` for both
Web instances. Then, through Traefik:
- **Admin probe:** a signed-in browser requests an admin page every second while
  `ha-kill.sh web 1` runs, and again while `ha-kill.sh api 1` runs: zero non-2xx, zero redirects
  to sign-in.
- **Refresh across instances:** with a short access-token lifetime in the mock OAuth2 server
  (just over the 60 s refresh window), alternate requests between Web 1 and Web 2 (by port) for
  three token lifetimes: the session survives and the token endpoint sees exactly one refresh per
  expiry.
- **No lost words:** type 2,000 characters into the editor through Traefik, kill the Web
  instance serving the session (read which from the sticky cookie), then: after reconnect or
  reload (with Restore if prompted), the editor holds all 2,000 characters.
Break-it-once: run the two Web instances with the in-memory store (no `WebDb`); the admin probe
fails with a sign-in redirect.

## Out of scope

The site repository and the cluster (08c), object storage, Redis, the render-mode spec, the API
(except reading its `/health`), the contents panel and tags (07i).
