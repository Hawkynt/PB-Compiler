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

# Warn about executables that cannot serve as a DOS oracle. This is where pds70 and
# pds71 went wrong: both were staged from a PDS 7.x BINB\ directory (the OS/2 tools)
# rather than BIN\ (the DOS ones), packed without complaint, and only surfaced as two
# batteries that skip - by which point the cause is several steps away. An SZDD file
# hides the same thing behind a second layer, which is how pds70 read as merely
# "still compressed" for as long as it did. A warning, not an error: a container may
# legitimately hold OS/2 or compressed members that are not the oracle itself.
warn_wrong_build() { # $1 = staged slot
  local f magic lfanew sig
  while IFS= read -r f; do
    magic=$(od -An -tx1 -N 8 "$f" 2>/dev/null | tr -d ' \n')
    if [ "$magic" = "535a2088f02733d1" ]; then
      echo "::warning::$f is SZDD-compressed - expand it first (scripts/expand-szdd.py)"
      continue
    fi
    [ "${magic:0:4}" = "4d5a" ] || continue
    lfanew=$(od -An -tu2 -j 60 -N 2 "$f" 2>/dev/null | tr -d ' \n')
    case "$lfanew" in ''|*[!0-9]*) continue;; esac
    [ "$lfanew" -gt 0 ] || continue
    sig=$(od -An -c -j "$lfanew" -N 2 "$f" 2>/dev/null | tr -d ' \n')
    if [ "$sig" = "NE" ] && [ "$(od -An -tu1 -j $((lfanew + 54)) -N 1 "$f" 2>/dev/null | tr -d ' \n')" = "1" ]; then
      echo "::warning::$f is an OS/2 executable, not a DOS one - PDS media keep the DOS tools in BIN\\, not BINB\\"
    fi
  done < <(find "$1" -type f \( -iname "*.exe" -o -iname "*.com" \) 2>/dev/null)
}


for dialect in "${dialects[@]}"; do
  slot="tools/$dialect"
  enc="tools/$dialect-toolchain.tar.enc"
  if [ ! -d "$slot" ] || [ -z "$(ls -A "$slot" 2>/dev/null)" ]; then
    echo "::warning::$slot is empty or missing - skipping"
    continue
  fi
  warn_wrong_build "$slot"
  # -C into the slot so the tar root holds the files directly (decrypt does tar xz -C slot)
  if tar czf - -C "$slot" . | openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:PB_TOOLCHAIN_KEY -out "$enc"; then
    echo "packed $slot -> $enc ($(wc -c < "$enc") bytes)"
  else
    echo "::error::failed to pack $slot"; exit 1
  fi
done
