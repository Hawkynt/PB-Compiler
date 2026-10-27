#!/usr/bin/env bash
set -euo pipefail

repoRoot=$(cd "$(dirname "$0")/../.." && pwd)
testRoot=$(mktemp -d)
trap 'rm -rf -- "$testRoot"' EXIT
sandbox="$testRoot/repo with spaces"
mkdir -p "$sandbox/scripts/lib" "$sandbox/tests/diff" "$testRoot/bin" "$testRoot/oracle"
for script in scripts/run-diff-tests.sh scripts/lib/dosbox.sh scripts/lib/diff-diagnostics.sh; do
  tr -d '\r' < "$repoRoot/$script" > "$sandbox/$script"
done
. "$sandbox/scripts/lib/diff-diagnostics.sh"

work="$testRoot/guest"
capture="$testRoot/captured"
mkdir -p "$work"
printf 'FIRST source\r\n' > "$work/T.BAS"
printf 'output\r\n' > "$work/RESULT.TXT"
printf 'compiler diagnostic\r\n' > "$work/BCLOG.TXT"
printf 'NEVER_UPLOAD_BINARY' > "$work/TB.EXE"
printf 'NEVER_UPLOAD_SECRET' > "$work/SECRET.KEY"
ln -s "$work/SECRET.KEY" "$work/RT.BAS"
printf '[autoexec]\n' > "$testRoot/oracle.conf"
printf 'emulator diagnostic\n' > "$testRoot/oracle.conf.log"
diff_capture_failure "$capture" "$work" "$testRoot/oracle.conf"
cmp "$work/T.BAS" "$capture/T.BAS"
cmp "$work/RESULT.TXT" "$capture/RESULT.TXT"
cmp "$work/BCLOG.TXT" "$capture/BCLOG.TXT"
cmp "$testRoot/oracle.conf" "$capture/DOSBOX.CONF"
cmp "$testRoot/oracle.conf.log" "$capture/DOSBOX.LOG"
grep -q 'TB.EXE' "$capture/FILES.TXT"
[ ! -e "$capture/RT.BAS" ] || { echo 'FAIL: followed a guest symlink' >&2; exit 1; }
if grep -R -q 'NEVER_UPLOAD' "$capture"; then
  echo 'FAIL: diagnostics copied a binary or secret' >&2
  exit 1
fi

# Exercise the real harness in an isolated repository. The first oracle exits
# without a sentinel; a later successful case overwrites the working directory.
cat > "$testRoot/bin/dotnet" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
cat > "$testRoot/bin/emulator" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [ "$1" = '--version' ]; then
  echo 'DOSBox-X version test fixture'
  exit 0
fi
conf="$2"
guest=$(sed -n 's/^mount c "\(.*\)"$/\1/p' "$conf")
if grep -q '^echo headless' "$conf"; then
  printf 'headless\r\n' > "$guest/PROBE.TXT"
  exit 0
fi
if grep -q FIRST "$guest/T.BAS"; then
  echo 'FIRST emulator stdout diagnostic'
  echo 'FIRST emulator stderr diagnostic' >&2
  exit 1
fi
if grep -q MISSING "$guest/T.BAS"; then
  echo 'MISSING oracle returned without output'
  printf 'ok\r\n' > "$guest/DONE.TXT"
  exit 0
fi
echo 'SECOND emulator diagnostic'
printf 'next case\r\n' > "$guest/RESULT.TXT"
printf 'ok\r\n' > "$guest/DONE.TXT"
STUB
chmod +x "$testRoot/bin/dotnet" "$testRoot/bin/emulator"
printf 'NEVER_UPLOAD_COMPILER' > "$testRoot/oracle/PBC.EXE"
printf "PRINT \"FIRST\"\n" > "$sandbox/tests/diff/FIRST.BAS"
printf "PRINT \"MISSING\"\n" > "$sandbox/tests/diff/MISSING.BAS"
printf "PRINT \"SECOND\"\n" > "$sandbox/tests/diff/SECOND.BAS"
status=0
PATH="$testRoot/bin:$PATH" DOSBOX_EXE="$testRoot/bin/emulator" \
  PB35_DIR="$testRoot/oracle" PB_TOOLCHAIN_KEY='' RT=0 \
  bash "$sandbox/scripts/run-diff-tests.sh" > "$testRoot/harness.log" 2>&1 || status=$?
[ "$status" -eq 1 ] || { echo "FAIL: expected failing harness status 1, got $status" >&2; exit 1; }
grep -q 'PASS  pb35/SECOND' "$testRoot/harness.log"
grep -q 'PASS  pb36/SECOND' "$testRoot/harness.log"
for dialect in pb35 pb36; do
  mapfile -t captures < <(find "$sandbox/build/diff-failures" -path "*/$dialect/FIRST/oracle/DOSBOX.LOG")
  [ "${#captures[@]}" -eq 1 ] || { echo "FAIL: missing $dialect failure evidence" >&2; exit 1; }
  grep -q 'FIRST emulator stdout diagnostic' "${captures[0]}"
  grep -q 'FIRST emulator stderr diagnostic' "${captures[0]}"
  grep -q FIRST "$(dirname "${captures[0]}")/T.BAS"
  mapfile -t completed < <(find "$sandbox/build/diff-failures" -path "*/$dialect/MISSING/oracle/DONE.TXT")
  [ "${#completed[@]}" -eq 1 ] || { echo "FAIL: lost completed $dialect oracle failure" >&2; exit 1; }
  grep -q MISSING "$(dirname "${completed[0]}")/T.BAS"
done
if grep -R -q 'NEVER_UPLOAD' "$sandbox/build/diff-failures"; then
  echo 'FAIL: harness diagnostics copied the oracle executable' >&2
  exit 1
fi
echo 'PASS: failure evidence survives later cases without binaries or secrets'
