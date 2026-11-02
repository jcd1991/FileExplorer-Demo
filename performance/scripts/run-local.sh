#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$(cd "$HARNESS_DIR/../TestProject" && pwd)"
BASE_URL="${BASE_URL:-http://127.0.0.1:5178}"
PROFILE="${PROFILE:-smoke}"
DIAGNOSTICS_ENABLED="${DIAGNOSTICS_ENABLED:-false}"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"
K6_BIN="${K6_BIN:-k6}"
DOTNET_COUNTERS_BIN="${DOTNET_COUNTERS_BIN:-}"
[[ -n "$DOTNET_COUNTERS_BIN" ]] || DOTNET_COUNTERS_BIN="$(command -v dotnet-counters 2>/dev/null || true)"
FIXTURE_FILE="${FIXTURE_FILE:-$HARNESS_DIR/fixtures/payload-1m.bin}"
K6_SCRIPT="${K6_SCRIPT:-$HARNESS_DIR/k6/upload.js}"
UPLOAD_PATH="${UPLOAD_PATH:-}"
RUN_ID="${RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)-$$}"
RESULTS_DIR="$HARNESS_DIR/results"
UPLOAD_ROOT="$HARNESS_DIR/uploads"
UPLOAD_DIR="$UPLOAD_ROOT/data"
UPLOAD_COPY_BUFFER_BYTES="${UPLOAD_COPY_BUFFER_BYTES:-65536}"
UPLOAD_STREAM_BUFFER_BYTES="${UPLOAD_STREAM_BUFFER_BYTES:-4096}"
MAX_CONCURRENT_UPLOADS="${MAX_CONCURRENT_UPLOADS:-0}"

node -e 'const u=new URL(process.argv[1]); if(u.protocol!=="http:" || !["127.0.0.1","localhost","[::1]"].includes(u.hostname) || u.pathname!=="/" || u.search || u.hash) process.exit(1)' "$BASE_URL" || {
  printf 'BASE_URL must be a plain HTTP loopback URL.\n' >&2
  exit 2
}
command -v "$DOTNET_BIN" >/dev/null || { printf 'Install the .NET 8 SDK or set DOTNET_BIN.\n' >&2; exit 2; }
command -v "$K6_BIN" >/dev/null || { printf 'Install k6 or set K6_BIN.\n' >&2; exit 2; }
[[ -f "$PROJECT_DIR/bin/Release/net8.0/TestProject.dll" ]] || {
  printf 'Build first: dotnet build TestProject/TestProject.sln -c Release\n' >&2
  exit 2
}

"$SCRIPT_DIR/generate-fixtures.sh"
[[ -f "$FIXTURE_FILE" ]] || { printf 'Fixture not found: %s\n' "$FIXTURE_FILE" >&2; exit 2; }
[[ -f "$K6_SCRIPT" ]] || { printf 'Local k6 script not found: %s\n' "$K6_SCRIPT" >&2; exit 2; }
mkdir -p "$RESULTS_DIR" "$UPLOAD_DIR"
touch "$UPLOAD_ROOT/.harness-owned"
RAW_SUMMARY="$RESULTS_DIR/k6-${RUN_ID}.json"
REPORT_FILE="$RESULTS_DIR/report-${RUN_ID}.json"
METADATA_FILE="$RESULTS_DIR/metadata-${RUN_ID}.json"
SERVER_LOG="$RESULTS_DIR/server-${RUN_ID}.log"
RUNTIME_METRICS="$RESULTS_DIR/runtime-${RUN_ID}.csv"
PROCESS_METRICS="$RESULTS_DIR/process-${RUN_ID}.csv"
DISK_METRICS="$RESULTS_DIR/disk-${RUN_ID}.txt"
REQUEST_TIMEOUT_SECONDS="${REQUEST_TIMEOUT_SECONDS:-120}"
P95_LIMIT_MS="${P95_LIMIT_MS:-}"
VUS="${VUS:-}"
ITERATIONS="${ITERATIONS:-}"
MAX_DURATION="${MAX_DURATION:-30m}"

if curl --silent --fail --max-time 1 "$BASE_URL/api/config" >/dev/null; then
  printf 'A service is already answering at %s; refusing to test an unknown process.\n' "$BASE_URL" >&2
  exit 2
fi
cd "$PROJECT_DIR"
FileBrowser__RootPath="$UPLOAD_DIR" Diagnostics__Enabled="$DIAGNOSTICS_ENABLED" \
FileBrowser__UploadCopyBufferBytes="$UPLOAD_COPY_BUFFER_BYTES" \
FileBrowser__UploadStreamBufferBytes="$UPLOAD_STREAM_BUFFER_BYTES" \
FileBrowser__MaxConcurrentUploads="$MAX_CONCURRENT_UPLOADS" \
  "$DOTNET_BIN" "$PROJECT_DIR/bin/Release/net8.0/TestProject.dll" --urls "$BASE_URL" >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
stop_server() {
  [[ -z "${SAMPLE_PID:-}" ]] || { kill "$SAMPLE_PID" 2>/dev/null || true; wait "$SAMPLE_PID" 2>/dev/null || true; }
  [[ -z "${COUNTERS_PID:-}" ]] || { kill -TERM "$COUNTERS_PID" 2>/dev/null || true; wait "$COUNTERS_PID" 2>/dev/null || true; }
  [[ -z "${IOSTAT_PID:-}" ]] || { kill "$IOSTAT_PID" 2>/dev/null || true; wait "$IOSTAT_PID" 2>/dev/null || true; }
  kill "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
}
trap stop_server EXIT INT TERM

ready=false
for attempt in {1..60}; do
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    printf 'Local app exited during startup. See %s\n' "$SERVER_LOG" >&2
    exit 1
  fi
  if curl --silent --fail --max-time 1 "$BASE_URL/api/config" >/dev/null; then ready=true; break; fi
  sleep 1
done
[[ "$ready" == true ]] || { printf 'Local app did not become ready. See %s\n' "$SERVER_LOG" >&2; exit 1; }
printf 'epoch,cpu_percent,rss_kib\n' > "$PROCESS_METRICS"
(
  while kill -0 "$SERVER_PID" 2>/dev/null; do
    sample="$(ps -p "$SERVER_PID" -o %cpu=,rss= 2>/dev/null | awk 'NF==2 {print $1 "," $2}')"
    [[ -z "$sample" ]] || printf '%s,%s\n' "$(date +%s)" "$sample"
    sleep 1
  done
) >> "$PROCESS_METRICS" &
SAMPLE_PID=$!
if [[ -n "$DOTNET_COUNTERS_BIN" ]]; then
  "$DOTNET_COUNTERS_BIN" collect --process-id "$SERVER_PID" --counters System.Runtime --refresh-interval 1 --format csv --output "$RUNTIME_METRICS" > "$RESULTS_DIR/counters-${RUN_ID}.log" 2>&1 &
  COUNTERS_PID=$!
else
  printf 'dotnet-counters not installed; runtime allocation and GC counters unavailable.\n' > "$RESULTS_DIR/counters-${RUN_ID}.log"
fi
iostat -d -w 1 > "$DISK_METRICS" 2>&1 &
IOSTAT_PID=$!

export BASE_URL FIXTURE_FILE K6_SCRIPT UPLOAD_PATH RUN_ID PROFILE REQUEST_TIMEOUT="${REQUEST_TIMEOUT_SECONDS}s" MAX_DURATION
[[ -z "$VUS" ]] || export VUS
[[ -z "$ITERATIONS" ]] || export ITERATIONS
[[ -z "$P95_LIMIT_MS" ]] || export P95_LIMIT_MS
START_MS="$(node -p 'Date.now()')"
set +e
"$K6_BIN" run --summary-trend-stats='avg,min,med,max,p(95),p(99)' --summary-export "$RAW_SUMMARY" "$K6_SCRIPT"
K6_EXIT=$?
END_MS="$(node -p 'Date.now()')"
VERIFY_EXIT=0
"$SCRIPT_DIR/verify-upload.sh" || VERIFY_EXIT=$?
set -e

ELAPSED_MS=$((END_MS - START_MS))
FIXTURE_BYTES="$(wc -c < "$FIXTURE_FILE" | tr -d ' ')"
FIXTURE_SHA256="$(shasum -a 256 "$FIXTURE_FILE" | awk '{print $1}')"
K6_VERSION="$("$K6_BIN" version | head -1)"
DOTNET_VERSION="$("$DOTNET_BIN" --version)"
HOST_MODEL="$(sysctl -n hw.model 2>/dev/null || uname -m)"
CPU_COUNT="$(sysctl -n hw.ncpu 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || printf unknown)"
MEMORY_BYTES="$(sysctl -n hw.memsize 2>/dev/null || printf unknown)"
RUN_ID="$RUN_ID" PROFILE="$PROFILE" BASE_URL="$BASE_URL" FIXTURE_FILE="$FIXTURE_FILE" FIXTURE_BYTES="$FIXTURE_BYTES" \
UPLOAD_COPY_BUFFER_BYTES="$UPLOAD_COPY_BUFFER_BYTES" UPLOAD_STREAM_BUFFER_BYTES="$UPLOAD_STREAM_BUFFER_BYTES" MAX_CONCURRENT_UPLOADS="$MAX_CONCURRENT_UPLOADS" \
FIXTURE_SHA256="$FIXTURE_SHA256" VUS="$VUS" ITERATIONS="$ITERATIONS" REQUEST_TIMEOUT="${REQUEST_TIMEOUT_SECONDS}s" \
DIAGNOSTICS_ENABLED="$DIAGNOSTICS_ENABLED" K6_VERSION="$K6_VERSION" DOTNET_VERSION="$DOTNET_VERSION" \
MEASURED_ITERATIONS="${MEASURED_ITERATIONS:-}" WARMUP_ITERATIONS="${WARMUP_ITERATIONS:-}" \
ELAPSED_MS="$ELAPSED_MS" HOST_MODEL="$HOST_MODEL" CPU_COUNT="$CPU_COUNT" MEMORY_BYTES="$MEMORY_BYTES" \
K6_EXIT="$K6_EXIT" VERIFY_EXIT="$VERIFY_EXIT" UPLOAD_COPY_BUFFER_BYTES="$UPLOAD_COPY_BUFFER_BYTES" UPLOAD_STREAM_BUFFER_BYTES="$UPLOAD_STREAM_BUFFER_BYTES" MAX_CONCURRENT_UPLOADS="$MAX_CONCURRENT_UPLOADS" METADATA_FILE="$METADATA_FILE" node -e 'const fs=require("node:fs"); const m={runId:process.env.RUN_ID,k6ExitCode:Number(process.env.K6_EXIT),verificationExitCode:Number(process.env.VERIFY_EXIT),workload:process.env.K6_SCRIPT.split("/").pop(),uploadCopyBufferBytes:Number(process.env.UPLOAD_COPY_BUFFER_BYTES),uploadStreamBufferBytes:Number(process.env.UPLOAD_STREAM_BUFFER_BYTES),maxConcurrentUploads:Number(process.env.MAX_CONCURRENT_UPLOADS),profile:process.env.PROFILE,baseUrl:process.env.BASE_URL,fixtureFile:process.env.FIXTURE_FILE,fixtureBytes:Number(process.env.FIXTURE_BYTES),fixtureSha256:process.env.FIXTURE_SHA256,concurrency:Number(process.env.VUS||({smoke:1,baseline:1,concurrent:10}[process.env.PROFILE])),iterations:Number(process.env.MEASURED_ITERATIONS||process.env.ITERATIONS||({smoke:1,baseline:20,concurrent:100}[process.env.PROFILE])),warmupIterations:Number(process.env.WARMUP_ITERATIONS||0),requestTimeout:process.env.REQUEST_TIMEOUT,diagnosticsEnabled:process.env.DIAGNOSTICS_ENABLED==="true",k6Version:process.env.K6_VERSION,dotnetVersion:process.env.DOTNET_VERSION,elapsedMs:Number(process.env.ELAPSED_MS),host:{model:process.env.HOST_MODEL,cpuCount:process.env.CPU_COUNT,memoryBytes:process.env.MEMORY_BYTES}}; fs.writeFileSync(process.env.METADATA_FILE,JSON.stringify(m,null,2)+"\n")'
RAW_SUMMARY="$RAW_SUMMARY" REPORT_FILE="$REPORT_FILE" METADATA_FILE="$METADATA_FILE" node "$SCRIPT_DIR/summarize.mjs"
printf 'Raw k6 summary: %s\nMachine-readable report: %s\nServer log: %s\n' "$RAW_SUMMARY" "$REPORT_FILE" "$SERVER_LOG"
if [[ "$K6_EXIT" -ne 0 || "$VERIFY_EXIT" -ne 0 ]]; then exit 1; fi
