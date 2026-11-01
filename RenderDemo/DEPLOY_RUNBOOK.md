# Render deployment runbook

Use this runbook to deploy the demo API and proxy from the `main` branch.

## Target service

- Render service: `file-explorer-demo-api` (Docker, Free)
- GitHub repository and branch: `jcd1991/FileExplorer-Demo`, `main`
- Blueprint: `RenderDemo/render.yaml`
- Dockerfile and build context: `RenderDemo/Dockerfile`, repository root
- Health check: `GET /api/config`
- Public URL: `https://file-explorer-demo-api.onrender.com`

The Blueprint is created from a public repository and does not auto-deploy on pushes. Use the Render dashboard for each deployment.

## Deploy the latest commit

1. Confirm the intended commit is on GitHub `main`. Record its full SHA before deploying.
2. Sign in to [Render](https://dashboard.render.com/) and open **Projects**.
3. Open the `file-explorer-demo-api` web service.
4. Select **Manual Deploy** and choose **Deploy latest commit**. Confirm the commit shown is the intended `main` commit before starting.
5. Follow the deploy in the service's deploy list. Wait for **Deploy succeeded** and **Live**.
6. On the service overview, verify **Last successfully deployed commit** matches the full SHA recorded in step 1.

If the latest commit is not the intended one, stop and check the service's linked repository, branch, and Blueprint before deploying. Do not edit environment values to compensate for a code deployment.

## Verify the live service

Run these read-only checks after the deploy:

```sh
curl -fsS https://file-explorer-demo-api.onrender.com/api/config
curl -fsS https://file-explorer-demo-api.onrender.com/api/demo-config
```

Both routes should return HTTP 200. Confirm the demo configuration reports `uploadLimitMiB: 100` and `turnstileReady: true`. The health route is intentionally lightweight; the Render dashboard's deployed commit is the evidence that the expected source revision is running.

For a browser smoke check, open the [GitHub Pages demo](https://jcd1991.github.io/FileExplorer-Demo/) and confirm it can browse the Render service. To verify a file change, use a disposable test file no larger than 100 MiB, complete the Cloudflare Turnstile check if prompted, upload it, confirm its displayed size, then download it and compare its size or checksum. Do not use personal or production data. A deploy resets files stored by the free service.

## Roll back

1. Open the service's **Deploys** page.
2. Identify the last known-good deployment and its commit SHA.
3. Select **Rollback** for that deployment and confirm the target SHA before proceeding.
4. Wait for **Deploy succeeded**, verify the dashboard's live commit, and repeat the read-only checks above.

## Credentials and automation

For repeatable local or CI deploys, create a dedicated Render API key in **Account settings → API Keys** with a narrow name such as `file-explorer-demo-deploy`. Copy it once, store it as a masked secret, and expose it only as `RENDER_API_KEY`. Never commit it, place it in a URL, or print it in logs. Revoke it immediately if exposed.

```sh
export RENDER_API_KEY='...'
export RENDER_SERVICE_ID='srv-dbj2p4u7bikc73e8pgo0'
export RELEASE_SHA='be8c07e'

curl --fail-with-body -sS \
  -H "Authorization: Bearer ${RENDER_API_KEY}" \
  -H 'Content-Type: application/json' \
  -X POST "https://api.render.com/v1/services/${RENDER_SERVICE_ID}/deploys" \
  --data "{\"commitId\":\"${RELEASE_SHA}\",\"clearCache\":\"do_not_clear\",\"deployMode\":\"build_and_deploy\"}"
```

Poll `GET https://api.render.com/v1/services/${RENDER_SERVICE_ID}/deploys` until the matching deploy is `live`, then run the read-only health checks above. The dashboard's **Manual Deploy** flow remains the recovery path.

## Current runtime limits

The Blueprint allows 100 MiB per file and a 101 MiB total multipart request body. Render Free can sleep after 15 minutes without traffic and clears local demo-file changes after sleep, restart, or redeploy. Shared rate limits and Turnstile protect file-changing routes; see [Render setup](README.md) for their configured values and operational behavior.
