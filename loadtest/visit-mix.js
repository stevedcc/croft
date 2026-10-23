// New-visitor mix: each iteration is one first-time visitor. A new connection and TLS handshake
// (noVUConnectionReuse), the page, then every asset a browser fetched for that page (CSS, JS,
// fonts, branding, the post's images), in parallel as a browser would. Open model: RATES visits
// per second, each held for STEP. This is the realistic test: it includes TLS set-up and the
// bytes of the assets, which a page-only ramp leaves out.
//
//   python3 loadtest/record-assets.py https://blog.example / /post/hello > loadtest/assets.json
//   BASE=https://blog.example PAGES='/:60,/post/hello:40' RATES=5,10,20,30 \
//     loadtest/run.sh visit-mix.js visits
//
// Environment:
//   BASE     the site, no trailing slash (required)
//   ASSETS   the file record-assets.py wrote, relative to this folder (default assets.json)
//   PAGES    "path:weight,..." (default: every page in ASSETS, equal weights); each path must be in ASSETS
//   RATES    visits per second, one per step (default 5,10,20,30,40)
//   STEP     how long each rate is held (default 90s); each step also has a 15 s climb
//   MAXVUS   maximum virtual users (default 1500)
//
// Asset lists go stale when the site is rebuilt (fingerprinted file names): record them again
// against the version you test.
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE;
if (!BASE) throw new Error('BASE is required, e.g. BASE=https://blog.example');

const ASSETS = JSON.parse(open(__ENV.ASSETS || 'assets.json'));
const PAGES = __ENV.PAGES
  ? __ENV.PAGES.split(',').map((entry) => {
      const at = entry.lastIndexOf(':');
      return at > 0 ? [entry.slice(0, at), Number(entry.slice(at + 1))] : [entry, 1];
    })
  : Object.keys(ASSETS).map((path) => [path, 1]);
for (const [path] of PAGES) {
  if (!ASSETS[path]) throw new Error(`no asset list for ${path}: record it with record-assets.py`);
}
const TOTAL = PAGES.reduce((sum, [, weight]) => sum + weight, 0);

function pick() {
  let r = Math.random() * TOTAL;
  for (const [path, weight] of PAGES) {
    if ((r -= weight) < 0) return path;
  }
  return PAGES[0][0];
}

const STEP = __ENV.STEP || '90s';
const RATES = (__ENV.RATES || '5,10,20,30,40').split(',').map(Number);

export const options = {
  noVUConnectionReuse: true, // a new connection per visitor, reused for its own assets (like one browser)
  scenarios: {
    visits: {
      executor: 'ramping-arrival-rate',
      startRate: RATES[0],
      timeUnit: '1s',
      preAllocatedVUs: 100,
      maxVUs: Number(__ENV.MAXVUS || 1500),
      stages: RATES.flatMap((rate) => [{ target: rate, duration: '15s' }, { target: rate, duration: STEP }]),
    },
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const COMPRESSED = { 'Accept-Encoding': 'br, gzip' };

export default function () {
  const path = pick();
  const page = http.get(BASE + path, { tags: { kind: 'page' }, timeout: '20s', headers: COMPRESSED });
  check(page, { 'page 200': (r) => r.status === 200 });
  const responses = http.batch(ASSETS[path].map((asset) =>
    ['GET', BASE + asset, null, { tags: { kind: 'asset' }, timeout: '20s', headers: COMPRESSED }]));
  responses.forEach((r) => check(r, { 'asset 200': (x) => x.status === 200 }));
}
