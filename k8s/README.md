# The kustomize base

Plain Kubernetes with placeholder values: a site overlays this folder (`resources: <this
folder>` plus patches) and sets its host, image tags, config values and whatever its cluster
needs. The root README, "Deploying a site", covers a full deployment; this file covers what
the base assumes about running **two of everything**.

## Images

The Deployments name the images by their bare names; an overlay maps them with `images:`.

| Name in the base | Released image (GHCR, see the root README, "Releases") | crawford.dev today |
|---|---|---|
| `croft-api` | `ghcr.io/stevedcc/croft-api:X.Y.Z` | its own builds from `registry.crawford.dev`, until the site repo switches |
| `croft-web` | `ghcr.io/stevedcc/croft-web:X.Y.Z` | the same |

```yaml
images:
  - name: croft-api
    newName: ghcr.io/stevedcc/croft-api
    newTag: X.Y.Z
  - name: croft-web
    newName: ghcr.io/stevedcc/croft-web
    newTag: X.Y.Z
```

## Sticky sessions are the ingress's job

Blazor Server circuits live in the Web pod's memory, so the ingress should keep a browser on
one pod. The base cannot express that — affinity is ingress-specific and plain Kubernetes has
no sticky-cookie field — so it is documented here and configured in the site's ingress. The
Traefik example, on the `blog-web` service:

```yaml
services:
  blog-web:
    sticky:
      cookie:
        httpOnly: true
        secure: true
        sameSite: lax
```

With the Web database — which this base configures — affinity is for efficiency only: the token
store and the Data Protection keys are shared, so any pod can serve any signed-in request, and
losing affinity only drops the circuit (which the editor's local draft backup covers). Only a
no-WebDb deployment (the single-instance shape below) keeps sessions in one pod's memory, where
affinity is moot because there is one pod.

## The Web database

The Web app uses a small Postgres database (`ConnectionStrings:WebDb`) when one is configured: it
holds the state several Web pods must share (token sessions and Data Protection keys), which is
what lets any pod serve any signed-in user. The base is the HA shape and configures it: the Web
`migrate` init container and the `web` container both read `ConnectionStrings__WebDb` from the
`croft-web-db` Secret (see `secrets.example.md`), so the base needs that Secret created before
it deploys. The `migrate` entry runs the Web app's migrations one chain at a time under a
Postgres advisory lock, so pods starting together migrate the schema exactly once.

Without `WebDb` the app runs with no database at all — in-memory sessions, file Data Protection
keys — and the `migrate` entry is a successful no-op. That is a real shape, not a failure mode,
but it is not what this base deploys: the single-instance overlay below deletes the env and the
init container, and with them the need for any Secret.

## Database connections

Croft.Api sizes its own pools and routes public reads; three settings do it, and a site sets them
like any other config (env, appsettings, ConfigMap):

| Setting | Default | Meaning |
|---|---|---|
| `Database:MaxPoolSize` | `20` | Npgsql `Maximum Pool Size` for every Croft.Api database connection string (primary and read) |
| `Database:MaxAutoPrepare` | `0` | Npgsql `Max Auto Prepare` (0 = off, Npgsql's default) |
| `Database:ReadHost` | unset | Host of the read replicas. Unset: everything uses `ConnectionStrings:BlogDb` exactly as before |

The two pool settings take integers (`MaxPoolSize` ≥ 1, `MaxAutoPrepare` ≥ 0); a value that is
not one stops the pod at startup naming the setting, so a typo never becomes a mystery 500 per
request.

`Database:MigrateTimeout` (a time span, default `00:05:00`; read by the `migrate` init containers
of both Croft.Api and Croft.Web) bounds how long `migrate` keeps retrying a database that is away
(for example during a restart): it retries after 1, 2, 4, 8, 16, then every 30 seconds, and at the
timeout logs one error and exits with code 1, so Kubernetes restarts the init container. A
migration that fails is not retried: it exits with code 1 at once. An invalid value is logged
and exits with code 1.

`Database:ReadHost` is setup-neutral: it can be any host name — a Kubernetes Service, a Docker
Swarm service, a managed Postgres reader endpoint (an Aurora reader, for example), pgpool or
HAProxy — or an Npgsql multi-host list (`db1,db2,db3`), since Croft replaces only `Host` in
`ConnectionStrings:BlogDb`. Port, database, user and password all come from `BlogDb`, so the
replicas must share them. Unset — the default, and what dev uses — sends everything to `BlogDb`.

Budget the connections per Postgres instance before raising any of the three:

```
API pods × MaxPoolSize × (1, or 2 with ReadHost)
  + Web pods × its WebDb pool
  + 2 listener connections
  < max_connections − 3
```

The `2 listener connections` term is one cache-listener connection per API pod (two in the
example below — count one per API pod in any other shape) on top of the pools, and the read
pool's connections land on the replicas, not the primary: with `ReadHost` set the primary pays
one pool per API pod and each replica pays one pool per API pod. The 2026-10-15 load test (2
API + 2 Web pods, `max_connections` 100) is the worked example: Npgsql's default pool of 100
per process broke the server at ~55 requests/s with `53300 too many clients`; capping the pool
at 30 per pod (the site patched the connection strings to do it — `Database:MaxPoolSize=30`
now) moved the limit to ~115–120 requests/s, and routing the anonymous reads to the replicas
takes the read pools off the primary entirely.

## Blazor circuits and memory

Only staff hold a Blazor circuit. The reading pages (home, post, category, tag, categories, static
pages, search, not-found and error) are static server-side rendering: a reader opens no circuit, no
WebSocket and holds nothing in the Web pod's memory beyond the request, however many readers arrive.
Circuits belong to the admin area and the editor, and a circuit start from a request that is not
signed in is refused (`CircuitAdmissionMiddleware`). So circuit memory is sized by how many staff
(and their reloads) the pod serves at once, not by the site's traffic. A member of staff who reloads
leaves the old circuit behind: a reload never reconnects to its old circuit, so retaining it is pure
cost beside the live ones.
Microsoft's sizing formula ("Manage memory in deployed server-side Blazor apps") is:

```
memory = (active circuits + disconnected circuits) × per-circuit memory
```

Three settings bound the terms, and a site sets them like any other config:

| Setting | Default | Meaning |
|---|---|---|
| `Croft:Web:Circuits:DisconnectedMaxRetained` | `20` | `CircuitOptions.DisconnectedCircuitMaxRetained`: how many disconnected circuits one process keeps for reconnects |
| `Croft:Web:Circuits:DisconnectedRetentionSeconds` | `60` | `CircuitOptions.DisconnectedCircuitRetentionPeriod`: how long one is kept before it is closed |
| `Croft:Web:Circuits:MaxActive` | `400` | circuits per process (connected + disconnected); at or above it new circuits are refused. `0` = no limit |

The values take integers (`≥ 0`); a value that is not one stops the pod at startup naming the
setting, like the `Database:*` settings. `MaxActive` is an admission gate, not a kill switch:
at the ceiling a request that would start a new circuit gets `503` with `Retry-After: 5` (one
warning line per refusal in the pod's log), the member of staff keeps the prerendered page (readable,
just not interactive), and reconnects of existing circuits go through whatever the count is.
It gates browsers starting circuits, not a hard process cap: a crafted client that sends any
`id` query is passed on to SignalR (which rejects unknown ids), and page loads stay behind the
request rate limiter. The `croft.web.circuits.active` gauge (Croft.Web meter; connected plus
disconnected circuits, per process) is the number to watch and to alert on — "at `MaxActive`"
is the alert threshold.

The 2026-10-15 reload-churn measurements (taken when every reader still held a circuit; they are the
numbers behind these defaults, and per-circuit cost is the same for staff): about **0.9 Mi per
idle circuit**, and a plateau of about **850 Mi per pod** under 20–37.5 reloads/s with the
pre-09g defaults (100 disconnected circuits retained per server for 3 minutes); a fresh
pod runs 76–79 Mi with Workstation GC and 167–189 Mi with Server GC. The defaults above (20
retained for 60 s, 400 circuits) put the same burst at a fraction of that plateau — estimated
from the retention cuts (5× fewer retained, 3× shorter window), not re-measured.

Rule of thumb for `MaxActive` from the Web pod's memory limit, rounding the measured 0.9 Mi per
circuit up to 1 Mi and keeping ~100 Mi for the baseline:

```
MaxActive ≈ (memory limit − 100 Mi) / 1 Mi per circuit
```

So the default 400 is right for a 512 Mi pod; halve it for a 256 Mi pod (`≈ 150`), and a 1 Gi
pod can keep 400 and still have most of its limit free; with staff-only circuits the default is
generous for any single-owner site. Size it from what the pod is actually allowed, not from the
node: a burst above `MaxActive` costs the newest staff session interactivity for a moment (they can
still read), while a burst above the pod's memory limit is the OOM-kill outage the gate exists to
prevent.

Garbage collection: **Workstation GC is the built default** (`ServerGarbageCollection=false` in
`src/Croft.Web/Croft.Web.csproj` — nothing in the base or any overlay sets `DOTNET_gcServer`,
so the built default is what runs). The 2026-10-15 OOM kills came from memory outside the GC
heap — which a container memory limit already caps at 75% of the limit — and Workstation GC's
smaller baseline leaves more of the limit for circuits. A site whose Web pods run sustained
CPU-bound rendering on many dedicated cores and measure GC pauses as the bottleneck can switch
to Server GC explicitly with `DOTNET_gcServer=1` (an environment variable overrides the built
runtimeconfig); size `MaxActive` from the larger Server-GC baseline if it does.

## Running a single instance

A site that wants one of everything overlays the base with `replicas: 1`, `ReadWriteOnce`
volumes and the `Recreate` strategy — one pod is all those volumes allow — and with no Web
database: sessions stay in the pod's memory and the Data Protection keys go back to a file ring
on a `ReadWriteOnce` claim. The overlay below is the whole shape: it deletes the WebDb env and
the `migrate` init container (so no `croft-web-db` Secret is needed) and puts the keys volume
back.

```yaml
# the site's blog-web-keys claim (a file in the overlay's folder, listed under resources:)
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: blog-web-keys
  namespace: blog
spec:
  accessModes: [ReadWriteOnce]
  resources:
    requests:
      storage: 100Mi
---
# the site's single-instance overlay (kustomization.yaml)
resources:
  - blog-web-keys-pvc.yaml   # the claim above
patches:
  - target: { kind: Deployment }
    patch: |-
      - op: replace
        path: /spec/replicas
        value: 1
      - op: replace
        path: /spec/strategy
        value: { type: Recreate }
  - target: { kind: PersistentVolumeClaim, name: blog-media-pvc }
    patch: |-
      - op: replace
        path: /spec/accessModes
        value: [ReadWriteOnce]
  # No WebDb anywhere: the env and the migrate init container go (with no database there is
  # nothing to migrate), so the site needs no croft-web-db Secret at all.
  - target: { kind: Deployment, name: blog-web }
    patch: |-
      spec:
        template:
          spec:
            initContainers:
              - name: migrate
                $patch: delete
            containers:
              - name: web
                env:
                  - name: ConnectionStrings__WebDb
                    $patch: delete
  # File keys again: DataProtection__KeysPath and the keys volume the base carries no more.
  - target: { kind: Deployment, name: blog-web }
    patch: |-
      spec:
        template:
          spec:
            containers:
              - name: web
                env:
                  - name: DataProtection__KeysPath
                    value: /keys
                volumeMounts:
                  - name: keys
                    mountPath: /keys
            volumes:
              - name: keys
                persistentVolumeClaim:
                  claimName: blog-web-keys
```

With one replica, `minAvailable: 1` can only block node drains, so the single-instance site
deletes the PodDisruptionBudgets (`kubectl delete pdb blog-api blog-web`) unless it wants
that.

## Pod hardening

Every container in both Deployments, the migrate init containers included, runs with
`allowPrivilegeEscalation: false`, `capabilities: { drop: [ALL] }` and
`readOnlyRootFilesystem: true`; both pods set `seccompProfile: { type: RuntimeDefault }` and
`automountServiceAccountToken: false` (no pod talks to the Kubernetes API, so none gets a token).
The writable paths are explicit: an `emptyDir` at `/tmp` in every container (the runtime's
diagnostics sockets, ASP.NET's buffering of large request bodies), the media volume at
`/app/uploads` in the API, and the single-instance overlay's `/keys` volume (above) in the Web;
with the Web database the Data Protection keys are in it and the Web needs no other writable path.
Branding and site files are mounted read-only. A site overlay that adds a container or a mount
keeps the same settings. `K8sBaseTests` pins all of it.

Network policies stay an optional component (`components/network-policies`): they say who may
connect to the pods (the ingress controller to the Web, the Web and the ingress to the API), and
their `from` selectors are specific to a cluster (they assume K3s and its Traefik chart). They add
no egress rules: what a pod may reach (the database, the API, the IdP, a telemetry collector) is
a site fact, so egress policies belong in the site overlay.

The component **assumes Traefik in `kube-system`**: both policies admit the pods labelled
`app.kubernetes.io/name=traefik` in the namespace labelled `kubernetes.io/metadata.name=kube-system`
and nothing else. With another ingress controller, or Traefik in another namespace, nothing can reach
Croft until the site overlay patches the selectors (one JSON 6902 patch per policy; the Traefik item is
`from/0` in `croft-web-ingress` and `from/1` in `croft-api-ingress`, whose `from/0` is the Web pods):

```yaml
patches:
  - target: {kind: NetworkPolicy, name: croft-web-ingress}
    patch: |-
      - {op: replace, path: /spec/ingress/0/from/0/namespaceSelector/matchLabels/kubernetes.io~1metadata.name, value: ingress-nginx}
      - {op: replace, path: /spec/ingress/0/from/0/podSelector/matchLabels/app.kubernetes.io~1name, value: ingress-nginx}
  - target: {kind: NetworkPolicy, name: croft-api-ingress}
    patch: |-
      - {op: replace, path: /spec/ingress/0/from/1/namespaceSelector/matchLabels/kubernetes.io~1metadata.name, value: ingress-nginx}
      - {op: replace, path: /spec/ingress/0/from/1/podSelector/matchLabels/app.kubernetes.io~1name, value: ingress-nginx}
```

(`~1` is the JSON Pointer escape for `/` in a label key.) Change the `ForwardedHeaders` network
(see "Client addresses") to the new controller's pod network at the same time.

## Client addresses

Only the ingress decides a visitor's address. The Web trusts `X-Forwarded-For` and
`X-Forwarded-Proto` only from the peers in `ForwardedHeaders:KnownNetworks` (a CIDR list, set
through `ForwardedHeaders__KnownNetworks__0` in `blog-web-config`), takes one hop (the entry the
proxy appended is the client) and handles the headers once, first in the pipeline. The base sets
`10.42.0.0/16`, the k3s default pod network, where K3s's Traefik runs: **change it for another
cluster** (the ingress controller's pod network). An empty or unset list trusts no proxy and the
headers are ignored (local development). The pod network is wider than the ingress controller
alone: any other pod in it could still set an address, which is why the optional network policies
(above) belong in any cluster that runs other workloads. The API handles no forwarded headers at
all: its visitor address comes only from `X-Visitor-IP` (honoured with the analytics ingest key,
and only when it parses as an IP address) or the socket peer. Never set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`: it clears the trusted networks and trusts every peer.

Example: a layer-4 load balancer in front of the ingress. The balancer sends the PROXY protocol to
the ingress controller, which reads it, learns the visitor's address and writes it into
`X-Forwarded-For` on the request it forwards (for Traefik, the entry point accepts PROXY protocol
from the balancer's addresses and the controller adds the header). The pod the Web sees is the
ingress controller's, in the pod network, so the setting stays exactly as above: trust one hop from
the ingress controller's pod network, and nothing from the balancer's address, which no pod ever
connects from. The Web never sees the PROXY protocol itself and must not be given the balancer's
network in `KnownNetworks`: a client could then claim any address through the balancer's own hop.

## Encrypting the key ring

The Web stores each session's access, refresh and ID tokens protected with ASP.NET Core Data
Protection, so a plain copy of the `UserSessions` table is not enough to use them. The key ring
that protects them is in the same Web database, though: **without a certificate a full copy of the
Web database (a backup, a replica, a stray `pg_dump`) still yields the keys and the tokens
together.** With an RSA certificate configured, the keys are stored encrypted under it and the
private key lives only in a Secret, so a database copy alone yields no usable key.

The base mounts the optional Secret `croft-dataprotection-cert` (type `kubernetes.io/tls`) read-only
at `/etc/croft/dataprotection`, and `blog-web-config` points `DataProtection__CertificatePath` /
`DataProtection__CertificateKeyPath` at its `tls.crt` and `tls.key`. A site without the Secret still
starts: the Web logs one Warning that the key ring is stored unencrypted. See
`secrets.example.md` for how to issue one.

- **RSA only.** The key ring is encrypted with EncryptedXml, which cannot use EC keys: an EC or
  unreadable certificate fails startup, naming RSA. A certificate file that exists next to a missing
  key file fails startup too, and so does a mount directory that holds other files (a Secret under other
  key names) or previous pairs without a current one; only both files absent from an absent or empty
  directory means "no certificate".
- **Any RSA certificate works.** Data Protection checks neither the chain nor the expiry. Prefer one
  nothing accepts for TLS: no `clientAuth` or `serverAuth` extended key usage, and not *no* extended
  key usage either (Go treats that as "any"), for example a self-signed certificate or a CA
  provisioner with a `keyEncipherment`-only template. Make the key locally; never let the CA
  generate it.
- **Rotation.** Put the new pair in the Secret and move the old pair to a second Secret (or keep it
  in the same one under other names), listed as `DataProtection__PreviousCertificatePaths__0__CertificatePath`
  and `DataProtection__PreviousCertificatePaths__0__CertificateKeyPath` (and `__1__`, ...): keys that
  were encrypted under it still read. Expiry is **not** checked when decrypting, so renew yearly and
  keep the old pair until the keys it encrypted have expired (a key lives 90 days and is
  replaced; sessions and cookies protected by it end with their own lifetime).
- **Upgrade.** Sessions stored in clear text by an earlier release cannot be read once tokens are
  protected: each signed-in user signs in again once.

## Health and probes

Both apps answer `/health/live` and `/health/ready`; each Deployment probes liveness on the live
endpoint and readiness on the ready one.

- **API.** `/health/live` is the process alone (no check runs). `/health/ready` opens a fresh
  connection to the **primary** database and runs `SELECT 1`, giving up after 2 s (the
  readiness probe's `timeoutSeconds` is 5, the kubelet's default of 1 s would be shorter than
  the bound): 200 when it answers, 503 when the connection cannot be opened or the query does not
  answer. `/health` is an alias of `/health/ready`, kept for older Web pods and monitors.
- **Web.** `/health/live` is the process alone. `/health/ready` is 200 only when the Web database
  (when one is configured; sessions and key rings live there) and the API's `/health` (its
  readiness, so the API's database too) each answer within 2 s.
- **Both, at start:** `/health/ready` also stays 503 ("Warming up") until the pod's warm-up has
  finished (see "Warm-up" below). `/health/live` never waits for it.

So a database or API outage takes the pods out of **service** (they answer unready, the
Service stops sending them traffic) but never **restarts** them: liveness asks no dependency,
a restart cannot fix one. The consequence to accept: while the primary database is unreachable the
API pods are unready, so the Web pods are too, and readers get no pages even though the API's
cache could still answer them. That is the deliberate reading of "ready" (a pod that cannot
reach what it must serve from takes no traffic); the pods recover by themselves the moment the
database does.

## Warm-up

A pod is warm before it takes readers. After a deploy or any pod restart, the first requests to the
home page and a post used to take 1 to 3.4 s, against 0.03 to 0.15 s once the pod had served a few
(measured on 0.6.5 in production: `/` 2.7, 1.2, 2.4, 1.3 s then 0.09 to 0.15 s; a post 3.4, 1.7,
1.8 s then 0.11 to 0.16 s). SQL was never the cause (20 to 340 ms in the worst case). The time is
first-use work: on each API pod the first call of each endpoint spent 1.3 to 2.3 s in managed code
(EF Core's model build and query compilation, first-use JIT that ReadyToRun cannot cover), and on
each Web pod the first render of each page type spent 0.5 to 1.6 s. It is not CPU starvation (the
site sets no CPU limit and the nodes had cores idle): the work is single-threaded and the arm64
cores are slow. The pods used to report ready long before that work was done, so readers paid for it.

Now each process warms itself up **after Kestrel is listening and before `/health/ready` says
ready**: it sends its own public read requests to itself over loopback, once each and one at a time
(the Web its page types: the home page, the sitemap, a post and its image, a static page, a category,
a tag, `/categories`, a search and the feed; the API the same query shapes the Web asks it for), then
opens the readiness gate. Every API pod warms itself, because the Web's warm-up reaches only the API
pods the Service happens to pick. The requests are plain anonymous GETs, so they are never counted as
page views (only the browser's beacon counts), and they hold one request slot at a time, so the
concurrency limits need no exemption. All of a warm-up's requests run inside one trace named
`croft.warmup` when telemetry is on.

Three settings, the same for both apps (`blog-api-config` and `blog-web-config` in `configmap.yaml`
carry them at their defaults):

| Setting | Default | Meaning |
|---|---|---|
| `Warmup__Enabled` | `true` | `false` skips the warm-up: the gate starts open. |
| `Warmup__Timeout` | `00:01:00` | The whole warm-up's budget. When it runs out the gate opens regardless. |
| `Warmup__RequestTimeout` | `00:00:15` | Each request's budget; a slower request counts as failed and the warm-up goes on. |

The gate never blocks forever: when the warm-up finishes, fails or runs out of time, the pod becomes
ready. The log gets one Information line (`Warm-up finished in N ms: X of Y requests succeeded`) and,
when anything failed or the budget ran out, one Warning naming each failed or not-reached path with
its status code or exception type. Content that does not exist (an empty site has no post) is skipped,
not failed, and logs no Warning. The pod's other readiness checks are unchanged: the Web still needs
its database and the API, the API still needs its database.

**A rollout now takes about the warm-up's time longer per pod** (a few seconds to a few tens of
seconds, bounded by `Warmup__Timeout`; the readiness probe polls every 10 s, so a pod joins at most
10 s after its warm-up). `maxUnavailable: 0` keeps the old pods serving until the new ones are ready,
so readers see no gap. The warm-up rides on **readiness, not liveness**: a restart cannot speed it up,
so `/health/live` never waits for it and the probes' timings stay as they are.

## Output cache

An anonymous reader's page comes from the Web pod's memory: no API round trips and no Markdown
render. The Web app keeps the reading pages (`/`, `/post/{slug}`, a static page at `/{slug}`,
`/category/{slug}`, `/tag/{slug}` and `/categories`) in ASP.NET Core's output cache. What is cached:
a `GET` that is not signed in, answered with a 200 and no `Set-Cookie`, for one of those routes.
Search, the not-found and error pages, the admin pages, the health endpoints and everything a
signed-in user asks for are never cached. The key is the path plus the query parameters the page
reads (`page` on the list pages): `?utm_source=x` is the entry of `/`, and the host and scheme are
not part of the key, so the warm-up (which asks over loopback) fills the entry readers get. The
cached response replays the headers the page was rendered with (the CSP is hash-based and has no
nonce, which is what makes the pages cacheable), and the pages send no `Cache-Control` of their own:
someone who signs in must not get their browser's copy of the anonymous page. The feed and the
sitemap go through the same cache for `FeedDuration` and send `Cache-Control: public, max-age=300`
(the same for every caller).

After an edit or a publish readers see the change **within one minute**: every cached entry is tagged
`content`, and a staff write through the Web app evicts that tag on the pod that handled it, so
that pod (and the staff member) see it at once. The other Web pods catch up when their entries expire,
at most `Duration` later. There is no cross-pod signal; a minute of staleness is the accepted
trade-off.

Five settings (`blog-web-config` in `configmap.yaml` carries them at their defaults):

| Setting | Default | Meaning |
|---|---|---|
| `Croft__Web__OutputCache__Duration` | `00:01:00` | How long a reading page is kept. `00:00:00` turns page caching off. |
| `Croft__Web__OutputCache__FeedDuration` | `00:05:00` | How long the feed and the sitemap are kept; their `max-age` follows it. `00:00:00` turns it off. |
| `Croft__Web__OutputCache__SizeLimitMegabytes` | `32` | The memory the cache may hold. |
| `Croft__Web__OutputCache__MaximumBodyKilobytes` | `1024` | The largest page that is cached; a larger one is served whole, not truncated, and not cached. |
| `Croft__Web__OutputCache__MediaMaximumBodyKilobytes` | `512` | The largest image that is cached (below); a larger one is streamed whole, not truncated, and not cached. |

Images (`/api/media/{id}`, the Web app's proxy to the API) are kept the same way, so only the first
request for a published image costs the API a database lookup and a file read. What is cached: a
request that is not signed in, which the API answered with a 200 and `Cache-Control: public`. A draft's
image (the API says `no-store`), anything a signed-in staff member asks for, any other status, the
proxy's own 503 and an image above `MediaMaximumBodyKilobytes` (or one whose length the API did not
say) are streamed through and never kept. The key is the id (`?v=1` and `?v=2` are one entry), the
entry carries the same `content` tag (a staff write, a media delete included, drops it on that pod)
and lives for `Duration`, so an unpublished post's images are gone from every pod within that minute.
The API's `Cache-Control` still reaches the browser unchanged.

A page that was changed behind the Web app's back (a direct API write, a database edit) shows after
`Duration` (the feed and the sitemap after `FeedDuration`). The E2E suite does exactly that, so
`dev/run-e2e.sh` starts its instances with both caches off, and runs the `OutputCache` tests in a pass
of their own on instances with them on.

The documents the cache may store (an anonymous reader's reading pages, the feed and the sitemap)
are sent compressed: Brotli if the request accepts `br`, else gzip, else plain. The compression runs
inside the cache, so an entry holds the compressed bytes and a hit compresses nothing; there is one
entry per page and encoding (`br`, `gzip`, plain), and the responses carry `Vary: Accept-Encoding`.
Nothing else is compressed by the Web app (no staff page, nothing signed in, no image; static files
come precompressed). Compression stays on when the cache is turned off, then once per request.

## Site files (well-known files)

The site publishes small well-known files (`security.txt`, WebFinger, Apple's AASA and similar)
from a mounted folder without rebuilding Croft: `Blog:SiteFilesPath`, default `/app/site-files`.
Each file becomes a public URL at its own path relative to the folder. **Allowed files
only** (`.txt`, `.json`, `.well-known/apple-app-site-association`, `.well-known/webfinger`); a
disallowed file, a 64 KiB+ file or a shadowing path fails startup. Symlinks (a Kubernetes
ConfigMap's `..data/` entries) work because the service reads the bytes once at startup.

The base already declares the `site-files` volume (the optional ConfigMap `blog-site-files`) and
mounts it read-only at `/app/site-files`, exactly like `blog-branding`, so it deploys alone with
no site files. A site that wants to publish some adds the ConfigMap in its overlay.

A ConfigMap key cannot contain `/`, so a key can only name a file at the top of the folder
(`humans.txt` is served at `/humans.txt`). Files that live below a folder, such as
`.well-known/security.txt` and `.well-known/webfinger`, are projected to their path with `items`
on the volume. This overlay (a `configMapGenerator`, so a change rolls the pods) publishes
`/humans.txt`, `/.well-known/security.txt` and `/.well-known/webfinger`:

```yaml
# the site overlay's kustomization.yaml (files sit next to it: humans.txt, security.txt, webfinger)
configMapGenerator:
  - name: blog-site-files
    namespace: blog
    files:
      - humans.txt
      - security.txt
      - webfinger
patches:
  - target: { kind: Deployment, name: blog-web }
    patch: |-
      apiVersion: apps/v1
      kind: Deployment
      metadata:
        name: blog-web
      spec:
        template:
          spec:
            volumes:
              - name: site-files
                configMap:
                  items:
                    - { key: humans.txt, path: humans.txt }
                    - { key: security.txt, path: .well-known/security.txt }
                    - { key: webfinger, path: .well-known/webfinger }
```

The `site-files.example/` folder in the repository holds example contents for these files.

The ConfigMap's **1 MiB limit** is the hard upper bound on total size; keep individual files
under the app's 64 KiB-per-file cap and split across multiple ConfigMaps if the total grows.
A change to the ConfigMap rolls the pods (a ConfigMap update bumps the `configMapGenerator`
hash), which is what the brief calls for: the service reads once at startup and never re-reads.

## Telemetry

Both apps export **metrics**, **traces** and **logs** to an OpenTelemetry collector when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. Without it nothing is exported and nothing changes. The
base deliberately does not set the endpoint: a site that runs a collector adds it to the
deployments (or its overlay) — for example, a collector in the cluster's `observability`
namespace listens at:

    OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector.observability:4317

The base sets `OTEL_SERVICE_NAME` (`croft-web` on the Web Deployment, `croft-api` on the API
one — the exported resource's `service.name`) and `OTEL_RESOURCE_ATTRIBUTES` with
`k8s.pod.name` and `k8s.namespace.name` taken from the downward API, so every exported signal
carries the pod and namespace it came from. Every other standard `OTEL_*` variable (sampling,
per-signal exporters, extra resource attributes) works as OpenTelemetry documents it.

The API's `Croft.Api` meter carries the two mechanisms that keep the public site up (the
per-instance read cache and its LISTEN/NOTIFY listener) next to the page-view and publish counters:

| Instrument | Meaning |
|---|---|
| `croft.cache.requests{outcome=hit\|miss}` | Public read cache lookups. A falling hit ratio after an edit is expected; a permanently low one is not |
| `croft.cache.invalidations{group=categories\|tags\|posts\|pages\|discussion\|all}` | Groups dropped on this instance; `all` is the whole cache cleared, which the listener does on every (re)connect |
| `croft.cache.refills{source=primary\|default}` | Database reads behind misses. `primary` is a refill forced to the primary because its group was invalidated within `Croft:Api:Cache:PrimaryAfterInvalidation`; `default` is the normal routing (the read replica for an anonymous read when `Database:ReadHost` is set) |
| `croft.cache.listener.connected` | Gauge: cache listeners connected to Postgres (1 per process when healthy). 0 means edits reach this instance only by the 5-minute expiry |
| `croft.cache.listener.reconnects` | Reconnects after a lost connection (each clears the whole cache, the latency spike on one pod); the first connect is not counted |

Privacy rule (the site's privacy statement): no exported span, metric or log attribute carries
the client IP address, a token, a cookie value, an email address or a query-string value —
search terms are reader input and stay inside the site. Health probes are never traced. The span of
an avatar download (social replies) exports its host alone, because the path of an avatar can carry a
handle; neither the logs nor the traces carry a reply's author or text.

## Content export (the second backup layer)

The API image has two console commands next to `migrate`. `export` saves the site's content as
plain files that need no database to read, diff or recover a single post from:

    dotnet Croft.Api.dll export --out /backup/content

It writes `manifest.json`, `categories.json`, `tags.json`, `media.json` (metadata only; the media
files themselves are the file backup's job), and for every post and page (drafts and scheduled
included) a `posts/<slug>.json` or `pages/<slug>.json` with every field plus the Markdown body
beside it as `<slug>.md`, which the JSON names in `markdownFile`. Two exports of unchanged content
are byte-identical except `manifest.json`'s `exportedAt`. Page views are not exported (analytics,
their own retention).

A site runs it as a CronJob: the API image, the `blog-db` Secret the API's own containers read (host, port, dbname, user, password, assembled
into `ConnectionStrings__BlogDb` exactly as in `api-deployment.yaml`), `args: ["export", "--out", "/backup/content"]`, and a volume
mounted at `/backup` that the site's backup tool (restic, for example) picks up. Mount the volume
**above** the output folder, as in the example: the command writes `<out>.tmp-<random>` next to
the output and swaps it in, so a crash never leaves a half export where the last good one was,
and a folder that is itself a mount point cannot be swapped. The next export clears
`<out>.tmp-*` / `<out>.old-*` leftovers of a crashed run and, if a crash between the two renames
left no `<out>`, restores the last good export from `<out>.old-*` first (run one export at a time:
`concurrencyPolicy: Forbid`).

The export only reads: one `REPEATABLE READ`, read-only transaction, no writes, no migration, no
advisory lock. So the CronJob may set `DB_HOST` to the `-r` (replica) service (CloudNativePG: `<cluster>-r`)
and leave the primary alone. If the database is away it retries until `Database:MigrateTimeout`
(default 5 minutes), then exits with code 1 so the Job fails visibly; any other error also exits
with code 1 and leaves the previous export in place.

To rebuild an **empty** database from those files (after `migrate`), run the same image with
`args: ["import", "--from", "/backup/content"]`. It refuses a database that already has any
post, page, category, tag or media row (exit code 1, nothing changed: it never merges or
overwrites), checks the whole export first (format version, every `.md` file named by a JSON, the
manifest counts) and writes in one transaction, so a bad export changes nothing. Ids, slugs,
timestamps, statuses, view counts, feedback and relations come back exactly; the search index is
rebuilt by the database. Media rows are restored as metadata; each file missing under
`FileStorage:BasePath` is logged as a Warning, and the files themselves come back from the file
backup.
