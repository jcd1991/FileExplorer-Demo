# FileExplorer Demo

A compact file browser built with ASP.NET Core and vanilla JavaScript. Open it locally as one application, or host the static browser on GitHub Pages and point it at the separate API on Render.

## What it can do

- Browse folders and search names recursively; receive JSON from the API.
- Keep the open folder, query, sort order, and page in the URL, so links and Back/Forward restore the view.
- Show counts and total file size for the current result set.
- Upload and download files from the browser.
- Copy, move, and delete files and folders with path validation and bounded work.
- Choose a same-origin or custom API server in **Backend connection**. The selection is saved in that browser.

The browser is plain HTML, CSS, and ES modules; it has no UI framework or frontend build step. It also works from a GitHub Pages project path because local assets use relative URLs.

## How the pieces fit

| Part | Responsibility |
| --- | --- |
| `TestProject/Program.cs` | Configuration, dependency registration, CORS, static files, and API routes |
| `TestProject/Controllers/FilesController.cs` | HTTP endpoints and request/response handling |
| `TestProject/Services/FileSystemService*.cs` | Root-relative path checks, enumeration, search snapshots, uploads, downloads, and file operations |
| `TestProject/wwwroot/js/app.js` | Dialog state, navigation, paging, upload flow, and operation controls |
| `TestProject/wwwroot/js/api.js` | API URL selection and JSON requests |
| `TestProject/wwwroot/js/url-state.js` | Deep-link state parsing and writing |
| `TestProject/wwwroot/js/render.js` | DOM rendering for breadcrumbs and rows |
| `TestProject/TestProject.Tests/` | Isolated filesystem and API tests |
| `RenderDemo/` | Demo-only container, Render Blueprint, fictional files, and a small rate-limiting proxy |

`TestProject/docs/PROJECT_GUIDE.md` is a guided tour through the request flow, core classes, and important trade-offs.

## Run locally

Prerequisite: .NET 8 SDK.

```sh
cd TestProject
dotnet restore TestProject.sln
dotnet run --project TestProject.csproj
```

Open the URL printed by `dotnet run`. The default data root is `TestProject/SampleFiles`. To use another directory, set `FileBrowser__RootPath` to an existing path outside `wwwroot`:

```sh
FileBrowser__RootPath="/absolute/path/to/demo-files" dotnet run --project TestProject.csproj
```

Build and run the .NET tests with:

```sh
dotnet build TestProject.sln -c Release
dotnet test TestProject.sln -c Release
```

The optional browser regression suite is under `TestProject/browser-tests/`; it uses Playwright only for tests, not in the app.

## API overview

```text
GET  /api/config
GET  /api/files/browse?path=Reports
GET  /api/files/search?path=Reports&q=budget
GET  /api/files/download?path=Reports%2Fnotes.txt
POST /api/files/upload?path=Reports
POST /api/files/copy
POST /api/files/move
POST /api/files/delete
```

All request paths are relative to the configured root. The server rejects traversal and unsupported filesystem entries. Uploads and downloads stream; uploads are limited to 100 MiB. Browse and search paginate results and return summary counts. The client renders only the current page.

The hosted Render demo applies stricter demo-only limits: uploads are capped at 10 MiB and creation operations are throttled. These limits do not change the local or reviewable app.

## Hosting

The GitHub Actions workflow publishes only `TestProject/wwwroot/` to Pages. Render hosts the API through a demo-only reverse proxy configured under `RenderDemo/`; its container uses only fictional files from `RenderDemo/DemoFiles/` and does not include the local `SampleFiles` directory. See [Render setup](RenderDemo/README.md).

For the Pages UI to call Render, the API must allow the Pages origin through `Cors__AllowedOrigins__0`. The site origin is `https://jcd1991.github.io`; the repository path is not part of the origin. CORS is a browser access rule, not authentication.

### Open the hosted demo

- [GitHub Pages browser](https://jcd1991.github.io/FileExplorer-Demo/)
- [Render-hosted browser and API](https://file-explorer-demo-api.onrender.com/)
- [Render API health/config](https://file-explorer-demo-api.onrender.com/api/config)

To connect the Pages browser, open **Backend connection**, choose **A custom API server**, enter `https://file-explorer-demo-api.onrender.com`, and select **Connect**. The choice is saved in that browser. Render also serves the browser and API from one origin at its own URL.

## Safety and limits

This is a single-user demo API with no authentication. Anyone who reaches the Render endpoint can mutate its fictional files. Keep the API's data root disposable and never point it at production files. The Render container has no production credentials, database connection, or data root.

Render Free sleeps after 15 minutes without traffic and resets local file changes on sleep, restart, or redeploy. The next request can take about a minute. It is suitable for a lightweight demo, not durable storage or performance measurements. The `RenderDemo/` guide covers free-plan limits and account setup.
