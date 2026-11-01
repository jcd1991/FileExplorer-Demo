# FileExplorer Demo

> A small, fast, security-minded file browser built with ASP.NET Core and vanilla JavaScript.

## 🚀 LIVE DEMO

**[Open the GitHub Pages demo →](https://jcd1991.github.io/FileExplorer-Demo/)**

The static browser runs on GitHub Pages and calls the disposable API on Render. The hosted demo supports 100 MiB uploads, Turnstile-protected file changes, and shared free-plan rate limits.

📖 **[Read the project guide](TestProject/docs/PROJECT_GUIDE.md)** for the request flow, code map, safety boundaries, and design decisions.

## What this project proves

### Original interview requirements

- ✅ **Buildable .NET 8 solution** with no frontend build step
- ✅ **Configurable server root** via `FileBrowser:RootPath`
- ✅ **JSON browse and search** for folders and filenames, without reading file contents
- ✅ **Deep links and history** for folder, query, sort, direction, and page state
- ✅ **Client-rendered browser** using HTML, CSS, native DOM APIs, ES modules, and `fetch`
- ✅ **Streaming upload and download**, including byte ranges and a 100 MiB upload cap
- ✅ **Current-view totals** for file count, folder count, and regular-file bytes before paging
- ✅ **Bounded paging and search** with cancellation, snapshots, expiry, and partial-result warnings

### ✨ Bonus features

- ✅ Accessible native `<dialog>` with Escape handling and focus return
- ✅ Debounced search, request cancellation, stale-response protection, and Refresh
- ✅ Accessible loading masks while the demo initializes, views load, and moves run
- ✅ Copy, move, delete, drag-and-drop move, keyboard/touch Move picker, and Undo move
- ✅ Root containment, traversal checks, symlink/reparse-point rejection, conflict checks, and filesystem identity checks
- ✅ Iterative, bounded mutation planning with staged copy and cleanup
- ✅ Optional structured diagnostics with correlation IDs and path-free logs
- ✅ Render demo with Turnstile, rate limits, disposable fixtures, and 100 MiB uploads
- ✅ Playwright browser regression suite, .NET tests, ESLint, and a bounded k6 harness

## 🧭 How it fits together

```text
Browser UI → fetch/API routes → FilesController → FileSystemService → configured root
     ↑              ↓                 ↓                 ↓
 URL state      JSON models       Problem Details    bounded filesystem work
```

| Area | Responsibility |
| --- | --- |
| `TestProject/wwwroot/js/` | Dialog state, deep links, API calls, and safe DOM rendering |
| `TestProject/Controllers/` | HTTP routes, validation, status codes, and streaming responses |
| `TestProject/Services/` | Path safety, browse/search, transfers, mutations, metadata, and snapshots |
| `TestProject/TestProject.Tests/` | API, filesystem, transfer, limit, and edge-case coverage |
| `RenderDemo/` | Docker image, Render Blueprint, demo fixtures, and rate-limit proxy |

## Run locally

Prerequisite: .NET 8 SDK.

```sh
cd TestProject
dotnet restore TestProject.sln
dotnet run --project TestProject.csproj
```

Open the URL printed by `dotnet run`. The default root is `SampleFiles`; choose another existing directory with:

```sh
FileBrowser__RootPath="/absolute/path/to/demo-files" dotnet run --project TestProject.csproj
```

The root must be outside `wwwroot`. Requests are root-relative, and the server rejects traversal, links, unsupported filesystem entries, and conflicting destinations.

## Verify it

```sh
cd TestProject
dotnet build TestProject.sln -c Release
dotnet test TestProject.sln -c Release
npm ci && npm run lint
```

Optional browser checks:

```sh
cd TestProject/browser-tests
npm ci
npx playwright install chromium
npm test
```

The performance method, local p95 findings, and hosted 100 MiB transfer notes are in [performance research](TestProject/docs/PERFORMANCE_RESEARCH.md).

## API at a glance

```text
GET  /api/files/browse?path=Reports
GET  /api/files/search?path=Reports&q=budget
GET  /api/files/download?path=Reports%2Fnotes.txt
POST /api/files/upload?path=Reports
POST /api/files/copy
POST /api/files/move
POST /api/files/delete
```

Responses use JSON for browse/search and Problem Details for expected failures. File transfers stream in bounded chunks; the browser never needs to hold a complete file in memory.

## Hosting and limits

- [GitHub Pages browser](https://jcd1991.github.io/FileExplorer-Demo/)
- [Render browser/API](https://file-explorer-demo-api.onrender.com/)
- [Render health/config](https://file-explorer-demo-api.onrender.com/api/config)
- [Render deployment runbook](RenderDemo/DEPLOY_RUNBOOK.md)

The hosted service is a disposable, single-user demo without authentication. Render Free can sleep and reset its local files after restart or redeploy. It is suitable for review and bounded experiments, not production multi-tenant storage or a production performance SLO.
