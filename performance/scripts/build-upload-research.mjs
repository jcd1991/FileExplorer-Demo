import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..');
const results = path.join(harness, 'results');
const batches = [
  { name: 'upload-tuning-20261031', kind: 'upload' },
  { name: 'upload-server-timings-20261031', kind: 'timing' },
  { name: 'download-p95-20261031', kind: 'download' },
];
const summary = { generatedAt: new Date().toISOString(), batches: {} };

for (const batch of batches) {
  const directory = path.join(results, batch.name);
  const state = JSON.parse(fs.readFileSync(path.join(directory, 'state.json'), 'utf8'));
  if (state.status !== 'complete') throw new Error(`${batch.name} is not complete`);
  summary.batches[batch.name] = [];
  for (const job of state.completed) {
    const reportId = batch.kind === 'upload' ? state.reportIds[job] : state.runIds[job];
    const metadataFile = path.join(results, `metadata-${reportId}.json`);
    const metadata = JSON.parse(fs.readFileSync(metadataFile, 'utf8'));
    const concurrencyJob = batch.kind === 'upload' && job.includes('concurrency');
    metadata.iterations = concurrencyJob ? 13 : 100;
    metadata.warmupIterations = 2;
    metadata.concurrency = concurrencyJob ? 8 : 1;
    fs.writeFileSync(metadataFile, `${JSON.stringify(metadata, null, 2)}\n`);
    const rawSummary = path.join(results, `k6-${reportId}.json`);
    const reportFile = path.join(results, `report-${reportId}.json`);
    const summarized = spawnSync(process.execPath, [path.join(here, 'summarize.mjs')], {
      encoding: 'utf8',
      env: { ...process.env, RAW_SUMMARY: rawSummary, REPORT_FILE: reportFile, METADATA_FILE: metadataFile },
    });
    if (summarized.status !== 0) throw new Error(`Could not summarize ${reportId}: ${summarized.stderr}`);
    const report = JSON.parse(fs.readFileSync(reportFile, 'utf8'));
    summary.batches[batch.name].push({ job, reportId, metadata: report, timing: batch.kind === 'timing' ? JSON.parse(fs.readFileSync(path.join(directory, `${job}.json`), 'utf8')) : undefined });
  }
}

const output = path.join(results, 'upload-research-summary-20261031.json');
fs.writeFileSync(output, `${JSON.stringify(summary, null, 2)}\n`);
console.log(`Wrote ${output}`);
