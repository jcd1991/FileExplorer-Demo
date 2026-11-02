import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const project = path.resolve(harness, '../TestProject');
const uploads = path.join(harness, 'uploads');
const dataRoot = path.join(uploads, 'data');
const stamp = new Date().toISOString().replaceAll(/[-:.TZ]/g, '').slice(0, 14);
const batch = process.env.SEARCH_EXCLUSION_BATCH || `search-exclusions-${stamp}`;
if (!/^[a-zA-Z0-9-]+$/.test(batch)) throw new Error('Invalid SEARCH_EXCLUSION_BATCH.');
if (!fs.existsSync(path.join(uploads, '.harness-owned'))) throw new Error('Upload harness marker is missing.');
const results = path.join(harness, 'results', batch);
const stateFile = path.join(results, 'state.json');
const logFile = path.join(results, 'run.log');
const serverLog = path.join(results, 'server.log');
const baseUrl = process.env.BASE_URL || 'http://127.0.0.1:5194';
const k6Script = path.join(harness, 'k6/search-exclusion.js');
fs.mkdirSync(results, { recursive: true });
const existingState = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : null;
const existingFixture = existingState?.fixtureDirectory
  ? path.join(dataRoot, existingState.fixtureDirectory)
  : existingState?.rootRetainedForInspection;
const root = existingFixture || fs.mkdtempSync(path.join(dataRoot, `${batch}-`));
if (path.dirname(root) !== dataRoot || !path.basename(root).startsWith(`${batch}-`)) throw new Error('Benchmark root escaped the marked data directory.');
const state = existingState || { batch, current: null, completed: [], total: 12, fixtureFiles: 11000 };
state.status = 'preparing';
state.fixtureDirectory = path.basename(root);
delete state.error;
const save = () => fs.writeFileSync(stateFile, `${JSON.stringify(state, null, 2)}\n`);
const log = message => {
  const line = `${new Date().toISOString()} ${message}\n`;
  fs.appendFileSync(logFile, line);
  process.stdout.write(message + '\n');
};
fs.writeFileSync(path.join(results, 'run.pid'), `${process.pid}\n`);
save();

function makeFiles(directory, group, count) {
  let currentDirectory = '';
  for (let index = 0; index < count; index++) {
    const bucket = Math.floor(index / 100);
    const relativeDirectory = path.join(directory, `${group}-${String(bucket).padStart(3, '0')}`, 'source', 'deep');
    if (relativeDirectory !== currentDirectory) {
      fs.mkdirSync(path.join(root, relativeDirectory), { recursive: true });
      currentDirectory = relativeDirectory;
    }
    fs.writeFileSync(path.join(root, relativeDirectory, `needle-${group}-${String(index).padStart(5, '0')}.txt`), 'fixture');
  }
}

function startServer() {
  const env = {
    ...process.env,
    FileBrowser__RootPath: root,
    Diagnostics__Enabled: 'true',
    'Logging__LogLevel__TestProject.Services.FileSystemService': 'Debug',
    ASPNETCORE_ENVIRONMENT: 'Production',
  };
  const logStream = fs.createWriteStream(serverLog, { flags: 'a' });
  const child = spawn('dotnet', [path.join(project, 'bin/Release/net8.0/TestProject.dll'), '--urls', baseUrl], {
    cwd: project,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.pipe(logStream);
  child.stderr.pipe(logStream);
  return { child, logStream };
}

async function waitForServer(child) {
  for (let attempt = 0; attempt < 60; attempt++) {
    if (child.exitCode !== null) throw new Error(`Local app exited during startup; see ${serverLog}.`);
    try {
      const response = await fetch(`${baseUrl}/api/config`, { signal: AbortSignal.timeout(1000) });
      if (response.ok) return;
    } catch { /* Wait for Kestrel to bind. */ }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error(`Local app did not become ready; see ${serverLog}.`);
}

async function stopServer(server) {
  if (!server || server.child.exitCode !== null) return;
  server.child.kill('SIGTERM');
  await new Promise(resolve => server.child.once('exit', resolve));
  server.logStream.end();
}

function p95OfServerScans(logText) {
  const values = [...logText.matchAll(/File browser search scan completed; DurationMs=([\d.]+); Inspected=(\d+); Matches=(\d+); Partial=(True|False)/g)]
    .map(match => ({ durationMs: Number(match[1]), inspected: Number(match[2]), matches: Number(match[3]), partial: match[4] === 'True' }))
    .sort((left, right) => left.durationMs - right.durationMs);
  if (values.length === 0) throw new Error('No server scan measurements found in the captured log.');
  return {
    samples: values.length,
    p95Ms: values[Math.ceil(values.length * 0.95) - 1].durationMs,
    inspectedEntries: values[0].inspected,
    matchCount: values[0].matches,
    partialScans: values.filter(value => value.partial).length,
  };
}

async function runK6(server, vuCount, iterations, repetition, mode) {
  const runId = `${vuCount}vus-rep${repetition}-${mode}`;
  const existingRun = state.completed.find(run => run.runId === runId);
  if (existingRun) return existingRun;
  state.current = runId;
  save();
  const logStart = fs.statSync(serverLog).size;
  const summaryPath = path.join(results, `k6-${runId}.json`);
  const output = fs.createWriteStream(path.join(results, `k6-${runId}.log`));
  const env = {
    ...process.env,
    BASE_URL: baseUrl,
    SKIP_GENERATED: String(mode === 'on'),
    EXPECTED_FILES: String(mode === 'on' ? 1000 : 11000),
    VUS: String(vuCount),
    ITERATIONS: String(iterations),
    WARMUP_ITERATIONS: '2',
  };
  const args = ['run', '--summary-trend-stats=avg,min,med,max,p(95),p(99)', '--summary-export', summaryPath, k6Script];
  const exitCode = await new Promise((resolve, reject) => {
    const child = spawn('k6', args, { cwd: project, env, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.pipe(output);
    child.stderr.pipe(output);
    child.once('error', reject);
    child.once('exit', resolve);
  });
  output.end();
  if (exitCode !== 0) throw new Error(`k6 failed for ${runId}; inspect ${path.join(results, `k6-${runId}.log`)}.`);
  await new Promise(resolve => setTimeout(resolve, 100));
  const serverText = fs.readFileSync(serverLog, 'utf8').slice(logStart);
  const scans = p95OfServerScans(serverText);
  const summary = JSON.parse(fs.readFileSync(summaryPath, 'utf8'));
  const metrics = summary.metrics;
  const result = {
    runId,
    mode,
    vus: vuCount,
    iterationsPerVu: iterations,
    measuredSamples: Math.max(0, iterations - 2) * vuCount,
    clientP50Ms: metrics.search_latency_ms.med ?? metrics.search_latency_ms.values?.med,
    clientP95Ms: metrics.search_latency_ms['p(95)'] ?? metrics.search_latency_ms.values?.['p(95)'],
    httpFailureRate: metrics.http_req_failed.value ?? metrics.http_req_failed.values?.rate,
    checkFailureRate: metrics.checks.fails / (metrics.checks.passes + metrics.checks.fails),
    ...scans,
  };
  if (result.httpFailureRate !== 0 || result.checkFailureRate !== 0 ||
      !Number.isFinite(result.clientP95Ms) || !Number.isFinite(result.p95Ms) ||
      scans.partialScans > 0 || scans.matchCount !== (mode === 'on' ? 1000 : 11000)) {
    throw new Error(`Invalid or incomplete metrics for ${runId}.`);
  }
  state.completed.push(result);
  save();
  log(`Passed ${runId}: client p95 ${result.clientP95Ms} ms; server scan p95 ${result.p95Ms} ms; inspected ${result.inspectedEntries}; matches ${result.matchCount}.`);
  return result;
}

let server;
try {
  if (await fetch(`${baseUrl}/api/config`, { signal: AbortSignal.timeout(500) }).then(response => response.ok, () => false)) {
    throw new Error(`A service is already answering at ${baseUrl}; refusing to test an unknown process.`);
  }
  if (!state.fixtureReady) {
    makeFiles('src', 'ordinary', 1000);
    makeFiles(path.join('node_modules'), 'dependency', 2500);
    makeFiles(path.join('bin'), 'build', 2500);
    makeFiles(path.join('obj'), 'intermediate', 2500);
    makeFiles(path.join('.git'), 'version-control', 2500);
    state.fixtureReady = true;
  }
  state.status = 'running';
  state.host = { cpuCount: os.cpus().length, memoryGiB: Math.round(os.totalmem() / 1024 ** 3), k6: 'v2.3.0' };
  save();

  server = startServer();
  await waitForServer(server.child);
  const resultsByMode = { off: [], on: [] };
  for (let repetition = 1; repetition <= 3; repetition++) {
    const modes = repetition % 2 === 1 ? ['off', 'on'] : ['on', 'off'];
    for (const mode of modes) resultsByMode[mode].push(await runK6(server, 5, 40, repetition, mode));
  }
  for (let repetition = 1; repetition <= 3; repetition++) {
    const modes = repetition % 2 === 1 ? ['off', 'on'] : ['on', 'off'];
    for (const mode of modes) resultsByMode[mode].push(await runK6(server, 20, 10, repetition, mode));
  }

  await stopServer(server);
  server = null;
  const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
  const cohortSummary = {};
  for (const [mode, runs] of Object.entries(resultsByMode)) {
    cohortSummary[mode] = {};
    for (const vus of [5, 20]) {
      const cohort = runs.filter(run => run.vus === vus);
      cohortSummary[mode][`${vus}vus`] = {
        medianRunClientP50Ms: median(cohort.map(run => run.clientP50Ms)),
        medianRunClientP95Ms: median(cohort.map(run => run.clientP95Ms)),
        medianRunServerScanP95Ms: median(cohort.map(run => run.p95Ms)),
        inspectedEntries: cohort[0].inspectedEntries,
        matchCount: cohort[0].matchCount,
        totalMeasuredSamples: cohort.reduce((sum, run) => sum + run.measuredSamples, 0),
      };
    }
  }
  const summary = { batch, host: state.host, fixture: { ordinaryFiles: 1000, generatedFiles: 10000, normalFolders: 10, generatedTrees: ['node_modules', 'bin', 'obj', '.git'] }, summary: cohortSummary, runs: state.completed };
  fs.writeFileSync(path.join(results, 'summary.json'), `${JSON.stringify(summary, null, 2)}\n`);
  state.summary = cohortSummary;
  state.status = 'complete';
  state.generatedDataRemoved = true;
  state.current = null;
  delete state.fixtureDirectory;
  delete state.rootRetainedForInspection;
  save();
  fs.rmSync(root, { recursive: true, force: true });
  log(`Completed paired search runs. Summary: ${path.join(results, 'summary.json')}`);
} catch (error) {
  state.status = 'failed';
  state.error = error.message;
  state.fixtureDirectory = path.basename(root);
  save();
  log(`Failed: ${error.message}`);
  process.exitCode = 1;
} finally {
  if (server) await stopServer(server);
}
