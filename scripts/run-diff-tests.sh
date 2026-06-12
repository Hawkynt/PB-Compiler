#!/usr/bin/env bash
# =============================================================================
# run-diff-tests.sh - differential testing against the genuine PowerBASIC
# compilers (and, eventually, other DOS-era BASIC dialects).
# =============================================================================
# Every tests/diff/*.BAS is compiled twice - once with the original PBC.EXE
# (inside DOSBox) and once with our pbc (on the host) - and executed under
# DOSBox. The programs write all observable results to RESULT.TXT (PB's PRINT
# goes straight to video memory, so stdout capture is useless with the real
# compiler); the two RESULT.TXT files must match byte for byte (CRLF aside).
#
# Dialect batteries: tests/diff/<dialect>/*.BAS (e.g. tests/diff/pb30/) are
# compiled with `pbc --dialect <dialect>` on our side and with the matching
# oracle compiler from tools/<dialect>/PBC.EXE (override via PB30_DIR etc.).
# Batteries whose oracle binary is absent are SKIPPED - drop the proprietary
# compiler into tools/<dialect>/ and they activate automatically.
#
# The proprietary toolchains are NOT in the repo: place PBC.EXE 3.50 in
# tools/pb35/ (or point PB35_DIR at it). Without it this harness SKIPS with
# exit 0.
set -euo pipefail
cd "$(dirname "$0")/.."

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

# Decrypt any AES-packed oracle toolchains into their tools/<dialect>/ slots.
# The proprietary binaries never live in the repo - only tools/<dialect>-toolchain.tar.enc
# is tracked; PB_TOOLCHAIN_KEY (a CI secret / local env var) unlocks them. A slot
# already populated on disk (a local install) wins and is left untouched.
if [ -n "${PB_TOOLCHAIN_KEY:-}" ]; then
  for enc in tools/*-toolchain.tar.enc; do
    [ -e "$enc" ] || continue
    dialect=$(basename "$enc"); dialect="${dialect%-toolchain.tar.enc}"
    slot="tools/$dialect"
    [ -d "$slot" ] && [ -n "$(ls -A "$slot" 2>/dev/null)" ] && continue
    mkdir -p "$slot"
    if openssl enc -d -aes-256-cbc -pbkdf2 -in "$enc" -pass env:PB_TOOLCHAIN_KEY 2>/dev/null | tar xz -C "$slot" 2>/dev/null; then
      echo "unpacked oracle toolchain: $dialect"
    else
      echo "::warning::could not decrypt $enc (wrong PB_TOOLCHAIN_KEY?) - $dialect battery will skip"
      rmdir "$slot" 2>/dev/null || true
    fi
  done
fi

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

run_battery() { # $1 = oracle dir, $2 = dialect flag ("" = default pb35), $3.. = test files
  local oracle="$1" dialect="$2"
  shift 2
  local label="${dialect:-pb35}"

  # Non-PBC oracles (QB/PDS/TB/...) describe their compile step in
  # tests/diff/<dialect>/oracle.conf: plain DOS commands that turn C:\T.BAS
  # into C:\T.EXE, with the oracle toolchain mounted read-only as D:.
  # Without such a template the classic PBC.EXE invocation is used.
  local template=""
  [ -n "$dialect" ] && [ -f "tests/diff/$dialect/oracle.conf" ] && template="tests/diff/$dialect/oracle.conf"

  rm -rf "build/diff/$label" && mkdir -p "build/diff/$label/real" "build/diff/$label/ours"
  [ -z "$template" ] && cp "$oracle/PBC.EXE" "build/diff/$label/real/"

  local t name
  for t in "$@"; do
    name=$(basename "$t"); name="${name%.*}"

    # --- genuine oracle: compile AND run inside DOSBox ----------------------
    cp "$t" "build/diff/$label/real/T.BAS"
    rm -f "build/diff/$label/real/RESULT.TXT" "build/diff/$label/real/T.EXE"
    {
      echo "[sdl]"
      echo "windowposition=-10000,-10000"   # dosbox-x key; others warn-and-ignore
      echo "window_position = -10000,-10000" # dosbox-staging key
      echo "[cpu]"; echo "core=auto"; echo "cycles=max"
      echo "[dosbox]"; echo "ems=true"
      echo "[autoexec]"
      echo "mount c \"$(winpath "build/diff/$label/real")\""
      if [ -n "$template" ]; then
        echo "mount d \"$(winpath "$oracle")\""
        echo "c:"
        sed -e 's/\r$//' "$template"
      else
        echo "c:"
        echo "PBC.EXE -CE T.BAS > PBCOUT.TXT"
      fi
      echo "T.EXE"
      echo "echo ok > DONE.TXT"
      echo "exit"
    } > "build/diff/$label/real.conf"
    if ! run_dosbox "build/diff/$label/real.conf" "build/diff/$label/real" || [ ! -f "build/diff/$label/real/RESULT.TXT" ]; then
      echo "FAIL  $label/$name (real PBC produced no RESULT.TXT)"
      [ -f "build/diff/$label/real/PBCOUT.TXT" ] && tail -5 "build/diff/$label/real/PBCOUT.TXT" | sed 's/^/      /'
      fail=1; continue
    fi

    # --- our compiler: compile on host, run inside DOSBox -------------------
    cp "$t" "build/diff/$label/ours/T.BAS"
    rm -f "build/diff/$label/ours/RESULT.TXT" "build/diff/$label/ours/T.EXE"
    local flags=()
    [ -n "$dialect" ] && flags=(--dialect "$dialect")
    if ! $PBC_OURS ${flags[@]+"${flags[@]}"} "build/diff/$label/ours/T.BAS" -O "build/diff/$label/ours/T.EXE" > "build/diff/$label/ours/pbcout.txt" 2>&1; then
      echo "FAIL  $label/$name (our compile)"; sed 's/^/      /' "build/diff/$label/ours/pbcout.txt"; fail=1; continue
    fi
    {
      echo "[sdl]"
      echo "windowposition=-10000,-10000"   # dosbox-x key; others warn-and-ignore
      echo "window_position = -10000,-10000" # dosbox-staging key
      echo "[cpu]"; echo "core=auto"; echo "cycles=max"
      echo "[dosbox]"; echo "ems=true"
      echo "[autoexec]"
      echo "mount c \"$(winpath "build/diff/$label/ours")\""
      echo "c:"
      echo "T.EXE"
      echo "echo ok > DONE.TXT"
      echo "exit"
    } > "build/diff/$label/ours.conf"
    if ! run_dosbox "build/diff/$label/ours.conf" "build/diff/$label/ours" || [ ! -f "build/diff/$label/ours/RESULT.TXT" ]; then
      echo "FAIL  $label/$name (our EXE produced no RESULT.TXT)"; fail=1; continue
    fi

    # --- compare -------------------------------------------------------------
    if diff -q <(tr -d '\r' < "build/diff/$label/real/RESULT.TXT") <(tr -d '\r' < "build/diff/$label/ours/RESULT.TXT") >/dev/null; then
      echo "PASS  $label/$name (identical to genuine $label)"
    else
      echo "FAIL  $label/$name (output differs from genuine $label)"
      { diff <(tr -d '\r' < "build/diff/$label/real/RESULT.TXT") <(tr -d '\r' < "build/diff/$label/ours/RESULT.TXT") || true; } | head -20 | sed 's/^/      /'
      fail=1
    fi
  done
}

shopt -s nullglob

# --- main battery: PB 3.5 (the default dialect) ------------------------------
PB35="${PB35_DIR:-tools/pb35}"
if [ -f "$PB35/PBC.EXE" ]; then
  tests=( tests/diff/*.BAS tests/diff/*.bas )
  [ ${#tests[@]} -gt 0 ] || { echo "::error::no tests/diff/*.BAS"; exit 1; }
  rm -rf build/diff && mkdir -p build/diff
  run_battery "$PB35" "" "${tests[@]}"
else
  echo "::notice::real PBC.EXE not found in $PB35 - differential tests skipped."
  exit 0
fi

# --- pb36 optimizer pass: same batteries, our side compiled with --dialect ---
# --- pb36; outputs must STILL match genuine PBC 3.50 byte for byte         ---
run_battery "$PB35" "pb36" "${tests[@]}"

# --- dialect batteries: tests/diff/<dialect>/ + tools/<dialect>/ oracle ------
# PB dialects ship PBC.EXE; every other family (QB/PDS/TB/...) ships an
# oracle.conf command template next to its battery (see run_battery above).
for dir in tests/diff/*/; do
  [ -d "$dir" ] || continue
  dialect=$(basename "$dir")
  [ "$dialect" = "pb36" ] && continue # pb36 runs against the pb35 oracle above
  var="$(echo "$dialect" | tr '[:lower:]' '[:upper:]')_DIR"
  oracle="${!var:-tools/$dialect}"
  dtests=( "$dir"*.BAS "$dir"*.bas )
  [ ${#dtests[@]} -gt 0 ] || continue
  if [ -f "$oracle/PBC.EXE" ] || { [ -f "$dir/oracle.conf" ] && [ -d "$oracle" ]; }; then
    run_battery "$oracle" "$dialect" "${dtests[@]}"
  else
    echo "SKIP  $dialect battery (${#dtests[@]} file(s)) - no oracle in $oracle (stage the toolchain there to activate)"
  fi
done

exit $fail
