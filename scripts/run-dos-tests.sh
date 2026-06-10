#!/usr/bin/env bash
# =============================================================================
# run-dos-tests.sh - compile the PB test battery with OUR compiler on the host,
# then run every produced EXE under headless DOSBox and verify the results.
# =============================================================================
# Two verification modes per tests/<NAME>.BAS, decided by what the program does:
#   1. golden output: tests/<NAME>.expected exists -> the EXE's redirected
#      stdout (<NAME>.OUT) must match it line-for-line (CRLF normalized,
#      trailing whitespace stripped - PB prints numerics with a trailing
#      space that editors would silently destroy in the goldens).
#   2. TESTLIB battery: the program appends to UNITTEST.LOG in the
#      [SUITE]/[PASS]/[FAIL]/[RESULT] format (see tests/TESTLIB.BI); the run
#      fails on any [FAIL] or a [SUITE] without [RESULT] (crash/hang).
# Both modes can coexist in one battery. Suites with SUB Test_* and no driver
# get one generated, exactly like PB-SvgaLibrary's harness.
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
PBC="dotnet run --project pbc -c Release --no-build --"

shopt -s nullglob
tests=( tests/*.BAS tests/*.bas )
[ ${#tests[@]} -gt 0 ] || { echo "::error::no tests/*.BAS"; exit 1; }

rm -rf build && mkdir -p build
cp tests/*.BI build/ 2>/dev/null || true
rm -f build/UNITTEST.LOG

fail=0

# $COMPILE UNIT sources are prerequisites, not programs: compile them FIRST
# into build/<NAME>.PBU (their own names, so $LINK "NAME.PBU" in a test that
# is compiled inside build/ resolves) and exclude them from the run loop.
is_unit() { grep -qiE '^[[:space:]]*\$COMPILE[[:space:]]+UNIT' "$1"; }
for t in "${tests[@]}"; do
  is_unit "$t" || continue
  name=$(basename "$t" .BAS); name=$(basename "$name" .bas)
  if $PBC "$t" -O "build/$name.PBU" > "build/$name.pbcout" 2>&1; then
    echo "UNIT  $name"
  else
    echo "FAIL  $name (unit compile)"; sed 's/^/      /' "build/$name.pbcout"; fail=1
  fi
done

i=0
for t in "${tests[@]}"; do
  is_unit "$t" && continue
  i=$((i+1)); name=$(basename "$t" .BAS); name=$(basename "$name" .bas)
  cp "$t" "build/T$i.BAS"

  # auto-generate the driver from SUB Test_* names unless the suite wires its own
  if grep -qiE '^[[:space:]]*SUB[[:space:]]+Test_' "build/T$i.BAS" && ! grep -qi 'Test_BeginSuite' "build/T$i.BAS"; then
    {
      printf "\r\n' === auto-generated test driver ===\r\n"
      grep -qiE '^[[:space:]]*SUB[[:space:]]+Test_Setup[[:space:]]*$' "build/T$i.BAS" && printf 'CALL Test_Setup\r\n'
      printf 'CALL Test_BeginSuite("%s")\r\n' "$name"
      grep -oiE '^[[:space:]]*SUB[[:space:]]+Test_[A-Za-z0-9_]+' "build/T$i.BAS" \
        | sed -E 's/^[[:space:]]*[Ss][Uu][Bb][[:space:]]+//' \
        | grep -viE '^Test_(Setup|Teardown)$' \
        | while read -r fn; do printf 'CALL %s\r\n' "$fn"; done
      printf 'CALL Test_EndSuite("%s")\r\n' "$name"
      grep -qiE '^[[:space:]]*SUB[[:space:]]+Test_Teardown[[:space:]]*$' "build/T$i.BAS" && printf 'CALL Test_Teardown\r\n'
      printf 'END\r\n'
    } >> "build/T$i.BAS"
  fi

  # host-side compile with our compiler
  if ! $PBC "build/T$i.BAS" -O "build/T$i.EXE" > "build/T$i.pbcout" 2>&1; then
    echo "FAIL  $name (compile)"; sed 's/^/      /' "build/T$i.pbcout"; fail=1; continue
  fi

  # one DOSBox session per test. DONE.TXT is the completion sentinel:
  # dosbox-staging refuses to exit when the program finishes "too quickly"
  # (anti-vanish UX), so the harness polls the sentinel and kills the emulator.
  # tests/<NAME>.IN, when present, is redirected into the program's stdin.
  run="T$i.EXE > T$i.OUT"
  if [ -f "tests/$name.IN" ]; then
    cp "tests/$name.IN" "build/T$i.IN"
    run="T$i.EXE < T$i.IN > T$i.OUT"
  fi
  {
    echo "[sdl]"; echo "[cpu]"; echo "core=auto"; echo "cycles=max"
    echo "[dosbox]"; echo "ems=true"
    echo "[autoexec]"
    echo "mount c \"$(pwd -W 2>/dev/null || pwd)/build\""  # pwd -W: Windows-style path under git-bash
    echo "c:"
    echo "$run"
    echo "echo ok > DONE.TXT"
    echo "exit"
  } > "build/dosbox-T$i.conf"
  rm -f build/DONE.TXT
  "$DOSBOX" -conf "build/dosbox-T$i.conf" >/dev/null 2>&1 &
  dospid=$!
  for _ in $(seq 1 600); do
    { [ -f build/DONE.TXT ] || ! kill -0 "$dospid" 2>/dev/null; } && break
    sleep 0.2
  done
  if kill -0 "$dospid" 2>/dev/null; then
    sleep 0.3
    kill "$dospid" 2>/dev/null || true
    wait "$dospid" 2>/dev/null || true
  fi
  [ -f build/DONE.TXT ] || { echo "FAIL  $name (hang)"; fail=1; continue; }

  if [ -f "tests/$name.expected" ]; then
    norm() { tr -d '\r' < "$1" | sed -e 's/[[:space:]]*$//'; }
    if [ -f "build/T$i.OUT" ] && diff -q <(norm "build/T$i.OUT") <(norm "tests/$name.expected") >/dev/null; then
      echo "PASS  $name"
    else
      echo "FAIL  $name (output mismatch)"
      [ -f "build/T$i.OUT" ] && diff <(norm "build/T$i.OUT") <(norm "tests/$name.expected") | head -20 || echo "      no output produced"
      fail=1
    fi
  else
    echo "RAN   $name (battery - results in UNITTEST.LOG)"
  fi
done

# evaluate the shared battery log, if any suite wrote one
log=build/UNITTEST.LOG
if [ -f "$log" ]; then
  echo; echo "=================== PB test battery ==================="
  awk '
    /^\[SUITE\]/   { printf "\n%s\n", substr($0,9); next }
    /^  \[PASS\]/  { total++; next }
    /^  \[FAIL\]/  { total++; tfail++; printf "  FAIL %s\n", substr($0,10); next }
    /^  \[SKIP\]/  { tskip++; printf "  SKIP %s\n", substr($0,10); next }
    END { printf "\nTotal: %d  Passed: %d  Failed: %d  Skipped: %d\n", total, total-tfail, tfail, tskip }
  ' "$log"
  started=$(grep -c '^\[SUITE\]' "$log" || true)
  finished=$(grep -c '^\[RESULT\]' "$log" || true)
  [ "$started" = "$finished" ] || { echo "::error::suite crashed/hung (started=$started finished=$finished)"; fail=1; }
  grep -qiE '^\s*\[FAIL\]' "$log" && { echo "::error::battery reported failures"; fail=1; }
fi

exit $fail
