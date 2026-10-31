# File browser performance research

Measurements below are bounded, single-VU observations for the demo. They are useful for comparing these request paths, not for making an availability or capacity claim. The Render results include its network path and reverse proxy; the local results call the ASP.NET app directly.

## Read-only browse and search baseline

An earlier Render k6 run used one VU for 64 sequential browse/search requests, alternating endpoints with a two-second pause. All 64 HTTP 200 checks passed and the summary reported no failed HTTP requests.

| Render operation | p95 | p99 | Samples |
| --- | ---: | ---: | ---: |
| Browse | 131 ms | 221 ms | 30 measured after warm-up |
| Search | 111 ms | 116 ms | 30 measured after warm-up |
| All requests | 118 ms | 218 ms | 64 |

The earlier run summary is the source for these values. The run was read-only and did not exercise file transfer.

## 100 MiB download comparison

On 2026-10-31, k6 downloaded the same 100 MiB fixture ten times sequentially with one VU and a two-second pause between iterations. Each request checked HTTP 200 and a `Content-Length` of 104,857,600 bytes. Each run passed all 20 checks (two per request) and had no failed HTTP requests. The p50, p95, and maximum below use k6's `http_req_duration`, which includes receiving the full response body.

| Target | p50 | p95 | Max | Requests |
| --- | ---: | ---: | ---: | ---: |
| Local ASP.NET app | 81 ms | 521 ms | 879 ms | 10 |
| Render demo | 2.03 s | 3.01 s | 3.04 s | 10 |

The hosted p95 was about 5.8 times the local p95 for this sample. The Render run transferred about 1.1 GB according to k6. A separate full download was also checked against the uploaded fixture: both files were 104,857,600 bytes and had SHA-256 `20492a4d0d84f8beb1767f6616229f85d44c2827b64bdbfb260ee12fa1109e0e`.

The committed repository script was also run locally to verify it before publishing: p50 78.28 ms, p95 107.41 ms, and max 113.57 ms across ten requests, with 20/20 checks passed and no failed requests. This local p95 differs from the 521 ms local result in the original paired comparison. Local transfer results vary with run conditions, so the table retains the original same-round local/Render pair.

## 100 MiB upload verification

The hosted UI accepted `render-100m-test.bin` after a hard reload of the GitHub Pages tab refreshed its browser state. The Render file list showed 100 MiB, and downloading the file returned the exact byte count and matching SHA-256 above. The first attempts from the long-lived tab reached Render without a Turnstile token; the successful attempt showed that the security check passed. Turnstile remains enabled.

This was one successful live upload, not an upload p95; one observation cannot establish a useful percentile. The demo permits two uploads or copies per five minutes and validates single-use Turnstile tokens, so a useful hosted upload distribution cannot be collected within this bounded run. No rate limits were disabled. Current advertised limits remain 30 API requests in a burst, 30 replenished every 30 seconds, at most four concurrent requests, two uploads/copies per five minutes, and 20 moves/deletes per minute.

## Repeating the download test

The k6 script is [`performance/k6/download-100m.js`](../performance/k6/download-100m.js). It is restricted to the local test URL and the Render demo URL, runs ten sequential reads, and discards response bodies while checking status and length.

The local app must expose `render-100m-verification.bin` from its configured file root. The Render demo must contain `render-100m-test.bin`; that test file is in Render's ephemeral filesystem and can disappear after a spin-down, restart, or redeploy.

```sh
BASE_URL=http://127.0.0.1:20080 \
  k6 run --summary-export=/tmp/local-download-summary.json \
  TestProject/performance/k6/download-100m.js

BASE_URL=https://file-explorer-demo-api.onrender.com \
  k6 run --summary-export=/tmp/render-download-summary.json \
  TestProject/performance/k6/download-100m.js
```
