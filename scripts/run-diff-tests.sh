#!/usr/bin/env bash
# =============================================================================
# run-diff-tests.sh - differential testing against the genuine PowerBASIC 3.5.
# =============================================================================
# Every tests/diff/*.BAS is compiled twice - once with the original PBC.EXE
# (inside DOSBox) and once with our pbc (on the host) - and executed under
# DOSBox. The programs write all observable results to RESULT.TXT (PB's PRINT
# goes straight to video memory, so stdout capture is useless with the real
# compiler); the two RESULT.TXT files must match byte for byte (CRLF aside).
#
# The proprietary toolchain is NOT in the repo: place PBC.EXE in tools/pb35/
# (or point PB35_DIR at it). Without it this harness SKIPS with exit 0.
set -euo pipefail
cd "$(dirname "$0")/.."

PB35="${PB35_DIR:-tools/pb35}"
[ -f "$PB35/PBC.EXE" ] || { echo "::notice::real PBC.EXE not found in $PB35 - differential tests skipped."; exit 0; }

DOSBOX="${DOSBOX_EXE:-}"
if [ -z "$DOSBOX" ]; then
  for candidate in dosbox-staging dosbox; do
    command -v "$candidate" >/dev/null && DOSBOX=$candidate && break
  done
fi
if [ -z "$DOSBOX" ]; then
  found=$(find tools/dosbox -iname "dosbox*.exe" 2>/dev/null | head -1 || true)
  [ -n "$found" ] && DOSBOX=$found
fi
[ -n "$DOSBOX" ] || { echo "::error::no DOSBox found (set DOSBOX_EXE)"; exit 1; }

echo "building compiler ..."
dotnet build pbc -c Release -v q --nologo
PBC_OURS="dotnet run --project pbc -c Release --no-build --"

shopt -s nullglob
tests=( tests/diff/*.BAS tests/diff/*.bas )
[ ${#tests[@]} -gt 0 ] || { echo "::error::no tests/diff/*.BAS"; exit 1; }

rm -rf build/diff && mkdir -p build/diff/real build/diff/ours
cp "$PB35/PBC.EXE" build/diff/real/

run_dosbox() { # $1 = conf file, $2 = sentinel dir
  rm -f "$2/DONE.TXT"
  "$DOSBOX" -conf "$1" >/dev/null 2>&1 &
  local pid=$!
  for _ in $(seq 1 600); do
    { [ -f "$2/DONE.TXT" ] || ! kill -0 "$pid" 2>/dev/null; } && break
    sleep 0.2
  done
  if kill -0 "$pid" 2>/dev/null; then
    sleep 0.3
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  [ -f "$2/DONE.TXT" ]
}

winpath() { cd "$1" && { pwd -W 2>/dev/null || pwd; }; }

fail=0
i=0
for t in "${tests[@]}"; do
  i=$((i+1)); name=$(basename "$t"); name="${name%.*}"

  # --- genuine PBC.EXE: compile AND run inside DOSBox -----------------------
  cp "$t" "build/diff/real/T.BAS"
  rm -f build/diff/real/RESULT.TXT build/diff/real/T.EXE
  {
    echo "[sdl]"; echo "[cpu]"; echo "core=auto"; echo "cycles=max"
    echo "[dosbox]"; echo "ems=true"
    echo "[autoexec]"
    echo "mount c \"$(winpath build/diff/real)\""
    echo "c:"
    echo "PBC.EXE -CE T.BAS > PBCOUT.TXT"
    echo "T.EXE"
    echo "echo ok > DONE.TXT"
    echo "exit"
  } > build/diff/real.conf
  if ! run_dosbox build/diff/real.conf build/diff/real || [ ! -f build/diff/real/RESULT.TXT ]; then
    echo "FAIL  $name (real PBC produced no RESULT.TXT)"
    [ -f build/diff/real/PBCOUT.TXT ] && tail -5 build/diff/real/PBCOUT.TXT | sed 's/^/      /'
    fail=1; continue
  fi

  # --- our compiler: compile on host, run inside DOSBox ---------------------
  cp "$t" "build/diff/ours/T.BAS"
  rm -f build/diff/ours/RESULT.TXT build/diff/ours/T.EXE
  if ! $PBC_OURS "build/diff/ours/T.BAS" -O "build/diff/ours/T.EXE" > "build/diff/ours/pbcout.txt" 2>&1; then
    echo "FAIL  $name (our compile)"; sed 's/^/      /' "build/diff/ours/pbcout.txt"; fail=1; continue
  fi
  {
    echo "[sdl]"; echo "[cpu]"; echo "core=auto"; echo "cycles=max"
    echo "[dosbox]"; echo "ems=true"
    echo "[autoexec]"
    echo "mount c \"$(winpath build/diff/ours)\""
    echo "c:"
    echo "T.EXE"
    echo "echo ok > DONE.TXT"
    echo "exit"
  } > build/diff/ours.conf
  if ! run_dosbox build/diff/ours.conf build/diff/ours || [ ! -f build/diff/ours/RESULT.TXT ]; then
    echo "FAIL  $name (our EXE produced no RESULT.TXT)"; fail=1; continue
  fi

  # --- compare ---------------------------------------------------------------
  if diff -q <(tr -d '\r' < build/diff/real/RESULT.TXT) <(tr -d '\r' < build/diff/ours/RESULT.TXT) >/dev/null; then
    echo "PASS  $name (identical to genuine PB 3.5)"
  else
    echo "FAIL  $name (output differs from genuine PB 3.5)"
    { diff <(tr -d '\r' < build/diff/real/RESULT.TXT) <(tr -d '\r' < build/diff/ours/RESULT.TXT) || true; } | head -20 | sed 's/^/      /'
    fail=1
  fi
done

exit $fail
