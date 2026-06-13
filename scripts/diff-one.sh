#!/usr/bin/env bash
# diff-one.sh <file.bas> [dialect]  - compile one battery with the genuine PB
# 3.5 oracle and with our compiler (default dialect, or the one given), run both
# in DOSBox, and diff RESULT.TXT. Exit 0 = byte-identical observable behavior.
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="$1"; DIALECT="${2:-}"
DOSBOX="${DOSBOX_EXE:-$PWD/tools/dosbox/dosbox}"
PB35="${PB35_DIR:-tools/pb35}"
OUT="build/diff-one"
rm -rf "$OUT" && mkdir -p "$OUT/real" "$OUT/ours"
cp "$PB35/PBC.EXE" "$OUT/real/"

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
  echo "mount c \"$(winpath "$OUT/real")\""; echo "c:"
  echo "PBC.EXE -CE T.BAS > PBCOUT.TXT"
  echo "T.EXE"; echo "echo ok > DONE.TXT"; echo "exit"
} > "$OUT/real.conf"
run_dosbox "$OUT/real.conf" "$OUT/real" || { echo "real failed"; tail "$OUT/real/PBCOUT.TXT" 2>/dev/null; exit 2; }

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
