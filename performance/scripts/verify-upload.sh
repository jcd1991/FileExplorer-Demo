#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BASE_URL="${BASE_URL:-http://127.0.0.1:5178}"
FIXTURE_FILE="${FIXTURE_FILE:-$HARNESS_DIR/fixtures/payload-1m.bin}"
UPLOAD_PATH="${UPLOAD_PATH:-}"
RUN_ID="${RUN_ID:-manual}"
VERIFY_NAME="verify-${RUN_ID}.bin"
VERIFY_ENCODED="$(node -p 'encodeURIComponent(process.argv[1])' "$VERIFY_NAME")"
PATH_ENCODED="$(node -p 'encodeURIComponent(process.argv[1] || "")' "$UPLOAD_PATH")"
DOWNLOAD_FILE="$HARNESS_DIR/results/verify-${RUN_ID}.bin"
RESPONSE_FILE="$HARNESS_DIR/results/verify-${RUN_ID}.json"
mkdir -p "$HARNESS_DIR/results"

curl --fail --silent --show-error --max-time "${REQUEST_TIMEOUT_SECONDS:-120}" \
  -F "file=@${FIXTURE_FILE};filename=${VERIFY_NAME};type=application/octet-stream" \
  "$BASE_URL/api/files/upload?path=${PATH_ENCODED}" > "$RESPONSE_FILE"
node -e 'const r=require(process.argv[1]); if (r.name!==process.argv[2] || r.sizeBytes!==Number(process.argv[3])) process.exit(1)' \
  "$RESPONSE_FILE" "$VERIFY_NAME" "$(wc -c < "$FIXTURE_FILE" | tr -d ' ')"
if [[ -z "$UPLOAD_PATH" ]]; then
  DOWNLOAD_PATH="$VERIFY_NAME"
else
  DOWNLOAD_PATH="${UPLOAD_PATH%/}/$VERIFY_NAME"
fi
DOWNLOAD_ENCODED="$(node -p 'encodeURIComponent(process.argv[1])' "$DOWNLOAD_PATH")"
curl --fail --silent --show-error --max-time "${REQUEST_TIMEOUT_SECONDS:-120}" \
  "$BASE_URL/api/files/download?path=${DOWNLOAD_ENCODED}" -o "$DOWNLOAD_FILE"
cmp -s "$FIXTURE_FILE" "$DOWNLOAD_FILE"
printf 'Upload/download verification passed: %s\n' "$VERIFY_NAME"
