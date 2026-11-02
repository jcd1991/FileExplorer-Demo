import fs from 'node:fs';
import os from 'node:os';

const rawPath = process.env.RAW_SUMMARY;
const outputPath = process.env.REPORT_FILE;
const metadataPath = process.env.METADATA_FILE;
const raw = JSON.parse(fs.readFileSync(rawPath, 'utf8'));
const metadata = JSON.parse(fs.readFileSync(metadataPath, 'utf8'));
const metrics = raw.metrics ?? {};
const getValues = (name) => metrics[name]?.values ?? metrics[name] ?? {};
const elapsedSeconds = Math.max(metadata.elapsedMs / 1000, 0.001);
const measuredUploadSuccesses = getValues('measured_upload_success').passes ?? 0;
const measuredUploadFailures = getValues('measured_upload_success').fails ?? 0;
const measuredDownloadSuccesses = getValues('measured_download_success').passes ?? 0;
const measuredDownloadFailures = getValues('measured_download_success').fails ?? 0;
const successes = getValues('successful_uploads').count ?? measuredUploadSuccesses + measuredDownloadSuccesses;
const failures = getValues('failed_uploads').count ?? measuredUploadFailures + measuredDownloadFailures;
const attempts = successes + failures;
const bytes = getValues('confirmed_payload_bytes').count ?? successes * (metadata.fixtureBytes ?? 0);
const successRatePerSecond = getValues('successful_uploads').rate ?? successes / elapsedSeconds;
const byteRatePerSecond = getValues('confirmed_payload_bytes').rate ?? bytes / elapsedSeconds;
const report = {
  ...metadata,
  host: { ...metadata.host, platform: os.platform(), architecture: os.arch(), cpus: os.cpus().length },
  result: {
    attempts,
    successes,
    failures,
    confirmedPayloadBytes: bytes,
    uploadsPerSecond: successRatePerSecond,
    payloadMiBPerSecond: byteRatePerSecond / 1048576,
    latencyMs: {
      p50: getValues('upload_latency_ms').med ?? getValues('measured_upload_latency_ms').med ?? getValues('measured_download_latency_ms').med ?? null,
      p95: getValues('upload_latency_ms')['p(95)'] ?? getValues('measured_upload_latency_ms')['p(95)'] ?? getValues('measured_download_latency_ms')['p(95)'] ?? null,
      p99: getValues('upload_latency_ms')['p(99)'] ?? getValues('measured_upload_latency_ms')['p(99)'] ?? getValues('measured_download_latency_ms')['p(99)'] ?? null,
      average: getValues('upload_latency_ms').avg ?? getValues('measured_upload_latency_ms').avg ?? getValues('measured_download_latency_ms').avg ?? null,
    },
    harnessElapsedSeconds: elapsedSeconds,
    httpRequests: getValues('http_reqs').count ?? 0,
    httpFailureRate: getValues('http_req_failed').value ?? getValues('http_req_failed').rate ?? null,
    failedChecks: getValues('checks').fails ?? null,
    ...(metrics.measured_upload_success ? {
      measuredUploadAttempts: getValues('measured_upload_success').passes + getValues('measured_upload_success').fails,
      measuredUploadFailures: getValues('measured_upload_success').fails,
      uploadCleanupFailures: getValues('upload_cleanup_success').fails,
      measuredUploadLatencyMs: { p50: getValues('measured_upload_latency_ms').med ?? null, p95: getValues('measured_upload_latency_ms')['p(95)'] ?? null, p99: getValues('measured_upload_latency_ms')['p(99)'] ?? null, average: getValues('measured_upload_latency_ms').avg ?? null },
      warmupUploadLatencyMs: { p50: getValues('warmup_upload_latency_ms').med ?? null, p95: getValues('warmup_upload_latency_ms')['p(95)'] ?? null },
    } : {}),
    ...(metrics.measured_download_success ? {
      measuredDownloadAttempts: getValues('measured_download_success').passes + getValues('measured_download_success').fails,
      measuredDownloadFailures: getValues('measured_download_success').fails,
      measuredDownloadLatencyMs: { p50: getValues('measured_download_latency_ms').med ?? null, p95: getValues('measured_download_latency_ms')['p(95)'] ?? null, p99: getValues('measured_download_latency_ms')['p(99)'] ?? null, average: getValues('measured_download_latency_ms').avg ?? null },
      warmupDownloadLatencyMs: { p50: getValues('warmup_download_latency_ms').med ?? null, p95: getValues('warmup_download_latency_ms')['p(95)'] ?? null },
    } : {}),
    ...(metrics.workflow_success ? {
      completedWorkflows: getValues('completed_workflows').count ?? 0,
      workflowSuccessRate: getValues('workflow_success').value ?? getValues('workflow_success').rate ?? null,
      snapshotExpirations: getValues('snapshot_expired').count ?? 0,
    } : {}),
  },
  k6: metrics,
};
fs.writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify({ runId: report.runId, profile: report.profile, ...report.result }, null, 2));
