# Load tests

k6 scripts for finding how much anonymous reading traffic a Croft site takes, and where it breaks.
They are what produced [the 2026-10-23 measurement](../docs/measurements/2026-10-23-load-test.md)
on four Raspberry Pi 4, with the site-specific pages and hosts turned into parameters.

| File | What it does |
|---|---|
| `http-ramp.js` | Page requests only, at rising arrival rates. Finds the page-throughput limit; with `ENC="br, gzip"` it also shows what compression costs and saves. |
| `visit-mix.js` | First-time visitors: a new TLS connection, the page, then every asset a browser fetched for it. The realistic test. |
| `record-assets.py` | Records those asset lists with Playwright (Chromium), for `visit-mix.js`. |
| `run.sh` | Runs a script in the `grafana/k6` container, writes `results/<name>.*`, summarises. |
| `summarise.py` | Turns k6's CSV into 30-second rows: rate, p50/p95/p99, failures, MB/s, pages and assets apart. |

## Running

From a machine **outside** the cluster you are testing (the load generator competes for CPU
otherwise), against the site's real address, so the run goes through the same ingress and TLS as
readers do:

```bash
# 1. Which pages, and what a browser loads for each (re-record after every release: asset names are fingerprinted)
python3 loadtest/record-assets.py https://blog.example / /post/hello /category/news > loadtest/assets.json

# 2. Page throughput, uncompressed and compressed
BASE=https://blog.example PAGES='/:40,/post/hello:30,/category/news:15,/feed.xml:5' \
  RATES=20,40,80,160,320 loadtest/run.sh http-ramp.js ramp-plain
BASE=https://blog.example PAGES='/:40,/post/hello:30,/category/news:15,/feed.xml:5' \
  RATES=20,40,80,160,320 ENC='br, gzip' loadtest/run.sh http-ramp.js ramp-br

# 3. Visitors
BASE=https://blog.example PAGES='/:40,/post/hello:40,/category/news:20' \
  RATES=5,10,20,30,40 loadtest/run.sh visit-mix.js visits
```

`results/` and `assets.json` are git-ignored.

## Reading the results

Both scripts use an **open model** (`ramping-arrival-rate`): requests keep arriving at the set rate
however slow the site gets, as real readers do. A saturated site therefore shows rising latency,
then errors, instead of the load politely backing off. Read `results/<name>-steps.txt` top to
bottom: the last step with 0 % failures and a p95 you would accept is the site's capacity; the
step after it is where it breaks. `http-ramp.js` aborts itself above 5 % failures or a 5 s p95.

## Before you run

- **It is a real load on a real site.** Pick a quiet time, tell whoever shares the cluster, and
  watch it (`kubectl top nodes`, `kubectl top pods -A`) while it runs.
- **Start idle.** Pods that started minutes ago are still compiling (JIT) and will look slow; wait
  until node CPU has settled.
- **Check nothing is draining.** An automatic node upgrade during a run (`kubectl get nodes`
  showing `SchedulingDisabled`) invalidates it.
- **Output cache and read cache are on in production.** Every repeated page is served from memory
  after its first render, so these tests measure the cached path, which is what readers get.
- **The client can be the limit.** If the load generator's own CPU is busy (one TLS handshake per
  visitor is expensive), the numbers describe it, not the site. Watch its load average too.

## What these do not test

Signed-in traffic, the editor, uploads and Blazor circuits. Reading pages are static server
rendering and open no circuit, so a reader costs no circuit memory; admin and editor sessions do,
and need a browser-driven test of their own.
