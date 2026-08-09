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
# compiler into tools/<dialect>/ and they activate automatically. So are those
# whose oracle is present but CANNOT RUN here, with the reason named: a staged
# executable that is still SZDD-compressed or is an OS/2 binary, or a command
# template needing a DOSBox command this emulator lacks (autotype). Such a
# battery produces no RESULT.TXT at all, and reporting that as a failure makes
# a missing toolchain indistinguishable from the fidelity divergence this
# harness exists to catch. A FAIL now always means the oracle ran and disagreed.
#
# WHICH EMULATOR MATTERS, and a score is meaningless without naming one:
#
#   vanilla DOSBox 0.74-3   472 pass /  8 fail / 5 skip
#   dosbox-staging 0.82     496 pass /  0 fail / 2 skip
#
# for two unrelated reasons. pb21, tb10 and tb11 have no command-line compiler at
# all - only an IDE - so their oracles drive the menus with autotype, a command
# only staging and DOSBox-X have; vanilla skips those 16 tests. And the 8 qb40 /
# qb45 "failures" are not ours: they are LOG and EXP results a digit off in the
# EMULATOR's x87, and they pass on staging. Vanilla is ~4x faster and still fine
# for a quick check - just do not read its 8 failures as a fidelity gap.
#
# Staging needs an X server (see scripts/lib/dosbox.sh); DOSBOX_EXE is still all a
# caller sets. The remaining 2 skips are pds70 and pds71, whose oracles are an
# SZDD-compressed and an OS/2 binary respectively.
#
# The proprietary toolchains are NOT in the repo: place PBC.EXE 3.50 in
# tools/pb35/ (or point PB35_DIR at it). Without it this harness SKIPS with
# exit 0.
set -euo pipefail
cd "$(dirname "$0")/.."

# shellcheck source=scripts/lib/dosbox.sh
. "$(dirname "$0")/lib/dosbox.sh"

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
  local conf
  conf=$(dosbox_conf_path "$1")
  # shellcheck disable=SC2086  # DOSBOX_PREFIX is a command prefix and must split
  setsid $DOSBOX_PREFIX "$DOSBOX" -conf "$conf" >/dev/null 2>&1 &
  local pid=$!
  for _ in $(seq 1 "${DOSBOX_TICKS:-600}"); do
    { [ -f "$2/DONE.TXT" ] || ! kill -0 "$pid" 2>/dev/null; } && break
    sleep 0.2
  done
  if kill -0 "$pid" 2>/dev/null; then
    sleep 0.3
    dosbox_kill "$pid"
    wait "$pid" 2>/dev/null || true
  fi
  [ -f "$2/DONE.TXT" ]
}

winpath() { cd "$1" && { pwd -W 2>/dev/null || pwd; }; }

# --- can the configured oracle actually run in THIS emulator? ----------------
# A battery whose oracle cannot start writes no RESULT.TXT, and counting that as
# a failure makes it indistinguishable from a real fidelity divergence - the one
# thing this harness exists to detect. Both causes seen so far are decidable up
# front, so they SKIP with the reason named, the same contract as a missing
# toolchain. What is NOT pre-checked stays a failure: an oracle that runs and
# rejects the program is a genuine result.
DOSBOX_FLAVOR=$("$DOSBOX" --version 2>&1 | tr -d '\r' | grep -im1 version || echo "unknown DOSBox")

dosbox_detect_prefix

u16_at() { od -An -tu2 -j "$2" -N 2 "$1" 2>/dev/null | tr -d ' \n'; }
u8_at()  { od -An -tu1 -j "$2" -N 1 "$1" 2>/dev/null | tr -d ' \n'; }

# Why one staged oracle executable cannot run, or nothing when it can. Two cases
# occur in practice: Microsoft's SZDD compression exactly as shipped on the
# distribution disks (DOSBox HANGS trying to execute one, so the whole battery
# stalls until the watchdog kills it), and OS/2 New Executables, whose MZ stub
# makes them look runnable to any check that only reads the first two bytes.
# Every path returns 0: the answer is the text on stdout, not the status. A
# non-zero return here would abort the whole run under `set -e`, because the
# caller reads it with a plain assignment.
exe_blocker() { # $1 = path to an oracle executable
  local f="$1" magic lfanew
  magic=$(od -An -tx1 -N 8 "$f" 2>/dev/null | tr -d ' \n')
  if [ "$magic" = "535a2088f02733d1" ]; then
    echo "$(basename "$f") is still SZDD-compressed (scripts/expand-szdd.py)"
    return 0
  fi
  if [ "${magic:0:4}" != "4d5a" ]; then
    echo "$(basename "$f") is not a DOS executable"
    return 0
  fi
  lfanew=$(u16_at "$f" 60)
  case "$lfanew" in ''|*[!0-9]*) return 0;; esac
  [ "$lfanew" -gt 0 ] || return 0
  [ "$(od -An -c -j "$lfanew" -N 2 "$f" 2>/dev/null | tr -d ' \n')" = "NE" ] || return 0
  [ "$(u8_at "$f" $((lfanew + 54)))" = "1" ] || return 0
  # An NE header does NOT mean "cannot run under DOS". The PDS 7.x tools are BOUND
  # executables - one file holding both builds, the MZ part being the entire DOS
  # program - and BC.EXE 7.10 carries a 13.6 KB stub with the compiler's banner in it
  # and runs under DOS perfectly well. A genuinely OS/2-only image has a stub of a few
  # hundred bytes whose only job is to print a complaint, so the stub's SIZE is what
  # separates them. Searching for that complaint does not work: the correct BC.EXE
  # contains the string too, in its OS/2 half.
  local pages last stub
  pages=$(u16_at "$f" 4); last=$(u16_at "$f" 2)
  case "$pages$last" in ''|*[!0-9]*) return 0;; esac
  stub=$(( pages > 0 ? (pages - 1) * 512 + last : 0 ))
  [ "$stub" -ge 4096 ] && return 0            # bound: the DOS stub is a real program
  echo "$(basename "$f") can only run under OS/2 (DOS stub is $stub bytes)"
  return 0
}

# Oracle executables a command template names, resolved from the D: mount.
conf_exes() { # $1 = conf file, $2 = oracle dir
  local tok p
  for tok in $(tr -d '\r' < "$1" | grep -oiE '[A-Za-z]:\\[^ >]*\.EXE' || true); do
    case "$tok" in [Cc]:*) continue;; esac
    p="$2/$(echo "${tok#??}" | tr '\\' '/')"
    [ -f "$p" ] && echo "$p"
  done
}

# Why a whole battery cannot run here, or nothing when it can.
battery_blocker() { # $1 = tests/diff/<dialect>, $2 = oracle dir
  local tpl exe why
  for tpl in "$1/oracle.conf" "$1/oracle.interpreter"; do
    [ -f "$tpl" ] || continue
    # A DOSBox shell command the emulator does not implement is silently rejected
    # and the IDE it was meant to drive never compiles anything. autotype is
    # DOSBox-X / dosbox-staging only.
    if grep -qiw autotype "$tpl" 2>/dev/null; then
      # "DOSBox-X version ..." / "dosbox-staging, version ...". Match the hyphen:
      # plain vanilla announces itself as "DOSBox version", which ends in an x and
      # slips through any looser pattern.
      case "$DOSBOX_FLAVOR" in
        *DOSBox-X*|*dosbox-x*|*staging*) ;;
        *) echo "needs the 'autotype' command, absent from $DOSBOX_FLAVOR"; return;;
      esac
    fi
    for exe in $(conf_exes "$tpl" "$2"); do
      why=$(exe_blocker "$exe")
      [ -n "$why" ] && { echo "$why"; return; }
    done
  done
  [ -f "$2/PBC.EXE" ] && { why=$(exe_blocker "$2/PBC.EXE"); [ -n "$why" ] && echo "$why"; }
  return 0
}

fail=0

run_battery() { # $1 = oracle dir, $2 = dialect flag ("" = default pb35), $3.. = test files
  local oracle="$1" dialect="$2"
  shift 2
  local label="${dialect:-pb35}"

  # Non-PBC oracles (QB/PDS/TB/...) describe their compile step in
  # tests/diff/<dialect>/oracle.conf: plain DOS commands that turn C:\T.BAS
  # into C:\T.EXE, with the oracle toolchain mounted read-only as D:.
  # Without such a template the classic PBC.EXE invocation is used.
  #
  # Interpreter oracles (GW-BASIC / BASICA / QBasic) ship no compiler: their
  # tests/diff/<dialect>/oracle.interpreter holds the DOS commands that run the
  # interpreter on C:\T.BAS (the program itself writes RESULT.TXT and ends with
  # SYSTEM). There is no T.EXE on the real side - the interpreter IS the run.
  local template="" interp=""
  if [ -n "$dialect" ]; then
    [ -f "tests/diff/$dialect/oracle.interpreter" ] && interp="tests/diff/$dialect/oracle.interpreter"
    [ -z "$interp" ] && [ -f "tests/diff/$dialect/oracle.conf" ] && template="tests/diff/$dialect/oracle.conf"
  fi

  rm -rf "build/diff/$label" && mkdir -p "build/diff/$label/real" "build/diff/$label/ours"
  [ -z "$template" ] && [ -z "$interp" ] && cp "$oracle/PBC.EXE" "build/diff/$label/real/"

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
      if [ -n "$interp" ]; then
        echo "mount d \"$(winpath "$oracle")\""
        echo "c:"
        sed -e 's/\r$//' "$interp"   # runs the interpreter on C:\T.BAS; the program writes RESULT.TXT
      elif [ -n "$template" ]; then
        echo "mount d \"$(winpath "$oracle")\""
        echo "c:"
        sed -e 's/\r$//' "$template"
        echo "T.EXE"
      else
        echo "c:"
        echo "PBC.EXE -CE T.BAS > PBCOUT.TXT"
        echo "T.EXE"
      fi
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

    # --- round-trip lane: dialect program -> pb35 source -> OPTIMIZED pb35 EXE -------------
    # Proves the back-emitter turns the program back into PB 3.5 source that, recompiled under the
    # pb35 dialect WITH the optimizer on, still produces the genuine oracle's output - i.e. "fully
    # optimized and fully turned back into pb35, yielding the same output". Reuses the real
    # RESULT.TXT already computed above. Runs for EVERY battery: the back-emitter emits a $COMPAT
    # directive that makes the pb35 recompile replicate the source dialect's runtime quirks (PRINT
    # float formatting, 16-bit integer arithmetic, float-to-integer rounding, VAL radix wrapping,
    # ^Z-on-close, constant-folding), so cross-family programs reproduce the oracle byte-for-byte.
    # Known residual: a few QB transcendental edges (LOG(e#)) differ in the 16th significant digit -
    # a sub-ULP x87 difference between the pb35 and QB codegen paths. Disable entirely with RT=0.
    if [ "${RT:-1}" = "1" ]; then
      local od="build/diff/$label/ours"
      rm -f "$od/RESULT.TXT" "$od/T.EXE" "$od/RT.BAS"
      if ! $PBC_OURS ${flags[@]+"${flags[@]}"} --emit-basic "$od/T.BAS" -O "$od/RT.BAS" > "$od/rtemit.txt" 2>&1; then
        echo "FAIL  $label/$name (round-trip emit-basic)"; sed 's/^/      /' "$od/rtemit.txt"; fail=1; continue
      fi
      if ! $PBC_OURS --dialect pb35 --optimize "$od/RT.BAS" -O "$od/T.EXE" > "$od/rtcomp.txt" 2>&1; then
        echo "FAIL  $label/$name (round-trip pb35 recompile)"; sed 's/^/      /' "$od/rtcomp.txt"; fail=1; continue
      fi
      if ! run_dosbox "build/diff/$label/ours.conf" "$od" || [ ! -f "$od/RESULT.TXT" ]; then
        echo "FAIL  $label/$name (round-trip EXE produced no RESULT.TXT)"; fail=1; continue
      fi
      if diff -q <(tr -d '\r' < "build/diff/$label/real/RESULT.TXT") <(tr -d '\r' < "$od/RESULT.TXT") >/dev/null; then
        echo "PASS  $label/$name (round-trip pb35 identical to genuine $label)"
      else
        echo "FAIL  $label/$name (round-trip pb35 output differs from genuine $label)"
        { diff <(tr -d '\r' < "build/diff/$label/real/RESULT.TXT") <(tr -d '\r' < "$od/RESULT.TXT") || true; } | head -20 | sed 's/^/      /'
        fail=1
      fi
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
  if [ -f "$oracle/PBC.EXE" ] \
     || { [ -f "$dir/oracle.conf" ] && [ -d "$oracle" ]; } \
     || { [ -f "$dir/oracle.interpreter" ] && [ -d "$oracle" ] && [ -n "$(ls -A "$oracle" 2>/dev/null)" ]; }; then
    blocker=$(battery_blocker "${dir%/}" "$oracle")
    if [ -n "$blocker" ]; then
      echo "SKIP  $dialect battery (${#dtests[@]} file(s)) - oracle cannot run here: $blocker"
    else
      run_battery "$oracle" "$dialect" "${dtests[@]}"
    fi
  else
    echo "SKIP  $dialect battery (${#dtests[@]} file(s)) - no oracle in $oracle (stage the toolchain there to activate)"
  fi
done

exit $fail
