# Brief 09g: Blazor circuits can't run croft-web out of memory

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item, TDD,
break-it-once for each key test, E2E twice at the end, the final whole-branch review. Brief 09e
runs at the same time (mobile outline: `post.js`, the contents panel's CSS, new E2E tests); add to
shared files, don't restructure them.

**Owner-visible outcome:** a burst of visitors who arrive, read briefly and leave (a post shared
on a busy site) can make reading slower or the page stay non-interactive for a while, but it can
no longer kill croft-web and drop every reader on that pod.

**Evidence (2026-10-15, `~/sites/observability/loadtest/results/2026-10-15-ignitor-reload-*`,
Ignitor = headless Blazor clients, each action a full page load + new circuit, the old circuit
disposed):**
- croft-web at 512 Mi and Server GC: **both pods OOMKilled** at 10–20 reloads/s. Every reload
  leaves its old circuit in the disconnected pool (Blazor's defaults: 100 per server for 3 min)
  beside the live ones; `/_blazor` is exempt from 09b's load shedding, so nothing limits circuits.
- With 1 Gi and Workstation GC (`DOTNET_gcServer=0`, site config): 20 reloads/s clean (p95 0.31 s),
  37.5/s p95 11 s, 503s from 75/s, no OOM; memory per pod climbed to a **plateau of ~850 Mi**.
  Fresh pods start at 76–79 Mi with Workstation GC, 167–189 Mi with Server GC.
- Microsoft's guidance for exactly this ("Manage memory in deployed server-side Blazor apps",
  learn.microsoft.com, 2026-07): memory = (active + disconnected circuits) × per-circuit memory;
  reduce the disconnected count and retention; Workstation GC. A reload never reconnects to its
  old circuit, so retaining it is pure cost.
- Not needed: a GC heap limit. In a container with a memory limit .NET already caps the GC heap at
  75 % of it; the OOM kill came from memory outside the GC heap.

## Contracts

| Setting | Default | Meaning |
|---|---|---|
| `Croft:Web:Circuits:DisconnectedMaxRetained` | `20` | `CircuitOptions.DisconnectedCircuitMaxRetained` |
| `Croft:Web:Circuits:DisconnectedRetentionSeconds` | `60` | `CircuitOptions.DisconnectedCircuitRetentionPeriod` |
| `Croft:Web:Circuits:MaxActive` | `400` | circuits per process (connected + disconnected); at or above it new circuits are refused. `0` = no limit |

An invalid value (non-integer, negative) fails at startup naming the setting (as 09d's
`Database:*` settings do).

## Item 1: circuit retention is configurable, with modest defaults

**Do:** bind the two retention settings in the `AddInteractiveServerComponents` options callback
(`src/Croft.Web/Program.cs:42`).

**Tests (Web):** `IOptions<CircuitOptions>` from a `WebApplicationFactory`: defaults 20 / 60 s;
configured 5 / 15 s are applied; `-1` and `abc` fail startup with the setting's name.
Break-it-once: drop the binding; the configured-values test fails.

## Item 2: admission control for circuits

**Do:**
- A `CircuitHandler` that counts circuits per process (opened minus closed; a disconnected
  circuit still counts until it is closed) and publishes the count as an observable gauge on
  Croft's Web meter (`croft.web.circuits.active`).
- Requests that would start a new circuit (`POST /_blazor/negotiate`, and a WebSocket request to
  `/_blazor` without an `id` query, the no-negotiate path) get **503 with `Retry-After: 5`** while
  the count is at or above `MaxActive`. Existing circuits (reconnects with an `id`) are never refused.
- The page itself is unaffected: it was prerendered, so the reader still sees the full content.
  Check what blazor.web.js shows when the circuit can't start (the site's `ReconnectModal`): it
  must not cover or block the content. Make it not do so if it does, and say what you changed.

**Tests:**
- Unit/integration (Web, `WebApplicationFactory`): with `MaxActive=2` and the counter at 2,
  `POST /_blazor/negotiate?negotiateVersion=1` → 503 + `Retry-After: 5`; at 1 → passes on to
  SignalR (any non-503); a request with `id=…` → never 503; `MaxActive=0` → never 503. The
  counter: opened/closed events move it; the gauge reports it.
- E2E (`tests/Croft.Web.E2E`, an instance with `Croft__Web__Circuits__MaxActive=1`): browser
  context A opens a post (its circuit connects); context B opens the same post: B's title and
  body are visible, and the element at the viewport's centre (`elementFromPoint`) belongs to the
  post's main content, not an overlay. Then A closes: within the retention period (set to 15 s for
  this test) B can get a circuit after a reload.
- Break-it-once: compare with `>` instead of `>=`; the "at 2 → 503" test fails.

## Item 3: Workstation GC is croft-web's default

**Do:** `<ServerGarbageCollection>false</ServerGarbageCollection>` in `src/Croft.Web/Croft.Web.csproj`
(a site can still choose Server GC with `DOTNET_gcServer=1`). Croft.Api unchanged.

**Tests:** a Web test reads the built `Croft.Web.runtimeconfig.json` from the test's output folder
and asserts `configProperties["System.GC.Server"] == false` (the test host's own
`GCSettings.IsServerGC` reflects the test runner, not croft-web, so it can't be the oracle).
Break-it-once: remove the property; the test fails.

## Item 4: the deployment docs say how to size it

**Do:** `k8s/README.md`, a section "Blazor circuits and memory": the three settings, Microsoft's
formula, the 2026-10-15 numbers (≈0.9 Mi per idle circuit; ~850 Mi plateau under reload churn
with 100 retained per server and Server-GC-era defaults), and a rule of thumb for `MaxActive`
from the Web pod's memory limit; Workstation GC as the default and when to switch.

**Tests:** none (docs). The README states the defaults from the Contracts table exactly.

## Out of scope

- Static SSR of the reading pages (brief 09f, next): it removes circuits from readers altogether;
  this brief protects whatever still has circuits.
- Load shedding for `/_blazor` beyond the admission rule; Azure SignalR; the API.
- E2E instances: use `--first-instance` from your launch message (09e uses other instances).
