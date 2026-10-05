#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 1 ]]; then
  echo 'usage: package-app-artifacts.sh [artifact-directory]' >&2
  exit 2
fi

artifact_dir=${1:-dist}
for binary in win-x64/pbc.exe linux-x64/pbc; do
  if [[ ! -s "$artifact_dir/$binary" ]]; then
    echo "missing or empty application artifact: $artifact_dir/$binary" >&2
    exit 1
  fi
done

# upload-artifact removes executable permissions; restore them before archiving on Linux.
chmod +x "$artifact_dir/linux-x64/pbc"
for platform in win-x64 linux-x64; do
  archive="$artifact_dir/pbc-$platform.tar.gz"
  tar -czf "$archive" -C "$artifact_dir" "$platform"
  if [[ ! -s "$archive" ]] || ! tar -tzf "$archive" > /dev/null; then
    echo "application archive is empty or invalid: $archive" >&2
    exit 1
  fi
done
