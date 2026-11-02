#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
UPLOAD_ROOT="$HARNESS_DIR/uploads"
UPLOAD_DIR="$UPLOAD_ROOT/data"
MARKER="$UPLOAD_ROOT/.harness-owned"

if [[ "$UPLOAD_ROOT" != "$HARNESS_DIR/uploads" || ! -f "$MARKER" || ! -d "$UPLOAD_DIR" ]]; then
  printf 'Refusing cleanup: expected harness marker and data directory are missing.\n' >&2
  exit 2
fi
find "$UPLOAD_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
printf 'Cleared files beneath the harness-owned directory: %s\n' "$UPLOAD_DIR"
