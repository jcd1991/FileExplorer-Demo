# File Browser Interview Project

A small ASP.NET Core and vanilla JavaScript proof of concept for browsing a configured server directory. The browser UI is rendered in the client from JSON. Application logic is original; native interop declarations follow the platform ABI references linked below.

New to the codebase? Take the [guided project tour](docs/PROJECT_GUIDE.md) for an architecture map, request walkthroughs, file-by-file guide, and implementation trade-offs.

## Original interview requirements

- **Buildable project:** .NET 8 SDK solution with no frontend build step. Build and test with `dotnet build TestProject.sln -c Release` and `dotnet test TestProject.sln -c Release`. Use Visual Studio 2022 17.8+ with the .NET 8 targeting pack, Rider, VS Code, or CLI SDK tools. This delivery was verified using the .NET CLI on macOS arm64.
- **Configurable server home:** `FileBrowser:RootPath`, overridden by `FileBrowser__RootPath`. Relative paths resolve from the application content root. The default is `SampleFiles`; the configured directory must exist and must be outside `wwwroot`.
- **JSON browse/search:** `GET /api/files/browse` lists immediate children. `GET /api/files/search` searches names recursively under the requested root-relative `path`. Search is case-insensitive and never reads file contents.
- **Deep links:** `/?open=1&path=Reports%2F2026&q=budget&sort=name&direction=asc&page=1`. URL state restores the dialog, current folder, query, sort, direction, and page after refresh and browser history navigation.
- **Client-rendered SPA:** static HTML, CSS, and JavaScript ES modules using native DOM APIs and `fetch`. No React, Angular, UI library, or server-rendered application UI.
- **Upload/download:** upload one file per request through `POST /api/files/upload?path=...`, multipart field `file`; download using `GET /api/files/download?path=...`. Uploads stream to a temporary file, are limited to 100 MiB, reject name conflicts, and are atomically moved into place after completion. Downloads stream with byte-range support.
- **Current-view totals:** the response summary counts all matching entries before paging and reports file count, folder count, and aggregate bytes for regular files. Browse counts immediate children; search counts matches found. File rows show their byte size. Folder rows say that size is not calculated; recursive folder sizes are intentionally outside this proof of concept. See the [refined acceptance criteria](docs/REQUIREMENTS.md) for the exact interpretation.

Paging reuses an opaque `snapshotId` from the first browse/search response: pass it back as `snapshot` with the same path/query/sort/direction to avoid rescanning and resorting. The server retains up to 16 views by default, each with at most 100,000 entries, subject to a 64 MiB estimated-retention budget and a two-minute lifetime. `FileBrowser:SnapshotCacheMaxViews` and `FileBrowser:SnapshotCacheMaxBytes` configure those limits. Expired or evicted tokens return 409 and the client transparently refreshes. Snapshots freeze results and totals, including partial searches, across pages. Changing the view, uploading, or performing an operation requests a fresh snapshot; external changes appear on Refresh. Snapshot tokens are transient and are not stored in deep links, so refreshing a URL requests current filesystem data. This small cache addresses measured repeat-paging work; it is not an index or a filesystem watcher.

The response entries use `path` (root-relative, `/` separators), `name`, `kind`, `sizeBytes`, and `modifiedUtc`. Responses also contain `page`, `pageSize`, `totalPages`, `summary`, `partial`, and coded `warnings`. Errors use Problem Details and avoid exposing physical server paths.

## Selected bonuses

The entire browser is in a native `<dialog>` opened by a trigger button. Escape and the close button close it and return focus to the trigger. Deep links with `open=1` open the dialog automatically. Search is debounced, obsolete requests are aborted, and rendering is bounded by pagination. Loading or failed navigation clears stale rows, totals, and pagination and disables uploads until the requested view succeeds. A Refresh button explicitly reloads the filesystem.

Delete, move, and copy work for both files and folders using the row action buttons. Delete asks for confirmation; Move opens a paged folder picker and preserves the entry name. Drag a listed entry onto a folder or breadcrumb to move it directly. The picker and Move button provide keyboard and touch access. After a successful move, **Undo move** returns the entry to its prior path. Undo uses the same server checks and can fail if the original path is occupied or unavailable. Existing entries are never overwritten. Ancestor checks compare filesystem identities as well as paths, so alternate Unicode/case/short-name spellings cannot place a folder inside itself. The configured root, traversal paths, symbolic links, and destinations within the source are rejected.

`POST /api/files/delete`, `/move`, and `/copy` accept JSON `{ "source": "Reports/old.txt", "destination": "Reports/new.txt" }`; delete omits destination. Operations preflight trees iteratively with a 10,000-entry / five-second limit. Copy caps total bytes at 1 GiB, streams into a hidden staging entry, then publishes by rename. Failed or cancelled copies attempt to clean their staged entries; a cleanup failure emits a warning. Mutations serialize with each other; uploads remain independent. Delete is permanent and can partially complete if permissions, cancellation, or external filesystem changes interrupt it. Refresh before retrying a failed delete. These operations retain the local single-user filesystem-race limitation described below.

## Additional engineering and tooling

Project instructions in `AGENTS.md` require KISS, DRY, pragmatic SOLID, YAGNI, and adapted NASA Power of 10 rules. They apply to implementation and review; the NASA rules are adaptations, not a compliance claim.

Optional structured diagnostics use built-in `ILogger`. Enable them with `Diagnostics:Enabled=true` or `Diagnostics__Enabled=true` and restart the process. The browser fetches `/api/config` and emits operation durations and outcomes via `console.debug` only when enabled. Server diagnostics cover browse, search, upload, download preparation, and rejected paths with a correlation ID, elapsed time, outcome, counts, and bytes. `download-prepare` records preparation only, with outcome `prepared` and zero bytes; it does not measure transfer completion or bytes delivered. They do not log paths, names, or file contents. To profile browse/search phases and cache accounting locally, set `Logging__LogLevel__TestProject.Services.FileSystemService=Debug`; those detailed logs are disabled at the default Information level. Ordinary warning/error logs, including temporary-upload cleanup failures, remain enabled.

Uploads accept exactly one multipart section named `file`, with no extra fields. File data is capped at 100 MiB; parser reads and the server request-body limit are capped at 101 MiB including multipart overhead, even without `Content-Length`. Failed uploads are cleaned up; a cleanup failure emits a warning without filesystem details. Tests use a separate temporary root per test and do not mutate process environment variables.

Upload experiments can tune `FileBrowser:UploadCopyBufferBytes` (default 65,536), `FileBrowser:UploadStreamBufferBytes` (default 4,096; `1` requests no extra `FileStream` buffer), and `FileBrowser:MaxConcurrentUploads` (default `0`, unlimited). The local repeated p95 follow-up did not establish a general win for disabling the stream buffer, so these defaults remain unchanged.

The sibling [`performance/`](../performance/README.md) folder contains a k6 upload harness that starts a local Release instance with an isolated disposable root, rejects remote targets, measures the public upload endpoint, and verifies one upload/download hash outside the measured workload. Harness files are not compiled or referenced by the application.

## Run the application

Prerequisites: .NET 8 SDK.

```sh
cd TestProject
dotnet restore TestProject.sln
dotnet build TestProject.sln -c Release
dotnet test TestProject.sln -c Release
dotnet run --project TestProject.csproj
```

The default launch settings use the starter profile; browse to its local HTTP/HTTPS URL. To choose a directory explicitly:

```sh
FileBrowser__RootPath="/absolute/path/to/demo-files" dotnet run --project TestProject.csproj
```

The configured root must already exist. Do not point it at `wwwroot`. The application rejects rooted/traversing request paths and skips symbolic links/reparse points. Only regular files and directories are supported. Named pipes, sockets, and devices are omitted with an `unsupported-entry-skipped` warning; downloads and mutations reject unsupported entries before reading or changing them. Search inspects at most 100,000 entries and five seconds, iteratively; inaccessible directories and truncated searches are reported. Browse caps enumeration at 100,000 entries. Page size defaults to 100 and is capped at 500.

## Backend connection

The dialog's **Backend connection** setting can target this site's API or a custom server origin. The selected origin is stored in the browser's local storage. A cross-origin API must allow the exact page origin through `Cors:AllowedOrigins` (or `Cors__AllowedOrigins__0`). CORS controls browser access; it is not authentication. The app remains a local single-user demo and must use a disposable data root when hosted publicly.

## API examples

```sh
curl 'http://localhost:5000/api/files/browse?path=Reports%2F2026&sort=name&direction=asc&page=1&pageSize=100'
curl 'http://localhost:5000/api/files/search?path=Reports&q=budget&page=1&pageSize=100'
curl -F 'file=@notes.txt' 'http://localhost:5000/api/files/upload?path=Reports%2F2026'
curl -OJ 'http://localhost:5000/api/files/download?path=Reports%2F2026%2Fnotes.txt'
```

Paths are root-relative and use forward slashes. Unsupported sort names fall back to name order. Entries order folders first, then the selected field, with a stable relative-path tie-breaker. Search returns partial-result metadata on its configured work limit. Requests are single-user local demo operations; the app does not protect against an external process changing filesystem links between validation and access.

## Tests and review walkthrough

Tests run against an isolated temporary filesystem and cover browse/search, paging totals, traversal rejection, symlink skipping, upload/download integrity, conflicts, range requests, diagnostics configuration, correlation headers, and the client dialog shell. Additional regression tests exercise unreadable directories, upload permission denial, snapshot scope/stability, file/folder mutations, rejected operations, and operation limits. Unix permission tests run on macOS/Linux; Windows ACL behavior requires separate verification.

Reviewer walkthrough:

1. Start the application and open the file-browser dialog.
2. Navigate into `Reports/2026`, then copy the URL and refresh it.
3. Search for “budget,” sort by size, and page through a larger fixture directory.
4. Upload a small file, download it, and compare the contents.
5. Try a traversal path in the API and confirm a Problem Details response.
6. Enable diagnostics, restart, and inspect logs/console while confirming no filename or physical path appears.
7. Run the k6 smoke profile, then baseline/concurrent profiles and compare reports with diagnostics off/on.

## Known limits

The app is a local, single-user interview exercise without authentication, file-content search, recursive folder sizes, indexing, or filesystem change monitoring. Its paging cache is bounded to 16 views by default and a 64 MiB estimated-retention budget, with a two-minute lifetime. Downloads report server-side setup timing rather than full transfer completion; client/k6 timing captures full request duration. Large file performance depends on disk, host, runtime, and the load generator; use `../performance/README.md` for bounded local measurements and do not treat them as a production SLO.

## Executed browser regression tests

The optional `browser-tests/` package uses Playwright only for testing; the application still has no frontend dependencies or build step. It starts its own Release server on loopback port 5187 with an isolated generated temporary root and removes that fixture after the run. It does not reuse an existing server or touch `SampleFiles`.

```sh
dotnet build TestProject.sln -c Release
cd browser-tests
npm ci
npx playwright install chromium
npm test
```

The browser suite executes deep-link restore/reload, Back/Forward, Escape/focus, stale-view handling, obsolete search cancellation, upload/download byte integrity, copy/move/delete controls and delete cancellation, and snapshot paging/refresh. It also prints five-sample median browse/search scan and snapshot-page durations for an isolated 6,000-file fixture. These are bounded local observations, not a production performance guarantee. The separate k6 harness measures uploads.

Latest local verification (2026-10-31, macOS arm64): the Release test run passed 57/57 .NET tests, the browser suite passed 6/6 Chromium tests, `dotnet format --verify-no-changes` passed, and JavaScript syntax checks passed. A three-run scan-only benchmark over 5,000 files and five VUs recorded median run-level client p95s of 65.6 ms for browse and 66.9 ms for search; server scan p95s were 54.0 ms and 55.9 ms. These same-host results are directional, not a production SLO. No Windows ACL or other-browser verification is claimed.

## Native filesystem checks

`Services/FileSystemMetadata.cs` isolates the small native boundary needed for file types and identity. The managed .NET 8 attributes do not expose Unix special-file types. Unix metadata uses the runtime's [fixed-width FileStatus ABI](https://github.com/dotnet/runtime/blob/v8.0.0/src/native/libs/System.Native/pal_io.h); Windows uses [volume and file IDs](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info). Review the interop declarations when retargeting the runtime. No package dependency, unsafe code, or frontend framework was added.

On macOS/Linux, copy opens input with nonblocking/no-follow/close-on-exec flags and checks the opened handle's type before reading. This prevents a FIFO from waiting for a writer even if it replaces a file after preflight. The input handle is disposed on rejection and the mutation gate is released in `finally`. Regular files still stream in bounded chunks; cancellation is checked during copying and immediately before publication. Native open flags are explicitly supported on macOS and Linux; other Unix platforms reject checked reads rather than guessing flags.

Filesystem identity checks also detect aliases without rewriting the user's filename. Special entries inside a tree reject the entire preflight before delete/move/copy starts. Existing local-demo limits remain: external parent-directory changes are not an atomic transaction, deletes may partially complete on failure/cancellation, and cleanup can fail. Windows/Linux execution and Windows ACL behavior remain unverified in this macOS session.

The final AGENTS.md review and verification results are recorded in [docs/FINAL_REVIEW.md](docs/FINAL_REVIEW.md).
