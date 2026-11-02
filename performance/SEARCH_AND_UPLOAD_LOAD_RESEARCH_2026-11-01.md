# Search and upload load research — 2026-11-01

## Scope and setup

These are local loopback measurements on the MapLarge interview File Explorer, not deployment capacity claims. The host had 8 logical CPUs and 16 GiB RAM. Upload-cap tests used an isolated temporary root, a 1 MiB file, upload → download → byte-count/SHA-256 verification → delete cycles, and k6. No shared demo files were touched. The raw k6 summary JSON for each cap run is retained under [`results/upload-cap-high-concurrency-20261101/`](results/upload-cap-high-concurrency-20261101/).

The server accepts `FileBrowser:MaxConcurrentUploads` values from 0 through 64; zero means unlimited. A cap limits active upload bodies but does not bound the number of HTTP requests waiting for a slot.

## Upload cap comparison

### 96 virtual users, four cycles each (384 cycles per run)

All cycles passed in every run. Unlimited and cap 16 were repeated to check whether a small apparent win persisted.

| Cap | Repetition | Complete cycles | Cycles/s | Overall p95 | Upload p95 |
|---:|---:|---:|---:|---:|---:|
| 0 (unlimited) | 1 | 384/384 | 136.4 | 567 ms | 534 ms |
| 0 (unlimited) | 2 | 384/384 | 128.8 | 663 ms | 639 ms |
| 16 | 1 | 384/384 | 143.1 | 594 ms | 425 ms |
| 16 | 2 | 384/384 | 133.2 | 611 ms | 406 ms |
| 32 | 1 | 384/384 | 133.9 | 670 ms | 706 ms |
| 64 | 1 | 384/384 | 136.5 | 607 ms | 506 ms |

Across the two repeats, cap 16 averaged about 138 cycles/s versus 133 for unlimited, and upload p95 averaged about 416 ms versus 587 ms. Overall p95 differed by only about 12 ms (603 vs. 615 ms). Cap 32 was slower in its single run; cap 64 was close to unlimited. This is a promising small-file workload signal, but two short repeats are not enough to set a default.

### 224 virtual users, two cycles each (448 cycles per run)

| Cap | Complete cycles | Complete-cycle rate | HTTP request failures | Overall p95 |
|---:|---:|---:|---:|---:|
| 0 (unlimited) | 448/448 | 100% | 0% | 1,517 ms |
| 16 | 366/448 | 81.7% | 6.95% | 936 ms |
| 32 | 382/448 | 85.3% | 5.45% | 880 ms |
| 64 | 404/448 | 90.2% | 3.50% | 1,309 ms |

Failures in capped runs were upload connection resets. Their lower p95 values are survivorship-biased: failed uploads often reset before they can contribute a slow successful latency. Cap 64 had the fewest failures among capped runs, but still underperformed the isolated unlimited run. Earlier tests on the original home-root setup were noisier, including failures without a cap, so these runs do not establish a reliable maximum sustainable load.

**Recommendation:** keep the default at unlimited. Cap 16 is a candidate for a representative small-file workload follow-up, not a general default. A semaphore alone does not protect the server from a large queue of open requests; if overload protection is needed, evaluate bounded admission and clear retry responses separately.

The previous 100 MiB study also found caps 1, 2, and 4 had higher per-upload p95 than unlimited. The broader upload and download measurements remain in [`UPLOAD_P95_RESEARCH_2026-10-31.md`](UPLOAD_P95_RESEARCH_2026-10-31.md) and [`K6_REPORT_2026-10-31.md`](K6_REPORT_2026-10-31.md).

## Search speed under load

For a generated 5,000-file catalog, the five-client mixed workflow measured search p95 at 117.35 ms. A later scan-only study measured client search p95 at 66.9 ms and server search-scan p95 at 55.9 ms across the median run-level results. The comparison is directional: the harnesses and sample counts differed, and local disk/cache behavior affects results. The metadata reuse follow-up reduced work in the measured scan path; increasing snapshot retention helps paging clients avoid rescans, but does not accelerate a cold search.

Search also has hard limits of 100,000 inspected entries and five seconds. The new default exclusion skips common dependency, VCS, build, test, and language-cache directories before descending into them. This should reduce traversal work in repositories and home folders with large generated trees, but its benefit depends on the folder mix and is not represented by the 5,000-file catalog result above. The user can uncheck the option to include those paths.

See [`P95_FOLLOWUP_RESEARCH.md`](P95_FOLLOWUP_RESEARCH.md) and [`K6_REPORT_2026-10-31.md`](K6_REPORT_2026-10-31.md) for search workload details, percentile caveats, and reproduction commands.

## Limits

- The app and load generator ran on the same local machine; loopback excludes real network and remote storage effects.
- The upload workload used 1 MiB fixtures. Large-file behavior is covered by separate low-concurrency research, not this cap matrix.
- The high-concurrency results varied between runs and between the isolated root and home-root setup.
- The raw JSON contains k6 summaries, not server logs or machine-wide telemetry. It should be read alongside the setup and caveats above.
