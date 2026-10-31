import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const base = (__ENV.BASE_URL || '').replace(/\/$/, '');
const targetFiles = new Map([
  ['http://127.0.0.1:20080', 'render-100m-verification.bin'],
  ['https://file-explorer-demo-api.onrender.com', 'render-100m-test.bin'],
]);
const fileName = targetFiles.get(base);
if (!fileName) {
  throw new Error('BASE_URL must be the isolated local app or the known Render demo URL.');
}

const downloadLatency = new Trend('download_100m_latency_ms', true);
const expectedBytes = '104857600';

export const options = {
  discardResponseBodies: true,
  scenarios: {
    sequential_downloads: {
      executor: 'per-vu-iterations',
      vus: 1,
      iterations: 10,
      maxDuration: '5m',
    },
  },
  thresholds: {
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
  },
};

export default function () {
  const response = http.get(
    `${base}/api/files/download?path=${encodeURIComponent(fileName)}`,
    { tags: { operation: 'download-100m' } },
  );
  const ok = check(response, {
    'download returns HTTP 200': (res) => res.status === 200,
    'download advertises exactly 100 MiB': (res) => res.headers['Content-Length'] === expectedBytes,
  });
  if (!ok) {
    console.error(`Download failed: status=${response.status}; content-length=${response.headers['Content-Length'] ?? 'missing'}`);
  }
  if (response.status === 200 && response.headers['Content-Length'] === expectedBytes) {
    downloadLatency.add(response.timings.duration);
  }
  sleep(2);
}
