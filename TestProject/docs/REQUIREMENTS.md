# Refined acceptance criteria

This page clarifies two phrases in the interview prompt so the implementation can be evaluated consistently. It keeps the project a small, bounded proof of concept.

## Current-view counts and sizes

“Current view” means the complete logical result set before pagination:

- **Browse:** the immediate entries in the selected directory.
- **Search:** all eligible name matches beneath the selected directory, subject to the documented scan limits.
- **Counts:** the summary reports file and folder counts for that result set, including entries on other pages.
- **Sizes:** each regular-file row reports its byte size, and the summary reports the sum of those file sizes for that result set.
- **Folder rows:** a folder has no recursive size value; the UI says its size is not calculated. Recursive folder-size calculation is excluded because it would require extra tree scans and make ordinary listing costs depend on subtree size.

Acceptance checks: browse and search return correct counts and file-byte totals before paging; a file row has a size; a folder row is clearly identified and does not imply that its size is zero or known.

## Bounded browse and search

- Browse enumerates at most 100,000 immediate entries.
- Search traverses iteratively, honors request cancellation, and stops at 100,000 inspected entries or five seconds. Truncated or inaccessible results are marked partial and include coded warnings.
- Page rendering remains bounded by the existing page-size limit. Snapshot paging may reuse a result, but a cold browse or search still scans the filesystem.
- Metadata optimizations must preserve root containment, symbolic-link rejection, regular-file checks, supported-entry behavior, cancellation, and the summary contract.

## Performance evidence

“Performance is highly valued” is not a universal latency SLO. Compare cold browse/search separately from snapshot paging. Record workload size, concurrency, warmups, repetitions, p50/p95, server scan and response-preparation timings, and host/storage conditions. Same-host local measurements are directional evidence and should not be presented as production capacity.

The current investigation uses a 5,000-file catalog and five clients; a follow-up should also exercise larger catalogs and check the snapshot cache's estimated memory budget against observed process memory.
