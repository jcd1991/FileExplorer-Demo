import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const base = (__ENV.BASE_URL || 'http://127.0.0.1:5178').replace(/\/$/, '');
const path = __ENV.UPLOAD_PATH || '';
const payload = open(__ENV.FIXTURE_FILE, 'b');
const warmups = Number(__ENV.WARMUP_ITERATIONS || 2);
const measured = Number(__ENV.MEASURED_ITERATIONS || 100);
const vuCount = Number(__ENV.VUS || 1);
const successes = new Counter('measured_uploads');
const failed = new Counter('failed_measured_uploads');
const uploadRate = new Rate('measured_upload_success');
const cleanupRate = new Rate('upload_cleanup_success');
const measuredUploadLatency = new Trend('measured_upload_latency_ms', true);
const warmupUploadLatency = new Trend('warmup_upload_latency_ms', true);

export const options = {
  scenarios: { uploads: { executor: 'per-vu-iterations', vus: vuCount, iterations: warmups + measured, maxDuration: __ENV.MAX_DURATION || '30m' } },
  thresholds: { http_req_failed: ['rate==0'], measured_upload_success: ['rate==1'], upload_cleanup_success: ['rate==1'] },
};

export default function () {
  const warm = __ITER < warmups;
  const name = `k6-${__ENV.RUN_ID}-vu${__VU}-iter${__ITER}.bin`;
  const response = http.post(`${base}/api/files/upload?path=${encodeURIComponent(path)}`, { file: http.file(payload, name, 'application/octet-stream') }, {
    timeout: __ENV.REQUEST_TIMEOUT || '120s', tags: { operation: 'upload', phase: warm ? 'warmup' : 'measured' },
  });
  (warm ? warmupUploadLatency : measuredUploadLatency).add(response.timings.duration);
  let sizeOk = false;
  let filePath = '';
  if (response.status === 201) {
    try { const body = response.json(); sizeOk = body.sizeBytes === payload.byteLength; filePath = body.path; } catch (_) {}
  }
  const uploaded = check(response, { 'upload returns 201': r => r.status === 201, 'server confirms exact bytes': () => sizeOk });
  if (!warm) { uploadRate.add(uploaded); uploaded ? successes.add(1) : failed.add(1); }
  if (!uploaded || !filePath) return;
  const del = http.post(`${base}/api/files/delete`, JSON.stringify({ source: filePath }), {
    headers: { 'Content-Type': 'application/json' }, timeout: __ENV.REQUEST_TIMEOUT || '120s', tags: { operation: 'fixture_cleanup', phase: warm ? 'warmup' : 'measured' },
  });
  const removed = check(del, { 'generated upload removed': r => r.status === 204 });
  if (!warm) cleanupRate.add(removed);
}
