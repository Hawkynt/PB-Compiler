#!/usr/bin/env bash
# Compare the exhaustive statement-form accept/reject matrix with genuine DOS compilers.
# Runtime behavior (and interpreter-only dialects) belongs to run-diff-tests.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

DOSBOX="${DOSBOX_EXE:-}"
if [ -z "$DOSBOX" ]; then
  for candidate in dosbox-staging dosbox; do
    command -v "$candidate" >/dev/null && DOSBOX=$candidate && break
  done
fi
[ -n "$DOSBOX" ] || { echo "::error::no DOSBox found (set DOSBOX_EXE)"; exit 1; }

dotnet test PowerBasic.Compiler.Tests -c Release \
  --filter "FullyQualifiedName~StatementSurfaceOracleMaterialTests" \
  --logger "console;verbosity=minimal"

if [ -n "${PB_TOOLCHAIN_KEY:-}" ]; then
  for enc in tools/*-toolchain.tar.enc; do
    [ -e "$enc" ] || continue
    dialect=$(basename "$enc"); dialect="${dialect%-toolchain.tar.enc}"
    slot="tools/$dialect"
    [ -d "$slot" ] && [ -n "$(ls -A "$slot" 2>/dev/null)" ] && continue
    mkdir -p "$slot"
    if openssl enc -d -aes-256-cbc -pbkdf2 -in "$enc" -pass env:PB_TOOLCHAIN_KEY 2>/dev/null \
        | tar xz -C "$slot" 2>/dev/null; then
      echo "unpacked oracle toolchain: $dialect"
    else
      echo "::warning::could not decrypt $enc; $dialect will skip"
      rmdir "$slot" 2>/dev/null || true
    fi
  done
fi

run_dosbox() { # $1 = conf, $2 = sentinel directory
  unlink "$2/DONE.TXT" 2>/dev/null || true
  "$DOSBOX" -conf "$1" >/dev/null 2>&1 &
  local pid=$!
  for _ in $(seq 1 300); do
    { [ -f "$2/DONE.TXT" ] || ! kill -0 "$pid" 2>/dev/null; } && break
    sleep 0.2
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  [ -f "$2/DONE.TXT" ]
}

winpath() { cd "$1" && { pwd -W 2>/dev/null || pwd; }; }

selected() { # comma-separated filter, candidate
  [ -z "$1" ] || case ",$1," in *",$2,"*) return 0;; *) return 1;; esac
}

manifest="build/conformance/syntax/manifest.tsv"
results="build/conformance/syntax/oracle-results.tsv"
work="build/conformance/syntax-oracle"
mkdir -p "$work"
printf '# dialect\tform\texpected\tobserved\tstatus\n' > "$results"

dialect_filter="${DIALECTS:-}"
form_filter="${FORMS:-}"
max_cases="${MAX_CASES:-0}"
cases=0
passes=0
failures=0
skips=0

while IFS=$'\t' read -r dialect form expected source; do
  [ "${dialect#\#}" = "$dialect" ] || continue
  selected "$dialect_filter" "$dialect" || continue
  selected "$form_filter" "$form" || continue
  if [ "$max_cases" -gt 0 ] && [ "$cases" -ge "$max_cases" ]; then
    break
  fi

  # An interpreter has no compile-time acceptance boundary. Its lazy/deferred syntax and output
  # are exercised by dedicated programs in tests/diff/<dialect>, including DEADTEXT.BAS.
  case "$dialect" in basica|gw|qbasic)
    printf '%s\t%s\t%s\t-\tskip-interpreter\n' "$dialect" "$form" "$expected" >> "$results"
    skips=$((skips + 1))
    continue
  esac

  upper=$(echo "$dialect" | tr '[:lower:]' '[:upper:]')
  dir_var="${upper}_DIR"
  oracle="${!dir_var:-tools/$dialect}"
  template="tests/diff/$dialect/oracle.conf"
  if [ ! -f "$oracle/PBC.EXE" ] && { [ ! -f "$template" ] || [ ! -d "$oracle" ]; }; then
    printf '%s\t%s\t%s\t-\tskip-no-oracle\n' "$dialect" "$form" "$expected" >> "$results"
    skips=$((skips + 1))
    continue
  fi

  cases=$((cases + 1))
  case_dir="$work/$dialect"
  mkdir -p "$case_dir"
  cp "build/conformance/syntax/$source" "$case_dir/T.BAS"
  for stale in "$case_dir/T.EXE" "$case_dir/T.OBJ" "$case_dir/DONE.TXT"; do
    unlink "$stale" 2>/dev/null || true
  done
  conf="$work/$dialect.conf"
  {
    echo "[sdl]"
    echo "windowposition=-10000,-10000"
    echo "window_position = -10000,-10000"
    echo "[cpu]"
    echo "core=auto"
    echo "cycles=max"
    echo "[autoexec]"
    echo "mount c \"$(winpath "$case_dir")\""
    echo "mount d \"$(winpath "$oracle")\""
    echo "c:"
    if [ -f "$template" ]; then
      sed -e 's/\r$//' "$template"
    else
      echo "D:\\PBC.EXE -CE T.BAS > PBCOUT.TXT"
    fi
    echo "echo ok > DONE.TXT"
    echo "exit"
  } > "$conf"

  if ! run_dosbox "$conf" "$case_dir"; then
    printf '%s\t%s\t%s\t-\tinfra-timeout\n' "$dialect" "$form" "$expected" >> "$results"
    echo "SKIP  $dialect/$form (oracle did not return to DOS)"
    skips=$((skips + 1))
    continue
  fi

  observed=reject
  [ -f "$case_dir/T.EXE" ] && observed=accept
  if [ "$observed" = "$expected" ]; then
    status=pass
    passes=$((passes + 1))
  else
    status=mismatch
    failures=$((failures + 1))
    echo "FAIL  $dialect/$form expected=$expected oracle=$observed"
  fi
  printf '%s\t%s\t%s\t%s\t%s\n' "$dialect" "$form" "$expected" "$observed" "$status" >> "$results"
done < "$manifest"

echo "syntax oracle: $passes pass, $failures mismatch, $skips skip ($cases compiler probes)"
echo "results: $results"
[ "$failures" -eq 0 ]
