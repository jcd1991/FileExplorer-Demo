# p95 research across file browser workloads — October 31, 2026

## Recommendation

Keep the 64 KiB upload copy buffer and unlimited upload concurrency as the defaults. The 128/256 KiB buffers did not consistently lower p95, and none of the tested upload caps improved client p95 when waiting time was included.

Disabling the extra `FileStream` buffer (`BufferSize = 1`) is the strongest tuning candidate: it lowered the measured upload p95 in each size group, by 6.9% at 100 MiB with the existing 64 KiB copy buffer. This is one run per configuration on one local machine, so repeat it on the target storage and server before changing the default. The code now accepts all three settings through `FileBrowser:UploadCopyBufferBytes`, `FileBrowser:UploadStreamBufferBytes`, and `FileBrowser:MaxConcurrentUploads`.

Across non-upload operations, browse and search scans dominated the measured mixed workflow. File and folder mutations were low latency. The two-view snapshot cache evicted three of 100 five-user workflows; the existing client refresh/retry recovered all 100. Large downloads were fast on loopback, but these results do not establish remote-network performance.

## Method and limits

The harness ran against an isolated Release server on `127.0.0.1:5180`, with a dedicated upload root. Host: MacBookPro18,3, macOS, arm64, 8 CPUs, 16 GiB RAM; .NET SDK 10.0.403 / runtime 8.0.33; k6 2.3.0. The application targets .NET 8. Fixtures were deterministic zero-filled files.

Each upload size/configuration had two warmup uploads followed by 100 measured uploads at one VU. The uploaded file was deleted through the API after each iteration. Each run also performed a separate upload/download byte-integrity check; generated trees and those large verification downloads were removed after success. Each download size had two warmups, 100 measured responses at one VU, exact response-length checks, and a separate byte-for-byte verification. The concurrency jobs used eight VUs and 13 measured uploads per VU (104 measured uploads per setting).

The upload matrix was ordered in a rotated sequence by size to reduce time/order effects. There was one run per cell, not repeated trials. Upload/download fixtures and the OS filesystem cache can make local results much faster than cold or remote storage. The benchmark used a single host for both k6 and the app; it does not establish an SLO or production throughput. No latency target was set.

## Upload buffer results

End-to-end client p95 in milliseconds; 100 measured uploads in each cell. “Stream 4 KiB” is the existing `FileStream` buffer. “Stream off” sets its size to 1 while the upload loop keeps its own copy buffer.

| File size | Copy buffer | Stream 4 KiB | Stream off |
|---|---:|---:|---:|
| 1 MiB | 64 KiB | 7.2 | 5.4 |
|  | 128 KiB | 6.8 | 5.5 |
|  | 256 KiB | 7.7 | 5.2 |
| 10 MiB | 64 KiB | 62.5 | 53.7 |
|  | 128 KiB | 80.2 | 56.5 |
|  | 256 KiB | 63.4 | 54.0 |
| 100 MiB | 64 KiB | 387.0 | 360.3 |
|  | 128 KiB | 364.9 | 359.6 |
|  | 256 KiB | 420.7 | 369.2 |

The stream-buffer-off result improved p95 in all three size groups, though the gain narrowed as file size increased. Larger copy buffers were mixed: 128 KiB helped one default-buffer 100 MiB cell but hurt the 10 MiB cell; 256 KiB did not beat 64 KiB for either 10 or 100 MiB. These cells are close enough that run-to-run variance could change their ordering.

The 100 MiB, 64 KiB / 4 KiB-buffer baseline had client p50/p95/p99 of 352 / 387 / 508 ms. A separate diagnostics-enabled run captured 104 controller upload-duration observations (100 measured requests plus warmups and integrity traffic): p50 354 ms, p95 429 ms, p99 520 ms. The corresponding client p95 in that run was 414 ms. Since these are independent client and server measurements, do not subtract their percentiles. At 1 MiB and 10 MiB, controller p95s were 7.4 and 58.0 ms.

## Upload concurrency experiment

Eight virtual users each made 13 measured 100 MiB uploads. Latency includes server queue time at the configured `SemaphoreSlim` upload gate.

| Max active uploads | Measured uploads | p50 | p95 | p99 | Batch time |
|---:|---:|---:|---:|---:|---:|
| Unlimited | 104 | 2,448 ms | 2,794 ms | 2,840 ms | 83.8 s |
| 1 | 104 | 3,184 ms | 3,429 ms | 3,445 ms | 51.1 s |
| 2 | 104 | 4,074 ms | 5,910 ms | 6,103 ms | 74.0 s |
| 4 | 104 | 2,471 ms | 3,971 ms | 4,970 ms | 83.8 s |

No cap improved p95 on this host; the 1-slot cap had the best total batch time but increased per-request p95 by 23%. Keep unlimited as the default for a p95-focused policy. A cap may still be useful to protect a server under a different mixed workload, but that needs a representative multi-client test with queue time included.

## Downloads and mixed workflows

| Download size | Successful downloads | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| 1 MiB | 100 | 1.35 ms | 1.60 ms | 2.00 ms |
| 10 MiB | 100 | 7.96 ms | 9.39 ms | 9.69 ms |
| 100 MiB | 100 | 58.72 ms | 74.38 ms | 101.64 ms |

All responses had the exact expected byte count, and the separate full-file comparison passed for each size. The download endpoint prepares a streamed physical-file response; its existing `download-prepare` diagnostic only measures path preparation, not response transfer time. The download p95 above covers the complete same-host response.

The separate five-VU mixed workflow browsed and searched a 5,000-file catalog, fetched a snapshot page, uploaded/downloaded a 1 MiB file, copied/moved files and folders, verified hashes/contents, then deleted generated items. Its relevant p95 measurements were:

| Operation | Samples | p95 |
|---|---:|---:|
| Browse 5,000 files | 100 | 123.44 ms |
| Search 5,000 matches | 100 | 117.35 ms |
| Snapshot request | 100 | 2.33 ms |
| Refresh after snapshot eviction | 3 | 94.98 ms |
| Copy file | 100 | 4.17 ms |
| Move file | 100 | 2.83 ms |
| Delete file | 200 | 1.48 ms |
| Copy folder | 100 | 3.82 ms |
| Move folder | 100 | 3.47 ms |
| Delete folder | 100 | 2.14 ms |
| Complete workflow | 100 | 259.07 ms |

The direct API variant saw three snapshot evictions in 100 workflows (97 completed); the SPA-equivalent refresh/retry completed 100/100. Browse and search dominate this workflow's p95, so measure scan work and cache retention next. Any cache adjustment should stay byte-bounded and be measured with multiple clients before changing the current simple policy.

## Resource observations

For the one-VU 100 MiB default-buffer upload run, .NET runtime samples (1 second interval) averaged 34% CPU, peaked at 40%, averaged 121 MiB working set (129 MiB peak), averaged 10.7 MiB GC heap (16.4 MiB peak), and averaged 326 KiB/s allocation rate (2.0 MiB/s peak). Process RSS sampling peaked at 125.5 MiB. Short 1 MiB jobs often ended before the runtime collector's first sample, so per-second runtime counters are available mainly for the longer sizes and concurrency runs.

`iostat` recorded host-wide disk activity; during the 100 MiB single-VU baseline, observed disk throughput peaked around 287 MB/s. This is not process-isolated and can include unrelated machine activity. The raw disk, process, and .NET runtime samples are retained per run. Generated upload data was deleted, and available disk space returned to about 6.2 GiB after the runs.

## Recommendations

1. Keep the 64 KiB upload copy buffer. The 128 and 256 KiB alternatives did not produce a repeatable win in this matrix.
2. Repeat `BufferSize = 1` vs 4 KiB for 10 and 100 MiB on the actual deployment storage, with at least three randomized runs per cell. If its p95 improvement persists without higher allocations/CPU or regressions under concurrent loads, set the stream buffer to 1 for that environment.
3. Do not enable an upload concurrency cap for p95 based on this evidence. If resource protection is the goal, compare limits 1/2/4 under the expected blend of file sizes and report both request p95 and throughput.
4. For browse/search p95, profile enumeration/search on representative larger catalogs and measure a byte-bounded cache policy under multiple clients. Cache refresh cost is near 95 ms and materially exceeds mutation latency.
5. No download buffering change is indicated by these loopback results. Validate downloads from a remote client and realistic storage before tuning the streaming path.

## Reproduction and raw evidence

From `TestProject/` after a Release build and with k6 and `dotnet-counters` installed:

```sh
BASE_URL=http://127.0.0.1:5180 DOTNET_COUNTERS_BIN=/path/to/dotnet-counters \
  EXPERIMENT_BATCH=upload-tuning-20261031 node ../performance/scripts/run-upload-experiments.mjs
BASE_URL=http://127.0.0.1:5180 TIMING_BATCH=upload-server-timings-20261031 \
  node ../performance/scripts/run-upload-timing.mjs
BASE_URL=http://127.0.0.1:5180 DOWNLOAD_BATCH=download-p95-20261031 \
  node ../performance/scripts/run-download-experiments.mjs
node ../performance/scripts/build-upload-research.mjs
```

Each runner writes a pollable `state.json`, `run.log`, and `run.pid`; completed jobs are skipped on resume, failures stop the batch and retain generated data, and successful batches remove only their generated roots. Set `BASE_URL` to a free loopback port.

Raw k6 summaries, reports, app logs, CPU/RSS samples, disk samples, .NET counter CSVs, batch states, and the normalized index are under `performance/results/`. The prior broader k6 workflow review is in [K6_REPORT_2026-10-31.md](K6_REPORT_2026-10-31.md). The benchmark build/test result was 53/53 passing tests.
