import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const base = __ENV.BASE_URL;
const catalog = __ENV.UPLOAD_PATH;
if (!base || !catalog) throw new Error('Use the local scan benchmark runner.');

const browseLatency = new Trend('browse_latency_ms', true);
const searchLatency = new Trend('search_latency_ms', true);
const warmupIterations = Number(__ENV.SCAN_WARMUP_ITERATIONS || 2);
const measuredIterations = Number(__ENV.ITERATIONS || 100);
const virtualUsers = Number(__ENV.VUS || 5);

export const options = {
  scenarios: {
    scan: {
      executor: 'per-vu-iterations',
      vus: virtualUsers,
      iterations: measuredIterations,
      maxDuration: __ENV.MAX_DURATION || '10m',
    },
  },
  thresholds: {
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
  },
};

export default function () {
  const path = encodeURIComponent(catalog);
  const warmup = __ITER < warmupIterations;

  const browse = http.get(`${base}/api/files/browse?path=${path}`, { tags: { operation: 'browse' } });
  if (!check(browse, {
    'browse includes the complete catalog': response => response.status === 200 && response.json('summary.fileCount') === 5000 && response.json('partial') === false,
  })) throw new Error(`browse failed with HTTP ${browse.status}`);
  if (!warmup) browseLatency.add(browse.timings.duration);

  const search = http.get(`${base}/api/files/search?path=${path}&q=item-`, { tags: { operation: 'search' } });
  if (!check(search, {
    'search includes all matches without truncation': response => response.status === 200 && response.json('summary.fileCount') === 5000 && response.json('partial') === false,
  })) throw new Error(`search failed with HTTP ${search.status}`);
  if (!warmup) searchLatency.add(search.timings.duration);
}
