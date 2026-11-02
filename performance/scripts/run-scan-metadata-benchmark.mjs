import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const batch = process.env.SCAN_METADATA_BATCH || `scan-metadata-${Date.now()}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid SCAN_METADATA_BATCH');

const output = path.join(harness, 'results', batch);
const uploads = path.join(harness, 'uploads/data');
const root = path.join(uploads, batch);
if (path.dirname(root) !== uploads) throw new Error('Benchmark root escaped the harness upload directory.');
const catalog = path.join(root, 'catalog');
const stateFile = path.join(output, 'state.json');
fs.mkdirSync(output, { recursive: true });
fs.mkdirSync(path.join(harness, 'uploads'), { recursive: true });
fs.writeFileSync(path.join(harness, 'uploads/.harness-owned'), '');

const state = fs.existsSync(stateFile)
  ? JSON.parse(fs.readFileSync(stateFile, 'utf8'))
  : { batch, completed: [], status: 'running', plannedRuns: [1, 2, 3] };
const save = () => fs.writeFileSync(stateFile, `${JSON.stringify(state, null, 2)}\n`);
const log = message => {
  fs.appendFileSync(path.join(output, 'run.log'), `${new Date().toISOString()} ${message}\n`);
  console.log(message);
};
fs.writeFileSync(path.join(output, 'run.pid'), `${process.pid}\n`);
if (state.status === 'complete') {
  log(`Already complete: ${output}`);
  process.exit(0);
}

fs.mkdirSync(catalog, { recursive: true });
state.total = state.plannedRuns.length;
state.status = 'running';
state.current ||= 'fixtures';
save();
if (!state.fixturesCreated) {
  for (let index = 0; index < 5000; index++) {
    const file = path.join(catalog, `item-${String(index).padStart(5, '0')}.txt`);
    fs.writeFileSync(file, 'fixture');
  }
  state.fixturesCreated = true;
  state.design = {
    catalogFiles: 5000,
    virtualUsers: 5,
    iterationsPerUser: 100,
    warmupIterationsPerUser: 2,
    measuredSamplesPerOperation: 490,
    repetitions: 3,
  };
  state.current = null;
  save();
}

const runLocal = path.join(here, 'run-local.sh');
const runResults = [];
state.status = 'running';
save();

for (const repetition of state.plannedRuns) {
  if (state.completed.includes(repetition)) continue;
  state.current = repetition;
  state.attempts ||= {};
  state.attempts[repetition] = (state.attempts[repetition] || 0) + 1;
  const runId = `${batch}-rep-${repetition}-attempt-${state.attempts[repetition]}`;
  state.runIds ||= {};
  state.runIds[repetition] = runId;
  save();
  log(`Starting repetition ${repetition}`);

  const childEnv = {
    ...process.env,
    RUN_ID: runId,
    PROFILE: 'concurrent',
    UPLOAD_PATH: `${batch}/catalog`,
    K6_SCRIPT: path.join(harness, 'k6/scan-only.js'),
    VUS: '5',
    ITERATIONS: '100',
    SCAN_WARMUP_ITERATIONS: '2',
    WARMUP_ITERATIONS: '2',
    REQUEST_TIMEOUT_SECONDS: '30',
    MAX_DURATION: '10m',
    DIAGNOSTICS_ENABLED: 'true',
    'Logging__LogLevel__TestProject.Services.FileSystemService': 'Debug',
  };
  const logPath = path.join(output, `rep-${repetition}.log`);
  const fd = fs.openSync(logPath, 'w');
  let exitCode;
  try {
    exitCode = await new Promise((resolve, reject) => {
      const child = spawn(runLocal, [], { cwd: project, env: childEnv, stdio: ['ignore', fd, fd] });
      child.once('error', reject);
      child.once('exit', resolve);
    });
  } catch (error) {
    state.status = 'failed';
    state.failedRun = repetition;
    save();
    log(`Could not start repetition ${repetition}; generated files are retained for inspection.`);
    throw error;
  } finally {
    fs.closeSync(fd);
  }
  const verificationName = `verify-${runId}.bin`;
  fs.rmSync(path.join(catalog, verificationName), { force: true });
  fs.rmSync(path.join(harness, 'results', verificationName), { force: true });
  fs.rmSync(path.join(harness, 'results', `verify-${runId}.json`), { force: true });
  if (exitCode !== 0) {
    state.status = 'failed';
    state.failedRun = repetition;
    save();
    log(`Failed repetition ${repetition}; generated files are retained for inspection.`);
    process.exit(1);
  }

  const reportPath = path.join(harness, 'results', `report-${runId}.json`);
  const report = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
  const browseP95 = report.k6?.browse_latency_ms?.['p(95)'];
  const searchP95 = report.k6?.search_latency_ms?.['p(95)'];
  if (report.result.httpFailureRate !== 0 || report.result.failedChecks !== 0 || !Number.isFinite(browseP95) || !Number.isFinite(searchP95)) {
    state.status = 'failed';
    state.failedRun = repetition;
    save();
    throw new Error(`Invalid metrics or failed checks in repetition ${repetition}.`);
  }

  const serverLog = fs.readFileSync(path.join(harness, 'results', `server-${runId}.log`), 'utf8');
  const serverP95 = pattern => {
    const durations = [...serverLog.matchAll(pattern)].map(match => Number(match[1])).sort((left, right) => left - right);
    return durations.length === 0 ? null : durations[Math.ceil(durations.length * 0.95) - 1];
  };
  const result = {
    repetition,
    runId,
    browseP95Ms: browseP95,
    searchP95Ms: searchP95,
    serverBrowseScanP95Ms: serverP95(/File browser directory scan completed; DurationMs=([\d.]+)/g),
    serverSearchScanP95Ms: serverP95(/File browser search scan completed; DurationMs=([\d.]+)/g),
    serverResponsePreparationP95Ms: serverP95(/File browser response prepared; DurationMs=([\d.]+)/g),
  };
  if (!Number.isFinite(result.serverBrowseScanP95Ms) ||
      !Number.isFinite(result.serverSearchScanP95Ms) ||
      !Number.isFinite(result.serverResponsePreparationP95Ms)) {
    state.status = 'failed';
    state.failedRun = repetition;
    save();
    throw new Error(`Missing server phase timings in repetition ${repetition}.`);
  }
  runResults.push(result);
  state.completed.push(repetition);
  state.results = [...(state.results || []).filter(item => item.repetition !== repetition), result];
  save();
  log(`Passed repetition ${repetition}: browse p95=${browseP95} ms; search p95=${searchP95} ms.`);
}

const sorted = values => [...values].sort((left, right) => left - right);
const median = values => sorted(values)[Math.floor(values.length / 2)];
const results = state.results || runResults;
state.summary = {
  medianRunP95Ms: {
    browse: median(results.map(result => result.browseP95Ms)),
    search: median(results.map(result => result.searchP95Ms)),
    serverBrowseScan: median(results.map(result => result.serverBrowseScanP95Ms)),
    serverSearchScan: median(results.map(result => result.serverSearchScanP95Ms)),
    serverResponsePreparation: median(results.map(result => result.serverResponsePreparationP95Ms)),
  },
};
delete state.failedRun;
state.status = 'complete';
state.current = null;
state.generatedDataRemoved = true;
save();
fs.rmSync(root, { recursive: true, force: true });
log(`Completed three scan repetitions. Median run-level p95: ${JSON.stringify(state.summary.medianRunP95Ms)}.`);
