# p95 follow-up: upload buffering and scan/cache behavior

## Findings

The repeat upload study does **not** support disabling the `FileStream` buffer as a general default. It remains a small candidate for 10 MiB single-client uploads, but its result flips with file size and concurrency. Keep the 4 KiB buffer until a deployment-storage run shows a reliable benefit for that deployment's workload.

The scan profile identifies entry inspection as the dominant server-side work for the 5,000-file catalog. Directory/search scan p95s were about 110–121 ms; response preparation (sorting, totals, and snapshot storage) was about 15 ms. A larger cache does not make a cold browse/search scan faster. It prevents concurrent clients from losing a snapshot before they request its next page.

The metadata hot path has since been changed to reuse `FileSystemEnumerable<T>` attributes, length, and last-write metadata. A follow-up scan-only run measured server scan p95s around 54–56 ms for the same catalog size. The comparison is directional rather than paired because the follow-up uses more scan samples and excludes upload/mutation steps in the earlier mixed workflow; details are recorded below.

For five concurrent clients, increasing the cache to 16 views eliminated the observed snapshot-expiration responses in this workload. An 8 MiB estimated-byte budget was enough for the tested 5,000-file catalog and retained fewer views than the 64 MiB budget. Larger catalog sizes still need validation before selecting a production default.

## Upload buffer repeats

### Setup

- One paired comparison per repetition: 64 KiB upload copy buffer with a 4 KiB `FileStream` buffer versus the same copy buffer with `FileStream` `BufferSize = 1`.
- Three randomized repetitions per file-size/concurrency condition; 100 measured uploads per run and two warmups per VU.
- Conditions: 10 MiB at one VU; 100 MiB at one VU; 100 MiB at four VUs (100 measured uploads total per run).
- Each upload was deleted after measurement. Each run also performed an outside-workload upload/download integrity check.
- The 18 runs transferred about 126,000 MiB of measured file payload in total; generated entries were removed as each run completed.

The available target in this session was the local MacBookPro18,3 APFS volume (8 CPUs, 16 GiB RAM), with the application and k6 on the same host. This is not an external deployment-storage target. At four VUs, k6 holds four 100 MiB fixtures in process memory; same-host CPU and filesystem-cache effects can affect the result.

### Results

Each cell shows p95 for that paired repetition, in milliseconds. The table contains run-level p95s; these are not pooled percentiles.

| Workload | Repeat | 4 KiB stream buffer | Buffer disabled (`1`) | Direction |
|---|---:|---:|---:|---|
| 10 MiB, 1 VU | 1 | 85.7 | 54.7 | disabled faster |
|  | 2 | 60.1 | 62.4 | disabled slower |
|  | 3 | 62.7 | 59.2 | disabled faster |
| 100 MiB, 1 VU | 1 | 438.6 | 429.0 | disabled faster |
|  | 2 | 473.3 | 492.2 | disabled slower |
|  | 3 | 397.3 | 412.9 | disabled slower |
| 100 MiB, 4 VUs | 1 | 2,176.5 | 2,763.2 | disabled slower |
|  | 2 | 2,405.6 | 2,470.9 | disabled slower |
|  | 3 | 2,293.0 | 2,362.0 | disabled slower |

Median of the three run-level p95s: 10 MiB was **62.7 vs 59.2 ms** (about 5.6% lower with the buffer disabled); 100 MiB at one VU was **438.6 vs 429.0 ms** (about 2.2% lower); 100 MiB at four VUs was **2,293 vs 2,471 ms** (about 7.8% higher). The paired direction was inconsistent at one VU and consistently worse at four VUs. Leave the default at 4 KiB.

## Browse/search scan and snapshot cache

### Setup

- 5,000 generated files, each containing a small fixture payload; five concurrent VUs; 100 mixed workflows per run.
- Each workflow browsed the catalog, searched all 5,000 matching names, requested page two using the returned snapshot, then performed the established 1 MiB file/folder mutation workflow.
- Three randomized repetitions for each policy (900 workflows total across the three policies).
- All variants enabled the same controller diagnostics and opt-in filesystem-service Debug phase logs.
- Cache policies: original count of two views / 64 MiB estimate budget; 16 views / 8 MiB; 16 views / 64 MiB.

The byte estimate is deliberately explicit: 256 bytes of per-snapshot overhead plus 128 bytes per row and UTF-16 storage for the entry path and name. It is a stable cache-accounting estimate, not a measurement of managed heap or RSS. At 5,000 entries, one view was estimated at **1,320,256 bytes** (about 1.26 MiB).

### Client latency and snapshot outcomes

Each latency value below is the median of the three run-level p95s. Averages across individual runs are not substituted for a pooled p95.

| Cache policy | Browse p95 | Search p95 | Page-two snapshot p95 | Full workflow p95 | Snapshot expirations |
|---|---:|---:|---:|---:|---:|
| 2 views / 64 MiB | 149.1 ms | 129.2 ms | 1.68 ms | 361.8 ms | 10 / 300 workflows |
| 16 views / 8 MiB | 138.2 ms | 120.1 ms | 1.55 ms | 302.0 ms | 0 / 300 workflows |
| 16 views / 64 MiB | 141.5 ms | 137.1 ms | 1.55 ms | 325.8 ms | 0 / 300 workflows |

With the original two-view policy, each run produced 2, 5, and 3 recoverable `409 View expired` responses. The SPA-equivalent workflow refreshed and completed all 300 workflows. Both 16-view policies completed all 600 workflows without an expiration. The 8 MiB policy retained at most six views (7,921,536 estimated bytes); the 64 MiB policy reached its 16-view cap (21,124,096 estimated bytes). They evicted older, unused views throughout the runs, but no active workflow's page-two snapshot was lost.

The candidate cache policies also had lower median full-workflow p95s in these runs, but run-to-run spread was material, and browse/search scans still execute for every workflow. Treat the zero-expiration result as strong for this exact five-VU, 5,000-file workflow; treat the aggregate latency change as a promising trend, not a guaranteed cache-only gain.

### Server-side phases

The opt-in service logs separate directory enumeration/entry inspection, recursive search traversal/entry inspection, and response preparation. For the original two-view policy, median of run-level server p95s was:

| Phase | Server-side p95 | Samples per run |
|---|---:|---:|
| Browse directory scan and metadata inspection | 110.0 ms | 100 |
| Search traversal and metadata inspection | 120.7 ms | 100–105 |
| Response preparation, including sorting, totals, and cache storage | 14.8 ms | 200–205 |

This points to per-entry filesystem inspection as the best area to profile for faster cold browse/search. The implementation now transforms each `FileSystemEntry` into a small record containing its full path, name, attributes, length, and last-write time. Browse/search reuse those values instead of separately creating `FileInfo` objects and reading `LinkTarget`. The existing reparse-point and native regular-file checks remain in place; inaccessible or changing entries still produce warnings.

### Cache recommendation

The original two-view cap was tight for five concurrent clients. A 16-view cache with a byte budget prevented all observed page expirations. The 8 MiB budget was sufficient for 5,000-entry results and had a lower estimated peak than the 64 MiB variant. The application now defaults to 16 views and 64 MiB; the larger budget avoids making the 8 MiB test's small-catalog assumption. A larger-catalog run should still verify memory use and expiration behavior. Oversized views are returned to the first request but are not retained, so page requests may refresh and rescan.

The code supports `FileBrowser:SnapshotCacheMaxViews` (default `16`) and `FileBrowser:SnapshotCacheMaxBytes` (default `64 MiB`). The byte budget is applied alongside the count limit. Debug phase and cache-accounting logs are disabled at normal Information logging; enable the `TestProject.Services.FileSystemService` category at Debug to collect them.

## Recommendations

1. **Do not change the upload stream-buffer default.** The repeat pairs did not show a stable improvement; four-client 100 MiB p95 was worse with buffering disabled.
2. **Keep the 64 KiB upload copy buffer.** This repeat focused on the stream buffer; earlier buffer-size tests did not show a consistent larger-buffer win.
3. **Keep the metadata-reuse implementation and retest on target storage.** The scan-only follow-up lowered observed scan p95s on the local host while preserving regular-file and link checks. Repeat on representative storage and larger catalogs before treating the gain as general.
4. **Validate the 16-view / 64 MiB default on larger catalogs.** The 16-view policy eliminated observed expirations for five clients and 5,000 entries. Test 50k/100k-entry views, mixed browse/search paths, and memory use.
5. **Retain the current client refresh behavior.** It recovered every observed cache eviction and keeps the UI usable when a view expires.

## Reproduction and evidence

From `TestProject/` after a Release build:

```sh
BASE_URL=http://127.0.0.1:5180 DOTNET_COUNTERS_BIN=/path/to/dotnet-counters \
  REPEAT_BATCH=upload-buffer-repeats-20261101 REPEAT_SEED=20261101 \
  node ../performance/scripts/run-upload-buffer-repeats.mjs
BASE_URL=http://127.0.0.1:5180 DOTNET_COUNTERS_BIN=/path/to/dotnet-counters \
  SCAN_BATCH=scan-cache-experiments-20261101 SCAN_SEED=20261101 \
  node ../performance/scripts/run-scan-cache-experiments.mjs
```

The runners write `state.json`, `run.log`, and `run.pid`, preserve per-job k6 summaries, server logs, runtime counters, CPU/RSS and disk samples, stop on failure, and remove only their generated data after success. The raw runs are under `performance/results/upload-buffer-repeats-20261101/` and `performance/results/scan-cache-experiments-20261101/`. The suite passed **54/54 .NET tests** after the cache and timing changes.

### Follow-up implementation

After this study, the application default was changed from two views to 16 while retaining the 64 MiB estimate budget. `Program.cs`, `appsettings.json`, and the direct service-constructor default agree. An API regression test fills all 16 slots, confirms the first view remains usable, then confirms the 17th view evicts the oldest. The final Release test run passed **56/56 .NET tests**. The 16-view / 64 MiB k6 variant above completed 300 workflows with zero expirations per repetition, for 900 workflows across the repeated cache study.

## Metadata enumeration follow-up

The new `performance/scripts/run-scan-metadata-benchmark.mjs` isolates the scan path. It creates 5,000 small files, runs five VUs with 100 iterations each, discards two warm-up iterations per VU, and repeats the run three times. Each measured iteration performs one cold browse and one cold search without passing a snapshot token. Every response must report all 5,000 files. The local runner also verifies a separate upload/download outside the measured scan workload and removes only its generated catalog after success.

| Repetition | Browse client p95 | Search client p95 | Browse server scan p95 | Search server scan p95 | Response preparation p95 |
|---:|---:|---:|---:|---:|---:|
| 1 | 65.6 ms | 66.9 ms | 54.0 ms | 57.7 ms | 12.3 ms |
| 2 | 69.7 ms | 68.9 ms | 56.6 ms | 55.9 ms | 13.9 ms |
| 3 | 63.2 ms | 63.5 ms | 51.5 ms | 50.8 ms | 12.5 ms |
| Median run-level p95 | **65.6 ms** | **66.9 ms** | **54.0 ms** | **55.9 ms** | **12.5 ms** |

This was a same-host loopback run on a MacBookPro18,3 (arm64, 8 CPUs, 16 GiB RAM), with k6 2.3.0 and .NET 8.0.33 runtime; the installed SDK selected by the runner was 10.0.403. The prior scan-phase medians were 110.0 ms for browse and 120.7 ms for search. That suggests roughly 51% and 54% lower server scan p95s, respectively, but it is **not a paired comparison**: the prior run had 100–105 samples per operation in a mixed workflow, while this scan-only run had 500 server samples per operation per repetition and 490 client samples after warm-up. Treat the change as a promising local result, not a universal capacity claim.

The runner records process CPU/RSS samples and disk activity. `dotnet-counters` was not installed, so this run does not include managed allocation-rate or GC-counter measurements. Raw reports and logs are retained locally under `performance/results/scan-metadata-final-v2-20261031/` and are excluded from the source ZIP.

Reproduce from `TestProject/` after a Release build with `node ../performance/scripts/run-scan-metadata-benchmark.mjs`. The runner writes `state.json`, `run.log`, and `run.pid`, supports resume through `SCAN_METADATA_BATCH`, and retains generated files if a run fails. The 50k/100k catalog and estimated-cache-memory checks remain follow-up work.
