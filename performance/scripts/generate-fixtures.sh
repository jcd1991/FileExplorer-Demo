#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIXTURE_DIR="$SCRIPT_DIR/../fixtures"
mkdir -p "$FIXTURE_DIR"
FIXTURE_DIR="$(cd "$FIXTURE_DIR" && pwd)"
for size in 1 10 100; do
  target="$FIXTURE_DIR/payload-${size}m.bin"
  if [[ ! -f "$target" ]]; then
    dd if=/dev/zero of="$target" bs=1048576 count="$size" status=none
  fi
done
printf 'Generated fixtures in %s\n' "$FIXTURE_DIR"
