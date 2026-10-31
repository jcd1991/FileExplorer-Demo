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

The optional browser regression suite is under `TestProject/browser-tests/`; it uses Playwright only for tests, not in the app. See [CI/CD and testing](#cicd-and-testing) for its command and what the current Pages workflow verifies.

## CI/CD and testing

`.github/workflows/pages.yml` runs when a commit is pushed to `main` or when the workflow is started manually. It publishes `TestProject/wwwroot/` to GitHub Pages. A successful Pages run confirms the static site was published; it does **not** restore or build the .NET API, run the .NET tests, or run the Playwright browser suite.

Run the .NET checks from `TestProject/` before publishing code:

```sh
dotnet restore TestProject.sln
dotnet build TestProject.sln -c Release
dotnet test TestProject.sln -c Release
```

To run the optional browser regression suite, install Node.js and run this from the repository root:

```sh
cd TestProject
dotnet build TestProject.sln -c Release
cd browser-tests
npm ci
npx playwright install chromium
npm test
```

The Playwright configuration starts the API on `127.0.0.1:5187` with generated temporary fixtures, then removes those fixtures after the run. The app itself has no Node.js or Playwright runtime dependency.

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

All request paths are relative to the configured root. The server rejects traversal and unsupported filesystem entries. Uploads and downloads stream; the app defaults to a 100 MiB upload cap, which the Render demo configures down to 10 MiB. Browse and search paginate results and return summary counts. The client renders only the current page.

The hosted demo uses a configurable 10 MiB upload cap, shared API rate limits, and a Turnstile check for file changes. Local runs keep the 100 MiB default upload limit. The GitHub Pages browser displays the hosted free-plan limits before visitors open the browser.

## Hosting

The GitHub Actions workflow publishes only `TestProject/wwwroot/` to Pages. Render hosts the API through a demo-only reverse proxy configured under `RenderDemo/`; its container uses only fictional files from `RenderDemo/DemoFiles/` and does not include the local `SampleFiles` directory. See [Render setup](RenderDemo/README.md).

For the Pages UI to call Render, the API must allow the Pages origin through `Cors__AllowedOrigins__0`. The site origin is `https://jcd1991.github.io`; the repository path is not part of the origin. CORS is a browser access rule, not authentication.

### Open the hosted demo

- [GitHub Pages browser](https://jcd1991.github.io/FileExplorer-Demo/)
- [Render-hosted browser and API](https://file-explorer-demo-api.onrender.com/)
- [Render API health/config](https://file-explorer-demo-api.onrender.com/api/config)

The Pages browser uses `https://file-explorer-demo-api.onrender.com` automatically, so a new visitor can browse without setting a backend URL. A previously saved custom URL in that browser still takes precedence. To switch back to Render after trying a local API, open **Backend connection**, choose **A custom API server**, enter the Render URL, and select **Connect**. Render also serves the browser and API from one origin at its own URL.

### Use GitHub Pages with your local API

The Pages browser can call the API running on your own computer. This is useful for trying local code from the public Pages UI: the page stays on GitHub Pages, while file requests go to `localhost` on the same computer where the browser is open.

Trust the local HTTPS development certificate once on Windows or macOS, if it is not already trusted:

```sh
dotnet dev-certs https --trust
```

Start the API from `TestProject/`, allowing the Pages site origin through the API's CORS configuration:

```sh
cd TestProject
Cors__AllowedOrigins__0=https://jcd1991.github.io dotnet run --project TestProject.csproj
```

In PowerShell, set the environment variable before starting the API:

```powershell
cd TestProject
$env:Cors__AllowedOrigins__0 = "https://jcd1991.github.io"
dotnet run --project TestProject.csproj
```

Then open the [GitHub Pages browser](https://jcd1991.github.io/FileExplorer-Demo/), expand **Backend connection**, choose **A custom API server**, enter `https://localhost:7146`, and select **Connect**. If Chrome asks whether the Pages site may access the local network or loopback, allow it for this demo. The app saves the selected API URL in that browser; choose the Render URL again to switch back to the shared hosted demo.

The local API uses `TestProject/SampleFiles` by default and listens on loopback through the supplied launch profile. Its file operations have no authentication, so keep it bound to localhost and use only demo or disposable files. CORS controls which browser origins may read responses; it is not authorization. See the [.NET HTTPS development certificate guide](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-dev-certs) and [Chrome's Local Network Access guide](https://developer.chrome.com/docs/modern-web-guidance/guides/security/local-network-access) for certificate setup and browser permission details.

## Safety and limits

This is a single-user demo API with no authentication. Anyone who reaches the Render endpoint can mutate its fictional files. Keep the API's data root disposable and never point it at production files. The Render container has no production credentials, database connection, or data root.

Render Free sleeps after 15 minutes without traffic and resets local file changes on sleep, restart, or redeploy. The next request can take about a minute. It is suitable for a lightweight demo, not durable storage or performance measurements. The `RenderDemo/` guide covers free-plan limits and account setup.
