# Render Free demo setup

This folder contains the hosting wrapper for the interview project. It stays outside `TestProject/` and outside `TestProject_2026_Review.zip`, so the Render account and deployment setup do not become part of the general code-review package.

The container builds the existing .NET app, but includes only fictional files from `DemoFiles/`. It excludes the project's `SampleFiles/`, test fixtures, build output, and local interview study guide. It has no EventYo credentials, database connection, or production data path.

## Create or sign in to Render

Open [Render Dashboard](https://dashboard.render.com/). Sign in or create the free account yourself. If the sign-up flow asks you to enter a password, approve GitHub access, or accept Render's terms, complete those steps directly in the browser.

## Deploy the backend

The Git repository connected to Render must contain both `TestProject/` and `RenderDemo/`. A private repository is fine for this backend. In the Render dashboard:

1. Create a **Blueprint** from that repository.
2. Set the Blueprint file path to `RenderDemo/render.yaml`.
3. Review the service plan and confirm it is **Free** before creating the service.
4. After the first deploy, open the generated `onrender.com` URL. The container serves the same-origin UI and API; `/api/config` is the health-check route.

## Streamlined deploys with a Render API key

Use an account API key for CI or local deploy scripts instead of storing a password. Keep the value in a local secret store or CI secret named `RENDER_API_KEY`; never commit it, place it in a URL, or print it in logs.

1. In Render, open **Account settings → API Keys → Create API key**.
2. Give it a narrow name such as `file-explorer-demo-deploy` and copy the value once. Render will not show the secret again.
3. Export it only in the shell or CI job that performs the deploy:

   ```sh
   export RENDER_API_KEY='...'
   export RENDER_SERVICE_ID='srv-dbj2p4u7bikc73e8pgo0'
   ```

4. Trigger a deploy for the service and poll its status (replace the commit SHA with the release being promoted):

   ```sh
   curl --fail-with-body -sS \
     -H "Authorization: Bearer ${RENDER_API_KEY}" \
     -H 'Content-Type: application/json' \
     -X POST "https://api.render.com/v1/services/${RENDER_SERVICE_ID}/deploys" \
     --data '{"commitId":"ce1e3ab","clearCache":"do_not_clear","deployMode":"build_and_deploy"}'
   ```

   The response includes a deploy ID. Poll `GET https://api.render.com/v1/services/${RENDER_SERVICE_ID}/deploys` until the promoted deploy is `live` or `failed`. Do not paste the key into browser fields or chat.

5. Confirm the deploy reaches `live`, then verify the service health endpoint:

   ```sh
   curl --fail-with-body -sS "https://file-explorer-demo-api.onrender.com/api/config"
   ```

6. Revoke the key in **Account settings → API Keys** when the work is complete or if it was exposed. Store only the replacement in the CI secret manager.

The Blueprint sets a separate root at `/app/demo-files`, one concurrent upload, and a smaller snapshot cache. The static UI hosted separately can connect through its **Backend connection** setting once CORS is configured.

## Connect GitHub Pages later

After the Pages URL is known, add this environment variable to the Render service, using the exact site origin (scheme and host only):

```text
Cors__AllowedOrigins__0=https://ACCOUNT.github.io
```

For a project Pages site, the origin is still `https://ACCOUNT.github.io`; the repository path is not part of the origin. CORS is a browser rule, not user authentication.

## Free-plan behavior and safety

Render Free uses 512 MB RAM and 0.1 CPU. It spins down after 15 minutes without requests; waking can take about a minute. File changes are lost when it spins down, restarts, or redeploys, so the demo data resets to the checked-in fictional fixtures. It is intended for a lightweight interview demo, not durable storage or performance testing. See [free-service limits](https://render.com/docs/free) and [compute plans](https://render.com/docs/compute-plans).

The browser API remains unauthenticated and allows delete, move, copy, and upload operations. Those operations affect only this disposable Render service. Visitors can still alter the demo data or exhaust the service's free usage. Set an account spend limit and review usage; if a payment method is attached, usage beyond included allowances can incur charges ([Render FAQ](https://render.com/docs/faq)).

The app build context uses `Dockerfile.dockerignore` beside the Dockerfile. It intentionally excludes the PDF-containing `SampleFiles/` directory and the ignored local study guide. Do not add production files, credentials, or personal data to `DemoFiles/`.
