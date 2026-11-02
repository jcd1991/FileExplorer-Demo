import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const batch = process.env.EXPERIMENT_BATCH || `upload-experiments-${Date.now()}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid EXPERIMENT_BATCH');
const out = path.join(harness, 'results', batch);
const root = path.join(harness, 'uploads/data', batch);
fs.mkdirSync(out, { recursive: true }); fs.mkdirSync(root, { recursive: true });
fs.writeFileSync(path.join(harness, 'uploads/.harness-owned'), '');
const statePath = path.join(out, 'state.json');
const state = fs.existsSync(statePath) ? JSON.parse(fs.readFileSync(statePath)) : { batch, status: 'running', completed: [], failed: [], current: null };
const log = text => { fs.appendFileSync(path.join(out, 'run.log'), `${new Date().toISOString()} ${text}\n`); console.log(text); };
const save = () => fs.writeFileSync(statePath, JSON.stringify(state, null, 2));
fs.writeFileSync(path.join(out, 'run.pid'), `${process.pid}`);
if (state.status === 'complete') { log(`Already complete: ${out}`); process.exit(0); }
fs.mkdirSync(path.join(root, 'ops'), { recursive: true });
const sizes = [1, 10, 100];
const buffers = [65536, 131072, 262144];
const streamBuffers = [4096, 1];
const jobs = [];
// Rotate variant ordering by size to reduce time/order bias.
for (let sizeIndex = 0; sizeIndex < sizes.length; sizeIndex++) {
  for (let offset = 0; offset < buffers.length * streamBuffers.length; offset++) {
    const index = (offset + sizeIndex * 2) % 6;
    jobs.push({ name: `size-${sizes[sizeIndex]}m-copy-${buffers[Math.floor(index / 2)]}-stream-${streamBuffers[index % 2]}`, size: sizes[sizeIndex], copyBuffer: buffers[Math.floor(index / 2)], streamBuffer: streamBuffers[index % 2], vus: 1 });
  }
}
for (const limit of [0, 1, 2, 4]) jobs.push({ name: `size-100m-concurrency-${limit || 'unlimited'}`, size: 100, copyBuffer: 65536, streamBuffer: 4096, vus: 8, limit });
state.total = jobs.length; state.status = 'running'; save();
for (const job of jobs) {
  if (state.completed.includes(job.name)) continue;
  state.current = job.name; state.attempts ||= {}; state.attempts[job.name] = (state.attempts[job.name] || 0) + 1;
  const runId = `${batch}-${job.name}-attempt-${state.attempts[job.name]}`;
  state.reportIds ||= {}; state.reportIds[job.name] = runId; save(); log(`Starting ${job.name}`);
  const measuredPerVu = job.vus > 1 ? 13 : 100;
  const expectedMeasuredAttempts = job.vus * measuredPerVu;
  const env = { ...process.env, RUN_ID: runId, PROFILE: 'baseline', K6_SCRIPT: path.join(harness, 'k6/upload-cycle.js'),
    UPLOAD_PATH: `${batch}/ops`, FIXTURE_FILE: path.join(harness, `fixtures/payload-${job.size}m.bin`),
    VUS: String(job.vus), MEASURED_ITERATIONS: String(measuredPerVu), WARMUP_ITERATIONS: '2', MAX_DURATION: '30m', REQUEST_TIMEOUT_SECONDS: '180',
    UPLOAD_COPY_BUFFER_BYTES: String(job.copyBuffer), UPLOAD_STREAM_BUFFER_BYTES: String(job.streamBuffer), MAX_CONCURRENT_UPLOADS: String(job.limit || 0),
    DIAGNOSTICS_ENABLED: 'false' };
  delete env.ITERATIONS; delete env.P95_LIMIT_MS;
  const fd = fs.openSync(path.join(out, `${job.name}.log`), 'w');
  const code = await new Promise((resolve, reject) => { const child = spawn(path.join(here, 'run-local.sh'), [], { cwd: project, env, stdio: ['ignore', fd, fd] }); child.once('error', reject); child.once('exit', resolve); });
  fs.closeSync(fd);
  if (code !== 0) { state.failed.push(job.name); state.status = 'failed'; state.current = job.name; save(); log(`FAILED ${job.name}; stopping. Inspect counters, process, and disk logs before resuming.`); process.exit(1); }
  const report = JSON.parse(fs.readFileSync(path.join(harness, 'results', `report-${runId}.json`)));
  if (report.result.measuredUploadAttempts !== expectedMeasuredAttempts || report.result.measuredUploadFailures !== 0 || report.result.uploadCleanupFailures !== 0 || report.verificationExitCode !== 0) {
    state.failed.push(job.name); state.status = 'failed'; save(); throw new Error(`Invalid measured results for ${job.name}`);
  }
  // The integrity check's 100 MiB download is disposable; retain its hash/result in the report only.
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.bin`), { force: true });
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.json`), { force: true });
  state.completed.push(job.name); state.failed = state.failed.filter(name => name !== job.name); save(); log(`Passed ${job.name} (${state.completed.length}/${jobs.length}) p95=${report.result.measuredUploadLatencyMs.p95}ms`);
}
state.status = 'complete'; state.current = null; state.generatedDataRemoved = true; save();
fs.rmSync(root, { recursive: true, force: true });
log(`All ${jobs.length} upload experiment jobs passed. Reports: ${out}`);
