# File Browser

ASP.NET Core and vanilla JavaScript file browser with configurable filesystem access, JSON browse/search APIs, streamed transfers, and URL-based navigation.

The local project is the primary interview deliverable. The hosted demo uses the same application source. Deployment configuration separates local API access from public demo hosting; separate release branches are not required.

## Local project

Prerequisite: .NET 8 SDK.

```sh
cd TestProject
dotnet restore TestProject.sln
dotnet run --project TestProject.csproj
```

Open the URL printed by the application. The default file root is `SampleFiles`; set `FileBrowser__RootPath` to use another existing directory outside `wwwroot`.

- [Project README](TestProject/README.md): requirements, configuration, API routes, and limits.
- [Architecture guide](TestProject/docs/PROJECT_GUIDE.md): module responsibilities and request flow.
- [Deployment modes](TestProject/docs/DEPLOYMENT_MODES.md): local and demo packaging.

## Functionality

- Browse directories and search filenames recursively.
- Restore folder, search, sort, and page state from the URL.
- Stream uploads and downloads, with range support and conflict checks.
- Copy, move, and delete files and folders.
- Move through a destination picker or drag and drop; undo the latest move.
- Bound filesystem scans, result retention, pagination, and mutation planning.

## Verification

```sh
cd TestProject
dotnet test TestProject.sln -c Release -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended -warnaserror
dotnet format TestProject.sln --verify-no-changes --no-restore
npm ci
npm run lint
cd browser-tests
npm ci
npx playwright install chromium
npm test
```

Browser tests use temporary local files. Demo-profile and Turnstile behavior is tested with intercepted requests; these tests do not consume hosted upload or mutation quotas. Pages deployment waits for the shared validation workflow.

## Hosted demo

[Open the demo](https://jcd1991.github.io/FileExplorer-Demo/).

Pages packages the shared UI with the demo profile. Render packages the application with fictional fixtures and a separate proxy for Turnstile and shared rate limits. Local execution does not start that proxy or require demo credentials. See the [deployment runbook](RenderDemo/DEPLOY_RUNBOOK.md).

The application remains an interview proof of concept. Public demo data is disposable; authentication, durable multi-user storage, and production capacity guarantees are outside its scope.
