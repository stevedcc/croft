# Brief 08b: high availability, part 1: the API runs twice safely, and a local HA stack

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review. The design is
`docs/superpowers/specs/2026-10-11-high-availability-design.md` (decisions D4, D5, D7; read it,
it is in the launch message if not yet in the repository). Briefs 07f (CSP), 07g (pages) and 07h
(mobile contents) run at the same time. You own: `Croft.Api`'s hosted services, migration
tooling and guard tests, `k8s/`, `dev/ha-*`, and AGENTS.md's new migration rule. 07g adds a
migration (additive); your guard must accept it. When the orchestrator tells you another brief
was accepted, `git fetch origin` and `git merge --no-edit origin/blog-framework` (never rebase),
keep both sides, re-run the suites. Pick E2E instances that `ss -ltn` shows free.

## Item 1: background jobs run once across instances (D4)

**Now:** `ScheduledPublisherHostedService.PublishDuePostsAsync` loads due posts as tracked
entities and saves them; two API instances both do it (`Services/ScheduledPublisherHostedService.cs`
lines 28-40). `PostSummaryBackfillService` likewise.
**Do:** each job tick runs inside a transaction holding `pg_try_advisory_xact_lock(<stable job
id>)` (one id per job, constants in one place); an instance that doesn't get it skips the tick.
The publisher's write becomes conditional: `ExecuteUpdateAsync` on
`Status == Scheduled && PublishedAt <= now` and logs/returns only the rows *it* changed, so a lock
bug still can't publish twice. Same idea for the backfill (only rows still missing a summary).
Tests (API, Testcontainers): two publisher instances run concurrently against one database with 20
due posts, each post published exactly once and logged once in total; the lock: while one
instance holds it, the other's tick returns "skipped". Break-it-once: remove the lock; the "logged
once" test fails. Restore the unconditional save; the double-publish test fails.

## Item 2: migrations, one at a time and compatible with the release before (D5)

- **Concurrency:** a test starts two `efbundle` (or `Database.MigrateAsync`) runs against one empty
  database at once: both exit 0 and the schema is migrated once. EF Core 9+ is documented to lock
  around migrations; confirm it for Npgsql here. If it fails, wrap migration in an advisory lock
  (the init container then runs a small `migrate` entry in the API instead of the raw bundle) and
  say so.
- **Expand/contract rule** in AGENTS.md (Conventions): a migration may add tables, nullable
  columns, columns with defaults and indexes; it may not drop, rename, or make NOT NULL anything
  the previous release reads. Removals ship one release later.
- **Guard test** (API tests, reading the compiled migrations): any migration newer than this
  brief's that calls `DropTable`, `DropColumn`, `RenameColumn`, `RenameTable`, or `AlterColumn`
  with `nullable: false` on an existing column fails, unless the migration class carries
  `[ContractMigration("why it is safe now")]`. `InitialCreate` and earlier are exempt.
Break-it-once: add a throwaway migration with `DropColumn`; the guard fails (delete it again).

## Item 3: the Kubernetes base for two of everything (D7)

In `k8s/`: Web and API `replicas: 2`; `RollingUpdate` with `maxUnavailable: 0`, `maxSurge: 1`
(remove the `Recreate` comments' reasons that no longer hold, keep the facts);
`topologySpreadConstraints` on `kubernetes.io/hostname` with `whenUnsatisfiable: ScheduleAnyway`;
a `PodDisruptionBudget` (`minAvailable: 1`) for each; `preStop` `sleep 10` and
`terminationGracePeriodSeconds: 40` on both; the media PVC `ReadWriteMany`; Web readiness and
liveness on `/health` **only if** Web has one (it doesn't yet: 08a adds it; leave a commented
block naming 08a). The Data Protection keys volume stays as it is (08a replaces it). Document in
`k8s/README` (or the base's comments) that sticky sessions are the ingress's job (the Traefik
example: `sticky.cookie` with `httpOnly`, `secure`, `sameSite: lax`), and how a single-instance
site overlays `replicas: 1`, `ReadWriteOnce`, `Recreate`.
Tests: `K8sBaseTests` gain assertions for each of the above (replicas, strategy, PDB per app,
spread constraint, RWX, preStop and grace period). Break-it-once: set `maxUnavailable: 1`; its test
fails.

## Item 4: a local HA stack and the API-side HA tests

`dev/ha-local.sh up|down`: two API and two Web instances against **one** database (a fresh
`blog_ha` each `up`, migrated once, the seed script run once), behind a local Traefik container
(file provider; rootless Podman; reach the host processes via `host.containers.internal` or host
networking, whichever works here; say which) that load-balances each app round-robin, with a
sticky cookie on Web. Ports in a block the agent and verify ranges don't use (pick and document).
The media folder is shared by both API instances (one directory, standing in for RWX).
`dev/ha-kill.sh web|api N` kills one instance by its pid file.

`HaTests` (E2E, a separate test category that `dev/run-e2e.sh` does not run by default; a
`dev/run-ha-e2e.sh` runs it against `ha-local`):
- **Readers through an API failure:** a loop requests `/`, a post, `/feed.xml` and one image every
  200 ms through Traefik while `ha-kill.sh api 1` runs: zero non-2xx responses. Same while killing
  Web 1 (a reader's next request lands on Web 2).
- **Scheduled publishing across instances:** schedule 10 posts 30 s ahead through the API; after
  they're due, each is published exactly once (the two API logs together contain each post's
  "Published scheduled post" line once).
- **Uploads visible from both API instances:** upload through one, fetch through the other
  (bypassing Traefik, by port): same bytes.
Signed-in and editor HA tests are brief 08a's (they need its shared token store).
Break-it-once: point the two API instances at separate media folders; the uploads test fails.

## Out of scope

Anything in `Croft.Web` (08a), the site repository and cluster (08c), object storage, Redis.
