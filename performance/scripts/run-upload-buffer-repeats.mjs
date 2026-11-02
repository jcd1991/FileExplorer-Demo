import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const batch = process.env.REPEAT_BATCH || `upload-buffer-repeats-${Date.now()}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid REPEAT_BATCH');
const seed = Number(process.env.REPEAT_SEED || 20261031);
const output = path.join(harness, 'results', batch);
const root = path.join(harness, 'uploads/data', batch);
fs.mkdirSync(output, { recursive: true });
fs.mkdirSync(path.join(root, 'ops'), { recursive: true });
fs.writeFileSync(path.join(harness, 'uploads/.harness-owned'), '');
const stateFile = path.join(output, 'state.json');
const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : { batch, seed, completed: [], failed: [], status: 'running' };
const save = () => fs.writeFileSync(stateFile, `${JSON.stringify(state, null, 2)}\n`);
const log = (message) => { fs.appendFileSync(path.join(output, 'run.log'), `${new Date().toISOString()} ${message}\n`); console.log(message); };
fs.writeFileSync(path.join(output, 'run.pid'), `${process.pid}\n`);
if (state.status === 'complete') { log(`Already complete: ${output}`); process.exit(0); }

let randomState = seed >>> 0;
const random = () => { randomState = (1664525 * randomState + 1013904223) >>> 0; return randomState / 0x100000000; };
const pairs = [];
for (const size of [10, 100]) {
  const vuCounts = size === 100 ? [1, 4] : [1];
  for (const vus of vuCounts) for (let repeat = 1; repeat <= 3; repeat++) {
    const measuredPerVu = 100 / vus;
    const variants = [4096, 1].map((streamBuffer) => ({
      name: `size-${size}m-vu-${vus}-rep-${repeat}-stream-${streamBuffer}`,
      size, vus, measuredPerVu, streamBuffer,
    }));
    pairs.push(random() < 0.5 ? variants : variants.reverse());
  }
}
for (let index = pairs.length - 1; index > 0; index--) {
  const other = Math.floor(random() * (index + 1));
  [pairs[index], pairs[other]] = [pairs[other], pairs[index]];
}
const plannedJobs = pairs.flat();
state.seed = seed;
state.design = { sizesMiB: [10, 100], repetitionsPerCell: 3, concurrency: { singleVu: true, fourVuAt100MiB: true }, measuredUploadsPerRun: 100, warmupUploadsPerVu: 2, streamBuffers: [4096, 1], randomizedPairs: true };
state.plannedJobs ||= plannedJobs;
state.total = state.plannedJobs.length;
state.status = 'running'; save();
log(`Seed ${seed}; planned ${state.total} randomized jobs as paired 4 KiB/disabled-buffer comparisons.`);

for (const job of state.plannedJobs) {
  if (state.completed.includes(job.name)) continue;
  state.current = job.name; state.attempts ||= {}; state.attempts[job.name] = (state.attempts[job.name] || 0) + 1;
  const runId = `${batch}-${job.name}-attempt-${state.attempts[job.name]}`;
  state.reportIds ||= {}; state.reportIds[job.name] = runId; save(); log(`Starting ${job.name}`);
  const env = {
    ...process.env,
    RUN_ID: runId, PROFILE: 'baseline',
    K6_SCRIPT: path.join(harness, 'k6/upload-cycle.js'),
    UPLOAD_PATH: `${batch}/ops`, FIXTURE_FILE: path.join(harness, `fixtures/payload-${job.size}m.bin`),
    VUS: String(job.vus), MEASURED_ITERATIONS: String(job.measuredPerVu), WARMUP_ITERATIONS: '2',
    MAX_DURATION: '30m', REQUEST_TIMEOUT_SECONDS: '180',
    UPLOAD_COPY_BUFFER_BYTES: '65536', UPLOAD_STREAM_BUFFER_BYTES: String(job.streamBuffer),
    MAX_CONCURRENT_UPLOADS: '0', DIAGNOSTICS_ENABLED: 'false',
  };
  delete env.ITERATIONS; delete env.P95_LIMIT_MS;
  const fd = fs.openSync(path.join(output, `${job.name}.log`), 'w');
  const exitCode = await new Promise((resolve, reject) => {
    const child = spawn(path.join(here, 'run-local.sh'), [], { cwd: project, env, stdio: ['ignore', fd, fd] });
    child.once('error', reject); child.once('exit', resolve);
  });
  fs.closeSync(fd);
  if (exitCode !== 0) {
    state.status = 'failed'; state.failed.push(job.name); save();
    log(`FAILED ${job.name}; retaining batch data for inspection.`);
    process.exit(1);
  }
  const report = JSON.parse(fs.readFileSync(path.join(harness, 'results', `report-${runId}.json`), 'utf8'));
  if (report.result.measuredUploadAttempts !== 100 || report.result.measuredUploadFailures !== 0 || report.result.uploadCleanupFailures !== 0 || report.verificationExitCode !== 0) {
    state.status = 'failed'; state.failed.push(job.name); save();
    throw new Error(`Invalid sample count, failed upload, or integrity check for ${job.name}`);
  }
  fs.rmSync(path.join(root, 'ops', `verify-${runId}.bin`), { force: true });
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.bin`), { force: true });
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.json`), { force: true });
  state.completed.push(job.name); state.failed = state.failed.filter((name) => name !== job.name); save();
  log(`Passed ${job.name}: n=100 p95=${report.result.measuredUploadLatencyMs.p95}ms`);
}

state.status = 'complete'; state.current = null; state.generatedDataRemoved = true; save();
fs.rmSync(root, { recursive: true, force: true });
log(`All ${state.total} upload repeat jobs passed: ${output}`);
