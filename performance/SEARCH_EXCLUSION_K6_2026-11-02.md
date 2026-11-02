# Generated-folder exclusion search benchmark — 2026-11-02

## Result

On this local synthetic tree, enabling **Skip generated folders** materially reduced search time. Each request searched for `needle` from the fixture root. “Off” corresponds to the checkbox unchecked (`skipGenerated=false`); “On” corresponds to its default checked state (`skipGenerated=true`).

| Concurrent VUs | Exclusions | Client p50 | Client p95 | Server scan p95 | Entries inspected | Matching files |
|---:|---|---:|---:|---:|---:|---:|
| 5 | Off | 122.5 ms | 150.5 ms | 140.9 ms | 11,335 | 11,000 |
| 5 | On | 10.9 ms | 12.7 ms | 11.5 ms | 1,035 | 1,000 |
| 20 | Off | 454.6 ms | 679.2 ms | 405.4 ms | 11,335 | 11,000 |
| 20 | On | 39.2 ms | 61.9 ms | 44.0 ms | 1,035 | 1,000 |

With exclusions on, median run-level client p95 was about **92% lower at 5 VUs** and **91% lower at 20 VUs**. Server scan p95 was about **92% lower at 5 VUs** and **89% lower at 20 VUs**. Inspected entries fell about **91%**. All 12 runs had zero HTTP failures, zero failed checks, and zero partial searches.

## Workload and method

- Local Mac with 8 logical CPUs and 16 GiB RAM; k6 2.3.0; app served from Release build on loopback.
- Isolated root contained 1,000 ordinary matches under `src/` and 10,000 matches under generated-style trees: `node_modules/`, `bin/`, `obj/`, and `.git/`. Files were nested several directories deep.
- Each condition ran three repetitions at each load. The order alternated: off/on, on/off, off/on.
- Five VUs ran 40 iterations each per repetition, with two warmups per VU (190 measured search samples per repetition). Twenty VUs ran 10 iterations each, with the same warmup (160 measured samples per repetition).
- Client p50/p95 are the median of the three per-run k6 percentiles. Server scan p95 is likewise the median of the three run-level p95s, parsed from the service’s search-scan diagnostic logs. No snapshot token was sent, so every request performed a cold application search; filesystem page cache remained warm after warmups.
- The app enforced its 100,000-entry and five-second limits; none of these runs reached either limit.

The match count changes from 11,000 to 1,000 because excluding generated folders intentionally omits their files. This measures the full behavior change—less traversal, fewer matches, less sorting and snapshot preparation—not equal-sized result sets. Treat the percentages as evidence for a folder tree with many generated entries, not as a general speed guarantee for every home folder or disk.

The k6 summaries and combined machine-readable results are under [`results/search-exclusions-20261102021722/`](results/search-exclusions-20261102021722/). The generated fixture was removed after all runs passed. The separate local app at `127.0.0.1:5188` remained online and untouched.

## Reproduce

From the repository root, after a Release build and with k6 installed:

```sh
node performance/scripts/run-search-exclusion-benchmark.mjs
```

The runner creates a uniquely named fixture below the marked `performance/uploads/data` directory, writes pollable `state.json`, `run.log`, and `run.pid` files, and refuses to use a port if an unknown app answers there. A failed run keeps its isolated fixture for inspection and can be resumed by setting `SEARCH_EXCLUSION_BATCH` to the recorded batch name. A successful run removes only its own fixture and retains the k6 summaries and server log in `performance/results/`.
