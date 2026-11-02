import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const baseUrl = (__ENV.BASE_URL || 'http://127.0.0.1:5178').replace(/\/$/, '');
const fixturePath = __ENV.FIXTURE_FILE || './performance/fixtures/payload-1m.bin';
const payload = open(fixturePath, 'b');
const payloadBytes = payload.byteLength;
const runId = __ENV.RUN_ID || 'manual';
const uploadPath = __ENV.UPLOAD_PATH || '';
const profile = __ENV.PROFILE || 'smoke';
const profileDefaults = {
  smoke: { vus: 1, iterations: 1 },
  baseline: { vus: 1, iterations: 20 },
  concurrent: { vus: 10, iterations: 100 },
};
const defaults = profileDefaults[profile];
if (!defaults) throw new Error(`Unknown PROFILE: ${profile}`);

const successRate = new Rate('upload_success');
const successCount = new Counter('successful_uploads');
const failureCount = new Counter('failed_uploads');
const confirmedBytes = new Counter('confirmed_payload_bytes');
const uploadLatency = new Trend('upload_latency_ms', true);

const vus = Number(__ENV.VUS || defaults.vus);
const iterations = Number(__ENV.ITERATIONS || defaults.iterations);
const thresholds = {
  http_req_failed: ['rate==0'],
  upload_success: ['rate==1'],
};
if (__ENV.P95_LIMIT_MS) thresholds.upload_latency_ms = [`p(95)<${Number(__ENV.P95_LIMIT_MS)}`];

export const options = {
  scenarios: {
    upload: {
      executor: 'shared-iterations',
      vus,
      iterations,
      maxDuration: __ENV.MAX_DURATION || '30m',
    },
  },
  thresholds,
  userAgent: 'file-browser-k6/1.0',
};

export default function () {
  const filename = `k6-${runId}-vu${__VU}-iter${__ITER}.bin`;
  const encodedPath = encodeURIComponent(uploadPath);
  const body = { file: http.file(payload, filename, 'application/octet-stream') };
  const response = http.post(`${baseUrl}/api/files/upload?path=${encodedPath}`, body, {
    timeout: __ENV.REQUEST_TIMEOUT || '120s',
    tags: { operation: 'upload' },
  });

  uploadLatency.add(response.timings.duration);
  let confirmed = false;
  let returnedSize = -1;
  if (response.status === 201) {
    try {
      returnedSize = response.json('sizeBytes');
      confirmed = returnedSize === payloadBytes;
    } catch (_) {
      confirmed = false;
    }
  }

  const passed = check(response, {
    'upload returns 201': (res) => res.status === 201,
    'server confirms payload size': () => confirmed,
  });
  successRate.add(passed);
  if (passed) {
    successCount.add(1);
    confirmedBytes.add(returnedSize);
  } else {
    failureCount.add(1);
  }
}
