# Local and Demo Deployment

Both deployments use `main` and the same `TestProject` application. Configuration is selected during packaging rather than by maintaining different application branches.

| Concern | Local project | Public demo |
| --- | --- | --- |
| Frontend profile | `wwwroot/js/deployment.js` | `RenderDemo/frontend-deployment.js` replaces the local profile |
| Default API | Same origin | Render API origin |
| Demo notices | Hidden | Visible |
| File root | `SampleFiles` or configured path | Fictional `RenderDemo/DemoFiles` |
| Public mutation protection | No proxy; local use | Turnstile and shared limits in `RateLimitProxy` |
| Credentials | None required | Render environment variables only |

The profile contains public presentation and connection settings, not secrets. It is not a security boundary. Render binds the application to loopback behind its proxy; the proxy verifies file mutations independently of the frontend profile.

## Packaging

Pages copies `TestProject/wwwroot` into `_site`, then replaces `_site/js/deployment.js` with the demo profile. The source file stays local by default. Publishing runs only after backend tests, analyzers, formatting, frontend lint, proxy build, and browser regressions pass.

The Render Docker image replaces `/app/app/wwwroot/js/deployment.js` with the same demo profile. It supplies its own file root and starts the public proxy. Deploy Render through the existing runbook; a Pages publish alone does not update the Render container.

The shared UI includes the Move picker, drag and drop, Undo, and loading feedback in both modes. Turnstile is activated by the selected backend's demo configuration. Choosing a custom backend remains an explicit browser setting stored in local storage.

## Verification

The browser suite tests ordinary local file operations, mocked Turnstile behavior, and both deployment profiles. Demo-profile requests are redirected to the disposable local test server. No hosted uploads, copies, moves, deletes, or CAPTCHA challenges are performed.

The older nested `FileExplorer-Demo/` copy is not a deployment input. It was moved intact to a local pre-merge backup, preserving its untracked files.
