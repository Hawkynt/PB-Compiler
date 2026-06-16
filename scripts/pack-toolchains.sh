#!/usr/bin/env bash
# =============================================================================
# pack-toolchains.sh - encrypt staged oracle toolchains into the AES containers
# the repo tracks (the inverse of the decrypt step in run-diff-tests.sh).
# =============================================================================
# The proprietary oracle binaries never live in the repo - only
# tools/<dialect>-toolchain.tar.enc is committed, and it is decrypted at run
# time with PB_TOOLCHAIN_KEY (a CI secret / local env var). This packs a
# populated tools/<dialect>/ directory back into that container, matching the
# decrypt format exactly:
#
#   openssl enc -d -aes-256-cbc -pbkdf2 -in <enc> -pass env:PB_TOOLCHAIN_KEY | tar xz -C tools/<dialect>
#
# Usage:
#   PB_TOOLCHAIN_KEY=... scripts/pack-toolchains.sh             # pack every populated tools/<dialect>/
#   PB_TOOLCHAIN_KEY=... scripts/pack-toolchains.sh tb10 gw     # pack only the named dialects
#
# Commit the resulting tools/<dialect>-toolchain.tar.enc; the raw directory
# stays gitignored.
set -euo pipefail
cd "$(dirname "$0")/.."

[ -n "${PB_TOOLCHAIN_KEY:-}" ] || { echo "::error::PB_TOOLCHAIN_KEY is not set"; exit 1; }

# explicit dialect list, or every populated tools/<dialect>/ (excluding helpers)
dialects=("$@")
if [ ${#dialects[@]} -eq 0 ]; then
  for d in tools/*/; do
    name=$(basename "$d")
    case "$name" in dosbox|_downloads) continue ;; esac
    [ -n "$(ls -A "$d" 2>/dev/null)" ] && dialects+=("$name")
  done
fi

[ ${#dialects[@]} -gt 0 ] || { echo "no populated tools/<dialect>/ directories to pack"; exit 0; }

for dialect in "${dialects[@]}"; do
  slot="tools/$dialect"
  enc="tools/$dialect-toolchain.tar.enc"
  if [ ! -d "$slot" ] || [ -z "$(ls -A "$slot" 2>/dev/null)" ]; then
    echo "::warning::$slot is empty or missing - skipping"
    continue
  fi
  # -C into the slot so the tar root holds the files directly (decrypt does tar xz -C slot)
  if tar czf - -C "$slot" . | openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:PB_TOOLCHAIN_KEY -out "$enc"; then
    echo "packed $slot -> $enc ($(wc -c < "$enc") bytes)"
  else
    echo "::error::failed to pack $slot"; exit 1
  fi
done
