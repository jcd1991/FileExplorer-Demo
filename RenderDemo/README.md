# Render Free deployment

This folder holds the backend deployment files, separate from the application source. The Docker build compiles the app and copies only fictional files from `DemoFiles/`. It does not package the local `SampleFiles` directory or connect to production infrastructure.

## Set up the service

1. Sign in to [Render](https://dashboard.render.com/) and create a **Blueprint** from the public repository URL `https://github.com/jcd1991/FileExplorer-Demo`.
2. Choose branch `main`, Blueprint name `FileExplorer Demo`, and Blueprint path `RenderDemo/render.yaml`.
3. Confirm the service uses the **Free** plan before applying the Blueprint.
4. After deployment, open the generated `onrender.com` address. A demo-only reverse proxy serves the browser and forwards API requests to the .NET app on loopback. `/api/config` is the health route.

The public-repository Blueprint flow does not connect Render to your GitHub account, so it does not auto-deploy on future pushes. To publish later changes, sync the Blueprint or trigger a manual deploy from Render.

The Blueprint sets the file root to `/app/demo-files`, allows one upload at a time, and reduces the snapshot cache budget. It allows the GitHub Pages origin `https://jcd1991.github.io` for cross-origin browser requests.

## Demo-only request limits

The `RateLimitProxy` sits in front of the .NET app only in this Render container. The app itself and its reviewable project source stay unchanged. The backend listens on loopback, so public requests pass through the proxy first.

| Request type | Limit per running service instance |
| --- | --- |
| API requests | 30-request burst, then 30 more every 30 seconds |
| Simultaneous API requests | 4 |
| Uploads and copies | 2 every 5 minutes |
| Delete and move | 20 per minute |
| Upload request body | 11 MiB total (about a 10 MiB file plus multipart overhead) |

These limits are shared by all visitors. Health checks, CORS preflight requests, and static assets do not use the API quota. Rejected API calls return `429 Too Many Requests` with `Retry-After`; requests are not queued. The counters are in memory and reset when the free service restarts. This caps application work and upload frequency for a demo, but it is not authentication or a guarantee of uninterrupted access; any visitor can consume the shared allowance. Render separately provides network-level DDoS protection; its docs recommend application-level limits for API abuse ([Render DDoS guidance](https://render.com/articles/how-render-handles-ddos-attacks)).

## Free-plan behavior

Render Free provides 512 MB RAM and 0.1 CPU. It sleeps after 15 minutes without requests; waking can take about a minute. Local file changes are lost when the service sleeps, restarts, or redeploys, so the demo resets to the fictional fixture files. This is for a lightweight demo, not durable storage or performance measurement. See [free-service limits](https://render.com/docs/free) and [compute plans](https://render.com/docs/compute-plans).

## Safety

The API has no authentication and allows upload, delete, move, and copy. Anyone who reaches the Render URL can change these demo files, subject to the demo rate limits above. The container has no production credentials, database URL, or production data root, so changes stay inside this disposable service. Do not add personal or production data to `DemoFiles/`.

Render can suspend free services when included usage is exhausted. If the account has a payment method, some overages can incur charges; set a spend limit and review usage ([Render billing FAQ](https://render.com/docs/faq)).
