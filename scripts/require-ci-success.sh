#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo 'Expected core and oracle job results' >&2
  exit 2
fi
for result in "$@"; do
  case "$result" in
    success|failure|cancelled|skipped) ;;
    *) echo "Invalid job result: $result" >&2; exit 2 ;;
  esac
done
if [ "$1" != success ] || [ "$2" != success ]; then
  echo "Required CI groups did not succeed: core=$1 oracle=$2" >&2
  exit 1
fi
echo 'Required core and oracle groups succeeded'
