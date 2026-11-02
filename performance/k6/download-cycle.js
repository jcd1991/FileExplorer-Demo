import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const base = (__ENV.BASE_URL || 'http://127.0.0.1:5178').replace(/\/$/, '');
const filePath = __ENV.DOWNLOAD_PATH || '';
const expectedBytes = Number(__ENV.EXPECTED_BYTES || 0);
const warmups = Number(__ENV.WARMUP_ITERATIONS || 2);
const measured = Number(__ENV.MEASURED_ITERATIONS || 100);
const vuCount = Number(__ENV.VUS || 1);
const successes = new Counter('measured_downloads');
const failed = new Counter('failed_measured_downloads');
const downloadRate = new Rate('measured_download_success');
const measuredLatency = new Trend('measured_download_latency_ms', true);
const warmupLatency = new Trend('warmup_download_latency_ms', true);

export const options = {
  scenarios: { downloads: { executor: 'per-vu-iterations', vus: vuCount, iterations: warmups + measured, maxDuration: __ENV.MAX_DURATION || '30m' } },
  thresholds: { http_req_failed: ['rate==0'], measured_download_success: ['rate==1'] },
};

export default function () {
  const warm = __ITER < warmups;
  const response = http.get(`${base}/api/files/download?path=${encodeURIComponent(filePath)}`, {
    timeout: __ENV.REQUEST_TIMEOUT || '180s', tags: { operation: 'download', phase: warm ? 'warmup' : 'measured' },
  });
  (warm ? warmupLatency : measuredLatency).add(response.timings.duration);
  const ok = check(response, {
    'download returns 200': (r) => r.status === 200,
    'download returns exact file bytes': (r) => r.body && r.body.length === expectedBytes,
  });
  if (!warm) { downloadRate.add(ok); ok ? successes.add(1) : failed.add(1); }
}
