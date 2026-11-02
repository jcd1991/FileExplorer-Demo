import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const batch = process.env.SCAN_BATCH || `scan-cache-${Date.now()}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid SCAN_BATCH');
const seed = Number(process.env.SCAN_SEED || 20261031);
const output = path.join(harness, 'results', batch);
const root = path.join(harness, 'uploads/data', batch);
fs.mkdirSync(output, { recursive: true });
fs.mkdirSync(path.join(root, 'catalog'), { recursive: true });
fs.mkdirSync(path.join(root, 'ops'), { recursive: true });
fs.mkdirSync(path.join(root, 'seed/Nested'), { recursive: true });
fs.writeFileSync(path.join(harness, 'uploads/.harness-owned'), '');
const stateFile = path.join(output, 'state.json');
const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : { batch, seed, completed: [], failed: [], status: 'running' };
const save = () => fs.writeFileSync(stateFile, `${JSON.stringify(state, null, 2)}\n`);
const log = (message) => { fs.appendFileSync(path.join(output, 'run.log'), `${new Date().toISOString()} ${message}\n`); console.log(message); };
fs.writeFileSync(path.join(output, 'run.pid'), `${process.pid}\n`);
if (state.status === 'complete') { log(`Already complete: ${output}`); process.exit(0); }

if (!state.fixturesCreated) {
  for (let index = 0; index < 5000; index++)
    fs.writeFileSync(path.join(root, 'catalog', `item-${String(index).padStart(5, '0')}.txt`), 'fixture');
  fs.writeFileSync(path.join(root, 'seed/Nested/keep.txt'), 'nested fixture');
  state.fixturesCreated = true;
}

let randomState = seed >>> 0;
const random = () => { randomState = (1664525 * randomState + 1013904223) >>> 0; return randomState / 0x100000000; };
const variants = [
  { id: 'views-2-bytes-64m', maxViews: 2, maxBytes: 64 * 1024 * 1024 },
  { id: 'views-16-bytes-8m', maxViews: 16, maxBytes: 8 * 1024 * 1024 },
  { id: 'views-16-bytes-64m', maxViews: 16, maxBytes: 64 * 1024 * 1024 },
];
const plannedJobs = [];
for (let repeat = 1; repeat <= 3; repeat++) {
  const order = [...variants];
  for (let index = order.length - 1; index > 0; index--) {
    const other = Math.floor(random() * (index + 1));
    [order[index], order[other]] = [order[other], order[index]];
  }
  for (const variant of order) plannedJobs.push({ ...variant, repeat, name: `${variant.id}-rep-${repeat}` });
}
state.design = { seed, clients: 5, workflows: 100, catalogFiles: 5000, repetitionsPerVariant: 3, cacheEstimateBytesPerEntry: '128 + 2 * (path characters + name characters)', variants };
state.plannedJobs ||= plannedJobs;
state.total = state.plannedJobs.length; state.status = 'running'; save();
log(`Seed ${seed}; ${state.total} randomized cache jobs, each with five VUs and 100 mixed workflows.`);

for (const job of state.plannedJobs) {
  if (state.completed.includes(job.name)) continue;
  state.current = job.name; state.attempts ||= {}; state.attempts[job.name] = (state.attempts[job.name] || 0) + 1;
  const runId = `${batch}-${job.name}-attempt-${state.attempts[job.name]}`;
  state.reportIds ||= {}; state.reportIds[job.name] = runId; save(); log(`Starting ${job.name}`);
  const env = {
    ...process.env,
    RUN_ID: runId, PROFILE: 'concurrent',
    K6_SCRIPT: path.join(harness, 'k6/workflow.js'),
    UPLOAD_PATH: batch, FIXTURE_FILE: path.join(harness, 'fixtures/payload-1m.bin'),
    VUS: '5', ITERATIONS: '100', REQUEST_TIMEOUT_SECONDS: '30', MAX_DURATION: '10m',
    DIAGNOSTICS_ENABLED: 'true',
    'Logging__LogLevel__TestProject.Services.FileSystemService': 'Debug',
    FileBrowser__SnapshotCacheMaxViews: String(job.maxViews),
    FileBrowser__SnapshotCacheMaxBytes: String(job.maxBytes),
  };
  delete env.P95_LIMIT_MS; delete env.MEASURED_ITERATIONS; delete env.WARMUP_ITERATIONS;
  const fd = fs.openSync(path.join(output, `${job.name}.log`), 'w');
  const exitCode = await new Promise((resolve, reject) => {
    const child = spawn(path.join(here, 'run-local.sh'), [], { cwd: project, env, stdio: ['ignore', fd, fd] });
    child.once('error', reject); child.once('exit', resolve);
  });
  fs.closeSync(fd);
  if (exitCode !== 0) {
    state.status = 'failed'; state.failed.push(job.name); save();
    log(`FAILED ${job.name}; retaining catalog and operation data for inspection.`);
    process.exit(1);
  }
  const report = JSON.parse(fs.readFileSync(path.join(harness, 'results', `report-${runId}.json`), 'utf8'));
  const measured = report.result.completedWorkflows;
  if (measured !== 100 || report.result.workflowSuccessRate !== 1 || report.result.httpFailureRate !== 0 || report.verificationExitCode !== 0) {
    state.status = 'failed'; state.failed.push(job.name); save();
    throw new Error(`Workflow or integrity check failed for ${job.name}`);
  }
  const verifyName = `verify-${runId}.bin`;
  fs.rmSync(path.join(root, verifyName), { force: true });
  fs.rmSync(path.join(harness, 'results', verifyName), { force: true });
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.json`), { force: true });
  state.completed.push(job.name); state.failed = state.failed.filter((name) => name !== job.name); save();
  log(`Passed ${job.name}: workflows=${measured}, snapshot expirations=${report.result.snapshotExpirations}`);
}

state.status = 'complete'; state.current = null; state.generatedDataRemoved = true; save();
fs.rmSync(root, { recursive: true, force: true });
log(`All cache experiments passed: ${output}`);
