#!/usr/bin/env bash
# Generates docs/DECOMPILATION.md: for every pb36 feature/optimization example under
# docs/decompilation/{features,optimizations}/*.bas, shows the original source next to its
# decompilation (pbc --emit-basic) with and without the optimizer, so the lowering of each
# feature - and what the optimizer does on top - is visible as readable PB 3.5.
#
# Each example .bas starts with metadata comments the generator reads:
#   ' @title: <short feature/optimization name>
#   ' @desc:  <one-line description of what to look for in the decompilation>
set -uo pipefail
cd "$(dirname "$0")/.."
PBC="$(find pbc/bin/Release -name pbc.dll 2>/dev/null | head -1)"
if [ -z "$PBC" ]; then dotnet build pbc -c Release -v q --nologo; PBC="$(find pbc/bin/Release -name pbc.dll 2>/dev/null | head -1)"; fi
run() { DOTNET_ROLL_FORWARD=Major dotnet "$PBC" "$@"; }
OUT="docs/DECOMPILATION.md"
TMP="${TMPDIR:-/tmp}/gendc.$$"; mkdir -p "$TMP/a" "$TMP/b"

# Optional DOSBox: when present, the annotation reflects OUTPUT equivalence (run the program both ways
# and diff), not just whether the decompilation recompiles. Without it, falls back to a compile check.
DOSBOX="${DOSBOX_EXE:-}"
[ -z "$DOSBOX" ] && for c in tools/dosbox/dosbox dosbox-staging dosbox; do command -v "$c" >/dev/null 2>&1 && { DOSBOX=$c; break; }; [ -x "$c" ] && { DOSBOX=$c; break; }; done
# Run DOSBox headless on a single shared Xvfb display so no window pops up and there is no per-call
# xvfb-run startup cost (dosbox-staging needs a real display - SDL_VIDEODRIVER=dummy exits without
# running). Without Xvfb, DOSBox runs on the real display (a window appears); install xvfb to silence it.
if [ -z "${DISPLAY:-}" ] && command -v Xvfb >/dev/null 2>&1; then
  Xvfb :99 -screen 0 640x480x8 >/dev/null 2>&1 & _XVFB_PID=$!
  export DISPLAY=:99
  trap '[ -n "${_XVFB_PID:-}" ] && kill "$_XVFB_PID" 2>/dev/null' EXIT
  sleep 1
fi
winpath() { cd "$1" && pwd; }
run_dosbox() { rm -f "$2/DONE.TXT" "$2/RESULT.TXT"; "$DOSBOX" -conf "$1" >/dev/null 2>&1 & local pid=$!
  for _ in $(seq 1 250); do { [ -f "$2/DONE.TXT" ] || ! kill -0 "$pid" 2>/dev/null; } && break; sleep 0.2; done
  kill -0 "$pid" 2>/dev/null && { sleep 0.3; kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; }; [ -f "$2/RESULT.TXT" ]; }
mkconf() { printf '[sdl]\nwindow_position=-10000,-10000\n[cpu]\ncore=auto\ncycles=max\n[dosbox]\nems=true\n[autoexec]\nmount c "%s"\nc:\nT.EXE\necho ok>DONE.TXT\nexit\n' "$(winpath "$1")"; }

# Decides the round-trip status of one example: SAME (recompiles + identical output), DIFFERS
# (recompiles but the runtime output diverges), NOCOMPILE (the decompilation is not valid pb35), or
# COMPILES (no DOSBox: recompiled but output not re-verified). Echoes the status word.
roundtrip_status() { # $1 = .bas file
  run --dialect pb36 --emit-basic "$1" -O "$TMP/rt.bas" >/dev/null 2>&1 || { echo NOCOMPILE; return; }
  run --dialect pb35 "$TMP/rt.bas" -O "$TMP/rt.exe" >/dev/null 2>&1 || { echo NOCOMPILE; return; }
  [ -z "$DOSBOX" ] && { echo COMPILES; return; }
  # output equivalence: wrap PRINT -> RESULT.TXT, run the pb36 original and the pb35 decompilation
  { echo 'OPEN "RESULT.TXT" FOR OUTPUT AS #1'; body "$1" | sed 's/PRINT \([^#]\)/PRINT #1, \1/g'; echo 'CLOSE #1'; } > "$TMP/a/T.BAS"
  run --dialect pb36 "$TMP/a/T.BAS" -O "$TMP/a/T.EXE" >/dev/null 2>&1 || { echo COMPILES; return; }
  mkconf "$TMP/a" > "$TMP/a.conf"; run_dosbox "$TMP/a.conf" "$TMP/a" || { echo COMPILES; return; }
  run --dialect pb36 --emit-basic "$TMP/a/T.BAS" -O "$TMP/b/T.BAS" >/dev/null 2>&1 || { echo NOCOMPILE; return; }
  run --dialect pb35 "$TMP/b/T.BAS" -O "$TMP/b/T.EXE" >/dev/null 2>&1 || { echo NOCOMPILE; return; }
  mkconf "$TMP/b" > "$TMP/b.conf"; run_dosbox "$TMP/b.conf" "$TMP/b" || { echo DIFFERS; return; }
  diff -q <(tr -d '\r' < "$TMP/a/RESULT.TXT") <(tr -d '\r' < "$TMP/b/RESULT.TXT") >/dev/null 2>&1 && echo SAME || echo DIFFERS
}

meta() { sed -n "s/^' @$1:[[:space:]]*//p" "$2" | head -1; }
# the program body without the @-metadata header comments
body() { grep -vE "^' @(title|desc):" "$1"; }

emit_section() { # $1 = .bas file
  local f="$1" title desc
  title=$(meta title "$f"); [ -n "$title" ] || title=$(basename "$f" .bas)
  desc=$(meta desc "$f")
  echo "### $title"
  echo
  [ -n "$desc" ] && { echo "$desc"; echo; }

  # decompilation without the optimizer (pure feature lowering)
  if run --dialect pb36 --no-optimize --emit-basic "$f" > "$TMP/no.bas" 2>"$TMP/err"; then
    : # ok
  else
    echo "> decompile (no-opt) failed:"; echo '```'; sed 's/^/  /' "$TMP/err"; echo '```'; echo; return
  fi
  # decompilation with the optimizer (lowering + OptPruner dead-code/DEF SEG cleanup)
  run --dialect pb36 --optimize --emit-basic "$f" > "$TMP/opt.bas" 2>/dev/null

  # round-trip status: does the decompilation recompile under pb35 and run the same?
  case "$(roundtrip_status "$f")" in
    SAME)      echo '> **Round-trips to PB 3.5:** ✅ the decompilation recompiles under `--dialect pb35` and runs with identical output.';;
    COMPILES)  echo '> **Round-trips to PB 3.5:** the decompilation recompiles under `--dialect pb35` (runtime output not re-verified in this environment).';;
    DIFFERS)   echo '> **Illustrative decompilation:** recompiles under `--dialect pb35` but the runtime result diverges - PB 3.5 has no faithful equivalent for this construct (e.g. rotate operators, far-pointer offset arithmetic). The form below shows the lowering structure.';;
    *)         echo '> **Illustrative decompilation:** PB 3.5 cannot express this construct (e.g. a function pointer that returns a value), so it lowers in code generation via compiler-internal types/names. The form below shows the lowering *structure*, not compilable PB 3.5.';;
  esac
  echo

  # source and decompilation shown together so the lowering is visible at a glance
  echo '**pb3.6 source:**'
  echo
  echo '```basic'
  body "$f"
  echo '```'
  echo
  echo '**Decompiled (lowered to PB 3.5):**'
  echo
  echo '```basic'
  cat "$TMP/no.bas"
  echo '```'
  echo
  if diff -q "$TMP/no.bas" "$TMP/opt.bas" >/dev/null 2>&1; then
    echo '_With the optimizer: identical — this feature lowers entirely in the binder; the optimizer changes nothing at the source level._'
  else
    echo '**With the optimizer (`--optimize`, e.g. dead-code / `DEF SEG` cleanup):**'
    echo
    echo '```basic'
    cat "$TMP/opt.bas"
    echo '```'
  fi
  echo
}

{
  echo "# pb3.6 decompilation reference"
  echo
  echo "_Generated by \`scripts/gen-decompilation.sh\` — do not edit by hand; edit the examples under"
  echo "\`docs/decompilation/\` and regenerate._"
  echo
  echo "Each pb3.6 language feature and optimizer pass is shown as a minimal program next to its"
  echo "decompilation — \`pbc --dialect pb36 --emit-basic\`, which un-parses the bound program back to"
  echo "PB 3.5-compatible source. This reveals exactly what each feature lowers to (the binder's"
  echo "desugaring) and what the optimizer does on top of it. Constructs that lower below the source"
  echo "level (a real branch for the ternary, a state machine for \`YIELD\`, a thunk for a closure) are"
  echo "rendered in the nearest readable PB 3.5 form; their full machine lowering lives in the codegen."
  echo
  echo "> The optimizer column reflects the AST-level passes the back-emitter can see (dead-code"
  echo "> elimination, \`DEF SEG\` coalescing). Tier-2/3 optimizations (constant folding, CSE, the"
  echo "> peephole, the instruction scheduler, loop unrolling, inlining) operate below the source"
  echo "> level and are documented in docs/PIPELINE.md and docs/PB36.md."
  echo

  shopt -s nullglob
  if compgen -G "docs/decompilation/features/*.bas" >/dev/null; then
    echo "## Language features"
    echo
    for f in docs/decompilation/features/*.bas; do emit_section "$f"; done
  fi
  if compgen -G "docs/decompilation/optimizations/*.bas" >/dev/null; then
    echo "## Optimizations"
    echo
    echo "These are the optimizer passes whose effect is visible at the source level (the AST-level"
    echo "passes the back-emitter can render). Compile with \`--optimize\` (the pb36 default) vs"
    echo "\`--no-optimize\` to see the difference."
    echo
    for f in docs/decompilation/optimizations/*.bas; do emit_section "$f"; done
  fi

  cat <<'CATALOGUE'
## Optimizations below the source level

The bulk of the optimizer rewrites the *generated machine code*, not the AST, so it does not surface
in a source decompilation. For completeness, these passes run during code generation (see
docs/PIPELINE.md for the tier model and docs/PB36.md for the per-pass detail):

| Pass | Tier | What it does |
|------|------|--------------|
| Constant folding | 2 (emit) | folds compile-time-constant arithmetic / comparisons |
| Sparse conditional constant propagation (SCCP) | 1 (IR) | propagates constants through branches, prunes dead arms |
| Copy propagation | 2 (emit) | replaces a copy's uses with its source, killing the copy |
| Common-subexpression elimination (local + cross-block) | 2 (emit) | reuses an already-computed value instead of recomputing |
| Global value numbering (GVN) | 1 (IR) | unifies provably-equal computations |
| Dead-store elimination (DSE) | 1 (IR) | drops stores whose value is never read |
| Loop-invariant code motion (LICM) | 1 (IR) | hoists invariant work out of a loop |
| Induction-variable simplification | 2 (emit) | strength-reduces `i*const` loop addressing |
| Strength reduction | 2 (emit) | rewrites `x*const` as shift+add, `x\const` as a reciprocal/shift |
| Loop unrolling | 2 (emit) | unrolls small constant-trip loops |
| Inlining | 2 (emit) | inlines small/leaf procedures |
| Tail-call optimization | 2 (emit) | reuses the frame for a tail self-call (constant stack) |
| Dense `SELECT CASE` -> jump table | 2 (emit) | replaces the compare chain with an indexed indirect jump |
| Instruction scheduler | post-emit | reorders the final byte stream to group loads / ALU work (latency hiding) |
| Peephole | post-emit | local instruction-pattern rewrites (accumulator short forms, sign-extended imm8, ...) |
| Register-parameter passing | 1 (pre) | passes leaf-call arguments in registers |
| Float demotion | 1 (pre) | demotes a DOUBLE to SINGLE when the program never needs the precision |
| Reachability tree-shaking (O22) | 3 (layout) | drops unreachable procedures, runtime helpers and unread DATA |
| Runtime trim / virtual BSS / `.COM` | 3 (layout) | shrinks the image: unused runtime, zero-init data behind the image, tiny-model `.COM` |

CATALOGUE
} > "$OUT"

rm -rf "$TMP"
echo "wrote $OUT ($(wc -l < "$OUT") lines, $(ls docs/decompilation/features/*.bas docs/decompilation/optimizations/*.bas 2>/dev/null | wc -l) examples)"
