# Render Free deployment

This folder holds the backend deployment files, separate from the application source. The Docker build compiles the app and copies only fictional files from `DemoFiles/`. It does not package the local `SampleFiles` directory or connect to production infrastructure.

## Set up the service

1. Sign in to [Render](https://dashboard.render.com/) and connect the GitHub repository `jcd1991/FileExplorer-Demo`.
2. Create a **Blueprint** from that repository and choose `RenderDemo/render.yaml` as the Blueprint file.
3. Confirm the service uses the **Free** plan before applying the Blueprint.
4. After deployment, open the generated `onrender.com` address. The container serves both the same-origin browser and the API; `/api/config` is the health route.

The Blueprint sets the file root to `/app/demo-files`, allows one upload at a time, and reduces the snapshot cache budget. It allows the GitHub Pages origin `https://jcd1991.github.io` for cross-origin browser requests.

## Free-plan behavior

Render Free provides 512 MB RAM and 0.1 CPU. It sleeps after 15 minutes without requests; waking can take about a minute. Local file changes are lost when the service sleeps, restarts, or redeploys, so the demo resets to the fictional fixture files. This is for a lightweight demo, not durable storage or performance measurement. See [free-service limits](https://render.com/docs/free) and [compute plans](https://render.com/docs/compute-plans).

## Safety

The API has no authentication and allows upload, delete, move, and copy. Anyone who reaches the Render URL can change these demo files. The container has no production credentials, database URL, or production data root, so changes stay inside this disposable service. Do not add personal or production data to `DemoFiles/`.

Render can suspend free services when included usage is exhausted. If the account has a payment method, some overages can incur charges; set a spend limit and review usage ([Render billing FAQ](https://render.com/docs/faq)).
