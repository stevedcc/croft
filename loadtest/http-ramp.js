// Read-only HTTP ramp against a Croft site's public pages: the HTML a reader's first request gets.
// Open model: the arrival rate rises step by step however slow the site gets, so the step where
// latency or errors break is the answer. No JavaScript runs, so it measures page throughput, not
// Blazor circuits (the reading pages are static SSR and open none).
//
//   BASE=https://blog.example PAGES='/:40,/post/hello:30,/feed.xml:5' RATES=20,40,80,160 \
//     loadtest/run.sh http-ramp.js ramp-plain
//
// Environment:
//   BASE     the site, no trailing slash (required)
//   PAGES    "path:weight,..." (default "/:1"); pick pages your readers actually hit
//   RATES    requests per second, one per step (default 5,10,20,40,80,160)
//   STEP     how long each rate is held (default 2m); each step also has a 15 s climb
//   ENC      an Accept-Encoding value, e.g. "br, gzip" (k6 asks for no compression by default)
//   PREVUS / MAXVUS   pre-allocated and maximum virtual users (default 50 / 600)
//
// Aborts itself when more than 5 % of requests fail or the p95 passes 5 s.
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE;
if (!BASE) throw new Error('BASE is required, e.g. BASE=https://blog.example');

const PAGES = (__ENV.PAGES || '/:1').split(',').map((entry) => {
  const at = entry.lastIndexOf(':');
  return at > 0 ? [entry.slice(0, at), Number(entry.slice(at + 1))] : [entry, 1];
});
const TOTAL = PAGES.reduce((sum, [, weight]) => sum + weight, 0);

function pick() {
  let r = Math.random() * TOTAL;
  for (const [path, weight] of PAGES) {
    if ((r -= weight) < 0) return path;
  }
  return PAGES[0][0];
}

const STEP = __ENV.STEP || '2m';
const RATES = (__ENV.RATES || '5,10,20,40,80,160').split(',').map(Number);

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-arrival-rate',
      startRate: RATES[0],
      timeUnit: '1s',
      preAllocatedVUs: Number(__ENV.PREVUS || 50),
      maxVUs: Number(__ENV.MAXVUS || 600),
      stages: RATES.flatMap((rate) => [{ target: rate, duration: '15s' }, { target: rate, duration: STEP }]),
    },
  },
  thresholds: {
    http_req_failed: [{ threshold: 'rate<0.05', abortOnFail: true, delayAbortEval: '30s' }],
    http_req_duration: [{ threshold: 'p(95)<5000', abortOnFail: true, delayAbortEval: '30s' }],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const path = pick();
  const res = http.get(BASE + path, {
    tags: { kind: 'page', page: path },
    timeout: '20s',
    headers: __ENV.ENC ? { 'Accept-Encoding': __ENV.ENC } : {},
  });
  check(res, { 'status 200': (r) => r.status === 200 });
}
