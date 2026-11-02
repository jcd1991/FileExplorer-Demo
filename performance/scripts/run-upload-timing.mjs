import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const batch = process.env.TIMING_BATCH || `upload-timing-${Date.now()}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid TIMING_BATCH');
const output = path.join(harness, 'results', batch);
const root = path.join(harness, 'uploads/data', batch);
fs.mkdirSync(output, { recursive: true });
fs.mkdirSync(path.join(root, 'ops'), { recursive: true });
fs.writeFileSync(path.join(harness, 'uploads/.harness-owned'), '');
const stateFile = path.join(output, 'state.json');
const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : { batch, completed: [], failed: [], status: 'running' };
const save = () => fs.writeFileSync(stateFile, `${JSON.stringify(state, null, 2)}\n`);
const log = (message) => { fs.appendFileSync(path.join(output, 'run.log'), `${new Date().toISOString()} ${message}\n`); console.log(message); };
fs.writeFileSync(path.join(output, 'run.pid'), `${process.pid}\n`);
if (state.status === 'complete') { log(`Already complete: ${output}`); process.exit(0); }

const jobs = [1, 10, 100].map((size) => ({ name: `size-${size}m-server-timing`, size }));
state.status = 'running'; state.total = jobs.length; state.attempts ||= {}; state.runIds ||= {}; save();
for (const job of jobs) {
  if (state.completed.includes(job.name)) continue;
  state.current = job.name; state.attempts[job.name] = (state.attempts[job.name] || 0) + 1;
  const runId = `${batch}-${job.name}-attempt-${state.attempts[job.name]}`;
  state.runIds[job.name] = runId; save(); log(`Starting ${job.name}`);
  const env = {
    ...process.env,
    RUN_ID: runId,
    PROFILE: 'baseline',
    K6_SCRIPT: path.join(harness, 'k6/upload-cycle.js'),
    UPLOAD_PATH: `${batch}/ops`,
    FIXTURE_FILE: path.join(harness, `fixtures/payload-${job.size}m.bin`),
    VUS: '1', MEASURED_ITERATIONS: '100', WARMUP_ITERATIONS: '2',
    MAX_DURATION: '30m', REQUEST_TIMEOUT_SECONDS: '180',
    UPLOAD_COPY_BUFFER_BYTES: '65536', UPLOAD_STREAM_BUFFER_BYTES: '4096',
    MAX_CONCURRENT_UPLOADS: '0', DIAGNOSTICS_ENABLED: 'true',
  };
  delete env.ITERATIONS;
  const fd = fs.openSync(path.join(output, `${job.name}.log`), 'w');
  const exitCode = await new Promise((resolve, reject) => {
    const child = spawn(path.join(here, 'run-local.sh'), [], { cwd: project, env, stdio: ['ignore', fd, fd] });
    child.once('error', reject);
    child.once('exit', resolve);
  });
  fs.closeSync(fd);
  if (exitCode !== 0) {
    state.status = 'failed'; state.failed.push(job.name); save();
    log(`FAILED ${job.name}; retain its generated data and inspect logs before resuming.`);
    process.exit(1);
  }
  const report = JSON.parse(fs.readFileSync(path.join(harness, 'results', `report-${runId}.json`), 'utf8'));
  const serverLog = fs.readFileSync(path.join(harness, 'results', `server-${runId}.log`), 'utf8');
  const timings = [...serverLog.matchAll(/DurationMs=([0-9]+(?:\.[0-9]+)?)/g)].map((match) => Number(match[1]));
  if (report.result.measuredUploadAttempts !== 100 || report.result.measuredUploadFailures !== 0 || report.result.uploadCleanupFailures !== 0 || report.verificationExitCode !== 0 || timings.length < 100) {
    state.status = 'failed'; state.failed.push(job.name); save();
    throw new Error(`Missing uploads, integrity verification, or controller timings for ${job.name}: ${timings.length} timings`);
  }
  timings.sort((a, b) => a - b);
  const percentile = (p) => timings[Math.ceil(p * timings.length) - 1];
  const timingSummary = { samples: timings.length, p50Ms: percentile(0.5), p95Ms: percentile(0.95), p99Ms: percentile(0.99), averageMs: timings.reduce((sum, value) => sum + value, 0) / timings.length };
  fs.writeFileSync(path.join(output, `${job.name}.json`), `${JSON.stringify({ reportId: runId, clientLatency: report.result.measuredUploadLatencyMs, controllerDurationMs: timingSummary }, null, 2)}\n`);
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.bin`), { force: true });
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.json`), { force: true });
  state.completed.push(job.name); state.failed = state.failed.filter((name) => name !== job.name); save();
  log(`Passed ${job.name}: controller p95=${timingSummary.p95Ms}ms; client p95=${report.result.measuredUploadLatencyMs.p95}ms`);
}
state.status = 'complete'; state.current = null; state.generatedDataRemoved = true; save();
fs.rmSync(root, { recursive: true, force: true });
log(`All upload timing jobs passed: ${output}`);
