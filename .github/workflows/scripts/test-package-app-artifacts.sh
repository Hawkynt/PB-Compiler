#!/usr/bin/env bash
set -euo pipefail

fixture_dir=$(mktemp -d)
mkdir -p "$fixture_dir/win-x64" "$fixture_dir/linux-x64"
printf 'windows fixture\n' > "$fixture_dir/win-x64/pbc.exe"
printf 'linux fixture\n' > "$fixture_dir/linux-x64/pbc"

bash .github/workflows/scripts/package-app-artifacts.sh "$fixture_dir"
tar -tzf "$fixture_dir/pbc-win-x64.tar.gz" | grep -Fxq 'win-x64/pbc.exe'
tar -tzf "$fixture_dir/pbc-linux-x64.tar.gz" | grep -Fxq 'linux-x64/pbc'
mode=$(tar -tvzf "$fixture_dir/pbc-linux-x64.tar.gz" linux-x64/pbc | cut -c4)
if [[ "$mode" != x ]]; then
  echo 'Linux application is not executable in the release archive' >&2
  exit 1
fi

missing_dir=$(mktemp -d)
mkdir -p "$missing_dir/win-x64" "$missing_dir/linux-x64"
printf 'windows fixture\n' > "$missing_dir/win-x64/pbc.exe"
if bash .github/workflows/scripts/package-app-artifacts.sh "$missing_dir" 2> /dev/null; then
  echo 'packaging accepted a missing Linux application' >&2
  exit 1
fi
touch "$missing_dir/linux-x64/pbc"
if bash .github/workflows/scripts/package-app-artifacts.sh "$missing_dir" 2> /dev/null; then
  echo 'packaging accepted an empty Linux application' >&2
  exit 1
fi
