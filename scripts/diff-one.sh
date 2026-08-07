#!/usr/bin/env bash
# diff-one.sh <file.bas> [dialect]  - compile one battery with the genuine PB
# 3.5 oracle and with our compiler (default dialect, or the one given), run both
# in DOSBox, and diff RESULT.TXT. Exit 0 = byte-identical observable behavior.
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="$1"; DIALECT="${2:-}"
DOSBOX="${DOSBOX_EXE:-$PWD/tools/dosbox/dosbox}"
OUT="build/diff-one"
rm -rf "$OUT" && mkdir -p "$OUT/real" "$OUT/ours"

# The oracle is the one belonging to the DIALECT, which is the whole point of passing one. This
# script used to compile the real side with PBC 3.5 unconditionally and only pass --dialect to our
# side, so `diff-one.sh x.bas qb45` compared QuickBASIC source built by PowerBASIC against the same
# source built as QuickBASIC - two languages - and reported the result as a fidelity verdict. Every
# non-PB comparison it printed was meaningless, and two investigations were run off its output
# before that was noticed.
LABEL="${DIALECT:-pb35}"
[ "$LABEL" = "pb36" ] && LABEL="pb35"      # pb36 is checked against the pb35 oracle
if [ "$LABEL" = "pb35" ]; then ORACLE="${PB35_DIR:-tools/pb35}"; else ORACLE="tools/$LABEL"; fi
INTERP="tests/diff/$LABEL/oracle.interpreter"   # GW/BASICA/QBasic: the interpreter IS the run
TEMPLATE="tests/diff/$LABEL/oracle.conf"        # QB/PDS/TB: DOS commands that build C:\T.EXE
[ -d "$ORACLE" ] || { echo "no oracle toolchain in $ORACLE (stage it, or set ${LABEL^^}_DIR)"; exit 2; }
if [ ! -f "$INTERP" ] && [ ! -f "$TEMPLATE" ]; then
  [ -f "$ORACLE/PBC.EXE" ] || { echo "no PBC.EXE in $ORACLE and no oracle.conf for $LABEL"; exit 2; }
  cp "$ORACLE/PBC.EXE" "$OUT/real/"
fi

winpath() { cd "$1" && { pwd -W 2>/dev/null || pwd; }; }
run_dosbox() {
  rm -f "$2/DONE.TXT"
  "$DOSBOX" -conf "$1" >/dev/null 2>&1 &
  local pid=$!
  for _ in $(seq 1 600); do
    { [ -f "$2/DONE.TXT" ] || ! kill -0 "$pid" 2>/dev/null; } && break
    sleep 0.2
  done
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  [ -f "$2/DONE.TXT" ]
}

cp "$SRC" "$OUT/real/T.BAS"
{
  echo "[sdl]"; echo "window_position = -10000,-10000"
  echo "[cpu]"; echo "core=auto"; echo "cycles=max"
  echo "[dosbox]"; echo "ems=true"
  echo "[autoexec]"
  echo "mount c \"$(winpath "$OUT/real")\""
  if [ -f "$INTERP" ]; then
    echo "mount d \"$(winpath "$ORACLE")\""; echo "c:"
    sed -e 's/\r$//' "$INTERP"                     # the program writes RESULT.TXT and ends with SYSTEM
  elif [ -f "$TEMPLATE" ]; then
    echo "mount d \"$(winpath "$ORACLE")\""; echo "c:"
    sed -e 's/\r$//' "$TEMPLATE"
    echo "T.EXE"
  else
    echo "c:"
    echo "PBC.EXE -CE T.BAS > PBCOUT.TXT"
    echo "T.EXE"
  fi
  echo "echo ok > DONE.TXT"; echo "exit"
} > "$OUT/real.conf"
run_dosbox "$OUT/real.conf" "$OUT/real" || { echo "real failed"; tail "$OUT/real/PBCOUT.TXT" "$OUT/real/BCLOG.TXT" 2>/dev/null; exit 2; }
# A missing RESULT.TXT means the genuine side never ran - not that the outputs match. Saying so is
# the difference between "no divergence" and "no measurement".
[ -f "$OUT/real/RESULT.TXT" ] || {
  echo "the $LABEL oracle produced no RESULT.TXT - it did not run, so there is nothing to compare"
  tail -5 "$OUT/real/PBCOUT.TXT" "$OUT/real/BCLOG.TXT" 2>/dev/null; exit 2; }

cp "$SRC" "$OUT/ours/T.BAS"
flags=(); [ -n "$DIALECT" ] && flags=(--dialect "$DIALECT")
DOTNET_ROLL_FORWARD=Major dotnet run --project pbc -c Release --no-build -- ${flags[@]+"${flags[@]}"} "$OUT/ours/T.BAS" -O "$OUT/ours/T.EXE"
{
  echo "[sdl]"; echo "window_position = -10000,-10000"
  echo "[cpu]"; echo "core=auto"; echo "cycles=max"
  echo "[dosbox]"; echo "ems=true"
  echo "[autoexec]"
  echo "mount c \"$(winpath "$OUT/ours")\""; echo "c:"
  echo "T.EXE"; echo "echo ok > DONE.TXT"; echo "exit"
} > "$OUT/ours.conf"
run_dosbox "$OUT/ours.conf" "$OUT/ours" || { echo "ours run failed"; exit 2; }

if diff <(tr -d '\r' < "$OUT/real/RESULT.TXT") <(tr -d '\r' < "$OUT/ours/RESULT.TXT"); then
  echo "PASS  identical"
else
  echo "FAIL  differs"; exit 1
fi
