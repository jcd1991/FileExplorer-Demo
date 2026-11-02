import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const base = __ENV.BASE_URL;
const skipGenerated = __ENV.SKIP_GENERATED === 'true';
const expectedFiles = Number(__ENV.EXPECTED_FILES);
const warmupIterations = Number(__ENV.WARMUP_ITERATIONS || 2);
const searchLatency = new Trend('search_latency_ms', true);

if (!base || !Number.isInteger(expectedFiles)) throw new Error('Use run-search-exclusion-benchmark.mjs.');

export const options = {
  scenarios: {
    search: {
      executor: 'per-vu-iterations',
      vus: Number(__ENV.VUS),
      iterations: Number(__ENV.ITERATIONS),
      maxDuration: '5m',
    },
  },
  thresholds: {
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
  },
};

export default function () {
  const response = http.get(`${base}/api/files/search?path=&q=needle&skipGenerated=${skipGenerated}`, {
    tags: { operation: 'search', exclusions: skipGenerated ? 'on' : 'off' },
  });
  const valid = check(response, {
    'search returns the expected complete result': result => result.status === 200 &&
      result.json('summary.fileCount') === expectedFiles && result.json('partial') === false,
  });
  if (!valid) throw new Error(`Search result mismatch: HTTP ${response.status}`);
  if (__ITER >= warmupIterations) searchLatency.add(response.timings.duration, { exclusions: skipGenerated ? 'on' : 'off' });
}
