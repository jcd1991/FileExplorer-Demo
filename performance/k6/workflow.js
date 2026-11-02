import http from 'k6/http';
import { check } from 'k6';
import crypto from 'k6/crypto';
import { Counter, Rate, Trend } from 'k6/metrics';

const base = __ENV.BASE_URL;
const root = __ENV.UPLOAD_PATH;
if (!base || !root) throw new Error('Use an isolated loopback runner with UPLOAD_PATH.');
const payload = open(__ENV.FIXTURE_FILE, 'b');
const hash = crypto.sha256(payload, 'hex');
const expectedCount = Number(__ENV.CATALOG_COUNT || 5000);
const completed = new Counter('completed_workflows');
const workflowSuccess = new Rate('workflow_success');
const snapshotExpired = new Counter('snapshot_expired');
const snapshotRetry = __ENV.SNAPSHOT_RETRY !== 'false';
const uploadSuccess = new Rate('upload_success');
const successfulUploads = new Counter('successful_uploads');
const failedUploads = new Counter('failed_uploads');
const bytes = new Counter('confirmed_payload_bytes');
const latency = {};
for (const operation of ['browse', 'search', 'snapshot', 'snapshot_refresh', 'upload', 'copy_file', 'move_file', 'download', 'delete_file', 'copy_folder', 'move_folder', 'delete_folder']) {
  latency[operation] = new Trend(`${operation}_latency_ms`, true);
}
export const options = {
  scenarios: { workflow: { executor: 'shared-iterations', vus: Number(__ENV.VUS || 5), iterations: Number(__ENV.ITERATIONS || 100), maxDuration: __ENV.MAX_DURATION || '5m' } },
  thresholds: { checks: ['rate==1'], http_req_failed: ['rate==0'], workflow_success: ['rate==1'], upload_success: ['rate==1'] },
};
function send(operation, method, route, body, expected, extra = {}) {
  const response = http.request(method, `${base}/api/files/${route}`, body, {
    timeout: __ENV.REQUEST_TIMEOUT || '10s', tags: { operation }, ...extra,
  });
  latency[operation].add(response.timings.duration);
  if (operation === 'snapshot' && response.status === 409) snapshotExpired.add(1);
  if (!check(response, { [`${operation} status ${expected}`]: r => Array.isArray(expected) ? expected.includes(r.status) : r.status === expected }))
    throw new Error(`${operation}: HTTP ${response.status}`);
  return response;
}
function change(operation, source, destination, metric) {
  return send(metric, 'POST', operation, JSON.stringify({ source, destination }), 204, { headers: { 'Content-Type': 'application/json' } });
}
export default function () {
  const id = `k6-${__ENV.RUN_ID}-${__VU}-${__ITER}`;
  const source = `${root}/ops/${id}.bin`;
  const copy = `${root}/ops/${id}-copy.bin`;
  const moved = `${root}/ops/${id}-moved.bin`;
  const folder = `${root}/ops/${id}-folder`;
  const movedFolder = `${root}/ops/${id}-folder-moved`;
  const cleanup = new Map();
  let passed = false;
  try {
    const path = encodeURIComponent(`${root}/catalog`);
    const view = send('browse', 'GET', `browse?path=${path}`, null, 200).json();
    if (!check(view, { 'browse exact 5000-file totals': r => r.summary.fileCount === expectedCount && !r.partial })) throw new Error('browse totals');
    const search = send('search', 'GET', `search?path=${path}&q=item-`, null, 200).json();
    if (!check(search, { 'search exact matching totals': r => r.summary.fileCount === expectedCount && !r.partial })) throw new Error('search totals');
    const snapshotResponse = send('snapshot', 'GET', `search?path=${path}&q=item-&page=2&snapshot=${search.snapshotId}`, null, snapshotRetry ? [200, 409] : 200, snapshotRetry ? { responseCallback: http.expectedStatuses(200, 409) } : {});
    let page;
    if (snapshotResponse.status === 409) {
      page = send('snapshot_refresh', 'GET', `search?path=${path}&q=item-&page=2`, null, 200).json();
    } else page = snapshotResponse.json();
    if (!check(page, { 'snapshot stable page 2 or refreshed equivalent': r => r.page === 2 && r.summary.fileCount === expectedCount && (snapshotResponse.status === 409 || r.snapshotId === search.snapshotId) })) throw new Error('snapshot');
    try {
      const upload = send('upload', 'POST', `upload?path=${encodeURIComponent(`${root}/ops`)}`, { file: http.file(payload, `${id}.bin`, 'application/octet-stream') }, 201).json();
      cleanup.set(source, 'delete_file');
      if (!check(upload, { 'upload byte count': r => r.sizeBytes === payload.byteLength })) throw new Error('upload bytes');
      uploadSuccess.add(true); successfulUploads.add(1); bytes.add(payload.byteLength);
    } catch (error) { uploadSuccess.add(false); failedUploads.add(1); throw error; }
    change('copy', source, copy, 'copy_file'); cleanup.set(copy, 'delete_file');
    change('move', copy, moved, 'move_file'); cleanup.delete(copy); cleanup.set(moved, 'delete_file');
    const downloaded = send('download', 'GET', `download?path=${encodeURIComponent(moved)}`, null, 200, { responseType: 'binary' });
    if (!check(downloaded, { 'copied/moved file SHA256 matches': r => crypto.sha256(r.body, 'hex') === hash })) throw new Error('download hash');
    change('copy', `${root}/seed`, folder, 'copy_folder'); cleanup.set(folder, 'delete_folder');
    change('move', folder, movedFolder, 'move_folder'); cleanup.delete(folder); cleanup.set(movedFolder, 'delete_folder');
    const nested = send('download', 'GET', `download?path=${encodeURIComponent(`${movedFolder}/Nested/keep.txt`)}`, null, 200);
    if (!check(nested, { 'copied/moved folder nested contents': r => r.body === 'nested fixture' })) throw new Error('folder contents');
    passed = true;
  } catch (error) { console.error(`workflow failed: ${error.message}`); }
  finally {
    for (const [path, metric] of cleanup) {
      try { change('delete', path, null, metric); }
      catch (error) { passed = false; console.error(`cleanup failed: ${error.message}`); }
    }
    workflowSuccess.add(passed);
    if (passed) completed.add(1);
  }
}
