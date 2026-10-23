# High availability: two of everything, no lost sessions, no lost words

Status: draft for the owner, 2026-10-11. Replaces open action "Brief 03b" (R5).

## Goal (what the owner sees)

1. **Rollouts and node maintenance are invisible.** `kubectl rollout restart` of Web or API, or
   draining any one node, causes no failed request for a reader and no sign-out for a signed-in
   admin. Measured: a reader probe (one request every 200 ms to `/`, a post, `/feed.xml`, an image)
   and an admin probe (an authenticated admin page every second) during each event: zero non-2xx,
   zero redirects to sign-in.
2. **No lost writing.** If the pod serving the editor dies mid-edit, the text survives: the page
   reconnects (or reloads) with every character typed up to the moment of loss.
3. **Background work happens exactly once.** A scheduled post is published once, not once per API
   pod; the summary backfill does each post once.
4. **Croft still runs as a single instance** with no extra infrastructure (a hobby site keeps
   today's setup); HA is configuration, not a fork.

## Today (checked 2026-10-11, `blog-framework` 8dd9b42)

| Concern | Now | Breaks with 2 replicas because |
|---|---|---|
| Login tokens | `InMemoryTokenStore` (`Croft.Web/Services`), singleton, sync `ITokenStore` | the other pod has no entry for `blog_sid` → signed out; back-channel logout clears one pod |
| Token refresh | one refresh per session, in-process | two pods refreshing one rotating refresh token → the loser's token is revoked → sign-out |
| Data Protection keys | `PersistKeysToFileSystem` on `blog-web-keys` (RWO, Longhorn) | second pod can't mount it, or makes its own keys → cookies unreadable across pods |
| Blazor circuits | one pod | a circuit lives in one pod's memory: needs session affinity; pod loss drops it |
| Editor text | only in the circuit; no autosave (07e) | pod loss = unsaved text lost |
| Uploads | `blog-media-pvc` RWO, `Recreate` strategy | second API pod can't mount it |
| Background jobs | `ScheduledPublisherHostedService`, `PostSummaryBackfillService` in every API pod | run twice |
| Migrations | `efbundle` init container per API pod | two pods starting migrate at once; old pods run against the new schema during a rollout |
| Probes | API: `/health` liveness+readiness; Web: none | Web gets traffic before it's ready; no PDB, no spread |
| Database | CloudNativePG `blog-db`, 3 instances | already HA (CNPG failover); nothing to do but test it |

The Web app has **no database connection** today (it calls the API only).

## Decisions (proposed)

**D1. The Web app gets its own small Postgres database** (`croft_web`, own role, same CNPG
cluster, declared in the site repo). It holds Data Protection keys and token sessions. Reasons:
both must be shared and durable; Postgres is already there and replicated; no new component
(Redis would be one more thing to run and back up). The API's database stays the API's; the Web
role can't read posts, the API role can't read tokens.
- `ITokenStore` becomes async; `PostgresTokenStore` (EF Core, `CroftWebDbContext`) with the
  in-memory store kept as the default when `ConnectionStrings:WebDb` is absent (goal 4).
- **Refresh under a row lock:** the refresh path takes `SELECT ... FOR UPDATE` on the session row,
  re-reads it, and only refreshes if it's still near expiry, so two pods never spend the same
  refresh token. Tested with two concurrent refreshers against Testcontainers Postgres.
- Data Protection: `PersistKeysToDbContext<CroftWebDbContext>()` when `WebDb` is set; files as now
  otherwise. Existing keys are imported once (a `croft-web keys import` command) so the switch
  doesn't sign anyone out.
- Expired sessions are purged by a job (D4's lock).

**D2. Session affinity at the edge, resilience behind it.** Traefik's sticky cookie on the Web
service keeps a browser on one pod (Blazor Server needs it for its circuit). The framework's
generic k8s base can't express that (it's ingress-specific); the base documents it and the site
repo's `traefik.yaml` sets `sticky.cookie` (`httpOnly`, `secure`, `sameSite: lax`). Affinity is
for efficiency only: D1 makes any pod able to serve any signed-in request.

**D3. No lost writing, two layers.**
- **Guaranteed:** the editor keeps a local draft backup in `localStorage` (per post id, the text
  and title, written on every input, debounced 500 ms, from a same-origin JS module, which 07f's CSP
  allows). On load, if the backup is newer than the saved version and different, the editor
  offers "Restore unsaved changes from <time>?" Cleared on save. Works for any failure (pod loss,
  crash, closed tab, laptop sleep).
- **Smoother where it works:** .NET 10 circuit state persistence (`[PersistentState]` on the
  editor's model; `HybridCache` over an `IDistributedCache` on `croft_web`) so a reconnect to
  another pod resumes the editor without a reload. Microsoft documents it as best-effort ("no
  guarantee of recovery"); the spike in brief 08a decides whether it survives an abrupt pod
  kill. If it doesn't, the local backup is the answer and this layer is dropped.
  - **Spike outcome (08a item 6, 2026-10-12): DROPPED — 0/10 SIGKILL trials.** The decision rule
    was "adopt if the editor resumes on the other instance without a reload, with every typed
    character, in at least 9 of 10 SIGKILL trials". Measured on the local HA stack (two Web
    instances behind Traefik, sticky cookie, editor opened through the load balancer, one
    SIGKILL of the instance serving the session per trial, fresh stack per trial):

    | Trial | Killed | Resumed without reload? | Every character? | Outcome |
    |---|---|---|---|---|
    | T1–T10 (SIGKILL) | sticky instance (web1 ×8, web2 ×2) | **no — reload in 10/10** | (after reload, via the local backup prompt) | fail |

    Every trial's framework log says the same: `Resuming circuit with ID …` → `Failed to find
    persisted circuit with ID …` → the reconnect UI's `rejected` state → `location.reload()`.
    The reason is structural, not flaky: circuit state is persisted **only at pause time** — a
    client-initiated pause (state to the browser) or a disconnected circuit's retention expiry
    (state to `HybridCache`). An abrupt kill runs neither; the state dies with the process's
    memory, so there is nothing for the other pod to restore. Two validation trials (SIGTERM,
    graceful) also reloaded (2/2): the graceful shutdown path does not pause circuits either in
    this shape. The layer itself works when a pause happened first — measured separately: an
    ungraceful connection loss → the disconnected circuit is persisted through `HybridCache`
    into `croft_web` after the retention period → SIGKILL of that pod → the surviving pod
    restores the editor from the shared database with every typed character, no reload, fully
    interactive (`Circuit state found for circuit …` in the survivor's log). It just cannot
    cover the case this layer exists for. The local backup (the guaranteed layer above) is the
    answer, and this smoother layer stays dropped; `dev/ha-local.sh` still provisions the shared
    `croft_web_ha` (migrated once through the Web `migrate` entry, `WebDb` for both Web
    instances) for 08a item 7's signed-in HA tests.

**D4. Background jobs take a Postgres advisory lock per run** (`pg_try_advisory_xact_lock(<job
id>)`); the pod that doesn't get it skips that tick. The publisher's update is also made
idempotent (`WHERE status = 'Scheduled' AND publish_at <= now()`), so even a lock bug can't
publish twice. Test: two API instances, 20 scheduled posts, each published exactly once.

**D5. Migrations: one at a time, and always compatible with the release before.**
- Concurrent `efbundle` runs: EF Core 9+ takes a database lock around `Migrate`; confirm for Npgsql
  10 with a test that starts two bundles against one database; if not, wrap the init container in
  an advisory lock.
- **Expand/contract rule** (AGENTS.md): a migration may add tables, nullable columns or columns
  with defaults, and indexes; it may not drop, rename or tighten anything the previous release
  reads. Removals ship one release later. Guard test: new migrations containing `DropColumn`,
  `DropTable`, `RenameColumn` or `AlterColumn(... nullable: false)` fail unless the migration
  carries `[Contract("releases after <version>")]`.

**D6. Uploads on a ReadWriteMany volume** (Longhorn RWX, served by its NFS share manager).
`IFileStorage` is unchanged. Longhorn's share manager is a single pod that Longhorn reschedules
on failure: media reads/writes pause for up to about a minute when its node dies. Accepted for
now; object storage (Garage or MinIO) is the later step if that pause ever matters. Requires an NFS client on every node: check whether the Fedora CoreOS nodes have `nfs-utils`
(unverified) and layer it if not, before the switch.
Migration: a one-off Job copies `blog-media-pvc` to the new RWX claim while the API is scaled to
zero (a short, announced media-write freeze; reads keep working from the old pod until the cut).

**D7. Kubernetes shape (framework base):** Web and API `replicas: 2`, `RollingUpdate`
(`maxUnavailable: 0`, `maxSurge: 1`), `topologySpreadConstraints` across hostnames
(`whenUnsatisfiable: ScheduleAnyway`), `PodDisruptionBudget` `minAvailable: 1` each, readiness
on a Web `/health` (Web gets one: it checks `croft_web` when configured, and the API's `/health`),
`preStop` sleep 10 s plus `terminationGracePeriodSeconds: 40` so endpoints are removed before the
process stops and circuits can be paused (D3). A single-instance overlay
(`replicas: 1`, RWO, `Recreate`) stays possible and is documented.

## Plan

| Brief | Scope | Runs |
|---|---|---|
| **08a** Web state | D1 (Web DB, async token store, refresh lock, keys in DB + import), D3 (local draft backup; circuit-persistence spike, adopt or drop), Web `/health`, single-instance default kept | after 07f (both touch Web `Program.cs`) and 08b (uses its HA stack) |
| **08b** API and base | D4, D5 (lock test, expand/contract guard + AGENTS.md rule), D7 in `k8s/`, `dev/ha-local.sh` (2 Web + 2 API behind a local Traefik with sticky cookies, for HA E2E) | now; parallel with 07f/07g/07h (owns `Croft.Api` jobs, migration tooling, `k8s/`, `dev/ha-*`) |
| **08c** Site repo (orchestrator + owner) | CNPG `Database`/role `croft_web`, its Secret, RWX claim + copy Job, Traefik sticky cookie, PDBs via base, keys import, cut-over runbook | after 08a and 08b are accepted and deployed as 0.3.0 |

**HA E2E (in 08b, run against `dev/ha-local.sh`):** a reader loop and a signed-in admin loop run
while the harness kills one Web, then one API process: zero failures, no sign-in redirect; an
editor session typing into a post while its Web instance is killed: after reconnect or reload,
the text is complete (restored from the backup or resumed). On the cluster (08c), the same probes
run during `kubectl rollout restart` and `kubectl drain` of a node carrying a Web pod, and the
owner watches.

## Decided (owner, 2026-10-11, all three as recommended)

1. D1: a **separate database `croft_web`** with its own role in the same CNPG cluster.
2. D3: restore offers a **prompt**: "You have unsaved changes from <time>. Restore them?" with
   Restore and Discard; never a silent replace.
3. D6: **Longhorn RWX now**, accepting up to about a minute's media pause when the share manager's
   node dies; object storage only if that ever matters.

Order (revised while planning): 08b first (it builds `dev/ha-local.sh`), then 08a after 07f and
08b (its signed-in and editor HA tests need the local HA stack), then 08c.
