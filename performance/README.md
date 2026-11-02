# Local API performance harness

Latest results: [p95 follow-up research](P95_FOLLOWUP_RESEARCH.md) repeats the stream-buffer comparison and profiles scan phases and byte-bounded snapshot policies across five clients. The [initial p95 research](UPLOAD_P95_RESEARCH_2026-10-31.md) covers the broader upload/download/mutation baseline; [k6 workflow findings](K6_REPORT_2026-10-31.md) retain the original snapshot-cache observations. Historical observations below are retained separately.

This directory is independent of the application build. It runs k6 against a locally launched Release build, using a disposable upload root beneath `performance/uploads/data`. It refuses remote URLs.

## Prerequisites and setup

- .NET 8 SDK
- k6
- Bash, curl, Node.js, `shasum`, and `dd`

From the repository root, build the application and generate deterministic binary fixtures:

```sh
dotnet build TestProject/TestProject.sln -c Release
./performance/scripts/generate-fixtures.sh
```

Fixture files are generated locally and ignored by git. The default fixture is 1 MiB; the script also generates 10 MiB and 100 MiB files.

## Run profiles

```sh
PROFILE=smoke ./performance/scripts/run-local.sh
PROFILE=baseline ./performance/scripts/run-local.sh
PROFILE=concurrent ./performance/scripts/run-local.sh
```

Smoke sends one upload at one virtual user. Baseline sends 20 uploads at one virtual user. Concurrent sends 100 uploads at ten virtual users. Every request has a unique run, VU, and iteration filename. `PROFILE` selects a finite scenario; `VUS` and `ITERATIONS` can override the defaults.

Options are supplied through environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `BASE_URL` | `http://127.0.0.1:5178` | Local application URL; must be HTTP loopback with no path/query. |
| `PROFILE` | `smoke` | `smoke`, `baseline`, or `concurrent`. |
| `FIXTURE_FILE` | `performance/fixtures/payload-1m.bin` | Binary fixture to upload. |
| `UPLOAD_PATH` | empty | Root-relative upload directory. |
| `VUS` / `ITERATIONS` | profile values | Finite workload overrides. |
| `REQUEST_TIMEOUT_SECONDS` | `120` | Per-upload request timeout. |
| `MAX_DURATION` | `30m` | k6 safety cap for a run. |
| `P95_LIMIT_MS` | unset | Optional measured latency threshold; unset means no latency SLO. |
| `DIAGNOSTICS_ENABLED` | `false` | Compare server diagnostic logging on and off. |
| `K6_SCRIPT` | `performance/k6/upload.js` | Optional local k6 script; use the review runner below to prepare workflow fixtures. |
| `SNAPSHOT_RETRY` | `true` for workflow | Model the SPA refresh/retry; `false` makes snapshot evictions fail a direct workflow. |
| `DOTNET_BIN` / `K6_BIN` | `dotnet` / `k6` | Executable overrides. |
| `DOTNET_COUNTERS_BIN` | `dotnet-counters` on `PATH` | Optional .NET runtime counter sampler; CSV output includes CPU, working set, GC heap, allocation rate, and GC activity. |
| `UPLOAD_COPY_BUFFER_BYTES` | `65536` | Application upload-loop copy buffer, configurable for the tuning experiments. |
| `UPLOAD_STREAM_BUFFER_BYTES` | `4096` | Upload `FileStream` buffer; `1` disables its additional buffer. |
| `MAX_CONCURRENT_UPLOADS` | `0` | Upload limit; zero means unlimited. |

For example, run the baseline profile with a 10 MiB fixture and a previously agreed p95 gate:

```sh
PROFILE=baseline FIXTURE_FILE="$PWD/performance/fixtures/payload-10m.bin" P95_LIMIT_MS=500 ./performance/scripts/run-local.sh
```

The script starts the app directly from the Release DLL with a dedicated `FileBrowser__RootPath`, waits for `/api/config`, runs k6, then uploads and downloads a verification fixture outside the measured workload and compares the bytes. It stops the local server afterward.

## Results and cleanup

Each run writes a raw k6 summary, a combined JSON report, metadata, verification files, and the server log under `performance/results/`. Reports include attempts, successes, failures, confirmed payload bytes, uploads/second, payload MiB/second, p50/p95/p99 latency, fixture hash and size, concurrency, elapsed workload time, diagnostics setting, SDK/k6 versions, and host details. Payload throughput measures confirmed file bytes, not multipart or network bytes.

## Local observations

Measured on a MacBookPro18,3 (macOS 27.0, arm64, 8 CPU cores, 16 GiB RAM) with .NET SDK 8.0.427 / runtime 8.0.33 and k6 2.3.0. The 10,000-entry browse used 10,000 one-byte files and returned the first 100 rows with exact totals in one cold local request. Upload profiles used deterministic zero-filled fixtures and all measured uploads succeeded; no p95 threshold was configured.

| Workload | Uploads / VUs | p50 | p95 | p99 | Confirmed payload rate |
|---|---:|---:|---:|---:|---:|
| Browse 10,000 entries | — | 192.89 ms | — | — | 100 rows rendered; 10,000 counted; complete |
| Smoke, 1 MiB | 1 / 1 | 26.50 ms | 26.50 ms | 26.50 ms | 33.06 MiB/s |
| Baseline, 1 MiB | 20 / 1 | 1.73 ms | 4.17 ms | 23.18 ms | 286.22 MiB/s |
| Concurrent, 1 MiB | 100 / 10 | 6.36 ms | 39.64 ms | 39.91 ms | 899.80 MiB/s |
| Single upload, 10 MiB | 1 / 1 | 38.71 ms | 38.71 ms | 38.71 ms | 241.19 MiB/s |
| Single upload, 100 MiB | 1 / 1 | 154.54 ms | 154.54 ms | 154.54 ms | 601.61 MiB/s |

The single-upload rows have only one latency sample, so their percentiles are that one observation. The high local payload rates reflect a same-host loopback test and are not network or production capacity estimates.

Diagnostics comparison used three 20-upload, 1 MiB baseline runs per setting. Median throughput was 278.49 MiB/s with diagnostics off and 268.26 MiB/s with diagnostics on; median p95 was 4.87 ms off and 4.71 ms on. These short runs varied from run to run and do not establish a reliable overhead estimate. They confirm the toggle and produce comparable local timings; use longer repeated runs on a representative host for a performance conclusion. The diagnostic logs contained correlation IDs, durations, outcomes, counts, and bytes, with no filenames or physical paths.

The generated per-run reports used for these observations are ignored local artifacts and are not included in the source ZIP.

Uploaded run data stays under `performance/uploads/data`. To remove only those files, use:

```sh
./performance/scripts/cleanup-uploads.sh
```

The cleanup script requires the `.harness-owned` marker created by the runner and only clears children of `performance/uploads/data`. It does not delete reports or fixtures.

Run large fixtures at one VU unless the load-generator machine has enough memory for k6 to retain the binary payload for every active VU. Keep large-blob concurrency runs finite and intentional.

## Bounded review suite

From the application root (`TestProject/`), after the Release build:

```sh
node ../performance/scripts/run-review.mjs
```

This executes three paired diagnostics off/on upload baselines, 100- and 500-upload ten-VU profiles, a 20-upload 10 MiB profile, a single 100 MiB upload, and single-/five-VU mixed workflows. It creates a unique 5,000-file catalog, seed folder, and operation directory beneath the harness-owned upload root. Plan for approximately 1.3 GiB of transient data plus retained verification downloads; it is a bounded local run, not a duration soak.

Each workflow browses/searches, pages a snapshot, uploads, copies/moves/downloads a file and checks its SHA256, copies/moves a folder and checks nested contents, then deletes its generated entries. Protocol-defined snapshot evictions are counted and refreshed by default, matching the SPA. Use `SNAPSHOT_RETRY=false` for a direct-client pressure experiment; the zero-error thresholds intentionally fail if an eviction occurs. There is no implicit latency SLO.

The runner writes pollable `state.json`, `run.log`, and `run.pid` in a unique results subdirectory. To resume an interrupted batch, set `REVIEW_BATCH` to its directory name. Completed jobs are skipped and retries get fresh run IDs to avoid filename conflicts. Failures stop expansion and retain generated data for inspection. Successful completion removes only that batch's generated tree, leaving reports and existing data intact.

## Browse/search metadata benchmark

From `TestProject/`, after a Release build:

```sh
node ../performance/scripts/run-scan-metadata-benchmark.mjs
```

This creates an isolated 5,000-file catalog, warms each of five VUs twice, then records 490 browse and 490 search samples per repetition across three runs. It writes pollable `state.json`, `run.log`, and `run.pid`, plus k6 summaries and server phase logs. Results separate client request p95 from server scan and response-preparation timings. It removes only its generated catalog after all repetitions pass; an interrupted or failed run retains fixtures for inspection and can be resumed with `SCAN_METADATA_BATCH=<batch>`.

Combined per-run reports now include overall HTTP/check outcomes and workflow completion rates, when available, as well as upload-only counters. Inspect the workflow metrics and k6 exit status: zero upload failures alone does not prove that a browse/search/mutation workflow passed. Older reports remain in their original schema; the October 31 aggregate index preserves their complete raw k6 metrics.

## Generated-folder exclusion benchmark

From the repository root, after a Release build and with k6 installed:

```sh
node performance/scripts/run-search-exclusion-benchmark.mjs
```

This compares the checkbox unchecked (`skipGenerated=false`) with its default checked state (`skipGenerated=true`). It creates an isolated 11,000-file tree with deep ordinary and generated folders, measures three repetitions at five and twenty VUs, checks complete expected result counts, and records client and server scan percentiles. It writes resumable state/log/PID files and removes only its fixture after every run passes. See [the exclusion benchmark results](SEARCH_EXCLUSION_K6_2026-11-02.md) for the paired measurements and limits.

## p95 buffer and large-download experiments

From `TestProject/` after a Release build, run the warmed 100-sample matrix and transfer checks:

```sh
BASE_URL=http://127.0.0.1:5180 DOTNET_COUNTERS_BIN=/path/to/dotnet-counters \
  EXPERIMENT_BATCH=upload-tuning-20261031 node ../performance/scripts/run-upload-experiments.mjs
BASE_URL=http://127.0.0.1:5180 TIMING_BATCH=upload-server-timings-20261031 \
  node ../performance/scripts/run-upload-timing.mjs
BASE_URL=http://127.0.0.1:5180 DOWNLOAD_BATCH=download-p95-20261031 \
  node ../performance/scripts/run-download-experiments.mjs
node ../performance/scripts/build-upload-research.mjs
```

The upload matrix compares 64/128/256 KiB copy buffers with a 4 KiB or disabled (`1`) extra `FileStream` buffer at 1/10/100 MiB. It then compares unlimited/1/2/4 active upload limits at eight VUs for 100 MiB, with queue time included in end-to-end latency. Each single-VU size uses two warmups and 100 measured uploads; each eight-VU cap uses 104 measured uploads total. Every upload is deleted after measurement. Downloads use two warmups and 100 exact-length responses per size, followed by a byte-for-byte check.

The runners write pollable state/log/PID files and preserve reports, server logs, runtime counters, CPU/RSS samples, and disk activity under `performance/results/`. They use isolated run roots, stop on failures, and delete only generated data after successful completion. The results are same-host measurements and need target-environment repetition before changing defaults. See [the p95 research report](UPLOAD_P95_RESEARCH_2026-10-31.md) for measured results and recommendations.
