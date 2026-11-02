import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const batch = process.env.REVIEW_BATCH || `review-20261031-${Date.now()}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid REVIEW_BATCH');
const output = path.join(harness, 'results', batch);
const data = path.join(harness, 'uploads/data', batch);
fs.mkdirSync(output, { recursive: true });
const stateFile = path.join(output, 'state.json');
const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile)) : { batch, completed: [], failed: [], status: 'running', current: null };
const save = () => fs.writeFileSync(stateFile, JSON.stringify(state, null, 2));
fs.writeFileSync(path.join(output, 'run.pid'), String(process.pid));
const logFile = path.join(output, 'run.log');
function log(text) { fs.appendFileSync(logFile, `${new Date().toISOString()} ${text}\n`); console.log(text); }
if (['complete', 'complete_with_findings'].includes(state.status)) { log(`Already complete: ${output}`); process.exit(0); }
for (const folder of ['catalog', 'ops', 'seed/Nested']) fs.mkdirSync(path.join(data, folder), { recursive: true });
for (let i = 0; i < 5000; i++) fs.writeFileSync(path.join(data, 'catalog', `item-${String(i).padStart(5, '0')}.txt`), 'fixture');
fs.writeFileSync(path.join(data, 'seed/Nested/keep.txt'), 'nested fixture');
const jobs = [
  ...[1, 2, 3].flatMap(i => [
    { name: `baseline-off-${i}`, profile: 'baseline', diagnostic: false },
    { name: `baseline-on-${i}`, profile: 'baseline', diagnostic: true },
  ]),
  { name: 'concurrent-100', profile: 'concurrent' },
  { name: 'concurrent-500', profile: 'concurrent', iterations: 500 },
  { name: 'baseline-10m', profile: 'baseline', size: 10 },
  { name: 'single-100m', profile: 'smoke', size: 100 },
  { name: 'workflow-smoke', profile: 'smoke', workflow: true, iterations: 1, vus: 1 },
  { name: 'workflow-concurrent', profile: 'concurrent', workflow: true, iterations: 100, vus: 5 },
];
state.total = jobs.length; state.status = 'running'; save();
for (const job of jobs) {
  if (state.completed.includes(job.name)) continue;
  state.current = job.name; save(); log(`Starting ${job.name}`);
  state.attempts ||= {};
  state.attempts[job.name] = (state.attempts[job.name] || 0) + 1;
  const runId = `${batch}-${job.name}-attempt-${state.attempts[job.name]}`;
  state.reportIds ||= {};
  state.reportIds[job.name] = runId;
  save();
  const env = {
    ...process.env, RUN_ID: runId, PROFILE: job.profile,
    UPLOAD_PATH: job.workflow ? batch : `${batch}/ops`,
    FIXTURE_FILE: path.join(harness, `fixtures/payload-${job.size || 1}m.bin`),
    DIAGNOSTICS_ENABLED: String(Boolean(job.diagnostic)),
    REQUEST_TIMEOUT_SECONDS: '15', MAX_DURATION: '3m',
  };
  delete env.P95_LIMIT_MS; delete env.VUS; delete env.ITERATIONS;
  if (job.vus) env.VUS = String(job.vus);
  if (job.iterations) env.ITERATIONS = String(job.iterations);
  if (job.workflow) env.K6_SCRIPT = path.join(harness, 'k6/workflow.js');
  else delete env.K6_SCRIPT;
  const fd = fs.openSync(path.join(output, `${job.name}.log`), 'w');
  const code = await new Promise((resolve, reject) => {
    const child = spawn(path.join(here, 'run-local.sh'), [], { cwd: project, env, stdio: ['ignore', fd, fd] });
    child.once('error', reject); child.once('exit', resolve);
  });
  fs.closeSync(fd);
  if (code !== 0) {
    state.failed.push(job.name); state.status = 'failed'; save();
    log(`FAILED ${job.name}; stopping before expanding workload. See ${output}`); process.exit(1);
  }
  state.failed = state.failed.filter(name => name !== job.name);
  state.completed.push(job.name); save(); log(`Passed ${job.name} (${state.completed.length}/${jobs.length})`);
}
const leftover = fs.readdirSync(path.join(data, 'ops')).filter(name => name.startsWith(`k6-${batch}-workflow`) || name.startsWith('.filebrowser-upload-'));
state.workflowLeftovers = leftover;
if (leftover.length) { state.status = 'failed'; save(); throw new Error('Workflow left temporary entries'); }
state.status = 'complete'; state.current = null; save();
// Only this batch's generated fixture tree is removed; reports and older runs stay intact.
fs.rmSync(data, { recursive: true, force: true });
log(`All ${jobs.length} jobs passed; generated batch data removed. Reports: ${output}`);
