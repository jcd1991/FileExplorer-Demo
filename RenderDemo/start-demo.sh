#!/usr/bin/env bash
set -euo pipefail

cd /app/app
dotnet TestProject.dll --urls http://127.0.0.1:10001 &
backend_pid=$!

proxy_pid=""
stop_children() {
    if [[ -n "$proxy_pid" ]]; then kill "$proxy_pid" 2>/dev/null || true; fi
    kill "$backend_pid" 2>/dev/null || true
}
trap stop_children TERM INT EXIT

dotnet /app/proxy/RateLimitProxy.dll --urls "http://0.0.0.0:${PORT:-10000}" &
proxy_pid=$!
wait "$proxy_pid"
