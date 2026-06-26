#!/usr/bin/env bash
# Host-side round-trip gate (no DOSBox): for every program, emit-basic under its
# dialect, then recompile the emitted source under pb35. Reports compile success
# and counts residual ' [unsupported / /* fallback markers (which would make the
# output non-faithful). This is the fast local proxy for "turned back into pb35".
set -uo pipefail
cd "$(dirname "$0")/.."
PBC="$(find pbc/bin/Release -name pbc.dll 2>/dev/null | head -1)"
if [ -z "$PBC" ]; then
  echo "building pbc ..."
  dotnet build pbc -c Release -v q --nologo
  PBC="$(find pbc/bin/Release -name pbc.dll 2>/dev/null | head -1)"
fi
[ -n "$PBC" ] || { echo "::error::pbc.dll not found under pbc/bin/Release"; exit 1; }
TMP="${TMPDIR:-/tmp}/rt-check.$$"
mkdir -p "$TMP"
run() { DOTNET_ROLL_FORWARD=Major dotnet "$PBC" "$@"; }

pass=0 fail=0 fb=0
check() { # $1 = file, $2 = dialect
  local f="$1" d="$2" bas="$TMP/rt.bas" exe="$TMP/rt.exe" log="$TMP/rt.log"
  if ! run --dialect "$d" --emit-basic "$f" -O "$bas" >"$log" 2>&1; then
    echo "EMITFAIL $d $(basename "$f")"; head -2 "$log" | sed 's/^/   /'; fail=$((fail+1)); return
  fi
  local marks; marks=$(grep -cE "\[unsupported:|/\* [A-Za-z]+ \*/" "$bas" || true)
  if ! run --dialect pb35 "$bas" -O "$exe" >"$log" 2>&1; then
    echo "COMPFAIL $d $(basename "$f") (marks=$marks)"
    grep -oE "error: .*" "$log" | head -1 | sed 's/^/   /' || true
    fail=$((fail+1)); return
  fi
  if [ "$marks" -gt 0 ]; then echo "MARKS    $d $(basename "$f") ($marks fallback markers)"; fb=$((fb+1)); fi
  pass=$((pass+1))
}

shopt -s nullglob nocaseglob
# pb35 top-level + pb36 over the same corpus
for f in tests/diff/*.bas; do check "$f" pb35; done
PB36ONLY="${1:-}"
if [ "$PB36ONLY" != "--pb35only" ]; then
  for f in tests/diff/*.bas; do check "$f" pb36; done
  for dir in tests/diff/*/; do
    d=$(basename "$dir"); [ "$d" = "basica" ] && d=basica
    fs=("$dir"*.bas); [ ${#fs[@]} -gt 0 ] || continue
    for f in "${fs[@]}"; do check "$f" "$d"; done
  done
fi

echo "================================================"
echo "pass=$pass  fail=$fail  (clean-compile)   fallback-marker-files=$fb"
rm -rf "$TMP"
[ "$fail" -eq 0 ]
