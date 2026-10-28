#!/usr/bin/env bash
set -euo pipefail

repoRoot=$(cd "$(dirname "$0")/../.." && pwd)
gate="$repoRoot/scripts/require-ci-success.sh"
[ -f "$gate" ] || { echo 'FAIL: required-result guard is missing' >&2; exit 1; }

expectStatus() {
  local expected="$1" actual=0
  shift
  bash <(tr -d '\r' < "$gate") "$@" >/dev/null 2>&1 || actual=$?
  if [ "$actual" -ne "$expected" ]; then
    echo "FAIL: results [$*] returned $actual, expected $expected" >&2
    exit 1
  fi
}

for core in success failure cancelled skipped; do
  for oracle in success failure cancelled skipped; do
    expected=1
    if [ "$core" = success ] && [ "$oracle" = success ]; then
      expected=0
    fi
    expectStatus "$expected" "$core" "$oracle"
  done
done
expectStatus 2
expectStatus 2 success
expectStatus 2 success success success
expectStatus 2 '' success
expectStatus 2 success ''
expectStatus 2 unknown success
expectStatus 2 success unknown
echo 'PASS: required status accepts only two successful job groups'
